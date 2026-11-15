import test from "node:test";
import assert from "node:assert/strict";
import {DEFAULT_SETTINGS,DEFAULT_RULES} from "../rules.js";
let storage={},downloads=new Map(),deliveries=[],online=true;
const event=()=>({listeners:[],addListener(fn){this.listeners.push(fn);}});
function events(){return {onCreated:event(),onDeterminingFilename:event(),onChanged:event()};}
const downloadEvents=events();
const runtime={onInstalled:event(),onStartup:event(),onMessage:event(),getURL:p=>"chrome-extension://gallery/"+p,async openOptionsPage(){}};
globalThis.chrome={
 storage:{local:{async get(keys){if(keys==null)return structuredClone(storage);if(typeof keys==="string")return {[keys]:structuredClone(storage[keys])};return Object.fromEntries(keys.map(k=>[k,structuredClone(storage[k])]));},async set(value){Object.assign(storage,structuredClone(value));},async remove(keys){for(const k of Array.isArray(keys)?keys:[keys])delete storage[k];}}},
 downloads:{...downloadEvents,async search({id}){return downloads.has(id)?[structuredClone(downloads.get(id))]:[];}},
 runtime,notifications:{async create(){}},action:{async setBadgeText(){},async setBadgeBackgroundColor(){}},alarms:{onAlarm:event(),create(){}}
};
globalThis.fetch=async(url,options)=>{
 if(!online)throw Error("offline");
 if(url.endsWith("/ping"))return {ok:true,async json(){return {ok:true,pending:0,protocol:2};}};
 const payload=JSON.parse(options.body);if(!payload.probe)deliveries.push(payload);
 return {ok:true,async json(){return {ok:true,persisted:true,ingest_id:payload.ingest_id};}};
};
storage.settings={...DEFAULT_SETTINGS,bridgeToken:"fixture"};storage.rules=DEFAULT_RULES;storage.clientId="fixture-client";
await import("../background.js");
async function waitFor(fn,timeout=2000){const end=Date.now()+timeout;while(Date.now()<end){if(fn())return;await new Promise(r=>setTimeout(r,5));}assert.ok(fn(),"asynchronous state timed out");}
async function message(value,sender={url:"chrome-extension://gallery/options.html"}){const handler=runtime.onMessage.listeners.at(-1);return new Promise(resolve=>handler(value,sender,resolve));}
function page(id){return {post_id:String(id),site:"danbooru",page_url:"https://danbooru.donmai.us/posts/"+id,tags_complete:true,tags:{copyright:["work_"+id],artist:["artist_"+id],general:["solo"]},rating:"g",original_urls:["https://cdn.donmai.us/original/image-"+id+".png"]};}
function item(id,postId){return {id,startTime:"2026-10-13T01:00:"+id+"Z",url:page(postId).original_urls[0],finalUrl:page(postId).original_urls[0],filename:"D:/fixture/Gallery/_inbox/image-"+id+".png",mime:"image/png",state:"in_progress"};}
test("sender document identity survives same-tab original navigation",async()=>{
 const payload=page(12);const result=await message({type:"pageTags",payload},{url:payload.page_url,tab:{url:payload.original_urls[0]}});
 assert.equal(result.ok,true);assert.equal(storage["post:12"].post_id,"12");
});
test("parallel downloads capture separate post snapshots",async()=>{
 await message({type:"pageTags",payload:page(123)},{url:page(123).page_url,tab:{url:page(123).page_url}});
 const items=[item(1,12),item(2,123)];
 for(const im of items){downloads.set(im.id,im);downloadEvents.onCreated.listeners[0](im);}
 await waitFor(()=>items.every(im=>Object.keys(storage).some(k=>k.startsWith("download:"+im.id+":"))));
 for(const im of items){im.state="complete";downloads.set(im.id,im);downloadEvents.onChanged.listeners[0]({id:im.id,state:{current:"complete"}});}
 await waitFor(()=>deliveries.length>=2);
 const records=deliveries.filter(p=>[1,2].includes(p.download_id));assert.equal(records.length,2);
 assert.deepEqual(records.find(p=>p.download_id===1).tags.copyright,["work_12"]);assert.deepEqual(records.find(p=>p.download_id===2).tags.copyright,["work_123"]);
 assert.ok(records.every(p=>p.metadata_complete && p.filename.startsWith("D:/fixture/")));
});
test("canceled download never enters the outgoing queue",async()=>{
 const im=item(3,12);downloads.set(3,im);downloadEvents.onCreated.listeners[0](im);await waitFor(()=>Object.keys(storage).some(k=>k.startsWith("download:3:")));
 im.state="interrupted";downloads.set(3,im);downloadEvents.onChanged.listeners[0]({id:3,state:{current:"interrupted"}});
 await waitFor(()=>Object.entries(storage).some(([k,v])=>k.startsWith("download:3:") && v.state==="interrupted"));
 assert.equal(deliveries.some(p=>p.download_id===3),false);
});
test("offline completion survives worker restart and startup flush",async()=>{
 online=false;const im=item(4,123);downloads.set(4,im);
 downloadEvents.onCreated.listeners[0](im);
 await waitFor(()=>Object.keys(storage).some(k=>k.startsWith("download:4:")));
 im.state="complete";downloads.set(4,im);downloadEvents.onChanged.listeners[0]({id:4,state:{current:"complete"}});
 await waitFor(()=>Object.keys(storage).some(k=>k.startsWith("outbox:") && k.includes(":4:")));
 for(const eventObject of Object.values(downloadEvents))eventObject.listeners=[];
 runtime.onMessage.listeners=[];runtime.onStartup.listeners=[];runtime.onInstalled.listeners=[];chrome.alarms.onAlarm.listeners=[];
 online=true;await import("../background.js?restart=1");runtime.onStartup.listeners[0]();
 await waitFor(()=>deliveries.some(p=>p.download_id===4));assert.equal(deliveries.filter(p=>p.download_id===4).length,1);
 await waitFor(()=>!Object.keys(storage).some(k=>k.startsWith("outbox:") && k.includes(":4:")));
});
test("website cannot change settings; extension options tab can",async()=>{
 assert.equal((await message({type:"saveState",settings:{enabled:false}},{url:page(12).page_url,tab:{url:page(12).page_url}})).ok,false);
 assert.equal((await message({type:"getState"},{url:"chrome-extension://gallery/options.html",tab:{url:"chrome-extension://gallery/options.html"}})).ok,true);
});
test('X 下载带来源与账号，仍强制人工处理且不提取名字哈希',async()=>{
 const url='https://pbs.twimg.com/media/GalleryX?format=jpg&name=orig';
 const reply=await message({type:'xImageContext',payload:{image_url:url,page_url:'https://x.com/SourceUser/status/987'}},{url:'https://x.com/home'});
 assert.equal(reply.ok,true);
 const im={id:90,startTime:'2026-10-15T01:00:00Z',url,finalUrl:url,filename:'D:/chosen/0123456789abcdef0123456789abcdef.jpg',mime:'image/jpeg',referrer:'',state:'in_progress'};
 downloads.set(im.id,im);downloadEvents.onCreated.listeners.at(-1)(im);
 await waitFor(()=>Object.keys(storage).some(k=>k.startsWith('download:90:')));
 im.state='complete';downloads.set(im.id,im);downloadEvents.onChanged.listeners.at(-1)({id:90,state:{current:'complete'}});
 await waitFor(()=>deliveries.some(p=>p.download_id===90));const payload=deliveries.find(p=>p.download_id===90);
 assert.equal(payload.filename,im.filename);assert.equal(payload.site,'x');assert.deepEqual(payload.tags,{artist:['SourceUser']});
 assert.equal(payload.md5,'');assert.equal(payload.metadata_complete,false);assert.equal(payload.manual_review,true);
});
test('非 X 页面不能注入 X 图片来源',async()=>{
 const reply=await message({type:'xImageContext',payload:{image_url:'https://pbs.twimg.com/media/GalleryX.jpg',page_url:'https://x.com/SourceUser/status/987'}},{url:'https://evil.test/'});
 assert.equal(reply.ok,false);
});
test('首次升级启用 X 规则，保留其他规则与连接配置',async()=>{
 const custom={id:'custom',name:'我的站点',enabled:true,matchOn:'host',matchType:'exact',pattern:'example.com'};
 const settings=structuredClone(storage.settings);storage.rules=[custom,{...DEFAULT_RULES.find(r=>r.id==='builtin-x'),enabled:false}];delete storage.xRulesVersion;
 await runtime.onInstalled.listeners.at(-1)();
 assert.deepEqual(storage.rules.find(r=>r.id==='custom'),custom);assert.equal(storage.rules.find(r=>r.id==='builtin-x').enabled,true);
 assert.equal(storage.rules.find(r=>r.id==='builtin-x-media').enabled,true);assert.deepEqual(storage.settings,settings);assert.equal(storage.xRulesVersion,1);
});

test('所有图片接收默认关闭，开启后仅接收已完成图片并保留浏览器路径',async()=>{
 assert.equal(DEFAULT_SETTINGS.captureAllImages,false);
 storage.rules=[];storage.settings={...DEFAULT_SETTINGS,bridgeToken:'fixture'};
 const make=(id,mime='image/png')=>({id,startTime:'2026-10-18T01:00:'+id+'Z',url:'https://unlisted.test/file/'+id,filename:'D:/chosen/download-'+id+(mime==='image/png'?'.png':'.zip'),mime,referrer:'https://unlisted.test/article',state:'in_progress'});
 const skipped=make(201);downloads.set(201,skipped);
 downloadEvents.onCreated.listeners.at(-1)(skipped);
 // 消息处理经过相同异步存储，等待一轮后检查未匹配的网站没有被绑定。
 await message({type:'getState'});await new Promise(r=>setTimeout(r,20));
 assert.equal(Object.keys(storage).some(k=>k.startsWith('download:201:')),false);
 assert.equal((await message({type:'saveState',settings:{captureAllImages:true}})).ok,true);
 const im=make(202);downloads.set(202,im);downloadEvents.onCreated.listeners.at(-1)(im);
 await waitFor(()=>Object.keys(storage).some(k=>k.startsWith('download:202:')));
 assert.equal(deliveries.some(p=>p.download_id===202),false);
 im.state='complete';downloads.set(202,im);downloadEvents.onChanged.listeners.at(-1)({id:202,state:{current:'complete'}});
 await waitFor(()=>deliveries.some(p=>p.download_id===202));
 const payload=deliveries.find(p=>p.download_id===202);
 assert.equal(payload.filename,im.filename);assert.equal(payload.page_url,im.referrer);
 assert.deepEqual(payload.tags,{});assert.equal(payload.rating,'');assert.equal(payload.manual_review,true);
 const zip=make(203,'application/zip');downloads.set(203,zip);downloadEvents.onCreated.listeners.at(-1)(zip);
 zip.state='complete';downloads.set(203,zip);downloadEvents.onChanged.listeners.at(-1)({id:203,state:{current:'complete'}});
 await message({type:'getState'});await new Promise(r=>setTimeout(r,20));
 assert.equal(deliveries.some(p=>p.download_id===203),false);
 const fast={...make(204),state:'complete'};downloads.set(204,fast);
 downloadEvents.onChanged.listeners.at(-1)({id:204,state:{current:'complete'}});
 await waitFor(()=>deliveries.some(p=>p.download_id===204));
 assert.equal(deliveries.filter(p=>p.download_id===204).length,1);
 await message({type:'saveState',settings:{enabled:false}});
 const disabled={...make(205),state:'complete'};downloads.set(205,disabled);
 downloadEvents.onChanged.listeners.at(-1)({id:205,state:{current:'complete'}});
 await message({type:'getState'});await new Promise(r=>setTimeout(r,20));
 assert.equal(deliveries.some(p=>p.download_id===205),false);
});

