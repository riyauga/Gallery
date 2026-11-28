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

/** 原文与译名作为一条记录传递；兼容更新前缓存中的字符串标签。 */
export function pixivSourceTags(tags, ignoredTags = []) {
  const ignored = new Set(ignoredTags.map(normalizeTag));
  const records = new Map();
  for (const value of Array.isArray(tags) ? tags : []) {
    const name = typeof value === "string" ? value.trim() : String(value?.name ?? value?.tag ?? "").trim();
    if (!name) continue;
    const translations = value?.translations ?? value?.translation;
    const key = normalizeTag(name);
    const record = records.get(key) || {name, translations: {}};
    if (translations && typeof translations === "object" && !Array.isArray(translations))
      for (const [language, text] of Object.entries(translations))
        if (typeof text === "string" && text.trim()) record.translations[language] = text.trim();
    records.set(key, record);
  }
  return [...records.values()].filter(record => ![record.name, ...Object.values(record.translations)]
    .some(name => ignored.has(normalizeTag(name))));
}

/** 页面接口返回体 → 统一快照。任何字段缺失都不抛错。 */
export function pixivSnapshot(body, pageUrl) {
  const id = pixivArtworkId(pageUrl);
  if (!id) return null;
  const tags = pixivSourceTags(body?.tags?.tags);
  return {
    site: "pixiv", page_url: `https://www.pixiv.net/artworks/${id}`, post_id: id,
    title: String(body?.illustTitle ?? ""),
    artist: String(body?.userName ?? ""),
    artist_id: String(body?.userId ?? ""),
    x_restrict: Number(body?.xRestrict ?? 0),
    page_count: Number(body?.pageCount ?? 0),
    tags, tag_string: tags.map(t => t.name).join(" "),
    original_url: String(body?.urls?.original ?? ""),
    scraped_at: Date.now(),
  };
}
