/** 读取页面内嵌的 pin 数据，并把用户正在看的图片绑定到具体 pin。 */
(() => {
  const PINS_KEY = "pinterest-pins:";

  function readProps() {
    try {
      const el = document.getElementById("__PWS_INITIAL_PROPS__");
      if (!el) return {};
      const state = JSON.parse(el.textContent)?.initialReduxState;
      return state?.pins || {};
    } catch { return {}; }
  }

  function sendPins(force = false) {
    const pins = readProps();
    const ids = Object.keys(pins);
    if (!ids.length) return;
    const payload = { page_url: location.href, pins: [] };
    for (const id of ids) {
      const pin = pins[id];
      if (!pin || !pin.id) continue;
      payload.pins.push({
        id: String(pin.id), title: pin.title || "", description: pin.description || "",
        link: pin.link || "", domain: pin.domain || "", board: pin.board?.name || "",
        artist: pin.pinner?.username || pin.creator?.username || "",
        images: Object.fromEntries(Object.entries(pin.images || {})
          .map(([key, value]) => [key, value?.url || ""]).filter(([, url]) => url)),
      });
    }
    if (!payload.pins.length) return;
    const signature = PINS_KEY + payload.pins.map(p => p.id).join(",");
    if (!force && signature === lastSignature) return;
    lastSignature = signature;
    chrome.runtime.sendMessage({ type: "pinterestPins", payload }).catch(() => {});
  }

  let lastSignature = "";

  /** 正在操作的那张图属于哪个 pin：先看最近的 pin 链接，再看页面数据的图片 hash。 */
  function bindImage(event) {
    const image = event.target?.closest?.("img");
    if (!image) return;
    const imageUrl = image.currentSrc || image.src || "";
    if (!/i\.pinimg\.com/.test(imageUrl)) return;
    let pinId = "";
    const link = image.closest('a[href*="/pin/"]')?.href || "";
    if (link) pinId = /\/pin\/(\d+)/.exec(new URL(link, location.href).pathname)?.[1] || "";
    if (!pinId) {
      const direct = /\/pin\/(\d+)/.exec(location.pathname)?.[1];
      if (direct) pinId = direct;
    }
    chrome.runtime.sendMessage({
      type: "pinterestImage",
      payload: { image_url: imageUrl, pin_id: pinId, page_url: location.href },
    }).catch(() => {});
  }

  sendPins();
  window.addEventListener("pageshow", () => sendPins(true));
  document.addEventListener("contextmenu", bindImage, true);
  document.addEventListener("click", bindImage, true);
  setTimeout(() => sendPins(), 1200);
})();
