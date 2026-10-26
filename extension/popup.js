import {findRule} from "./rules.js";
const $=id=>document.getElementById(id);
async function message(payload){const result=await chrome.runtime.sendMessage(payload);if(!result?.ok)throw Error(result?.error || "操作失败");return result;}
function feedback(text,error=false){$("feedback").textContent=text;$("feedback").classList.toggle("error",error);}
async function load(){
  try{
    const state=await message({type:"getState"});document.documentElement.dataset.theme=state.settings.theme || "system";
    const [tab]=await chrome.tabs.query({active:true,currentWindow:true});const rule=findRule(state.rules,{url:tab?.url || "",referrer:tab?.url || "",filename:"preview.jpg",mime:"image/jpeg"});
    $("site-title").textContent=!state.settings.enabled ? "接收已暂停" : rule ? rule.rule.name+" · 正在接收" : "当前网站未启用";
    $("site-detail").textContent=rule ? (tab?.url?.includes("donmai.us") ? "原图另存为时自动带上帖子标签" : rule.rule.id.startsWith('builtin-x') ? "记录来源、可确认的发帖账号和分辨率；目录与其他标签手动填写" : "没有专用适配时，下载进入待处理") : "可在设置中添加网站接收规则";
    $("connection-title").textContent=state.bridge.paired ? "桌面端已连接" : state.bridge.running ? "桌面端在线 · 需要配对" : "桌面端未连接";
    $("connection-detail").textContent=state.bridge.paired ? "图片和标签可以自动归档" : state.bridge.running ? "请在设置中粘贴配对码并测试连接" : "打开 Gallery；离线期间标签仍保存在队列";
    $("pending-title").textContent=state.bridge.pending+" 张待处理";
    $("pending-detail").textContent=state.bridge.queued ? state.bridge.queued+" 条标签等待补推" : "没有等待补推的标签";
    for(const id of ["open-gallery","open-pending"]){$(id).disabled=!state.bridge.paired;$(id).title=state.bridge.paired ? "" : "请先打开 Gallery 并完成配对";}
  }catch(error){feedback(error.message,true);}
}
async function run(button,fn){
  button.disabled=true;feedback("正在处理…");
  try{await fn();feedback("已完成");}catch(error){feedback(error.message,true);}
  finally{button.disabled=false;}
}
async function pollSource(){
  try{
    const {job}=await message({type:"sourceJobState"});
    if(!job){$("source-title").textContent="来源补全未运行";$("source-detail").textContent="按文件名里的 danbooru md5 反查帖子";return false;}
    if(job.running){
      $("source-title").textContent="正在补全 "+job.done+"/"+job.total;
      $("source-detail").textContent="已写回 "+job.updated+" 张 · 未找到 "+job.missing;
      return true;
    }
    $("source-title").textContent=job.error ? "补全中断" : "来源补全已完成";
    $("source-detail").textContent=job.error ? job.error : "查 "+job.total+" 个 md5 · 写回 "+job.updated+" 张 · 未找到 "+job.missing;
    $("source-title").style.color=job.error ? "#f87171" : "";
    return false;
  }catch(error){return false;}
}
$("backfill-sources").onclick=()=>run($("backfill-sources"),async()=>{
  await message({type:"backfillSources"});
  await new Promise(r=>setTimeout(r,800));
  for(let i=0;i<600;i++){if(!await pollSource())break;await new Promise(r=>setTimeout(r,1000));}
});
$("open-gallery").onclick=()=>run($("open-gallery"),()=>message({type:"openDesktop",view:"library"}));
$("open-pending").onclick=()=>run($("open-pending"),()=>message({type:"openDesktop",view:"pending"}));
$("settings").onclick=()=>chrome.runtime.openOptionsPage();
$("retry").onclick=()=>run($("retry"),async()=>{await message({type:"retry"});await load();});
load();
pollSource();

