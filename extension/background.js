/** Gallery download coordinator: persist post snapshots, bind downloads, deliver durable records. */
import {DEFAULT_SETTINGS,DEFAULT_RULES,SEED_VERSION,BUILTIN_SITES,belongsToSite,normalizeRules,findRule,isImageDownload} from "./rules.js";
import {chooseMetadata,completeMetadata,validPost,MD5_RE} from "./metadata.js";
import {bridgePing,bridgeVerify,queueAdd,queueFlush,queueSize,migrateLegacyQueue,openDesktop} from "./bridge.js";
import {isXPage,xPost,xImageKey,isXDownload,xMetadata} from './x-metadata.js';
import {isPixivPage,pixivArtworkId,pixivImage,pixivRating,isPixivDownload,pixivSourceTags,pixivSnapshot} from './pixiv-metadata.js';
import {isPinterestPage,pinImageHash,isPinterestDownload} from './pinterest-metadata.js';
import {matchFilename} from "./filename-rules.js";

const ALARM="gallery-retry-v2";
let mutation=Promise.resolve();
let flushing=null;
let lastSyncError="";
let syncRunning=false;
let nativePort=null;
function waitForDesktop() {
  if (nativePort || typeof chrome.runtime.connectNative !== "function") return;
  const port=chrome.runtime.connectNative("gallery.bridge");
  nativePort=port;
  port.onMessage.addListener(async message => {
    if (port !== nativePort || !Number.isInteger(message.port)) return;
    nativePort=null;
    port.disconnect();
    const current=await state();
    const ping=await bridgePing(message.port,current.settings.bridgeToken);
    if (ping.ok && Number.isInteger(ping.data.pending)) {
      if (message.port !== current.settings.bridgePort) {
        await serial(async () => {
          const data=await chrome.storage.local.get("settings");
          await chrome.storage.local.set({settings:{...data.settings,bridgePort:message.port}});
        });
      }
      await flush();
    }
  });
  port.onDisconnect.addListener(() => {
    if (nativePort === port) nativePort=null;
    void chrome.runtime.lastError;
  });
}
async function updateBadge(ping,count=0) {
  const text=!ping.ok && count ? (count > 999 ? "999+" : String(count)) : "";
  await chrome.action.setBadgeBackgroundColor({color:"#b45309"});
  await chrome.action.setBadgeText({text});
  await chrome.action.setTitle({title:count ? `Gallery · ${count} 条待推送` : "Gallery"});
}
function captureSettings(saved) {
  return {...DEFAULT_SETTINGS, dragEnabled:saved?.enabled ?? true, ...saved};
}
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
      return {settings:captureSettings(current.settings),
        rules:normalizeRules(current.rules || DEFAULT_RULES),clientId:current.clientId};
    });
  }
  return {settings:captureSettings(data.settings),
    rules:normalizeRules(data.rules || DEFAULT_RULES),clientId:data.clientId};
}
function notify(title,message) {
  chrome.notifications.create({type:"basic",iconUrl:"icons/icon128.png",title,message}).catch(() => {});
}
async function captureError(error) {
  notify("Gallery 需要处理","标签暂存失败，图片仍由 Chrome 保存。请打开扩展设置检查。"+String(error?.message || ""));
}
/** 按键名前缀取值：整库 get(null) 会把下载记录和页面快照一起反序列化，容易拖住浏览器进程。 */
async function readPrefixed(prefix) {
  const keys=(await chrome.storage.local.getKeys()).filter(key=>key.startsWith(prefix));
  return keys.length ? Object.entries(await chrome.storage.local.get(keys)) : [];
}
async function snapshots() {
  return (await readPrefixed("post:")).map(([,value]) => value);
}
async function xContexts() {
  return (await readPrefixed("x-image:")).map(([,value])=>value);
}
async function rememberX(payload,sender) {
  if(!isXPage(sender.url || sender.tab?.url) || !xPost(payload?.page_url) || !xImageKey(payload?.image_url))throw new Error('无法确认 X 图片来源');
  const key='x-image:'+xImageKey(payload.image_url);
  await chrome.storage.local.set({[key]:{image_url:payload.image_url,page_url:xPost(payload.page_url).page_url,text:String(payload.text||''),seen:Date.now()}});
  const old=(await readPrefixed('x-image:')).sort((a,b)=>b[1].seen-a[1].seen).slice(512);
  if(old.length)await chrome.storage.local.remove(old.map(([k])=>k));
}
async function rememberPage(payload,sender) {
  const pageUrl=sender.url || sender.tab?.url || "";
  if (!validPost(pageUrl,payload?.post_id)) throw new Error("无法确认帖子身份");
  const page={...payload,page_url:new URL(pageUrl).origin + new URL(pageUrl).pathname};
  await chrome.storage.local.set({["post:" + page.post_id]:page});
  if (completeMetadata(page)) {
    for (const [key,record] of await readPrefixed('download:')) {
      if (!['queued','delivered'].includes(record.state) || completeMetadata(record.page)) continue;
      if (chooseMetadata({url:record.url},[page])) {
        await chrome.storage.local.set({[key]:{...record,page,state:'in_progress'}});
        await finishDownload(record.download_id,{...record,page});
      }
    }
  }
}
/** Pixiv 作品快照：页面脚本在用户点图/右键前把标题、画师、标签、分级送过来。 */
async function rememberPixiv(payload,sender) {
  if(!isPixivPage(sender.url||sender.tab?.url) || !payload?.post_id) throw new Error('无法确认 Pixiv 作品');
  const id=String(payload.post_id);
  const page={...payload,site:'pixiv',page_url:`https://www.pixiv.net/artworks/${id}`,tags:Array.isArray(payload.tags)?payload.tags:[]};
  await chrome.storage.local.set({['pixiv:'+id]:page});
}

/** Pinterest 页面快照：详情页一条，列表页一批；同时建立 图片hash → pin 的索引。 */
async function rememberPinterest(payload,sender) {
  if(!isPinterestPage(sender.url||sender.tab?.url)) throw new Error('无法确认 Pinterest 页面');
  const store={};
  for(const pin of payload?.pins||[]) {
    const id=String(pin?.id||'');
    if(!id) continue;
    store['pinterest:'+id]={...pin,id,page_url:`https://www.pinterest.com/pin/${id}/`,site:'pinterest',scraped_at:Date.now()};
    for(const url of Object.values(pin.images||{})) {
      const hash=pinImageHash(url);
      if(hash) store['pin-image:'+hash]=id;
    }
  }
  if(Object.keys(store).length) await chrome.storage.local.set(store);
}

/** 用户正在看的那张图属于哪个 pin：hash 精确匹配，匹配不到才回落到来源页地址。 */
async function rememberPinterestImage(payload,sender) {
  if(!isPinterestPage(sender.url||sender.tab?.url)) throw new Error('无法确认 Pinterest 页面');
  const hash=pinImageHash(payload?.image_url);
  if(!hash) return;
  const pinId=String(payload?.pin_id||'');
  await chrome.storage.local.set({['pin-image:'+hash]:pinId});
}

async function pixivContext(item) {
  const image=[item.url,item.finalUrl].map(pixivImage).find(Boolean);
  const id=image?.artwork_id || pixivArtworkId(item.referrer);
  if(!id) return null;
  const stored=(await chrome.storage.local.get('pixiv:'+id))['pixiv:'+id];
  let page=stored;
  if(!page?.tags?.length || !page.artist) {
    const response=await fetch(`https://www.pixiv.net/ajax/illust/${id}?lang=zh`,{
      credentials:'include',signal:AbortSignal.timeout(10000)
    });
    if(!response.ok) throw new Error(`Pixiv 标签读取失败（${response.status}），本次不再获取`);
    const result=await response.json();
    if(result.error || String(result.body?.illustId) !== id || !Array.isArray(result.body?.tags?.tags))
      throw new Error('Pixiv 未返回对应作品的标签，本次不再获取');
    page={...pixivSnapshot(result.body,`https://www.pixiv.net/artworks/${id}`),
      commentary_desc:String(stored?.commentary_desc || '')};
    await chrome.storage.local.set({['pixiv:'+id]:page});
  }
  return {page,image};
}

async function pinterestContext(item) {
  const hashes=[item.url,item.finalUrl].map(pinImageHash).filter(Boolean);
  const index={};
  if(hashes.length) Object.assign(index,await chrome.storage.local.get(hashes.map(h=>'pin-image:'+h)));
  let pinId=hashes.map(h=>index['pin-image:'+h]).find(Boolean) || '';
  if(!pinId && isPinterestPage(item.referrer)) {
    pinId=/\/pin\/(\d+)/.exec(new URL(item.referrer).pathname)?.[1] || '';
  }
  const stored=pinId ? (await chrome.storage.local.get('pinterest:'+pinId))['pinterest:'+pinId] : null;
  return stored ? {...stored,pin_id:pinId} : null;
}

function downloadKey(item) { return "download:" + item.id + ":" + item.startTime; }

function targetName(url) {
  try {
    const parsed = new URL(url);
    const last = decodeURIComponent(parsed.pathname.split("/").filter(Boolean).pop() || "");
    const clean = (last || "image").replace(/[\\/:*?"<>|]/g, "_");
    if (/\.[a-z0-9]{2,5}$/i.test(clean)) return clean.slice(0, 160);
    // X/Twitter 这类 CDN 把后缀放在查询串里（?format=jpg），路径本身没有扩展名；
    // 缺后缀的文件图库认不出来，这里补回去。
    const format = (parsed.searchParams.get("format") || "").toLowerCase();
    const ext = /^[a-z0-9]{2,5}$/.test(format) ? format : "jpg";
    return `${clean.slice(0, 150)}.${ext}`;
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
  if (!s.settings.dragEnabled) throw new Error("拖拽收图已关闭");
  const dir = safeDir(s.settings.dragTargetDir);
  const name = targetName(url);
  // 拖拽没有下载来源：用发起页补一条 X 绑定，否则后台认不出作者。
  const pageUrl = String(message?.page_url || "");
  if (isXPage(pageUrl) && xPost(pageUrl) && xImageKey(url)) {
    try { await rememberX({ image_url: url, page_url: pageUrl, text: String(message?.text || "") }, { url: pageUrl }); }
    catch { /* 不是 X 图就跳过，不影响下载 */ }
  }
  const id = await chrome.downloads.download({
    url, filename: dir ? dir + "/" + name : name, conflictAction: "uniquify", saveAs: false
  });
  return { ok: true, id };
}
async function bindDownload(item) {
  const s=await state();
  // 拖拽是扩展发起下载的唯一入口。浏览器保留此标记，后台重启后仍可识别。
  const dragged=item.byExtensionId === chrome.runtime.id;
  if ((!dragged && !s.settings.enabled) || !isImageDownload(item)) return null;
  const x=isXDownload(item);
  const page=x ? xMetadata(item,await xContexts()) : chooseMetadata(item,await snapshots());
  const enriched={...item,referrer:page?.page_url || item.referrer || ""};
  const rule=findRule(s.settings.captureAllImages
    ? s.rules.map(rule=>({...rule,enabled:true})) : s.rules,enriched);
  if (!rule && !s.settings.captureAllImages && !dragged) return null;
  const key=downloadKey(item);
  return serial(async () => {
    const previous=(await chrome.storage.local.get(key))[key];
    if (previous) return previous;
    const record={
      key,ingest_id:s.clientId + ":" + item.id + ":" + item.startTime,
      client_id:s.clientId,download_id:item.id,start_time:item.startTime,
      state:"in_progress",rule_id:rule?.rule.id || (dragged ? "drag" : "all-images"),
      filename_rules:rule?.rule.filenameRules || [],
      site:x ? 'x' : '',page,url:item.url,referrer:item.referrer || ""
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
  const prefix="download:"+id+":";
  const keys=(await chrome.storage.local.getKeys()).filter(key => key.startsWith(prefix));
  if (!keys.length) return null;
  const found=await chrome.storage.local.get(keys);
  return keys.map(key => found[key]).find(value => value?.download_id === id) || null;
}
async function finishDownload(id, knownRecord) {
  let record=knownRecord || await findDownload(id);
  const [item]=await chrome.downloads.search({id});
  if (!item) return;
  // 极快的下载可能先完成，再写入 onCreated 的绑定记录；完成事件也可按同一规则绑定。
  if (!record && item.state === "complete") record=await bindDownload(item);
  if (!record || ["queued","delivered","metadata_failed"].includes(record.state)) return;
  if (item.state === "interrupted") {
    await chrome.storage.local.set({[record.key]:{...record,state:"interrupted"}});
    return;
  }
  if (item.state !== "complete" || !item.filename) return;
  const x=record.site==='x' || isXDownload(item);
  const itemRef={...item,referrer:item.referrer || record.referrer};
  let pixiv=null;
  if (!x && isPixivDownload(itemRef)) {
    try {
      pixiv=await pixivContext(itemRef);
    } catch (error) {
      await chrome.storage.local.set({[record.key]:{...record,state:"metadata_failed"}});
      throw error;
    }
  }
  const pinterest=!x && !pixiv && isPinterestDownload(itemRef) ? await pinterestContext(itemRef) : null;
  const page=x ? xMetadata({...item,referrer:item.referrer || record.referrer},[...await xContexts(),...(record.page?.post_id ? [{page_url:record.page.page_url,image_url:record.url}] : [])])
    : pixiv ? pixiv.page
    : pinterest || record.page || chooseMetadata(item,await snapshots());
  const filename=item.filename;
  const name=filename.replace(/\\/g,"/").split("/").pop();
  let tags=page?.tags || {}, rating=page?.rating || "", source=page?.source || "";
  const sourceTags=pixiv ? pixivSourceTags(page.tags,(await state()).settings.pixivIgnoredTags) : [];
  if(pixiv) {
    // 分类由桌面端按当前标签与资料库决定；旧桌面端仍可保留原文到自定义。
    tags={artist:page.artist?[page.artist]:[],custom:sourceTags.map(t=>t.name)};
    rating=pixivRating(page.x_restrict);
    source=page.original_url || "";
  } else if(pinterest) {
    // 作者是保存这条 pin 的账号，按已确认的口径归到画师；标题和描述不写标签。
    tags={artist:pinterest.artist?[pinterest.artist]:[]};
    rating="";
    source=pinterest.link || "";
  }
  const payload={
    version:2,ingest_id:record.ingest_id,client_id:record.client_id,download_id:id,
    download_state:"complete",download_key:record.key,
    filename,inbox_name:name,image_url:item.finalUrl || item.url,
    page_url:page?.page_url || record.referrer || "",post_id:page?.post_id || "",site:page?.site || "",
    rule_id:record.rule_id || "",
    tags,rating,source,
    ...(pixiv ? {source_tags:sourceTags} : {}),
    tag_string:pixiv ? sourceTags.map(t=>t.name).join(" ") : page?.tag_string || "",
    commentary_title:String(page?.commentary_title || ""),
    commentary_desc:String(page?.commentary_desc || ""),
    filename_tags:matchFilename(record.filename_rules,name),
    metadata_complete:x || pixiv || pinterest ? false : completeMetadata(page),
    manual_review:x || !page,
    md5:x || !page ? '' : MD5_RE.exec(name)?.[0]?.toLowerCase() || "",sent_at:Date.now()
  };
  await queueAdd(payload);
  await chrome.storage.local.set({[record.key]:{...record,page,state:"queued"}});
  await flush();
}
chrome.downloads.onChanged.addListener(delta => {
  if (delta.state && ["complete","interrupted"].includes(delta.state.current)) finishDownload(delta.id).catch(captureError);
});
/** 一次读齐下载记录，逐条结算时不再各自全量重读；卡住的记录 10 分钟内不重复重试。 */
const RETRY_BACKOFF=10*60*1000;
async function reconcile({pruneHistory=false}={}) {
  const records=await readPrefixed("download:");
  const stale=[];
  const delivered=pruneHistory ? records.filter(([,record]) => record.state === "delivered") : [];
  if (delivered.length) {
    const history=await chrome.downloads.search({limit:0});
    const existing=new Set(history.map(downloadKey));
    for (const [key] of delivered) {
      if (!existing.has(key)) stale.push(key);
    }
  }
  for (const [key,record] of records) {
    if (record.state === "delivered") continue;
    if (["queued","interrupted","metadata_failed"].includes(record.state)) continue;
    if (Date.now() - (record.last_attempt || 0) < RETRY_BACKOFF) continue;
    await chrome.storage.local.set({[key]:{...record,last_attempt:Date.now()}});
    await finishDownload(record.download_id,record).catch(captureError);
  }
  if (stale.length) await chrome.storage.local.remove(stale);
  if (pruneHistory) await housekeeping();
}
/** 保留策略：只清已送达的旧记录和过期页面快照，每天最多跑一次。 */
const KEEP_DELIVERED=800,POST_TTL=14*24*60*60*1000;
async function housekeeping() {
  const marked=(await chrome.storage.local.get("lastPrune")).lastPrune || 0;
  if (Date.now() - marked < 24*60*60*1000) return;
  await chrome.storage.local.set({lastPrune:Date.now()});
  const deliveries=(await readPrefixed("download:"))
    .filter(([,record]) => record.state === "delivered")
    .sort((a,b) => String(b[1].start_time || "").localeCompare(String(a[1].start_time || "")));
  const excess=deliveries.slice(KEEP_DELIVERED).map(([key]) => key);
  if (excess.length) await chrome.storage.local.remove(excess);
  const expired=(await readPrefixed("post:"))
    .filter(([,page]) => Date.now() - (page?.scraped_at || 0) > POST_TTL).map(([key]) => key);
  if (expired.length) await chrome.storage.local.remove(expired);
}
chrome.downloads.onErased.addListener(id => {
  (async () => {
    const keys=(await chrome.storage.local.getKeys()).filter(key=>key.startsWith("download:"+id+":"));
    if (!keys.length) return;
    const found=await chrome.storage.local.get(keys);
    const gone=keys.filter(key => found[key]?.state === "delivered");
    if (gone.length) await chrome.storage.local.remove(gone);
  })().catch(captureError);
});
async function flush() {
  if (flushing) return flushing;
  flushing=(async () => {
    if (!await queueSize()) {
      if (nativePort) {
        const port=nativePort;
        nativePort=null;
        port.disconnect();
      }
      await updateBadge({ok:true},0);
      return;
    }
    const {settings}=await state();
    const ping=await bridgePing(settings.bridgePort,settings.bridgeToken);
    syncRunning=true;
    try {
      if (ping.ok) {
        const result=await queueFlush(settings.bridgePort,settings.bridgeToken);
        lastSyncError=result.stopped;
      } else lastSyncError=ping.error;
    } finally {
      syncRunning=false;
      const left=await queueSize();
      await updateBadge(ping,left);
      if (!ping.ok && left) waitForDesktop();
      else if (!left && nativePort) {
        const port=nativePort;
        nativePort=null;
        port.disconnect();
      }
    }
  })();
  try { return await flushing; } finally { flushing=null; }
}
async function initialize() {
  await serial(async () => {
    const data=await chrome.storage.local.get(["settings","rules","seedVersion","sourceJob"]);
    if (data.settings?.dragEnabled === undefined)
      await chrome.storage.local.set({settings:captureSettings(data.settings)});
    if (!Array.isArray(data.rules) || (data.seedVersion || 0) < SEED_VERSION) {
      const rules=Array.isArray(data.rules) ? [...data.rules] : [];
      for (const rule of DEFAULT_RULES) {
        if (rules.some(current=>current.id === rule.id)) continue;
        const [siteId]=BUILTIN_SITES.find(([id])=>belongsToSite(rule,id));
        const current=rules.find(candidate=>belongsToSite(candidate,siteId));
        rules.push({...rule,enabled:current?.enabled ?? (!Array.isArray(data.rules) && rule.enabled)});
      }
      await chrome.storage.local.set({rules,seedVersion:SEED_VERSION});
    }
    if (data.sourceJob) await chrome.storage.local.remove("sourceJob");
  });
  await migrateLegacyQueue();
  await scheduleRetry();
}
async function scheduleRetry() {
  await chrome.alarms.create(ALARM,{periodInMinutes:1});
}
chrome.runtime.onInstalled.addListener(() => initialize().catch(captureError));
chrome.runtime.onStartup.addListener(() => scheduleRetry().catch(captureError));
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
      if(message?.type==='pixivPage') {await rememberPixiv(message.payload,sender);sendResponse({ok:true});return;}
      if(message?.type==='pinterestPins') {await rememberPinterest(message.payload,sender);sendResponse({ok:true});return;}
      if(message?.type==='pinterestImage') {await rememberPinterestImage(message.payload,sender);sendResponse({ok:true});return;}
      if (message?.type === "grabImage") {
        try { sendResponse(await grabImage(message)); }
        catch (error) { sendResponse({ ok: false, error: String(error.message || error) }); }
        return;
      }
      if (sender.url && !sender.url.startsWith(chrome.runtime.getURL(""))) throw new Error("页面不能修改 Gallery 配置");
      const s=await state();
      switch(message?.type) {
        case "getSettings":
          sendResponse({ok:true,...s});
          break;
        case "getState": {
          const ping=await bridgePing(s.settings.bridgePort,s.settings.bridgeToken);
          const paired=ping.ok && Number.isInteger(ping.data.pending);
          const queued=await queueSize();
          await updateBadge(ping,queued);
          if (ping.ok && paired && queued) void flush();
          sendResponse({ok:true,...s,bridge:{running:ping.ok,paired,queued,error:lastSyncError,syncing:syncRunning}});
          break;
        }
        case "saveState": {
          const rules=await serial(async () => {
            const current=await chrome.storage.local.get(["settings","rules"]);
            const settings={...captureSettings(current.settings),...message.settings};
            if (message.settings && "pixivIgnoredTags" in message.settings) {
              if (!Array.isArray(settings.pixivIgnoredTags) || settings.pixivIgnoredTags.some(tag=>typeof tag !== "string"))
                throw new Error("忽略标签必须是文本列表");
              settings.pixivIgnoredTags=[...new Set(settings.pixivIgnoredTags.map(tag=>tag.trim()).filter(Boolean))];
            }
            if (message.settings && "theme" in message.settings && !["system","light","dark"].includes(settings.theme)) throw new Error("主题无效");
            if (message.settings && "bridgePort" in message.settings &&
              (!Number.isInteger(Number(settings.bridgePort)) || settings.bridgePort < 1024 || settings.bridgePort > 65535)) throw new Error("端口应为 1024 到 65535");
            if (message.settings && !message.rules && !message.customRules && !message.addRule && !message.ruleToggle && !message.site) {
              await chrome.storage.local.set({settings});
              return;
            }
            let rules=normalizeRules(message.rules ?? current.rules ?? DEFAULT_RULES);
            if (message.customRules) {
              const edited=new Map(normalizeRules(message.customRules).map(rule=>[rule.id,rule]));
              rules=rules.flatMap(rule => {
                if (BUILTIN_SITES.some(([id])=>belongsToSite(rule,id))) return [rule];
                const replacement=edited.get(rule.id);
                return replacement ? [{...replacement,enabled:rule.enabled}] : [];
              });
            }
            if (message.addRule) {
              const [rule]=normalizeRules([message.addRule]);
              if (rules.some(current=>current.matchOn === rule.matchOn && current.pattern === rule.pattern))
                throw new Error("该网站已添加");
              rules.push(rule);
            }
            if (message.ruleToggle) {
              const rule=rules.find(rule=>rule.id === message.ruleToggle.id);
              if (!rule) throw new Error("网站规则已删除");
              rule.enabled=message.ruleToggle.enabled;
            }
            if (message.site) {
              if (!BUILTIN_SITES.some(([id])=>id === message.site.id)) throw new Error("未知站点");
              for (const rule of rules) {
                if (belongsToSite(rule,message.site.id)) rule.enabled=message.site.enabled;
              }
            }
            for (const rule of rules) {
              if (!rule.pattern.trim()) throw new Error("网站匹配内容不能为空");
              if (rule.matchType === "regex") new RegExp(rule.pattern,rule.caseSensitive ? "" : "i");
            }
            await chrome.storage.local.set({settings,rules});
            return rules;
          });
          sendResponse({ok:true,rules});
          if (message.settings && ("bridgeToken" in message.settings || "bridgePort" in message.settings))
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
          await reconcile({pruneHistory:true}); await flush(); sendResponse({ok:true});
          break;
        default: throw new Error("未知操作");
      }
    } catch(error) { sendResponse({ok:false,error:String(error.message || error)}); }
  })();
  return true;
});

