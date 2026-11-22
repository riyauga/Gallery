import {setTheme} from "./theme.js";
const $ = id => document.getElementById(id);
let bridge;
let updating = false;

async function message(payload) {
  const result = await chrome.runtime.sendMessage(payload);
  if (!result?.ok) throw Error(result?.error || "操作失败");
  return result;
}

function feedback(text) {
  $("feedback").textContent = text;
  $("feedback").hidden = !text;
}

function render() {
  $("connection").textContent = !bridge.running ? "Gallery 未连接"
    : !bridge.paired ? "Gallery 在线 · 需要配对"
    : bridge.syncing ? "Gallery 已连接 · 正在推送" : "Gallery 已连接";
  $("queued").hidden = !bridge.queued;
  $("queued").textContent = bridge.queued ? `${bridge.queued} 条待推送` : "";
  $("queue-error").textContent = bridge.queued ? bridge.error || "" : "";
  $("queue-error").hidden = !$("queue-error").textContent;
}

async function refresh() {
  if (updating) return;
  updating = true;
  try {
    const state = await message({type:"getState"});
    bridge = state.bridge;
    render();
  } catch (error) {
    feedback(error.message);
  } finally {
    updating = false;
  }
}

function launchLibrary() {
  feedback("若 Gallery 没有打开，请手动启动，并在桌面端设置中检查启动路径。");
  window.location.href = "gallery-open://library";
}

async function openView(view, button) {
  button.disabled = true;
  try {
    await message({type:"openDesktop", view});
    window.close();
  } catch (error) {
    feedback(error.message);
    await refresh();
    if (view === "library" && !bridge?.paired) launchLibrary();
  } finally {
    button.disabled = false;
  }
}

$("library").onclick = () => {
  if (bridge?.paired) {
    void openView("library", $("library"));
    return;
  }
  launchLibrary();
};
$("settings").onclick = () => chrome.runtime.openOptionsPage();

void chrome.storage.local.get("settings").then(({settings}) => {
  setTheme(settings?.theme);
}, () => setTheme("system"));
void refresh();
setInterval(refresh, 2000);
