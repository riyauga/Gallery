/** Danbooru metadata matching. Exact image identity replaces ambiguous page-prefix matching. */
export const MD5_RE = /(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])/i;

export function isDanbooruUrl(value) {
  try {
    const u = new URL(value);
    return u.protocol === "https:" && (u.hostname === "donmai.us" || u.hostname.endsWith(".donmai.us"));
  } catch { return false; }
}

export function imageKey(value) {
  try {
    const u = new URL(value);
    u.search = "";
    u.hash = "";
    return u.href;
  } catch { return ""; }
}

export function imageMd5(value) {
  try { return MD5_RE.exec(decodeURIComponent(new URL(value).pathname))?.[0]?.toLowerCase() || ""; }
  catch { return ""; }
}

export function validPost(value, postId) {
  try {
    const u = new URL(value);
    const id = /^\/posts\/(\d+)\/?$/.exec(u.pathname)?.[1];
    return isDanbooruUrl(value) && !!id && (!postId || String(postId) === id);
  } catch { return false; }
}

export function chooseMetadata(download, pages) {
  const urls = [download.url, download.finalUrl].filter(Boolean);
  const exact = pages.filter(page => validPost(page.page_url, page.post_id) &&
    (page.original_urls || []).some(original => urls.some(url => imageKey(original) === imageKey(url))));
  let matches = exact;
  if (!matches.length && urls.every(isDanbooruUrl)) {
    const hashes = new Set(urls.map(imageMd5).filter(Boolean));
    matches = pages.filter(page => validPost(page.page_url, page.post_id) &&
      (page.original_urls || []).some(original => hashes.has(imageMd5(original)) && imageMd5(original)));
  }
  if (!matches.length || new Set(matches.map(p => p.post_id)).size !== 1) return null;
  return matches.sort((a,b) => b.scraped_at - a.scraped_at)[0];
}

export function completeMetadata(page) {
  return !!(page && validPost(page.page_url,page.post_id) && page.tags_complete &&
    (page.original_urls || []).some(isDanbooruUrl));
}

