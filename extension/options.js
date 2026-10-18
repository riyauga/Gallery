import {MATCH_TYPES,findRule,normalizeRules} from "./rules.js";
const $=id=>document.getElementById(id);let state=null,dirty=false;
async function message(payload){const value=await chrome.runtime.sendMessage(payload);if(!value?.ok)throw Error(value?.error || "操作失败");return value;}
function feedback(text,error=false){$("feedback").textContent=text;$("feedback").classList.toggle("error",error);}
function input(value){const e=document.createElement("input");e.value=value || "";return e;}
function select(options,value){const e=document.createElement("select");for(const [v,label] of options){const o=document.createElement("option");o.value=v;o.textContent=label;e.append(o);}e.value=value;return e;}
function field(label,control,wide=false){const wrapper=document.createElement("label");wrapper.className=wide ? "wide stack" : "stack";wrapper.append(document.createTextNode(label),control);return wrapper;}
function changed(){dirty=true;feedback("有未保存的修改");}
function renderRules(){
  const container=$("rules");container.replaceChildren();
  state.rules.forEach((rule,index)=>{
    const details=document.createElement("details");details.className="rule";const summary=document.createElement("summary");
    const check=input();check.type="checkbox";check.checked=rule.enabled;check.onclick=e=>e.stopPropagation();check.onchange=()=>{rule.enabled=check.checked;changed();};
    const text=document.createElement("strong");text.textContent=rule.name;summary.append(check,text);details.append(summary);
    const fields=document.createElement("div");fields.className="rule-fields";
    const name=input(rule.name),on=select([["referrer","来源页面"],["url","图片地址"],["host","图片域名"],["filename","文件名"]],rule.matchOn),type=select(MATCH_TYPES,rule.matchType),pattern=input(rule.pattern);
    name.oninput=()=>{rule.name=name.value;text.textContent=name.value;changed();};on.onchange=()=>{rule.matchOn=on.value;changed();};type.onchange=()=>{rule.matchType=type.value;changed();};pattern.oninput=()=>{rule.pattern=pattern.value;changed();};
    fields.append(field("网站名称",name),field("匹配对象",on),field("匹配方式",type),field("匹配内容",pattern,true));
    const sensitive=input();sensitive.type="checkbox";sensitive.checked=rule.caseSensitive;sensitive.onchange=()=>{rule.caseSensitive=sensitive.checked;changed();};fields.append(field("区分大小写",sensitive));
    const remove=document.createElement("button");remove.textContent="删除规则";remove.onclick=()=>{state.rules.splice(index,1);renderRules();changed();};fields.append(remove);details.append(fields);container.append(details);
  });
}
function collect(){
  return {...state.settings,enabled:$("enabled").checked,theme:$("theme").value,bridgeToken:$("bridgeToken").value.trim(),bridgePort:Number($("bridgePort").value)};
}
async function busy(id,fn){
  const button=$(id),label=button.textContent;button.disabled=true;button.textContent="正在处理…";
  try{return await fn();}catch(error){feedback(error.message,true);return null;}
  finally{button.disabled=false;button.textContent=label;}
}
document.querySelectorAll("nav button").forEach(button=>button.onclick=()=>{
  document.querySelectorAll("nav button").forEach(b=>b.classList.toggle("active",b===button));
  for(const id of ["general","sites","connection"])$(id).hidden=id!==button.dataset.page;
});
$("save").onclick=()=>busy("save",async()=>{const settings=collect();await message({type:"saveState",settings,rules:state.rules});state.settings=settings;dirty=false;feedback("设置已保存");});
$("add-rule").onclick=()=>{state.rules.push({id:crypto.randomUUID(),name:"新网站",enabled:true,matchOn:"host",matchType:"exact",pattern:"example.com",onlyImages:true,caseSensitive:false,folder:"{site}"});renderRules();$("rules").lastElementChild.open=true;changed();};
$("test-rule").onclick=()=>{const url=$("test-url").value;try{const rule=findRule(normalizeRules(state.rules),{url,referrer:url,filename:new URL(url).pathname,mime:"image/jpeg"});$("test-result").textContent=rule ? "命中："+rule.rule.name : "未命中接收规则";}catch(error){$("test-result").textContent=error.message;}};
$("test-bridge").onclick=()=>busy("test-bridge",async()=>{
  $("bridge-result").textContent="正在验证桌面端与配对码…";const settings=collect();
  try{const result=await message({type:"testBridge",port:settings.bridgePort,token:settings.bridgeToken});$("bridge-result").textContent="连接成功 · 配对码有效"+(dirty ? "。请保存设置。" : "");}
  catch(error){$("bridge-result").textContent="连接失败："+error.message;throw error;}
});
$("retry-queue").onclick=()=>busy("retry-queue",async()=>{await message({type:"retry"});const current=await message({type:"getState"});$("bridge-result").textContent=current.bridge.queued ? current.bridge.queued+" 条仍在队列，请检查连接和配对码" : "队列已全部送达";});
for(const id of ["enabled","theme","bridgeToken","bridgePort"])$(id).addEventListener("input",()=>{if(id==="theme")document.documentElement.dataset.theme=$("theme").value;changed();});
window.addEventListener("beforeunload",event=>{if(dirty){event.preventDefault();event.returnValue="";}});
(async()=>{try{state=await message({type:"getState"});for(const id of ["theme","bridgeToken","bridgePort"])$(id).value=state.settings[id];$("enabled").checked=state.settings.enabled;document.documentElement.dataset.theme=state.settings.theme || "system";renderRules();feedback("设置已读取");$("bridge-result").textContent=state.bridge.running ? "桌面端已在线" : "桌面端未连接";}catch(error){feedback(error.message,true);$("save").disabled=true;}})();

