/** Pixiv 作品身份与标签映射：作品 id 只从页面地址和图片地址解析，不猜。 */

/** 作品页：https://www.pixiv.net/artworks/150101034 */
export function isPixivPage(value) {
  try {
    const u = new URL(value);
    return u.protocol === "https:" && /^(?:www\.)?pixiv\.net$/.test(u.hostname);
  } catch { return false; }
}

export function pixivArtworkId(value) {
  if (!isPixivPage(value)) return "";
  const m = /^\/artworks\/(\d+)(?:\/|$)/.exec(new URL(value).pathname);
  return m ? m[1] : "";
}

/**
 * 图片地址 → {artwork_id, page}。
 * 原图 /img-original/img/2026/09/25/22/26/53/150101034_p0.jpg
 * 缩略图 /c/540x540_70/img-master/.../150101034_p0_master1200.jpg
 */
export function pixivImage(value) {
  try {
    const u = new URL(value);
    if (u.protocol !== "https:" || !/(?:^|\.)pximg\.net$/.test(u.hostname)) return null;
    const last = u.pathname.split("/").filter(Boolean).pop() || "";
    const m = /^(\d+)_p(\d+)(?:_[a-z]+\d+)?\.(?:jpe?g|png|gif|webp)$/i.exec(last);
    return m ? { artwork_id: m[1], page: Number(m[2]) } : null;
  } catch { return null; }
}

export function isPixivDownload(item) {
  return isPixivPage(item?.referrer) || [item?.url, item?.finalUrl].some(url => !!pixivImage(url));
}

/** xRestrict：0 全年龄、1 R-18、2 R-18G。全年龄标 g，R-18 给 q，猎奇给 e。 */
export function pixivRating(xRestrict) {
  const n = Number(xRestrict);
  return n === 1 ? "q" : n === 2 ? "e" : n === 0 ? "g" : "";
}

/** 标签归一化成 danbooru 的写法：小写、空格换下划线。 */
export function normalizeTag(value) {
  return String(value ?? "").trim().toLowerCase().replace(/\s+/g, "_");
}

/**
 * 只保留库里已有的作品与角色。index 来自桌面端 /tags：
 * { works:["wuthering_waves"], characters:["hsin"], aliases:{"心":"hsin"} }
 * 命中别名时用规范名归类；未命中一律丢弃（用户要求：七类之外的不要）。
 */
export function matchPixivTags(tags, index) {
  const works = new Map((index?.works || []).map(n => [String(n).toLowerCase(), n]));
  const characters = new Map((index?.characters || []).map(n => [String(n).toLowerCase(), n]));
  const aliases = index?.aliases || {};
  const result = { copyright: [], character: [] };
  const seen = { copyright: new Set(), character: new Set() };
  for (const raw of tags || []) {
    const key = normalizeTag(raw);
    if (!key) continue;
    const canonical = normalizeTag(aliases[key] ?? aliases[String(raw).trim()] ?? key);
    const kind = works.has(canonical) ? "copyright" : characters.has(canonical) ? "character" : "";
    if (!kind || seen[kind].has(canonical)) continue;
    seen[kind].add(canonical);
    result[kind].push(canonical);
  }
  return result;
}

/** 页面接口返回体 → 统一快照。任何字段缺失都不抛错。 */
export function pixivSnapshot(body, pageUrl) {
  const id = pixivArtworkId(pageUrl);
  if (!id) return null;
  const tags = (body?.tags?.tags || []).map(t => String(t?.tag ?? "")).filter(Boolean);
  return {
    site: "pixiv", page_url: `https://www.pixiv.net/artworks/${id}`, post_id: id,
    title: String(body?.illustTitle ?? ""),
    artist: String(body?.userName ?? ""),
    artist_id: String(body?.userId ?? ""),
    x_restrict: Number(body?.xRestrict ?? 0),
    page_count: Number(body?.pageCount ?? 0),
    tags, tag_string: tags.join(" "),
    original_url: String(body?.urls?.original ?? ""),
    scraped_at: Date.now(),
  };
}
