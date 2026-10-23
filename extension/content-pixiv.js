/** 作品页取标题、画师、标签、分级与原图地址；接口拿不到时退回页面元素。 */
(() => {
  const artworkId = /^\/artworks\/(\d+)/.exec(location.pathname)?.[1];
  if (!artworkId) return;
  let last = "";

  /** 作品说明是作者写的 HTML，取纯文本；没有就留空。 */
  function plainText(value) {
    const html = String(value ?? "");
    if (!html) return "";
    const box = document.createElement("div");
    box.innerHTML = html;
    return (box.innerText || box.textContent || "").trim();
  }

  function fromDom() {
    const title = document.querySelector("h1")?.textContent?.trim()
      || document.querySelector('meta[property="og:title"]')?.content?.trim() || "";
    const artist = document.querySelector('a[href^="/users/"]')?.textContent?.trim() || "";
    const tags = [...document.querySelectorAll('a[href*="/tags/"]')]
      .map(a => a.textContent?.trim()).filter(Boolean).slice(0, 50);
    return { illustTitle: title, userName: artist, xRestrict: 0, tags: { tags: tags.map(tag => ({ tag })) }, urls: {} };
  }

  async function scrape() {
    let body = null;
    try {
      const response = await fetch(`/ajax/illust/${artworkId}`, { headers: { accept: "application/json" }, credentials: "same-origin" });
      const json = await response.json();
      if (json && !json.error && json.body) body = json.body;
    } catch { /* 退回页面元素 */ }
    return body || fromDom();
  }

  async function report(force = false) {
    const body = await scrape();
    const tags = (body?.tags?.tags || []).map(t => String(t?.tag ?? "")).filter(Boolean);
    const payload = {
      site: "pixiv", page_url: `https://www.pixiv.net/artworks/${artworkId}`, post_id: artworkId,
      title: String(body?.illustTitle ?? ""), artist: String(body?.userName ?? ""),
      artist_id: String(body?.userId ?? ""), x_restrict: Number(body?.xRestrict ?? 0),
      page_count: Number(body?.pageCount ?? 0), tags, tag_string: tags.join(" "),
      commentary_desc: plainText(body?.description),
      original_url: String(body?.urls?.original ?? ""), scraped_at: Date.now(),
    };
    const signature = JSON.stringify({ ...payload, scraped_at: 0 });
    if (!force && signature === last) return;
    last = signature;
    chrome.runtime.sendMessage({ type: "pixivPage", payload }).catch(() => {});
  }

  report();
  window.addEventListener("pageshow", () => report(true));
  document.addEventListener("pointerdown", event => { if (event.target.closest?.("img")) report(true); }, true);
  document.addEventListener("contextmenu", () => report(true), true);
  let timer;
  const observer = new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(() => report(), 300); });
  observer.observe(document.body, { childList: true, subtree: true });
  setTimeout(() => report(), 800);
})();
