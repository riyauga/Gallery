/** Pinterest 的 pin 身份：pin id 来自页面地址或卡片链接，图片只认 hash。 */

export function isPinterestPage(value) {
  try {
    const u = new URL(value);
    return u.protocol === "https:" && /(?:^|\.)pinterest\.[a-z.]+$/.test(u.hostname);
  } catch { return false; }
}

export function pinterestPinId(value) {
  if (!isPinterestPage(value)) return "";
  const m = /^\/pin\/(\d+)(?:\/|$)/.exec(new URL(value).pathname);
  return m ? m[1] : "";
}

/** i.pinimg.com/{236x|474x|736x|originals}/{aa}/{bb}/{cc}/{hash}.jpg → hash */
export function pinImageHash(value) {
  try {
    const u = new URL(value);
    if (u.protocol !== "https:" || !/(?:^|\.)pinimg\.com$/.test(u.hostname)) return "";
    const m = /^\/[^/]+\/(?:[0-9a-f]{2}\/){3}([0-9a-f]{16,})\.[a-z]+$/i.exec(u.pathname);
    return m ? m[1].toLowerCase() : "";
  } catch { return ""; }
}

export function isPinterestDownload(item) {
  return isPinterestPage(item?.referrer) || [item?.url, item?.finalUrl].some(url => !!pinImageHash(url));
}

/**
 * 页面里的一条 pin（__PWS_INITIAL_PROPS__ 的 pins[id]）→ 统一快照。
 * 作者是保存这条 pin 的账号，按用户要求归到画师。
 */
export function pinterestSnapshot(pin, pageUrl = "") {
  const id = String(pin?.id ?? "");
  if (!id) return null;
  const images = pin?.images || {};
  const original = images.orig?.url || images["736x"]?.url || "";
  return {
    site: "pinterest", page_url: `https://www.pinterest.com/pin/${id}/`, post_id: id,
    title: String(pin?.title ?? ""),
    description: String(pin?.description ?? ""),
    link: String(pin?.link ?? ""),
    domain: String(pin?.domain ?? ""),
    board: String(pin?.board?.name ?? ""),
    artist: String(pin?.pinner?.username || pin?.creator?.username || ""),
    image_hash: pinImageHash(original),
    original_url: original,
    scraped_at: Date.now(),
  };
}

/** 从 pin 里取出所有尺寸的 hash，供「按图片反查 pin」用。 */
export function pinHashes(pin) {
  const images = pin?.images || {};
  const hashes = new Set();
  for (const key of Object.keys(images)) {
    const h = pinImageHash(images[key]?.url);
    if (h) hashes.add(h);
  }
  return [...hashes];
}
