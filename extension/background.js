/** Gallery download coordinator: persist post snapshots, bind downloads, deliver durable records. */
import {DEFAULT_SETTINGS,DEFAULT_RULES,normalizeRules,findRule,isImageDownload,ruleFromUrl} from "./rules.js";
import {chooseMetadata,completeMetadata,validPost,MD5_RE} from "./metadata.js";
import {bridgePing,bridgeVerify,queueAdd,queueFlush,queueSize,migrateLegacyQueue,openDesktop} from "./bridge.js";
import {isXPage,xPost,xImageKey,isXDownload,xMetadata} from './x-metadata.js';

const ALARM="gallery-retry-v2";
let mutation=Promise.resolve();
let flushing=null;
function serial(fn) {
  const result=mutation.then(fn,fn);
  mutation=result.catch(() => {});
  return result;
}
async function state() {
  const data=await chrome.storage.local.get(["settings","rules","clientId"]);
  if (!data.clientId) {
    return serial(async () => {
      const current=await chrome.storage.local.get(["settings","rules","clientId"]);
      if (!current.clientId) {
        current.clientId=crypto.randomUUID();
        await chrome.storage.local.set({clientId:current.clientId});
      }
      return {settings:{...DEFAULT_SETTINGS,...current.settings},
        rules:normalizeRules(current.rules || DEFAULT_RULES),clientId:current.clientId};
    });
  }
  return {settings:{...DEFAULT_SETTINGS,...data.settings},
    rules:normalizeRules(data.rules || DEFAULT_RULES),clientId:data.clientId};
}
function notify(title,message) {
  chrome.notifications.create({type:"basic",iconUrl:"icons/icon128.png",title,message}).catch(() => {});
}
async function captureError(error) {
  await chrome.action.setBadgeText({text:"!"});
  await chrome.action.setBadgeBackgroundColor({color:"#b45309"});
  notify("Gallery 需要处理","标签暂存失败，图片仍由 Chrome 保存。请打开扩展设置检查。"+String(error?.message || ""));
}
async function snapshots() {
  const data=await chrome.storage.local.get(null);
  return Object.entries(data).filter(([key]) => key.startsWith("post:")).map(([,value]) => value);
}
async function xContexts() {
  const data=await chrome.storage.local.get(null);
  return Object.entries(data).filter(([key])=>key.startsWith('x-image:')).map(([,value])=>value);
}
async function rememberX(payload,sender) {
  if(!isXPage(sender.url || sender.tab?.url) || !xPost(payload?.page_url) || !xImageKey(payload?.image_url))throw new Error('无法确认 X 图片来源');
  const key='x-image:'+xImageKey(payload.image_url);
  await chrome.storage.local.set({[key]:{image_url:payload.image_url,page_url:xPost(payload.page_url).page_url,seen:Date.now()}});
  const data=await chrome.storage.local.get(null),old=Object.entries(data).filter(([k])=>k.startsWith('x-image:')).sort((a,b)=>b[1].seen-a[1].seen).slice(512);
  if(old.length)await chrome.storage.local.remove(old.map(([k])=>k));
}
async function rememberPage(payload,sender) {
  const pageUrl=sender.url || sender.tab?.url || "";
  if (!validPost(pageUrl,payload?.post_id)) throw new Error("无法确认帖子身份");
  const page={...payload,page_url:new URL(pageUrl).origin + new URL(pageUrl).pathname};
  await chrome.storage.local.set({["post:" + page.post_id]:page});
  if (completeMetadata(page)) {
    const data=await chrome.storage.local.get(null);
    for (const [key,record] of Object.entries(data)) {
      if (!key.startsWith('download:') || !['queued','delivered'].includes(record.state) || completeMetadata(record.page)) continue;
      if (chooseMetadata({url:record.url},[page])) {
        await chrome.storage.local.set({[key]:{...record,page,state:'in_progress'}});
        await finishDownload(record.download_id);
      }
    }
  }
}
function downloadKey(item) { return "download:" + item.id + ":" + item.startTime; }
// 拖拽落点发起的下载：URL 在 15 秒内视为“用户主动拖进来”，不受接收网站规则限制。
const dragUrls = new Map();
function markDrag(url) {
  if (!url) return;
  dragUrls.set(url, Date.now());
  setTimeout(() => dragUrls.delete(url), 15000);
}
function isDragDownload(item) {
  return dragUrls.has(item.url) || (!!item.finalUrl && dragUrls.has(item.finalUrl));
}
function targetName(url) {
  try {
    const last = decodeURIComponent(new URL(url).pathname.split("/").filter(Boolean).pop() || "");
    return (last || "image").replace(/[\\/:*?"<>|]/g, "_").slice(0, 160);
  } catch { return "image"; }
}
/** 只接受浏览器下载目录下的相对子目录；绝对路径和 .. 一律丢弃，避免写到别的盘。 */
function safeDir(value) {
  const dir = String(value || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "").trim();
  if (!dir) return "";
  if (dir.split("/").some(part => part === ".." || part === "" || /^[a-zA-Z]:/.test(part))) return "";
  return dir;
}
async function grabImage(message) {
  const url = String(message?.url || "");
  if (!/^https?:/i.test(url)) throw new Error("只支持 http/https 图片地址");
  const s = await state();
  if (!s.settings.enabled) throw new Error("图片接收已停用");
  const dir = safeDir(s.settings.dragTargetDir);
  const name = targetName(url);
  markDrag(url);
  const id = await chrome.downloads.download({
    url, filename: dir ? dir + "/" + name : name, conflictAction: "uniquify", saveAs: false
  });
  return { ok: true, id };
}
async function bindDownload(item) {
  const s=await state();
  if (!s.settings.enabled || !isImageDownload(item)) return null;
  const x=isXDownload(item);
  const page=x ? xMetadata(item,await xContexts()) : chooseMetadata(item,await snapshots());
  const enriched={...item,referrer:page?.page_url || item.referrer || ""};
  const rule=findRule(s.rules,enriched);
  const dragged=isDragDownload(item);
  if (!rule && !s.settings.captureAllImages && !dragged) return null;
  const key=downloadKey(item);
  return serial(async () => {
    const previous=(await chrome.storage.local.get(key))[key];
    if (previous) return previous;
    const record={
      key,ingest_id:s.clientId + ":" + item.id + ":" + item.startTime,
      client_id:s.clientId,download_id:item.id,start_time:item.startTime,
      state:"in_progress",rule_id:rule?.rule.id || (dragged && !s.settings.captureAllImages ? "drag" : "all-images"),site:x ? 'x' : '',page,url:item.url,referrer:item.referrer || ""
    };
    await chrome.storage.local.set({[key]:record});
    return record;
  });
}
chrome.downloads.onCreated.addListener(item => {
  bindDownload(item).catch(captureError);
});
// 2026-10-14 起不再劫持保存路径：下载位置由浏览器原生「另存为」决定，
// 扩展只负责在下载完成后，按实际落盘的路径把记录交给桌面端接管归档。
async function findDownload(id) {
  const data=await chrome.storage.local.get(null);
  return Object.entries(data).find(([key,value]) => key.startsWith("download:") && value.download_id === id)?.[1] || null;
}
async function finishDownload(id) {
  let record=await findDownload(id);
  const [item]=await chrome.downloads.search({id});
  if (!item) return;
  // 极快的下载可能先完成，再写入 onCreated 的绑定记录；完成事件也可按同一规则绑定。
  if (!record && item.state === "complete") record=await bindDownload(item);
  if (!record || ["queued","delivered"].includes(record.state)) return;
  if (item.state === "interrupted") {
    await chrome.storage.local.set({[record.key]:{...record,state:"interrupted"}});
    return;
  }
  if (item.state !== "complete" || !item.filename) return;
  const x=record.site==='x' || isXDownload(item);
  const page=x ? xMetadata({...item,referrer:item.referrer || record.referrer},[...await xContexts(),...(record.page?.post_id ? [{page_url:record.page.page_url,image_url:record.url}] : [])]) : record.page || chooseMetadata(item,await snapshots());
  const filename=item.filename;
  const name=filename.replace(/\\/g,"/").split("/").pop();
  const payload={
    version:2,ingest_id:record.ingest_id,client_id:record.client_id,download_id:id,
    download_state:"complete",download_key:record.key,
    filename,inbox_name:name,image_url:item.finalUrl || item.url,
    page_url:page?.page_url || record.referrer || "",post_id:page?.post_id || "",site:page?.site || "",
    rule_id:record.rule_id || "",
    tags:page?.tags || {},rating:page?.rating || "",source:page?.source || "",
    tag_string:page?.tag_string || "",metadata_complete:x ? false : completeMetadata(page),manual_review:x || !page,
    md5:x || !page ? '' : MD5_RE.exec(name)?.[0]?.toLowerCase() || "",sent_at:Date.now()
  };
  await queueAdd(payload);
  await chrome.storage.local.set({[record.key]:{...record,page,state:"queued"}});
  await flush();
}
chrome.downloads.onChanged.addListener(delta => {
  if (delta.state && ["complete","interrupted"].includes(delta.state.current)) finishDownload(delta.id).catch(captureError);
});
async function reconcile() {
  const data=await chrome.storage.local.get(null);
  for (const [key,record] of Object.entries(data)) {
    if (!key.startsWith("download:") || ['queued','delivered','interrupted'].includes(record.state)) continue;
    await finishDownload(record.download_id);
  }
}
async function flush() {
  if (flushing) return flushing;
  flushing=(async () => {
    const {settings}=await state();
    const queued=await queueSize();
    const result=await queueFlush(settings.bridgePort,settings.bridgeToken);
    const ping=await bridgePing(settings.bridgePort);
    if (ping.ok && settings.bridgeToken) await bridgeVerify(settings.bridgePort,settings.bridgeToken);
    const total=result.left + (ping.ok ? Number(ping.data.pending || 0) : 0);
    await chrome.action.setBadgeText({text:total ? String(Math.min(total,99)) + (total > 99 ? "+" : "") : ""});
    await chrome.action.setBadgeBackgroundColor({color:ping.ok ? "#525252" : "#b45309"});
    if (queued && result.left && (!ping.ok || result.stopped)) {
      const data=await chrome.storage.local.get("lastOfflineNotice");
      if (Date.now() - (data.lastOfflineNotice || 0) > 60000) {
        await chrome.storage.local.set({lastOfflineNotice:Date.now()});
        notify("请打开 Gallery 桌面端","图片已保存，"+result.left+" 条标签暂存，连接恢复后会自动继续。");
      }
    }
  })();
  try { return await flushing; } finally { flushing=null; }
}
async function initialize() {
  const data=await chrome.storage.local.get(["settings","rules","xRulesVersion"]);
  if (!data.settings) await chrome.storage.local.set({settings:{...DEFAULT_SETTINGS,theme:"system"}});
  if (!Array.isArray(data.rules)) {
    const defaults=DEFAULT_RULES.map(rule => ({...rule,enabled:rule.id.startsWith("builtin-danbooru") || rule.id.startsWith('builtin-x')}));
    await chrome.storage.local.set({rules:defaults});
  }
  if(!data.xRulesVersion) {
    const current=(await chrome.storage.local.get('rules')).rules;
    const rules=current.map(rule=>rule.id==='builtin-x' ? {...rule,enabled:true,folder:''} : rule);
    for(const rule of DEFAULT_RULES.filter(rule=>rule.id.startsWith('builtin-x')))if(!rules.some(r=>r.id===rule.id))rules.push({...rule});
    await chrome.storage.local.set({rules,xRulesVersion:1});
  }
  await state();
  await migrateLegacyQueue();
  chrome.alarms.create(ALARM,{periodInMinutes:1});
  await reconcile();
  await flush();
}
chrome.runtime.onInstalled.addListener(() => initialize().catch(captureError));
chrome.runtime.onStartup.addListener(() => initialize().catch(captureError));
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === ALARM) (async () => { await reconcile(); await flush(); })().catch(captureError);
});
chrome.runtime.onMessage.addListener((message,sender,sendResponse) => {
  (async () => {
    try {
      if (message?.type === "pageTags") {
        await rememberPage(message.payload,sender);
        sendResponse({ok:true});
        return;
      }
      if(message?.type==='xImageContext') {await rememberX(message.payload,sender);sendResponse({ok:true});return;}
      if (message?.type === "grabImage") {
        try { sendResponse(await grabImage(message)); }
        catch (error) { sendResponse({ ok: false, error: String(error.message || error) }); }
        return;
      }
      if (sender.url && !sender.url.startsWith(chrome.runtime.getURL(""))) throw new Error("页面不能修改 Gallery 配置");
      const s=await state();
      switch(message?.type) {
        case "getState": {
          const ping=await bridgePing(s.settings.bridgePort);
          const pair=ping.ok ? await bridgeVerify(s.settings.bridgePort,s.settings.bridgeToken) : {ok:false,error:ping.error};
          sendResponse({ok:true,...s,bridge:{running:ping.ok,paired:pair.ok,error:pair.error,queued:await queueSize(),pending:ping.data?.pending || 0,protocol:ping.data?.protocol || 0}});
          break;
        }
        case "saveState": {
          const settings={...s.settings,...message.settings};
          if (!["system","light","dark"].includes(settings.theme || "system")) throw new Error("主题无效");
          if (!Number.isInteger(Number(settings.bridgePort)) || settings.bridgePort < 1024 || settings.bridgePort > 65535) throw new Error("端口应为 1024 到 65535");
          const rules=normalizeRules(message.rules || s.rules);
          for (const rule of rules) {
            if (!rule.pattern.trim()) throw new Error("网站匹配内容不能为空");
            if (rule.matchType === "regex") new RegExp(rule.pattern,rule.caseSensitive ? "" : "i");
          }
          await chrome.storage.local.set({settings,rules});
          sendResponse({ok:true});
          await flush();
          break;
        }
        case "testBridge": {
          sendResponse(await bridgeVerify(message.port || s.settings.bridgePort,message.token ?? s.settings.bridgeToken));
          break;
        }
        case "openDesktop":
          sendResponse(await openDesktop(s.settings.bridgePort,s.settings.bridgeToken,message.view,message.path));
          break;
        case "retry":
          await reconcile(); await flush(); sendResponse({ok:true});
          break;
        case "addSite": {
          const rule=ruleFromUrl(message.url,message.name);
          if (!rule) throw new Error("请输入有效的网站地址");
          await chrome.storage.local.set({rules:[...s.rules,rule]});
          sendResponse({ok:true});
          break;
        }
        case "openOptions":
          await chrome.runtime.openOptionsPage(); sendResponse({ok:true}); break;
        default: throw new Error("未知操作");
      }
    } catch(error) { sendResponse({ok:false,error:String(error.message || error)}); }
  })();
  return true;
});

