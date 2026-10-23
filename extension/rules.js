/**
 * 规则引擎（纯函数，不引用任何 chrome.* API，因此可以用 Node 直接单测）。
 *
 * 一条规则长这样：
 * {
 *   id: "r1",
 *   name: "Danbooru",                 // 站点显示名，也是默认的目录名与自动标签来源
 *   enabled: true,
 *   matchOn: "referrer",              // referrer | url | filename | host
 *   matchType: "prefix",              // prefix | suffix | contains | exact | wildcard | regex
 *   pattern: "https://danbooru.donmai.us/",
 *   caseSensitive: false,
 *   folder: "{site}/{yyyy}-{mm}",     // 相对 Gallery 根目录的模板
 *   onlyImages: true
 * }
 */

export const IMAGE_EXT_RE =
  /\.(jpe?g|jfif|png|gif|webp|bmp|tiff?|avif|heic|heif|ico|jxl|svg)(?:[?#]|$)/i;

export const DEFAULT_SETTINGS = {
  enabled: true,
  captureAllImages: false,
  galleryRoot: "Gallery", // 相对浏览器“下载”目录
  defaultFolder: "{site}/{yyyy}-{mm}",
  unmatched: "skip", // skip = 不改动 | folder = 归到 _其他/{yyyy}-{mm}
  unmatchedFolder: "_其他/{yyyy}-{mm}",
  maxBatch: 200, // 单次“采集整页图片”上限
  scanBackgroundImages: false, // 扫描 CSS 背景图（慢，默认关）
  conflictAction: "uniquify",
  // 拖拽下载：只允许浏览器“下载”目录下的相对子目录（Chrome 扩展写不到其他盘符）。
  dragTargetDir: "",
  // 拖拽黑洞的停靠位置：right-center | right-top | right-bottom | left-bottom
  dropZoneCorner: "right-center",

  // --- 审核流程（2026-10-14 起停用，字段留作兼容读取）---------------------
  // 旧行为：命中的下载一律落到 <galleryRoot>/<inboxFolder>/，由桌面端审核后归档。
  // 现在：保存位置由浏览器原生「另存为」决定，桌面端按实际落盘的路径接管。
  inboxMode: true,
  inboxFolder: "_inbox",

  // --- 本地桥（桌面端）---------------------------------------------------
  bridgeEnabled: true,
  bridgePort: 47821,
  bridgeToken: "",
  theme: "system",
};

// --------------------------------------------------------------------------- //
// 匹配方式
// --------------------------------------------------------------------------- //

/** [值, 界面标签]，顺序即下拉框顺序 */
export const MATCH_TYPES = [
  ["prefix", "前缀匹配"],
  ["suffix", "后缀匹配"],
  ["contains", "包含关键词"],
  ["exact", "完全匹配"],
  ["wildcard", "通配符（* 任意多个字符，? 单个字符）"],
  ["regex", "正则表达式"],
];

export const MATCH_TYPE_VALUES = MATCH_TYPES.map(([v]) => v);

/** 匹配对象字段 */
export const MATCH_FIELDS = [
  ["referrer", "来源页面 URL"],
  ["url", "文件 / 图片 URL"],
  ["filename", "文件名"],
  ["host", "域名"],
];

const _SAMPLE = {
  referrer: "https://danbooru.donmai.us/posts/123456",
  url: "https://cdn.donmai.us/original/a1/b2/c3d4.jpg",
  filename: "c3d4.jpg",
  host: "danbooru.donmai.us",
};

/** 根据“匹配对象 + 匹配方式”给出输入框的示例提示。 */
export function patternPlaceholder(matchType, matchOn) {
  const sample = _SAMPLE[matchOn] || _SAMPLE.referrer;
  switch (matchType) {
    case "prefix":
      return sample;
    case "suffix":
      if (matchOn === "host") return "donmai.us";
      if (matchOn === "filename") return ".jpg";
      return "/posts/";
    case "contains":
      return matchOn === "host" ? "donmai" : "donmai.us";
    case "exact":
      return matchOn === "filename" ? "c3d4.jpg" : sample;
    case "wildcard":
      return matchOn === "host" ? "*.donmai.us" : "*donmai.us*";
    default:
      return matchOn === "host"
        ? "([^/]+\\.)?danbooru\\.donmai\\.us"
        : "^https?://([^/]+\\.)?danbooru\\.donmai\\.us/";
  }
}

/** 给界面用的一句话说明 */
export function matchTypeHint(matchType) {
  switch (matchType) {
    case "prefix":
      return "只要开头一致就算命中，适合 URL 前缀（如 https://danbooru.donmai.us/）。";
    case "suffix":
      return "只要结尾一致就算命中，常用于域名（donmai.us）或扩展名（.jpg）。";
    case "contains":
      return "只要中间出现这段文字就算命中，最宽松，适合记住一个关键词。";
    case "exact":
      return "整段必须一模一样，最严格。";
    case "wildcard":
      return "用 * 代表任意多个字符、? 代表一个字符，例如 *.donmai.us。";
    default:
      return "功能最强，支持捕获组 $1..$9（可拼进目录模板）。";
  }
}

// --------------------------------------------------------------------------- //
// 开箱可用的示例规则（用户可改可删）
// --------------------------------------------------------------------------- //

/** 每次新增内置规则时把这个数字 +1，升级时会自动补齐缺失的内置规则 */
export const SEED_VERSION = 3;

export const DEFAULT_RULES = [
  {
    id: "builtin-danbooru",
    name: "Danbooru",
    enabled: true,
    matchOn: "referrer",
    matchType: "prefix",
    pattern: "https://danbooru.donmai.us/",
    caseSensitive: false,
    folder: "{site}/{yyyy}-{mm}",
    onlyImages: true,
  },
  {
    // danbooru 的图片实际放在 cdn.donmai.us，有些老帖还在 danbooru.donmai.us/data/。
    // 这里用「包含关键词」兜住两种情况，并写死目录让它和上面那条进同一个文件夹。
    id: "builtin-danbooru-cdn",
    name: "Danbooru 图片 CDN",
    enabled: true,
    matchOn: "url",
    matchType: "regex",
    pattern: "^https://(?:[a-z0-9-]+\\.)?donmai\\.us/",
    caseSensitive: false,
    folder: "Danbooru/{yyyy}-{mm}",
    onlyImages: true,
  },
  {
    id: "builtin-pinterest",
    name: "Pinterest",
    enabled: true,
    matchOn: "referrer",
    matchType: "regex",
    pattern: "^https?://([^/]+\\.)?(pinterest\\.[a-z.]+|pinimg\\.com)/",
    caseSensitive: false,
    folder: "{site}/{yyyy}-{mm}",
    onlyImages: true,
  },
  {
    id: "builtin-x",
    name: "X",
    enabled: true,
    matchOn: "referrer",
    matchType: "regex",
    pattern: "^https?://([^/]+\\.)?(twitter\\.com|x\\.com|twimg\\.com)/",
    caseSensitive: false,
    folder: "",
    onlyImages: true,
  },
  {id:'builtin-x-media',name:'X 图片',enabled:true,matchOn:'url',matchType:'regex',pattern:'^https://pbs\\.twimg\\.com/media/',caseSensitive:false,folder:'',onlyImages:true},
  {
    id: "builtin-pixiv",
    name: "Pixiv",
    enabled: true,
    matchOn: "referrer",
    matchType: "regex",
    pattern: "^https?://([^/]+\\.)?(pixiv\\.net|pximg\\.net)/",
    caseSensitive: false,
    folder: "{site}/{yyyy}-{mm}",
    onlyImages: true,
  },
];

// --------------------------------------------------------------------------- //
// 基础工具
// --------------------------------------------------------------------------- //

/** Windows / macOS / Linux 都不安全的文件名字符 */
const BAD_CHARS_RE = /[<>:"/\\|?*\u0000-\u001f]/g;
const RESERVED_RE = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;

/** 单个路径片段允许的最大长度 */
export const MAX_SEGMENT = 120;

/**
 * 超长文件名截断：**保留扩展名，并且保头保尾**。
 *
 * 不能简单地从尾部切掉 —— danbooru 这类站点的文件名末尾是内容的 md5，
 * 那是全名里唯一保证不重复的部分。切掉它会造成：
 *   1. 扩展名丢失（.jpg 没了，图片软件和缩略图都认不出来）
 *   2. 前缀相同的两个文件撞成同一个名字
 */
export function shortenSegment(name, max = MAX_SEGMENT) {
  const s = String(name ?? "");
  if (s.length <= max) return s;

  // 只有在末尾看起来像扩展名时才把点当作扩展名分隔符
  const dot = s.lastIndexOf(".");
  const hasExt = dot > 0 && s.length - dot <= 12;
  const ext = hasExt ? s.slice(dot) : "";
  const stem = hasExt ? s.slice(0, dot) : s;

  const room = max - ext.length;
  if (room < 10) return stem.slice(0, Math.max(1, max - ext.length)) + ext;

  const head = Math.ceil(room * 0.55);
  const tail = room - head - 1; // 留给中间的 "~"
  const middle = tail > 0 ? "~" + stem.slice(-tail) : "";
  return (stem.slice(0, head) + middle).replace(/[\s.]+$/g, "") + ext;
}

/** 把任意字符串变成安全的单个路径片段。 */
export function sanitizeSegment(seg, fallback = "_") {
  let s = String(seg ?? "")
    .replace(BAD_CHARS_RE, "_")
    .replace(/\s+/g, " ")
    .replace(/^[\s.]+|[\s.]+$/g, "");
  if (!s) s = fallback;
  if (RESERVED_RE.test(s)) s = "_" + s;
  if (s.length > MAX_SEGMENT) {
    s = shortenSegment(s, MAX_SEGMENT);
    if (!s) s = fallback;
  }
  return s;
}

/** 清洗整条相对路径，逐段处理。 */
export function sanitizeRelPath(rel) {
  return String(rel ?? "")
    .split(/[\\/]+/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => sanitizeSegment(s))
    .join("/");
}

export function extOf(nameOrUrl) {
  const s = String(nameOrUrl ?? "").split(/[?#]/)[0];
  const m = /\.([A-Za-z0-9]{1,8})$/.exec(s);
  return m ? m[1].toLowerCase() : "";
}

/** 从任意 URL 里取 basename（已去查询串、已解码）。 */
export function basenameFromUrl(url, fallback = "") {
  try {
    const u = new URL(url);
    const parts = u.pathname.split("/").filter(Boolean);
    const last = parts.length ? parts[parts.length - 1] : "";
    const decoded = safeDecode(last);
    return decoded || fallback;
  } catch {
    return fallback;
  }
}

export function safeDecode(s) {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
}

export function hostOf(url) {
  try {
    return new URL(url).hostname.toLowerCase().replace(/^www\./, "");
  } catch {
    return "";
  }
}

/**
 * 完整主机（**保留 www. 与端口**）。
 * 生成 URL 前缀规则必须用它：hostname 会丢端口、hostOf 会剥掉 www，
 * 拿它们拼出来的前缀永远匹配不到真实 referrer。
 */
export function fullHostOf(url) {
  try {
    return new URL(url).host.toLowerCase(); // host = hostname + port
  } catch {
    return "";
  }
}

/** 从下载项里推断“页面来源”：referrer 优先，其次 url。 */
export function originOf(item) {
  const ref = item?.referrer || "";
  if (ref && !/^about:|^chrome/.test(ref)) return ref;
  return item?.url || "";
}

export function isImageDownload(item) {
  const mime = String(item?.mime || "");
  if (mime.startsWith("image/")) return true;
  const name = String(item?.filename || "") || String(item?.url || "");
  return IMAGE_EXT_RE.test(name);
}

// --------------------------------------------------------------------------- //
// 模板展开
// --------------------------------------------------------------------------- //

export function pad2(n) {
  return String(n).padStart(2, "0");
}

export function dateParts(ts = Date.now()) {
  const d = new Date(ts);
  return {
    yyyy: String(d.getFullYear()),
    mm: pad2(d.getMonth() + 1),
    dd: pad2(d.getDate()),
    HH: pad2(d.getHours()),
    MM: pad2(d.getMinutes()),
    ss: pad2(d.getSeconds()),
  };
}

/**
 * 展开 folder 模板。
 * 支持 {site} {host} {yyyy} {mm} {dd} {HH} {MM} {ss} {ext} {name}
 * 以及 $0（整段命中内容）和 $1..$9（正则捕获组）。
 */
export function expandTemplate(tpl, ctx) {
  const groups = ctx.groups || [];
  const asText = (v) => (v === undefined || v === null ? "" : String(v));

  // 注意：$1 对应 groups[0]；$0 是整段命中内容
  const group = (n) => {
    const i = Number(n);
    if (i === 0) return asText(ctx.fullMatch);
    return asText(groups[i - 1]);
  };

  const named = (key) => {
    switch (key) {
      case "site":
        return asText(ctx.site);
      case "host":
        return asText(ctx.host);
      case "name":
        return asText(ctx.name);
      case "ext":
        return asText(ctx.ext);
      case "match":
        return asText(ctx.fullMatch);
      case "yyyy":
      case "mm":
      case "dd":
      case "HH":
      case "MM":
      case "ss":
        return asText(ctx.date?.[key]);
      default:
        return null; // 未知占位符
    }
  };

  let out = String(tpl ?? "");
  // 1) {$1} —— 花括号包住的捕获组，必须先于 {name} 处理，否则会残留大括号
  out = out.replace(/\{\$(\d+)\}/g, (whole, n) => group(n));
  // 2) 裸 $1
  out = out.replace(/\$(\d+)/g, (whole, n) => group(n));
  // 3) {name} —— 未知的保留原样，方便用户发现拼错
  out = out.replace(/\{(\w+)\}/g, (whole, key) => {
    const v = named(key);
    return v === null ? whole : v;
  });
  return out;
}

// --------------------------------------------------------------------------- //
// 匹配：所有匹配方式最终都编译成一个 RegExp，行为统一
// --------------------------------------------------------------------------- //

const RE_SPECIAL_RE = /[.*+?^${}()|[\]\\]/g;

export function escapeRe(s) {
  return String(s ?? "").replace(RE_SPECIAL_RE, "\\$&");
}

/** 通配符 -> 正则：先全部转义，再把 \* 和 \? 还原成通配语义 */
export function wildcardToReBody(pattern) {
  return escapeRe(pattern).replace(/\\\*/g, ".*").replace(/\\\?/g, ".");
}

/** 规则的区分大小写设置（兼容旧数据里的 flags 字段） */
export function ruleIsCaseSensitive(rule) {
  if (typeof rule?.caseSensitive === "boolean") return rule.caseSensitive;
  if (typeof rule?.flags === "string") return !rule.flags.includes("i");
  return false; // 默认不区分大小写
}

/** 统一的规则规范化（读存储后调用一次即可） */
export function normalizeRule(rule) {
  const r = { ...(rule || {}) };
  if (!MATCH_TYPE_VALUES.includes(r.matchType)) r.matchType = "regex";
  if (!MATCH_FIELDS.some(([v]) => v === r.matchOn)) r.matchOn = "referrer";
  r.caseSensitive = ruleIsCaseSensitive(r);
  if (typeof r.onlyImages !== "boolean") r.onlyImages = true;
  if (typeof r.enabled !== "boolean") r.enabled = true;
  r.name = String(r.name ?? "").trim() || "未命名站点";
  r.pattern = String(r.pattern ?? "");
  return r;
}

export function normalizeRules(rules) {
  return (Array.isArray(rules) ? rules : []).map(normalizeRule);
}

/**
 * 把一条规则编译成 RegExp。
 * 空模式返回 null（**不匹配任何东西**，避免 contains "" 命中全部下载）。
 * 非法正则也返回 null，不让一条坏规则拖垮所有下载。
 */
export function compileMatcher(rule) {
  const type = rule?.matchType || "regex";
  const raw = String(rule?.pattern ?? "");
  if (!raw.trim()) return null;
  const flags = ruleIsCaseSensitive(rule) ? "" : "i";

  try {
    switch (type) {
      case "prefix":
        return new RegExp("^" + escapeRe(raw), flags);
      case "suffix":
        return new RegExp(escapeRe(raw) + "$", flags);
      case "contains":
        return new RegExp(escapeRe(raw), flags);
      case "exact":
        return new RegExp("^" + escapeRe(raw) + "$", flags);
      case "wildcard":
        return new RegExp("^" + wildcardToReBody(raw) + "$", flags);
      case "regex":
      default:
        return new RegExp(raw, flags);
    }
  } catch {
    return null;
  }
}

/** 旧名字，保留兼容 */
export const compileRule = compileMatcher;

/** 规则是否语法有效（给界面实时提示用） */
export function ruleError(rule) {
  if (!String(rule?.pattern ?? "").trim()) return "匹配内容不能为空";
  if ((rule?.matchType || "regex") !== "regex") return "";
  try {
    new RegExp(rule.pattern, ruleIsCaseSensitive(rule) ? "" : "i");
    return "";
  } catch (e) {
    return "正则无效：" + (e?.message || e);
  }
}

/**
 * 用一条规则去测一个下载项。
 * @returns {{ok: boolean, groups: string[], matched: string}}
 */
export function testRule(rule, item) {
  const re = compileMatcher(rule);
  if (!re) return { ok: false, groups: [], matched: "" };

  const candidates = {
    referrer: () => [item?.referrer || ""],
    url: () => [item?.url || "", item?.finalUrl || ""],
    filename: () => [item?.filename || "", basenameFromUrl(item?.url || "")],
    host: () => [hostOf(item?.referrer || ""), hostOf(item?.url || "")],
  };

  const getter = candidates[rule?.matchOn] || candidates.referrer;
  for (const text of getter()) {
    if (!text) continue;
    const m = re.exec(text);
    if (m) {
      return {
        ok: true,
        groups: m.slice(1).map((g) => (g === undefined ? "" : g)),
        matched: m[0],
      };
    }
  }
  return { ok: false, groups: [], matched: "" };
}

/** 按顺序找出第一条命中的启用规则。 */
export function findRule(rules, item) {
  for (const rule of rules || []) {
    if (!rule || rule.enabled === false) continue;
    if (rule.onlyImages !== false && !isImageDownload(item)) continue;
    const res = testRule(rule, item);
    if (res.ok) return { rule, groups: res.groups, matched: res.matched };
  }
  return null;
}

// --------------------------------------------------------------------------- //
// 目标路径
// --------------------------------------------------------------------------- //

/**
 * 计算下载项的最终相对路径（相对浏览器「下载」目录）。
 *
 * 2026-10-14 起不再用它决定保存位置 —— 下载位置交给浏览器原生「另存为」对话框。
 * 保留实现是因为它承载着「规则 → 目录模板」这套映射，规则测试也还在覆盖。
 *
 * @returns {string|null} 形如 "Gallery/Danbooru/2026-01/xxx.jpg"；不改动时返回 null
 */
export function buildTarget(item, matched, settings = DEFAULT_SETTINGS) {
  const cfg = { ...DEFAULT_SETTINGS, ...(settings || {}) };

  let rule = null;
  let groups = [];
  let fullMatch = "";
  if (matched) {
    rule = matched.rule || null;
    groups = matched.groups || [];
    fullMatch = matched.matched || "";
  }
  if (!rule && cfg.unmatched !== "folder") return null;
  if (rule?.id?.startsWith('builtin-x')) return null;

  const origin = originOf(item);
  const siteRaw = rule ? rule.name : "_其他";
  const ctx = {
    site: siteRaw,
    host: hostOf(origin),
    name: "",
    ext: "",
    groups,
    fullMatch,
    date: dateParts(),
  };

  // 原始文件名
  let base = "";
  if (item?.filename) {
    const norm = String(item.filename).replace(/\\/g, "/");
    base = norm.split("/").filter(Boolean).pop() || "";
  }
  if (!base) base = basenameFromUrl(item?.url || "");
  if (!base) base = `image-${Date.now()}`;
  base = sanitizeSegment(safeDecode(base), "image");

  const dot = base.lastIndexOf(".");
  ctx.name = dot > 0 ? base.slice(0, dot) : base;
  ctx.ext = dot > 0 ? base.slice(dot + 1).toLowerCase() : extOf(item?.url || "");

  let folder;
  if (rule && cfg.inboxMode) {
    // 审核流程：命中的下载一律先进待审区，落点由桌面端审核后决定。
    // 规则里的 folder 模板此时不再参与（它只决定“要不要拦”）。
    folder = sanitizeRelPath(cfg.inboxFolder || "_inbox") || "_inbox";
  } else {
    const tpl = rule ? rule.folder || cfg.defaultFolder : cfg.unmatchedFolder;
    folder = sanitizeRelPath(expandTemplate(tpl, ctx));
  }

  const root = sanitizeRelPath(cfg.galleryRoot || "Gallery") || "Gallery";
  const name = sanitizeSegment(base, "image");

  return folder ? `${root}/${folder}/${name}` : `${root}/${name}`;
}

/** 判断一个路径是否落在待审区里。 */
export function isInsideInbox(relPath, settings = DEFAULT_SETTINGS) {
  const cfg = { ...DEFAULT_SETTINGS, ...(settings || {}) };
  const root = sanitizeRelPath(cfg.galleryRoot || "Gallery") || "Gallery";
  const inbox = sanitizeRelPath(cfg.inboxFolder || "_inbox") || "_inbox";
  const norm = String(relPath || "").replace(/\\/g, "/").replace(/^\/+/, "");
  return norm === `${root}/${inbox}` || norm.startsWith(`${root}/${inbox}/`);
}

/** 判断一个路径是否已经在我们管理的目录里（避免自己下载的东西被二次加工）。 */
export function isInsideGallery(relPath, settings = DEFAULT_SETTINGS) {
  const cfg = { ...DEFAULT_SETTINGS, ...(settings || {}) };
  const root = sanitizeRelPath(cfg.galleryRoot || "Gallery");
  const norm = String(relPath || "").replace(/\\/g, "/").replace(/^\/+/, "");
  return norm === root || norm.startsWith(root + "/");
}

// --------------------------------------------------------------------------- //
// 从 URL 猜规则（给“为此站点创建规则”用）
// --------------------------------------------------------------------------- //
export function ruleFromUrl(url, name) {
  const host = fullHostOf(url);
  if (!host) return null;

  let proto = "https:";
  try {
    const p = new URL(url).protocol;
    if (p === "http:" || p === "https:") proto = p;
  } catch {
    /* 用默认值 */
  }

  return {
    id: newRuleId(),
    // 显示名用去掉 www 的短域名，看着干净；pattern 里保留 www/端口 才是关键
    name: (name || "").trim() || hostOf(url) || host,
    enabled: true,
    matchOn: "referrer",
    matchType: "prefix",
    pattern: `${proto}//${host}/`,
    caseSensitive: false,
    folder: "{site}/{yyyy}-{mm}",
    onlyImages: true,
  };
}

export function newRuleId() {
  return `r-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
}
