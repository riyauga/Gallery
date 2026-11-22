import {BUILTIN_SITES, belongsToSite, hostOf, newRuleId} from "./rules.js";
import {TAG_CATEGORIES} from "./filename-rules.js";
import {animateDisclosure} from "./disclosure.js";

const $ = id => document.getElementById(id);
const element = (tag, text, cls) => {
  const node = document.createElement(tag);
  if (text) node.textContent = text;
  if (cls) node.className = cls;
  if (tag === "button" && cls !== "site-expand") {
    node.classList.add("btn");
    if (cls !== "primary") node.dataset.variant = "outline";
  }
  return node;
};

export function setupSites(state, send, feedback) {
  let openId = null;
  let adding = false;

  async function saveRules(rules) {
    const result = await send({type:"saveState", customRules:rules});
    state.rules = result.rules;
  }

  function inputField(label, value) {
    const wrap = element("label", "", "field");
    const input = document.createElement("input");
    input.className = "input";
    input.value = value || "";
    wrap.append(label, input);
    return [wrap, input];
  }

  function editor(original, isNew) {
    const draft = structuredClone(original);
    const box = element("div", "", "site-editor");
    if (isNew) box.append(element("h3", "添加自定义网站"));
    const [nameLabel, name] = inputField("网站名称", draft.name);
    const [domainLabel, domain] = inputField(isNew || draft.matchOn === "host" ? "网站域名" : "匹配内容", isNew ? "" : draft.pattern);
    const identity = element("div", "", "rule-fields");
    identity.append(nameLabel, domainLabel);
    box.append(identity);
    const details = element("details");
    const summary = element("summary", "高级接收条件");
    const advanced = element("div", "", "rule-fields");
    const on = document.createElement("select");
    on.className = "select";
    for (const [v,n] of [["host","域名"],["referrer","来源页面"],["url","图片地址"],["filename","文件名"]]) on.add(new Option(n,v));
    on.value = draft.matchOn;
    const type = document.createElement("select");
    type.className = "select";
    for (const [v,n] of [["exact","完全匹配"],["prefix","开头匹配"],["contains","包含"],["wildcard","通配符"],["regex","正则表达式"],["suffix","结尾匹配"]]) type.add(new Option(n,v));
    type.value = draft.matchType;
    const labelOn = element("label", "匹配对象", "field"); labelOn.append(on);
    const labelType = element("label", "匹配方式", "field"); labelType.append(type);
    advanced.append(labelOn,labelType);
    const reveal = element("div", "", "disclosure-panel");
    reveal.append(advanced);
    details.append(summary, reveal);
    box.append(details);
    const heading = element("h3", "文件名标签规则");
    const ruleList = element("div");
    draft.filenameRules ||= [];
    box.append(heading,ruleList);

    function renderRules() {
      ruleList.replaceChildren();
      draft.filenameRules.forEach((rule,index) => {
        const block = element("div", "", "filename-rule");
        const [exampleLabel,example] = inputField("示例文件名",rule.example);
        const [patternLabel,pattern] = inputField("正则表达式",rule.pattern);
        const tokens = element("div", "", "capture-tokens");
        const slots = element("div", "", "capture-slots");
        const status = element("p", "", "muted");
        const remove = element("button","删除此规则"); remove.type="button";
        remove.onclick=()=>{ draft.filenameRules.splice(index,1); renderRules(); };
        rule.groups ||= {};
        function preview() {
          rule.example=example.value;
          rule.pattern=pattern.value;
          tokens.replaceChildren(); slots.replaceChildren();
          let match;
          try { match=new RegExp(rule.pattern,"i").exec(rule.example); }
          catch { status.textContent="正则表达式无效"; return; }
          if (!match) { status.textContent="示例未匹配"; return; }
          status.textContent=match.length > 1 ? "拖动捕获组到分类，或使用下拉框选择。" : "请在正则中添加捕获组。";
          for(let i=1;i<match.length;i++) {
            const token=element("div","","capture-token");
            const text=element("span",`第 ${i} 组 · ${match[i] || "空"}`);
            token.draggable=true;
            token.ondragstart=e=>e.dataTransfer.setData("text/plain",String(i));
            const select=document.createElement("select");
            select.className="select";
            select.setAttribute("aria-label",`第 ${i} 组分类`);
            select.add(new Option("未分类",""));
            for(const [key,label] of TAG_CATEGORIES) select.add(new Option(label,key));
            select.value=rule.groups[i] || "";
            select.onchange=()=>{ if(select.value) rule.groups[i]=select.value; else delete rule.groups[i]; preview(); };
            token.append(text,select); tokens.append(token);
          }
          for(const [key,label] of TAG_CATEGORIES) {
            const slot=element("div","","capture-slot");
            const values=Object.keys(rule.groups).filter(index=>rule.groups[index]===key && Number(index)<match.length).map(index=>match[index] || "空");
            slot.append(element("strong",label),element("span",values.join("、") || "拖到这里","muted"));
            slot.ondragover=e=>e.preventDefault();
            slot.ondrop=e=>{ e.preventDefault(); const i=Number(e.dataTransfer.getData("text/plain")); if(i>0 && i<match.length){rule.groups[i]=key;preview();} };
            slots.append(slot);
          }
        }
        example.oninput=preview; pattern.oninput=preview;
        const fields = element("div", "", "rule-fields");
        fields.append(exampleLabel, patternLabel);
        block.append(fields,status,tokens,slots,remove);
        ruleList.append(block); preview();
      });
    }
    const add=element("button","＋ 添加文件名规则"); add.type="button";
    add.onclick=()=>{ draft.filenameRules.push({example:"",pattern:"",groups:{}}); renderRules(); };
    const actions=element("div","","editor-actions");
    const cancel=element("button","取消"); cancel.type="button"; cancel.onclick=()=>{openId=null;adding=false;render();};
    const save=element("button",isNew?"添加":"保存","primary"); save.type="button";
    const error=element("p","","feedback error"); error.setAttribute("role","status");
    save.onclick=async()=>{
      try {
        draft.name=name.value.trim();
        if(!draft.name) throw Error("请输入网站名称");
        draft.matchOn=on.value; draft.matchType=type.value; draft.pattern=domain.value.trim();
        if(isNew && draft.matchOn==="host") {
          if(!/^[^/\\:@?#\s]+$/.test(draft.pattern) || !hostOf("https://"+draft.pattern).includes(".")) throw Error("请输入有效的网站域名");
          draft.pattern=hostOf("https://"+draft.pattern);
        }
        if(!draft.pattern) throw Error("请输入匹配内容");
        if(draft.matchType==="regex") new RegExp(draft.pattern);
        for(const rule of draft.filenameRules) {
          const match=new RegExp(rule.pattern,"i").exec(rule.example);
          if(!rule.pattern || !match) throw Error("请填写有效的正则和匹配的示例文件名");
          if(Object.keys(rule.groups).some(i=>Number(i)>=match.length)) throw Error("分类中包含不存在的捕获组");
        }
        if(isNew) {
          const result=await send({type:"saveState",addRule:draft});
          state.rules=result.rules;
        } else {
          await saveRules(state.rules.filter(r=>!BUILTIN_SITES.some(([id])=>belongsToSite(r,id))).map(r=>r.id===draft.id ? draft : r));
        }
        openId=null; adding=false; render(); feedback("已保存");
      } catch(e) { error.textContent=e.message; }
    };
    if(!isNew) {
      const remove=element("button","删除网站"); remove.type="button";
      remove.onclick=async()=>{ await saveRules(state.rules.filter(r=>r.id!==draft.id && !BUILTIN_SITES.some(([id])=>belongsToSite(r,id))));openId=null;render(); };
      actions.append(remove);
    }
    actions.append(cancel,save); box.append(add,error,actions); renderRules();
    return box;
  }

  function render() {
    const list=$("site-list"); list.replaceChildren();
    const newEditor=$("new-site-editor"); newEditor.replaceChildren();
    if(adding) newEditor.append(editor({id:newRuleId(),name:"",enabled:true,matchOn:"host",matchType:"exact",pattern:"",onlyImages:true},true));
    const groups=BUILTIN_SITES.map(([id,name])=>({id,name,rules:state.rules.filter(r=>belongsToSite(r,id)),builtin:true}));
    groups.push(...state.rules.filter(r=>!BUILTIN_SITES.some(([id])=>belongsToSite(r,id))).map(rule=>({id:rule.id,name:rule.name,rules:[rule],builtin:false})));
    for(const group of groups) {
      const card=element("article","","site-card");
      const header=element("div","","site-card-header");
      const toggle=element("button","","site-expand");
      toggle.type="button"; toggle.setAttribute("aria-expanded",String(openId===group.id));
      toggle.dataset.siteId = group.id;
      const summary = element("span", "", "site-summary");
      summary.append(element("strong",group.name),element("span",group.builtin?"站内数据":group.rules[0]?.pattern || "","muted"));
      const chevron = element("span", "", "site-chevron");
      chevron.setAttribute("aria-hidden", "true");
      toggle.append(summary, chevron);
      const restoreFocus = () => {
        render();
        const trigger = $("site-list").querySelector(`[data-site-id="${CSS.escape(group.id)}"]`);
        trigger?.focus({preventScroll:true});
        return trigger;
      };
      toggle.onclick=()=>{
        if(openId===group.id) {
          openId=null;
          toggle.setAttribute("aria-expanded","false");
          panel.inert = true;
          animateDisclosure(panel, false, {onFinish: () => {
            if (panel.isConnected && openId === null) panel.replaceChildren();
          }});
          return;
        }
        openId=group.id;adding=false;
        if (panel.firstElementChild) {
          panel.inert = false;
          toggle.setAttribute("aria-expanded","true");
          animateDisclosure(panel, true);
          return;
        }
        const opened = restoreFocus()?.closest(".site-card")?.querySelector(".site-card-reveal");
        if (opened) animateDisclosure(opened, true, {from:0});
      };
      header.append(toggle);
      if(!state.settings.captureAllImages) {
        const control=document.createElement("input"); control.type="checkbox"; control.className="input"; control.setAttribute("role","switch");
        control.checked=group.rules.length>0 && group.rules.every(r=>r.enabled);
        control.indeterminate=group.rules.some(r=>r.enabled) && !control.checked;
        control.setAttribute("aria-label",group.name+" 接收");
        control.onchange=async()=>{
          control.disabled=true;
          try {
            const result=await send({type:"saveState",...(group.builtin?{site:{id:group.id,enabled:control.checked}}:{ruleToggle:{id:group.id,enabled:control.checked}})});
            state.rules=result.rules; feedback("已保存");
          } catch(e){
            const previous = state.rules.filter(rule => group.builtin ? belongsToSite(rule, group.id) : rule.id === group.id);
            control.checked=previous.length>0 && previous.every(rule=>rule.enabled);
            control.indeterminate=previous.some(rule=>rule.enabled) && !control.checked;
            feedback(e.message,true);
          } finally { control.disabled=false; }
        };
        header.append(control);
      }
      const panel=element("div","","disclosure-panel site-card-reveal");
      if(openId===group.id) {
        const body=element("div","","site-card-body");
        if(group.builtin) body.append(element("p",({ "builtin-x":"记录来源和作者", "builtin-pixiv":"记录来源、作者、分级与资料库已有标签", "builtin-danbooru":"记录来源、分级与站内标签", "builtin-pinterest":"记录来源和作者" })[group.id]));
        else body.append(editor(group.rules[0],false));
        panel.append(body);
      }
      card.append(header,panel); list.append(card);
    }
  }
  $("new-site").onclick=()=>{
    adding=!adding;openId=null;render();
    if (adding) $("new-site-editor").querySelector("input")?.focus();
  };
  return {render};
}
