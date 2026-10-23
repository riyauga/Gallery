/** Capture tags and original URLs before the user leaves the post for its original image. */
(() => {
  const categories = {artist:1,copyright:3,character:4,general:0,meta:5};
  const ratingNames = {general:"g",sensitive:"s",questionable:"q",explicit:"e"};
  let last = "";
  /** 作者评论：优先译文，没译文用原文；两样都空就当没有。 */
  function commentary() {
    for (const id of ["translated-artist-commentary", "original-artist-commentary"]) {
      const box = document.getElementById(id);
      if (!box) continue;
      const title = (box.querySelector("h3")?.innerText || "").trim();
      const desc = [...box.querySelectorAll("div.prose")]
        .map(node => (node.innerText || "").trim()).filter(Boolean).join("\n");
      if (title || desc) return { title, desc };
    }
    return { title: "", desc: "" };
  }
  function scrape() {
    const container = document.querySelector("section.image-container");
    const list = document.querySelector("#tag-list");
    const id = /^\/posts\/(\d+)/.exec(location.pathname)?.[1];
    if (!id || !container || !list) return null;
    const tags = {};
    for (const [category,number] of Object.entries(categories)) {
      const ul = list.querySelector("ul." + category + "-tag-list");
      const nodes = ul ? ul.querySelectorAll("li[data-tag-name]") :
        list.querySelectorAll("li.tag-type-" + number + "[data-tag-name]");
      tags[category] = [...new Set([...nodes].map(li => li.dataset.tagName).filter(Boolean))];
    }
    let rating = String(container.dataset.rating || "").toLowerCase();
    if (!["g","s","q","e"].includes(rating)) {
      const word = (document.querySelector("#post-info-rating")?.textContent || "").replace(/^rating:\s*/i,"").trim().toLowerCase();
      rating = ratingNames[word] || "";
    }
    const urls = [...document.querySelectorAll(".image-view-original-link, #post-option-download a, #post-info-size a")]
      .map(a => a.href).filter(url => {
        try { const u = new URL(url); return u.hostname === "donmai.us" || u.hostname.endsWith(".donmai.us"); }
        catch { return false; }
      });
    if (container.dataset.fileUrl) urls.push(new URL(container.dataset.fileUrl,location.href).href);
    const note = commentary();
    return {
      site:"danbooru",page_url:location.origin + location.pathname,post_id:id,
      tags,tag_string:container.dataset.tags || "",tags_complete:!!list,
      rating,source:container.dataset.source || document.querySelector("#post-info-source a")?.href || "",
      commentary_title:note.title,commentary_desc:note.desc,
      original_urls:[...new Set(urls)],scraped_at:Date.now()
    };
  }
  function report(force=false) {
    const payload = scrape();
    if (!payload) return;
    const signature = JSON.stringify({...payload,scraped_at:0});
    if (!force && signature === last) return;
    last = signature;
    chrome.runtime.sendMessage({type:"pageTags",payload}).catch(() => {});
  }
  report();
  window.addEventListener("pageshow",() => report(true));
  document.addEventListener("pointerdown",event => {
    if (event.target.closest?.(".image-view-original-link, #post-option-download a, #post-info-size a")) report(true);
  },true);
  document.addEventListener("contextmenu",() => report(true),true);
  let timer;
  const observer = new MutationObserver(() => { clearTimeout(timer); timer=setTimeout(() => report(),150); });
  observer.observe(document.querySelector("#tag-list") || document.body,{childList:true,subtree:true,attributes:true});
  setTimeout(() => report(),800);
})();

