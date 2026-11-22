import {setTheme} from "./theme.js";
import {setupSites} from "./sites-editor.js";
import {animateDisclosure} from "./disclosure.js";
import {BUILTIN_SITES, MATCH_FIELDS, MATCH_TYPES, belongsToSite, findRule, hostOf, newRuleId, ruleError} from "./rules.js";

const $ = id => document.getElementById(id);
const pages = Array.from(document.querySelectorAll("nav button"));
const captureFields = ["enabled", "captureAllImages", "dragEnabled", "theme", "dropZoneCorner"];
let state;
let rulesDirty = false;

document.querySelector(".options main").addEventListener("click", event => {
  const summary = event.target.closest("details > summary");
  const panel = summary?.nextElementSibling;
  if (!panel?.classList.contains("disclosure-panel")) return;

  event.preventDefault();
  const details = summary.parentElement;
  // Keep the native details open until the closing panel reaches zero height.
  const opening = !details.open || panel.inert;
  const from = details.open ? panel.getBoundingClientRect().height : 0;
  if (!opening && panel.contains(document.activeElement)) summary.focus();
  details.open = true;
  panel.inert = !opening;
  animateDisclosure(panel, opening, {from, onFinish: () => { details.open = opening; }});
});

async function message(payload) {
  const reply = await chrome.runtime.sendMessage(payload);
  if (!reply?.ok) throw Error(reply?.error || "操作失败");
  return reply;
}

function feedback(id, text, error = false) {
  $(id).textContent = text;
  $(id).classList.toggle("error", error);
}

async function run(id, operation) {
  feedback(id, "正在处理…");
  try {
    feedback(id, await operation());
    return true;
  } catch (error) {
    feedback(id, error.message, true);
    return false;
  }
}

function showPage(page) {
  for (const button of pages) {
    const selected = button.dataset.page === page;
    if (selected) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
    $(button.dataset.page).hidden = !selected;
  }
  if (page === "connection") {
    void refreshBridge().catch(error => feedback("bridge-feedback", error.message, true));
  }
}

function syncCapture() {
  for (const id of captureFields) {
    const input = $(id);
    if (input.type === "checkbox") input.checked = !!state.settings[id];
    else input.value = state.settings[id] || "system";
  }
  $("captureAllImages").disabled = !state.settings.enabled;
  setTheme(state.settings.theme);
}

let sites;
function renderSites() { sites?.render(); }
for (const button of pages) button.onclick = () => {
  if (state) showPage(button.dataset.page);
};
for (const id of captureFields) {
  const input = $(id);
  input.onchange = async () => {
    const value = input.type === "checkbox" ? input.checked : input.value;
    input.disabled = true;
    await run("capture-feedback", async () => {
      await message({type:"saveState", settings:{[id]:value}});
      state.settings[id] = value;
      return "已保存";
    });
    input.disabled = false;
    syncCapture();
    renderSites();
  };
}
$("drag-directory").onsubmit = event => {
  event.preventDefault();
  const value = $("dragTargetDir").value.trim();
  void run("capture-feedback", async () => {
    await message({type:"saveState", settings:{dragTargetDir:value}});
    return "下载子目录已保存";
  });
};

async function refreshBridge() {
  const {bridge} = await message({type:"getState"});
  $("bridge-status").textContent = bridge.paired ? "Gallery 已连接"
    : bridge.running ? "Gallery 在线 · 需要配对" : "Gallery 未连接";
  $("bridge-queue").hidden = !bridge.queued;
  $("bridge-queue").textContent = bridge.queued ? bridge.queued + " 条待推送" : "";
  $("retry-queue").hidden = !bridge.queued;
  return bridge;
}
function connectionSettings() {
  let token = $("bridgeToken").value.trim();
  let port = Number($("bridgePort").value);
  if (token.startsWith("{")) {
    let config;
    try { config = JSON.parse(token); }
    catch { throw Error("连接配置格式无效"); }
    token = String(config.token || "").trim();
    port = Number(config.port);
    if (!token) throw Error("连接配置中缺少配对码");
    $("bridgeToken").value = token;
    $("bridgePort").value = port;
  }
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw Error("端口应为 1024 到 65535");
  return {bridgeToken:token, bridgePort:port};
}
$("bridge-form").onsubmit = event => {
  event.preventDefault();
  void run("bridge-feedback", async () => {
    const settings = connectionSettings();
    await message({type:"saveState", settings});
    Object.assign(state.settings, settings);
    await refreshBridge();
    return "连接设置已保存";
  });
};
$("test-bridge").onclick = () => run("bridge-feedback", async () => {
  const settings = connectionSettings();
  await message({type:"testBridge", port:settings.bridgePort, token:settings.bridgeToken});
  return "连接成功";
});
$("retry-queue").onclick = () => run("bridge-feedback", async () => {
  await message({type:"retry"});
  const bridge = await refreshBridge();
  return bridge.queued ? bridge.queued + " 条仍待同步" : "没有待同步记录";
});

window.addEventListener("beforeunload", event => {
  if (!rulesDirty) return;
  event.preventDefault();
  event.returnValue = "";
});

try {
  state = await message({type:"getSettings"});
  syncCapture();
  for (const id of ["dragTargetDir", "bridgeToken", "bridgePort"]) $(id).value = state.settings[id] ?? "";
  sites = setupSites(state, message, (text, error) => feedback("sites-feedback", text, error));
  renderSites();
  $("loading").hidden = true;
  showPage("capture");
} catch (error) {
  feedback("loading", error.message, true);
  $("loading").hidden = false;
}
