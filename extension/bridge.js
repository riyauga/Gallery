/** Persistent per-record outbox. Only an acknowledged durable record is removed. */
import {DEFAULT_SETTINGS} from "./rules.js";
const PREFIX = "outbox:";
let serial = Promise.resolve();
let flushing = null;

function locked(fn) {
  const result = serial.then(fn,fn);
  serial = result.catch(() => {});
  return result;
}
function base(port) { return "http://127.0.0.1:" + (Number(port) || DEFAULT_SETTINGS.bridgePort); }
async function request(port,path,options,timeout=5000) {
  const controller = new AbortController();
  const timer=setTimeout(() => controller.abort(),timeout);
  try {
    const response=await fetch(base(port)+path,{...options,signal:controller.signal});
    const data=await response.json();
    if (!response.ok || !data.ok) return {ok:false,error:data.error || "HTTP " + response.status};
    return {ok:true,data};
  } catch(error) { return {ok:false,error:error.name === "AbortError" ? "连接超时" : "桌面端未连接"}; }
  finally { clearTimeout(timer); }
}
export async function bridgePing(port,timeout=900) {
  return request(port,"/ping",{method:"GET"},timeout);
}
/** 库里已有的作品/角色名单，用于 Pixiv 标签过滤。 */
export async function bridgeTags(port,timeout=4000) {
  return request(port,"/tags",{method:"GET"},timeout);
}
export async function bridgeVerify(port,token) {
  return request(port,'/ingest',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Token':String(token || '')},body:JSON.stringify({probe:true,filename:'__gallery_probe__',client:'gallery-extension'})});
}
export async function bridgeSend(port,token,payload,timeout=8000) {
  const result=await request(port,"/ingest",{
    method:"POST",headers:{"Content-Type":"application/json","X-Gallery-Token":String(token || "")},
    body:JSON.stringify(payload)
  },timeout);
  if (result.ok && (!result.data.persisted || result.data.ingest_id !== payload.ingest_id)) {
    return {ok:false,error:"请更新 Gallery 桌面端，当前版本未确认持久保存"};
  }
  return result.ok ? {ok:true,queued:result.data.queued} : result;
}
export async function openDesktop(port,token,view="library",path="") {
  return request(port,"/ui/open",{method:"POST",headers:{"Content-Type":"application/json","X-Gallery-Token":String(token || "")},body:JSON.stringify({view,path:String(path || "")})});
}
export async function queueAdd(record) {
  return locked(async () => {
    if (!record.ingest_id) record={...record,ingest_id:crypto.randomUUID(),version:1,metadata_complete:false};
    await chrome.storage.local.set({[PREFIX + record.ingest_id + (record.metadata_complete ? ':complete' : ':partial')]:record});
    return queueSize();
  });
}
export async function queueAll() {
  const data=await chrome.storage.local.get(null);
  return Object.entries(data).filter(([key]) => key.startsWith(PREFIX)).map(([key,record]) => ({...record,_outbox_key:key}))
    .sort((a,b) => (a.sent_at || 0) - (b.sent_at || 0));
}
export async function queueSize() { return (await queueAll()).length; }
export async function queueClear() {
  return locked(async () => {
    const data=await chrome.storage.local.get(null);
    await chrome.storage.local.remove(Object.keys(data).filter(key => key.startsWith(PREFIX)));
  });
}
export async function migrateLegacyQueue() {
  return locked(async () => {
    const data=await chrome.storage.local.get("bridgeQueue");
    const old=Array.isArray(data.bridgeQueue) ? data.bridgeQueue : [];
    for (let i=0;i<old.length;i++) {
      const record={...old[i],ingest_id:old[i].ingest_id || "legacy:" + i + ":" + (old[i].sent_at || 0),version:1,metadata_complete:false};
      await chrome.storage.local.set({[PREFIX + record.ingest_id]:record});
    }
    if (old.length) await chrome.storage.local.remove("bridgeQueue");
  });
}
export async function queueFlush(port,token) {
  if (flushing) return flushing;
  flushing=(async () => {
    const records=await queueAll();
    let sent=0,stopped="";
    for (const record of records) {
      const result=await bridgeSend(port,token,record);
      if (!result.ok) { stopped=result.error; break; }
      await locked(() => chrome.storage.local.remove(record._outbox_key));
      if (record.download_key) {
        if (record.metadata_complete) await chrome.storage.local.remove(record.download_key);
        else {
          const current=(await chrome.storage.local.get(record.download_key))[record.download_key];
          if (current && !current.page?.tags_complete) await chrome.storage.local.set({[record.download_key]:{...current,state:'delivered'}});
        }
      }
      sent++;
    }
    return {sent,left:await queueSize(),stopped};
  })();
  try { return await flushing; } finally { flushing=null; }
}

