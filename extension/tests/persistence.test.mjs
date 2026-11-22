import test from "node:test";
import assert from "node:assert/strict";
import {webcrypto} from "node:crypto";
import {chooseMetadata,completeMetadata} from "../metadata.js";
if (!globalThis.crypto) Object.defineProperty(globalThis,'crypto',{value:webcrypto});
let storage={};
globalThis.chrome={storage:{local:{
  async get(keys){if(keys==null)return structuredClone(storage);if(typeof keys==="string")return {[keys]:structuredClone(storage[keys])};return Object.fromEntries(keys.map(k=>[k,structuredClone(storage[k])]));},
  async getKeys(){return Object.keys(storage);},
  async set(entries){Object.assign(storage,structuredClone(entries));},
  async remove(keys){for(const k of Array.isArray(keys)?keys:[keys])delete storage[k];}
}}};
const bridge=await import("../bridge.js");

test("missing tags and rating do not weaken post and image identity checks",()=>{
  const empty={post_id:'12',page_url:'https://danbooru.donmai.us/posts/12',tags_complete:true,
    tags:{copyright:[],character:[],artist:[],general:[],meta:[]},rating:'',
    original_urls:['https://cdn.donmai.us/original/empty.png']};
  assert.equal(completeMetadata(empty),true);
  assert.equal(completeMetadata({...empty,tags_complete:false}),false);
  assert.equal(completeMetadata({...empty,post_id:'123'}),false);
  assert.equal(completeMetadata({...empty,page_url:'https://example.com/posts/12'}),false);
  assert.equal(completeMetadata({...empty,original_urls:[]}),false);
});
const page=(id,url)=>({post_id:id,page_url:"https://danbooru.donmai.us/posts/"+id,tags_complete:true,tags:{copyright:["work"],general:["solo"]},rating:"g",original_urls:[url],scraped_at:Number(id)});

test("original link matches post identity; no prefix or CDN lookalike pairing",()=>{
  const one="https://cdn.donmai.us/original/12.png",two="https://cdn.donmai.us/original/123.png";
  const pages=[page("12",one),page("123",two)];
  assert.equal(chooseMetadata({url:two,referrer:pages[0].page_url},pages).post_id,"123");
  assert.equal(chooseMetadata({url:one+"?download=1"},pages).post_id,"12");
  assert.equal(chooseMetadata({url:"https://other.example/original/123.png"},pages),null);
  assert.equal(chooseMetadata({url:one},[page("12",one),page("123",one)]),null);
  assert.equal(completeMetadata({...pages[0],rating:""}),true);
  assert.equal(completeMetadata(pages[0]),true);
});
test("600 concurrent records persist with no truncation; additions during flush survive",async()=>{
  storage={};await Promise.all(Array.from({length:600},(_,i)=>bridge.queueAdd({ingest_id:"id-"+i,sent_at:i,metadata_complete:true})));
  assert.equal(await bridge.queueSize(),600);
  let added=false,received=new Set();
  globalThis.fetch=async(_url,options)=>{
    const payload=JSON.parse(options.body);received.add(payload.ingest_id);
    if(!added){added=true;await bridge.queueAdd({ingest_id:"new-during-flush",metadata_complete:true,sent_at:999});}
    return {ok:true,async json(){return {ok:true,persisted:true,ingest_id:payload.ingest_id};}};
  };
  const result=await bridge.queueFlush(47821,"test");assert.equal(result.sent,600);assert.equal(result.left,1);assert.equal(received.size,600);
  assert.equal((await bridge.queueAll())[0].ingest_id,"new-during-flush");
  await bridge.queueFlush(47821,"test");assert.equal(await bridge.queueSize(),0);
});
test("offline, lost acknowledgement and worker module restart retain durable queue",async()=>{
  storage={};await bridge.queueAdd({ingest_id:"retry",metadata_complete:true});
  globalThis.fetch=async()=>{throw Error("offline");};assert.equal((await bridge.queueFlush(47821,"")).left,1);
  globalThis.fetch=async()=>({ok:true,async json(){return {ok:true};}});
  assert.equal((await bridge.queueFlush(47821,"")).left,1);
  const restarted=await import("../bridge.js?restart=1");assert.equal(await restarted.queueSize(),1);
  let delivered=0;globalThis.fetch=async(_url,options)=>{const p=JSON.parse(options.body);delivered++;return {ok:true,async json(){return {ok:true,persisted:true,ingest_id:p.ingest_id};}};};
  assert.equal((await restarted.queueFlush(47821,"")).left,0);assert.equal(delivered,1);
});
test("late complete metadata cannot be removed by an earlier partial acknowledgement",async()=>{
  storage={};await bridge.queueAdd({ingest_id:"late",metadata_complete:false});
  globalThis.fetch=async(_url,options)=>{const p=JSON.parse(options.body);await bridge.queueAdd({ingest_id:"late",metadata_complete:true,sent_at:Date.now()});return {ok:true,async json(){return {ok:true,persisted:true,ingest_id:p.ingest_id};}};};
  await bridge.queueFlush(47821,"");assert.equal(await bridge.queueSize(),1);assert.equal((await bridge.queueAll())[0].metadata_complete,true);
});
test("legacy queue migration is idempotent and preserves all rows",async()=>{
  storage={bridgeQueue:[{filename:"first"},{filename:"second"}]};await bridge.migrateLegacyQueue();await bridge.migrateLegacyQueue();assert.equal(await bridge.queueSize(),2);assert.equal(storage.bridgeQueue,undefined);
});

test("a rejected record is retained while later records are delivered",async()=>{
  storage={};
  await bridge.queueAdd({ingest_id:"bad",sent_at:1});
  await bridge.queueAdd({ingest_id:"good",sent_at:2});
  globalThis.fetch=async(_url,options)=>{
    const payload=JSON.parse(options.body);
    if(payload.ingest_id==="bad")
      return {ok:false,status:400,async json(){return {ok:false,error:"记录无效"};}};
    return {ok:true,status:200,async json(){return {ok:true,persisted:true,ingest_id:payload.ingest_id};}};
  };
  const result=await bridge.queueFlush(47821,"test");
  assert.equal(result.sent,1);
  assert.equal(result.rejected,1);
  assert.equal(result.stopped,"记录无效");
  assert.deepEqual((await bridge.queueAll()).map(record=>record.ingest_id),["bad"]);
});
