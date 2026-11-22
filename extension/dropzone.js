/**
 * 拖拽落点：在图片被拖起来时于视口右侧浮出黑洞，丢进去就下载。
 * 只负责手势与视觉；真正的下载和入队交给后台 worker（消息 type: grabImage）。
 * 样式全部收在 shadow root 里，避免被站点 CSS 影响。
 */
(() => {
  if (window.__galleryDropzone) return;
  window.__galleryDropzone = true;

  const HOLE_ART = `
    <svg class="hole-art" viewBox="0 0 120 120" aria-hidden="true">
      <defs>
        <radialGradient id="ghGlow" cx="50%" cy="50%" r="50%">
          <stop offset="42%" stop-color="#ffffff" stop-opacity=".3"/>
          <stop offset="55%" stop-color="#ffffff" stop-opacity=".16"/>
          <stop offset="70%" stop-color="#ffffff" stop-opacity=".05"/>
          <stop offset="100%" stop-color="#ffffff" stop-opacity="0"/>
        </radialGradient>
        <radialGradient id="ghCore" cx="41%" cy="35%" r="68%">
          <stop offset="0%" stop-color="#1d1d1d"/>
          <stop offset="52%" stop-color="#070707"/>
          <stop offset="100%" stop-color="#000000"/>
        </radialGradient>
      </defs>
      <circle cx="60" cy="60" r="60" fill="url(#ghGlow)"/>
      <g class="hole-body">
        <path class="hole-fill" d="M60 14.5C84 13.5 105 34 105.5 59C106 84 85.5 106 60.5 105.5C36 105 14.5 85 14 60.5C13.5 36 36 14 60 14.5Z" fill="url(#ghCore)"/>
        <path class="hole-ink" d="M60 14.5C84 13.5 105 34 105.5 59C106 84 85.5 106 60.5 105.5C36 105 14.5 85 14 60.5C13.5 36 36 14 60 14.5Z"/>
        <path class="hole-ink ghost" d="M60 14.5C84 13.5 105 34 105.5 59C106 84 85.5 106 60.5 105.5C36 105 14.5 85 14 60.5C13.5 36 36 14 60 14.5Z"/>
      </g>
      <g class="hole-swirl">
        <path d="M40 84C56 94 82 90 89 71"/>
        <path d="M31 60C29 40 47 24 66 27"/>
        <path d="M52 73C43 64 46 51 57 47C67 43 76 51 74 62C72 70 63 73 57 68"/>
        <path d="M92 46C98 56 97 68 90 78"/>
        <path d="M62 51C71 54 72 65 64 69"/>
      </g>
      <circle class="hole-dot" cx="36" cy="44" r="1.6"/>
      <circle class="hole-dot" cx="86" cy="90" r="1.3"/>
      <circle class="hole-dot" cx="70" cy="21" r="1"/>
    </svg>`;

  const STYLE = `
    :host { all: initial }
    .zone {
      position: fixed; right: 26px; top: 50%; width: 104px; height: 104px;
      margin-top: -52px; opacity: 0; pointer-events: none;
      transform: scale(.7); transition: opacity .16s ease, transform .22s cubic-bezier(.2,.8,.3,1);
      font: 12px/1.5 "Microsoft YaHei UI", "Segoe UI", sans-serif; color: #fff;
      -webkit-font-smoothing: antialiased;
    }
    .zone.show { opacity: 1; transform: scale(1); pointer-events: auto }
    .hole { position: absolute; inset: 0; display: grid; place-items: center; cursor: copy }
    .hole-art {
      width: 100%; height: 100%; overflow: visible;
      filter: drop-shadow(0 0 8px rgba(255,255,255,.38));
      animation: gh-breathe 2.8s ease-in-out infinite;
    }
    .hole-body { transform-origin: 60px 60px; animation: gh-wobble 7s ease-in-out infinite }
    .hole-ink { fill: none; stroke: rgba(255,255,255,.66); stroke-width: 3.2; stroke-linecap: round; stroke-linejoin: round }
    .hole-ink.ghost { stroke: rgba(255,255,255,.3); stroke-width: 1.8; translate: 2.6px -2px }
    .hole-swirl { fill: none; stroke: rgba(255,255,255,.34); stroke-width: 2.6; stroke-linecap: round; transform-origin: 60px 60px; animation: gh-spin 15s linear infinite }
    .hole-dot { fill: rgba(255,255,255,.5) }
    .zone.hot .hole-art { filter: drop-shadow(0 0 12px rgba(255,255,255,.9)) drop-shadow(0 0 26px rgba(255,255,255,.42)) }
    .zone.hot .hole { transform: scale(1.06) }
    .tip {
      position: absolute; left: 50%; top: 100%; margin-top: 10px; transform: translateX(-50%);
      white-space: nowrap; padding: 3px 9px; border-radius: 999px;
      background: rgba(0,0,0,.62); color: rgba(255,255,255,.92);
      opacity: 0; transition: opacity .16s ease;
    }
    .zone.show .tip { opacity: 1 }
    .zone.flash .tip { opacity: 1; background: rgba(160,40,30,.86) }
    .suck {
      position: fixed; width: 72px; height: 72px; object-fit: cover; border-radius: 8px;
      transform: translate(-50%,-50%); transform-origin: center;
      transition: transform .58s cubic-bezier(.55,0,.8,.25), opacity .58s ease;
      box-shadow: 0 6px 22px rgba(0,0,0,.55); pointer-events: none; z-index: 1;
    }
    @keyframes gh-breathe {
      50% { filter: drop-shadow(0 0 13px rgba(255,255,255,.58)) }
    }
    @keyframes gh-spin { to { transform: rotate(360deg) } }
    @keyframes gh-wobble {
      0%, 100% { transform: rotate(-2deg) scale(1) }
      50% { transform: rotate(2deg) scale(1.014) }
    }
    @media (prefers-reduced-motion: reduce) {
      .hole-body { animation: none }
      .hole-swirl { animation: none }
      .hole-art { animation: none }
      .suck { transition-duration: .01s }
    }`;

  let host = null;
  let shadow = null;
  let zone = null;
  let tip = null;
  let hideTimer = 0;
  let flashTimer = 0;
  // 停靠位置：设置页可选右侧居中／右上／右下／左下。
  let enabled = true;
  let corner = "right-center";
  let settingsLoaded = false;
  const CORNERS = {
    "right-center": { right: "26px", top: "50%", left: "", bottom: "", marginTop: "-52px" },
    "right-top": { right: "26px", top: "26px", left: "", bottom: "", marginTop: "0" },
    "right-bottom": { right: "26px", top: "", left: "", bottom: "26px", marginTop: "0" },
    "left-bottom": { right: "", top: "", left: "26px", bottom: "26px", marginTop: "0" },
  };

  function applyCorner() {
    if (!zone) return;
    const spot = CORNERS[corner] || CORNERS["right-center"];
    zone.style.right = spot.right;
    zone.style.left = spot.left;
    zone.style.top = spot.top;
    zone.style.bottom = spot.bottom;
    zone.style.marginTop = spot.marginTop;
  }

  /** 设置只在第一次有拖拽迹象时读一次：每个页面加载都读会白白唤醒后台 worker。 */
  function loadSettings() {
    if (settingsLoaded) return;
    settingsLoaded = true;
    try {
      chrome.storage.local.get("settings")
        .then(data => {
          enabled = data.settings?.dragEnabled ?? data.settings?.enabled ?? true;
          corner = data.settings?.dropZoneCorner || "right-center";
          applyCorner();
          if (!enabled) hide();
        })
        .catch(() => { settingsLoaded = false; });
    } catch { settingsLoaded = false; }
  }

  try {
    // 设置页改完即时生效，不用刷新已打开的页面。
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === "local" && changes.settings) {
        enabled = changes.settings.newValue?.dragEnabled ?? changes.settings.newValue?.enabled ?? true;
        if (!enabled) hide();
        corner = changes.settings.newValue?.dropZoneCorner || "right-center";
        applyCorner();
      }
    });
  } catch { /* 扩展上下文失效时保持默认位置 */ }

  function build() {
    if (host) return;
    host = document.createElement("div");
    host.id = "__gallery-dropzone";
    host.style.cssText = "position:fixed;inset:0;pointer-events:none;z-index:2147483647";
    shadow = host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = STYLE;
    zone = document.createElement("div");
    zone.className = "zone";
    const hole = document.createElement("div");
    hole.className = "hole";
    hole.innerHTML = HOLE_ART;
    tip = document.createElement("div");
    tip.className = "tip";
    tip.textContent = "丢进来";
    zone.append(hole, tip);
    shadow.append(style, zone);
    document.documentElement.append(host);
    applyCorner();
  }

  function show() {
    build();
    clearTimeout(hideTimer);
    zone.classList.add("show");
    zone.classList.remove("flash");
    tip.textContent = "丢进来";
  }

  function hide(delay = 0) {
    if (!zone) return;
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      zone.classList.remove("show", "hot");
      clearTimeout(flashTimer);
    }, delay);
  }

  function flash(text) {
    if (!zone) return;
    tip.textContent = text;
    zone.classList.add("flash");
    clearTimeout(flashTimer);
    flashTimer = setTimeout(() => {
      zone.classList.remove("flash");
      tip.textContent = "丢进来";
    }, 2600);
  }

  /** 拖拽时只有 drop 阶段能读到 dataTransfer 内容，dragover 阶段读不到。 */
  function readUrl(data) {
    if (!data) return "";
    const list = (data.getData("text/uri-list") || "").split(/\r?\n/).map(value => value.trim())
      .filter(value => value && !value.startsWith("#"));
    if (list.length) return list[0];
    const html = data.getData("text/html") || "";
    const match = /<img[^>]+src\s*=\s*["']([^"']+)["']/i.exec(html);
    if (match) return match[1];
    return (data.getData("text/plain") || "").trim();
  }

  function suck(url, x, y) {
    const box = zone.getBoundingClientRect();
    const cx = box.left + box.width / 2;
    const cy = box.top + box.height / 2;
    const node = document.createElement("img");
    node.className = "suck";
    node.src = url;
    node.style.left = x + "px";
    node.style.top = y + "px";
    shadow.append(node);
    requestAnimationFrame(() => {
      node.style.transform = "translate(-50%,-50%) translate(" + (cx - x) + "px," + (cy - y) + "px) scale(.07) rotate(200deg)";
      node.style.opacity = "0";
    });
    setTimeout(() => node.remove(), 720);
  }

  function isImageDrag(event) {
    const target = event.target;
    if (!(target instanceof Element)) return false;
    const image = target.tagName === "IMG" ? target : target.closest?.("img");
    return !!image && !!(image.currentSrc || image.src);
  }

  // 按下图片或开始拖拽时才读设置；读到已关闭就立刻收起。
  document.addEventListener("pointerdown", event => {
    if (isImageDrag(event)) loadSettings();
  }, true);

  document.addEventListener("dragstart", event => {
    if (!isImageDrag(event)) return;
    loadSettings();
    if (!enabled) return;
    show();
  }, true);

  document.addEventListener("dragend", () => hide(120), true);

  // 页面内元素可能吞掉拖拽事件；鼠标松开时兜底收起浮窗。
  window.addEventListener("mouseup", () => hide(220), true);

  function onDragOver(event) {
    const data = event.dataTransfer;
    if (!data) return;
    const carries = data.types.includes("text/uri-list") || data.types.includes("text/html") || data.types.includes("Files");
    if (!carries) return;
    event.preventDefault();
    event.stopPropagation();
    data.dropEffect = "copy";
    zone.classList.add("hot");
  }

  function onDrop(event) {
    event.preventDefault();
    event.stopPropagation();
    zone.classList.remove("hot");
    const url = readUrl(event.dataTransfer);
    if (!url) {
      flash("没有拿到图片地址");
      return;
    }
    suck(url, event.clientX, event.clientY);
    hide(360);
    try {
      chrome.runtime.sendMessage({ type: "grabImage", url, page_url: location.href })
        .then(reply => { if (!reply?.ok) flash(reply?.error || "下载失败"); })
        .catch(() => flash("扩展已更新，请刷新页面"));
    } catch {
      flash("扩展已更新，请刷新页面");
    }
  }

  document.addEventListener("drop", event => {
    // 落在浮窗以外的地方保持浏览器默认行为，只在浮窗上拦截。
    if (!zone || !event.composedPath?.().includes(zone)) return;
    onDrop(event);
  }, true);

  document.addEventListener("dragover", event => {
    if (!zone || !event.composedPath?.().includes(zone)) return;
    onDragOver(event);
  }, true);

  document.addEventListener("dragleave", event => {
    if (zone && !event.composedPath?.().includes(zone)) zone.classList.remove("hot");
  }, true);
})();
