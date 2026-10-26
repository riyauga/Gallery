/** Gallery download coordinator: persist post snapshots, bind downloads, deliver durable records. */
import {DEFAULT_SETTINGS,DEFAULT_RULES,SEED_VERSION,normalizeRules,findRule,isImageDownload,ruleFromUrl} from "./rules.js";
import {chooseMetadata,completeMetadata,validPost,MD5_RE} from "./metadata.js";
import {bridgePing,bridgeVerify,bridgeTags,queueAdd,queueFlush,queueSize,migrateLegacyQueue,openDesktop,bridgeSourceQueue,bridgeSourceApply} from "./bridge.js";
import {isXPage,xPost,xImageKey,isXDownload,xMetadata} from './x-metadata.js';
import {isPixivPage,pixivArtworkId,pixivImage,pixivRating,isPixivDownload,matchPixivTags} from './pixiv-metadata.js';
import {isPinterestPage,pinImageHash,isPinterestDownload} from './pinterest-metadata.js';

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
  await chrome.storage.local.set({[key]:{image_url:payload.image_url,page_url:xPost(payload.page_url).page_url,text:String(payload.text||''),seen:Date.now()}});
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
  const page=stored || {site:'pixiv',page_url:`https://www.pixiv.net/artworks/${id}`,post_id:id,artist:'',tags:[],x_restrict:0};
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

/** 库里已有的作品/角色名单，缓存 24 小时；拉不到就返回空表（标签一律丢弃）。 */
const TAG_INDEX_KEY='tagIndex';
async function tagIndex(settings) {
  const cached=(await chrome.storage.local.get(TAG_INDEX_KEY))[TAG_INDEX_KEY];
  if(cached && Date.now()-(cached.at||0) < 86400000) return cached.index;
  const result=await bridgeTags(settings.bridgePort);
  if(result.ok && result.data?.works) {
    const index={works:result.data.works,characters:result.data.characters||[],aliases:result.data.aliases||{}};
    await chrome.storage.local.set({[TAG_INDEX_KEY]:{at:Date.now(),index}});
    return index;
  }
  return cached?.index || {works:[],characters:[],aliases:{}};
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
  if (!s.settings.enabled) throw new Error("图片接收已停用");
  const dir = safeDir(s.settings.dragTargetDir);
  const name = targetName(url);
  markDrag(url);
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
  const itemRef={...item,referrer:item.referrer || record.referrer};
  const pixiv=!x && isPixivDownload(itemRef) ? await pixivContext(itemRef) : null;
  const pinterest=!x && !pixiv && isPinterestDownload(itemRef) ? await pinterestContext(itemRef) : null;
  const page=x ? xMetadata({...item,referrer:item.referrer || record.referrer},[...await xContexts(),...(record.page?.post_id ? [{page_url:record.page.page_url,image_url:record.url}] : [])])
    : pixiv ? pixiv.page
    : pinterest || record.page || chooseMetadata(item,await snapshots());
  const filename=item.filename;
  const name=filename.replace(/\\/g,"/").split("/").pop();
  let tags=page?.tags || {}, rating=page?.rating || "", source=page?.source || "";
  if(pixiv) {
    // 只保留库里已有的作品与角色；画师直接用接口给的名字，不靠标签猜。
    const index=await tagIndex((await state()).settings);
    const matched=matchPixivTags(page.tags,index);
    tags={artist:page.artist?[page.artist]:[],...matched};
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
    tag_string:pixiv ? (page.tags||[]).join(" ") : page?.tag_string || "",
    commentary_title:String(page?.commentary_title || ""),
    commentary_desc:String(page?.commentary_desc || ""),
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
  const data=await chrome.storage.local.get(["settings","rules","xRulesVersion","seedVersion"]);
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
  // 补齐缺失的内置规则（不覆盖用户改过的同名规则），让已安装的扩展也能拿到新增站点。
  if((data.seedVersion||0)<SEED_VERSION) {
    const current=(await chrome.storage.local.get('rules')).rules || DEFAULT_RULES;
    const rules=[...current];
    for(const rule of DEFAULT_RULES) if(!rules.some(r=>r.id===rule.id)) rules.push({...rule});
    await chrome.storage.local.set({rules,seedVersion:SEED_VERSION});
  }
  await state();
  await migrateLegacyQueue();
  chrome.alarms.create(ALARM,{periodInMinutes:1});
  await reconcile();
  await flush();
}
// ---- 按文件名里的 danbooru md5 补全来源 ----
// 桌面端给出待查清单 → 本页同源查 posts.json → 回写 page_url/source_url。
// 之所以在浏览器里跑而不是桌面端跑：Danbooru 前面有 Cloudflare 人机校验，只有浏览器能过。
const SOURCE_KEY="sourceJob";
const DANBOORU_API="https://danbooru.donmai.us/posts.json";
let sourceRunning=null;

async function sourceJob() {
  return (await chrome.storage.local.get(SOURCE_KEY))[SOURCE_KEY] || null;
}
async function saveSourceJob(job) {
  await chrome.storage.local.set({[SOURCE_KEY]:job});
}
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));

/** 查一个 md5。返回 null 表示 Danbooru 没有这张图；抛错表示通道被挡。 */
async function danbooruPost(md5) {
  const url=DANBOORU_API+"?tags=md5:"+encodeURIComponent(md5)+"&only=id,md5,source";
  const response=await fetch(url,{credentials:"include",cache:"no-store"});
  const type=response.headers.get("content-type") || "";
  if (response.status === 429) throw new Error("Danbooru 限流（429），请过几分钟再试");
  if (!response.ok || !type.includes("json")) {
    throw new Error("Danbooru 返回的不是数据（多半是人机校验）。先在浏览器打开 danbooru.donmai.us 通过校验，再点一次补全");
  }
  const data=await response.json();
  if (!Array.isArray(data) || data.length === 0) return null;
  const post=data[0];
  const id=String(post.id || "");
  return {
    md5,
    post_id:id,
    page_url:id ? "https://danbooru.donmai.us/posts/"+id : "",
    source_url:typeof post.source === "string" ? post.source.trim() : ""
  };
}

async function backfillSources(settings) {
  if (sourceRunning) throw new Error("补全正在进行中");
  sourceRunning=(async () => {
    const queue=await bridgeSourceQueue(settings.bridgePort);
    if (!queue.ok) throw new Error(queue.error || "读不到待补清单");
    const items=queue.data.items || [];
    const job={running:true,total:items.length,done:0,found:0,updated:0,missing:0,error:"",startedAt:Date.now(),finishedAt:0};
    await saveSourceJob(job);
    if (items.length === 0) { await saveSourceJob({...job,running:false,finishedAt:Date.now()}); return job; }

    let batch=[];
    const send=async () => {
      if (batch.length === 0) return;
      const result=await bridgeSourceApply(settings.bridgePort,settings.bridgeToken,batch);
      if (!result.ok) throw new Error(result.error || "回写失败");
      job.updated+=Number(result.data.updated) || 0;
      batch=[];
      await saveSourceJob({...job});
    };
    try {
      for (const item of items) {
        const hit=await danbooruPost(item.md5);
        job.done+=1;
        if (hit) { job.found+=1; batch.push(hit); }
        else job.missing+=1;
        if (batch.length >= 40) await send();
        await saveSourceJob({...job});
        await sleep(180);
      }
      await send();
      job.running=false; job.finishedAt=Date.now();
      await saveSourceJob(job);
      return job;
    } catch (error) {
      await send().catch(() => {});
      job.running=false; job.finishedAt=Date.now(); job.error=String(error.message || error);
      await saveSourceJob(job);
      throw error;
    }
  })();
  try { return await sourceRunning; }
  finally { sourceRunning=null; }
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
        case "backfillSources":
          backfillSources(s.settings).catch(error=>captureError(error));
          sendResponse({ok:true});
          break;
        case "sourceJobState":
          sendResponse({ok:true,job:await sourceJob()});
          break;
        default: throw new Error("未知操作");
      }
    } catch(error) { sendResponse({ok:false,error:String(error.message || error)}); }
  })();
  return true;
});

