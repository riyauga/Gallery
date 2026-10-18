/**
 * 规则引擎单测：node --test extension/tests/rules.test.mjs
 * 不依赖任何浏览器 API。
 */
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  DEFAULT_RULES,
  DEFAULT_SETTINGS,
  MATCH_TYPES,
  MATCH_TYPE_VALUES,
  basenameFromUrl,
  buildTarget,
  compileMatcher,
  expandTemplate,
  extOf,
  findRule,
  hostOf,
  isImageDownload,
  isInsideGallery,
  isInsideInbox,
  normalizeRule,
  normalizeRules,
  originOf,
  patternPlaceholder,
  ruleError,
  ruleFromUrl,
  safeDecode,
  sanitizeRelPath,
  sanitizeSegment,
  testRule,
  wildcardToReBody,
} from "../rules.js";

const S = { ...DEFAULT_SETTINGS };
/** 关闭审核流程时的旧行为：按站点规则直接算目标路径 */
const LEGACY = { ...DEFAULT_SETTINGS, inboxMode: false };

/** 当前年月，用来断言目录模板展开 */
function thisYm() {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}`;
}

// --------------------------------------------------------------------------- //
// 基础工具
// --------------------------------------------------------------------------- //
test("sanitizeSegment 清理非法字符并兜底", () => {
  assert.equal(sanitizeSegment('a<b>c:d"e/f\\g|h?i*j'), "a_b_c_d_e_f_g_h_i_j");
  assert.equal(sanitizeSegment("   "), "_");
  assert.equal(sanitizeSegment(""), "_");
  assert.equal(sanitizeSegment("...trailing..."), "trailing");
  assert.equal(sanitizeSegment("CON"), "_CON"); // Windows 保留名
  assert.equal(sanitizeSegment("中文站点"), "中文站点");
});

// --------------------------------------------------------------------------- //
// 真实 danbooru 文件名（用户实际下载到的那个）
// --------------------------------------------------------------------------- //
const REAL_DANBOORU_NAME =
  "__eous_and_belle_zenless_zone_zero_drawn_by_shengkai167__f48edf42392e5115064303c6cf053779.jpg";

test("真实的 danbooru 文件名原样保留，一个字符都不动", () => {
  // 结构: "__"(2) + 标签段(53) + "__"(2) + md5(32) + ".jpg"(4) = 93
  assert.equal(REAL_DANBOORU_NAME.length, 93);
  assert.equal(sanitizeSegment(REAL_DANBOORU_NAME), REAL_DANBOORU_NAME);
  // 开头的双下划线不能被当成“首尾的点/空格”清掉
  assert.ok(sanitizeSegment(REAL_DANBOORU_NAME).startsWith("__"));
  assert.ok(sanitizeSegment(REAL_DANBOORU_NAME).endsWith(".jpg"));
});

test("能从中拆出标签段、32 位 md5，并反推 CDN 原图地址", () => {
  const cut = REAL_DANBOORU_NAME.lastIndexOf("__");
  const tagPart = REAL_DANBOORU_NAME.slice(2, cut);
  const md5 = REAL_DANBOORU_NAME.slice(cut + 2, REAL_DANBOORU_NAME.lastIndexOf("."));

  assert.equal(tagPart, "eous_and_belle_zenless_zone_zero_drawn_by_shengkai167");
  assert.match(md5, /^[0-9a-f]{32}$/); // md5 一定是 32 位小写 hex
  assert.equal(md5, "f48edf42392e5115064303c6cf053779");

  // danbooru 的磁盘路径规则：md5 前两位 / 再两位 / md5.ext
  const cdnPath = `original/${md5.slice(0, 2)}/${md5.slice(2, 4)}/${md5}.jpg`;
  assert.equal(cdnPath, "original/f4/8e/f48edf42392e5115064303c6cf053779.jpg");
});

test("真实 danbooru 文件按内置规则落到 Danbooru/年-月/", () => {
  const item = {
    referrer: "https://danbooru.donmai.us/posts/1234567",
    url: "https://cdn.donmai.us/original/f4/8e/f48edf42392e5115064303c6cf053779.jpg",
    filename: REAL_DANBOORU_NAME,
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  assert.ok(matched);
  const target = buildTarget(item, matched, LEGACY);
  assert.equal(target, `Gallery/Danbooru/${thisYm()}/${REAL_DANBOORU_NAME}`);
  // 文件名里的 md5 必须完整出现在结果里 —— 它是全名里唯一不重复的部分
  assert.ok(target.includes("f48edf42392e5115064303c6cf053779"));
  assert.ok(target.endsWith(".jpg"));
});

test("超长文件名：保留扩展名，并保住尾巴上的 md5", () => {
  const md5 = "a".repeat(31) + "f";
  const long = `${"x".repeat(160)}_drawn_by_someone__${md5}.jpg`;
  assert.ok(long.length > 200);

  const out = sanitizeSegment(long, 120);
  assert.ok(out.length <= 120, `截断后仍超长: ${out.length}`);
  assert.ok(out.endsWith(".jpg"), "扩展名必须保留");
  assert.ok(out.includes(md5), "尾部 md5 必须保留（否则不同文件会撞名）");
  assert.ok(out.includes("~"), "中间用 ~ 表示省略");

  // 老实现是 s.slice(0, 120)，会把扩展名和 md5 一起切掉 —— 这里做个回归断言
  const naive = long.slice(0, 120);
  assert.ok(!naive.endsWith(".jpg"));
  assert.ok(!naive.includes(md5));

  // 没有扩展名的情况
  const noExt = sanitizeSegment("y".repeat(200), 120);
  assert.equal(noExt.length, 120);
});

test("截断后前缀不同、只有尾部不同的两个文件不会撞名", () => {
  const stem = "z".repeat(200);
  const a = sanitizeSegment(`${stem}__${"1".repeat(32)}.jpg`, 120);
  const b = sanitizeSegment(`${stem}__${"2".repeat(32)}.jpg`, 120);
  assert.notEqual(a, b);
});

test("sanitizeRelPath 逐段清洗", () => {
  assert.equal(sanitizeRelPath("微博/2026-01"), "微博/2026-01");
  assert.equal(sanitizeRelPath("a//b/../c"), "a/b/_/c"); // ".." 被兜底成 "_"
  assert.equal(sanitizeRelPath("a\\b\\c"), "a/b/c");
});

test("extOf / basenameFromUrl / hostOf", () => {
  assert.equal(extOf("https://x.com/a.JPG?w=100"), "jpg");
  assert.equal(extOf("noext"), "");
  assert.equal(basenameFromUrl("https://x.com/p/hello%20world.png?q=1"), "hello world.png");
  assert.equal(basenameFromUrl("not a url"), "");
  assert.equal(hostOf("https://www.Pixiv.NET/artworks/1"), "pixiv.net");
  assert.equal(hostOf("garbage"), "");
  assert.equal(safeDecode("%E4%B8%AD"), "中");
  assert.equal(safeDecode("%E4%B8"), "%E4%B8"); // 不完整序列不抛异常
});

test("isImageDownload 同时看 MIME 与扩展名", () => {
  assert.ok(isImageDownload({ mime: "image/png", filename: "x" }));
  assert.ok(isImageDownload({ mime: "", filename: "a/b/photo.JPEG" }));
  assert.ok(isImageDownload({ mime: "", url: "https://x/a.webp?w=2" }));
  assert.ok(!isImageDownload({ mime: "text/html", filename: "a.html" }));
  assert.ok(!isImageDownload({ mime: "", filename: "a.zip" }));
});

test("originOf 优先 referrer，退化到 url", () => {
  assert.equal(originOf({ referrer: "https://a.com/p", url: "https://cdn/a.jpg" }), "https://a.com/p");
  assert.equal(originOf({ referrer: "", url: "https://cdn/a.jpg" }), "https://cdn/a.jpg");
  assert.equal(originOf({ referrer: "about:blank", url: "https://cdn/a.jpg" }), "https://cdn/a.jpg");
});

// --------------------------------------------------------------------------- //
// 模板展开
// --------------------------------------------------------------------------- //
test("expandTemplate 展开日期与捕获组", () => {
  const ctx = {
    site: "微博",
    host: "weibo.com",
    name: "pic",
    ext: "jpg",
    date: { yyyy: "2026", mm: "01", dd: "05", HH: "09", MM: "08", ss: "07" },
    groups: ["weibo", "cn"],
    fullMatch: "weibo.cn",
  };
  assert.equal(expandTemplate("{site}/{yyyy}-{mm}", ctx), "微博/2026-01");
  assert.equal(expandTemplate("$1/$2-{dd}", ctx), "weibo/cn-05");
  assert.equal(expandTemplate("{$1}/{$2}", ctx), "weibo/cn"); // 花括号包捕获组
  assert.equal(expandTemplate("{unknown}", ctx), "{unknown}"); // 未知占位符保留
  assert.equal(expandTemplate("{$9}", ctx), ""); // 越界捕获组 -> 空
});

test("$0 与 {match} 展开为整段命中内容", () => {
  const ctx = {
    site: "S",
    host: "h",
    date: {},
    groups: ["g1"],
    fullMatch: "FULL",
  };
  assert.equal(expandTemplate("$0", ctx), "FULL");
  assert.equal(expandTemplate("{$0}", ctx), "FULL");
  assert.equal(expandTemplate("{match}", ctx), "FULL");
  assert.equal(expandTemplate("$1", ctx), "g1");
  assert.equal(expandTemplate("$0/$1", ctx), "FULL/g1");
});

// --------------------------------------------------------------------------- //
// 匹配方式
// --------------------------------------------------------------------------- //
const modeRule = (matchType, pattern, extra = {}) => ({
  id: "m",
  name: "M",
  enabled: true,
  matchOn: "referrer",
  matchType,
  pattern,
  caseSensitive: false,
  folder: "{site}",
  onlyImages: false,
  ...extra,
});

test("MATCH_TYPES 覆盖 6 种模式", () => {
  assert.deepEqual(MATCH_TYPE_VALUES, [
    "prefix",
    "suffix",
    "contains",
    "exact",
    "wildcard",
    "regex",
  ]);
  assert.equal(MATCH_TYPES.length, 6);
});

test("前缀匹配：只认开头", () => {
  const r = modeRule("prefix", "https://danbooru.donmai.us/");
  assert.ok(testRule(r, { referrer: "https://danbooru.donmai.us/posts/1" }).ok);
  assert.ok(!testRule(r, { referrer: "https://evil.com/?u=https://danbooru.donmai.us/" }).ok);
  assert.ok(!testRule(r, { referrer: "https://beta.danbooru.donmai.us/posts/1" }).ok);
});

test("后缀匹配：只认结尾", () => {
  const byExt = modeRule("suffix", ".jpg", { matchOn: "filename" });
  assert.ok(testRule(byExt, { filename: "a/b/photo.JPG" }).ok); // 默认不区分大小写
  assert.ok(!testRule(byExt, { filename: "photo.png" }).ok);

  const byDomain = modeRule("suffix", "donmai.us", { matchOn: "host" });
  assert.ok(testRule(byDomain, { url: "https://cdn.donmai.us/x.jpg" }).ok);
  assert.ok(!testRule(byDomain, { url: "https://donmai.us.evil.com/x.jpg" }).ok);
});

test("包含关键词：最宽松，中间出现即可", () => {
  const r = modeRule("contains", "donmai.us/", { matchOn: "url" });
  assert.ok(testRule(r, { url: "https://cdn.donmai.us/original/x.jpg" }).ok);
  assert.ok(testRule(r, { url: "https://danbooru.donmai.us/data/x.jpg" }).ok);
  assert.ok(!testRule(r, { url: "https://example.com/x.jpg" }).ok);
});

test("完全匹配：整段一模一样", () => {
  const r = modeRule("exact", "https://a.com/p/1");
  assert.ok(testRule(r, { referrer: "https://a.com/p/1" }).ok);
  assert.ok(!testRule(r, { referrer: "https://a.com/p/12" }).ok);
  assert.ok(!testRule(r, { referrer: "xhttps://a.com/p/1" }).ok);
});

test("通配符：* 任意多个字符，? 单个字符", () => {
  const r = modeRule("wildcard", "https://*.donmai.us/*");
  assert.ok(testRule(r, { referrer: "https://danbooru.donmai.us/posts/1" }).ok);
  assert.ok(testRule(r, { referrer: "https://cdn.donmai.us/a/b.jpg" }).ok);
  assert.ok(!testRule(r, { referrer: "https://donmai.us.evil.com/x" }).ok);

  const q = modeRule("wildcard", "img_??.jpg", { matchOn: "filename" });
  assert.ok(testRule(q, { filename: "img_01.jpg" }).ok);
  assert.ok(!testRule(q, { filename: "img_1.jpg" }).ok);

  assert.equal(wildcardToReBody("a*b?c"), "a.*b.c");
});

test("正则模式仍然工作（含捕获组与整段命中）", () => {
  const r = modeRule("regex", "^https?://([^/]+\\.)?pixiv\\.net/users/(\\d+)");
  const hit = testRule(r, { referrer: "https://www.pixiv.net/users/12345" });
  assert.equal(hit.ok, true);
  assert.deepEqual(hit.groups, ["www.", "12345"]);
  assert.equal(hit.matched, "https://www.pixiv.net/users/12345");
});

test("区分大小写开关对所有模式生效", () => {
  const loose = modeRule("exact", "ABC", { caseSensitive: false });
  const strict = modeRule("exact", "ABC", { caseSensitive: true });
  assert.ok(testRule(loose, { referrer: "abc" }).ok);
  assert.ok(!testRule(strict, { referrer: "abc" }).ok);
  assert.ok(testRule(strict, { referrer: "ABC" }).ok);

  assert.ok(!testRule(modeRule("regex", "^abc$", { caseSensitive: true }), { referrer: "ABC" }).ok);
  assert.ok(testRule(modeRule("prefix", "ABC", { caseSensitive: true }), { referrer: "ABCdef" }).ok);
  assert.ok(!testRule(modeRule("prefix", "ABC", { caseSensitive: true }), { referrer: "abcdef" }).ok);
});

test("字面量模式里的正则元字符按普通字符处理", () => {
  const r = modeRule("exact", "a.b(c)+[d]");
  assert.ok(testRule(r, { referrer: "a.b(c)+[d]" }).ok);
  assert.ok(!testRule(r, { referrer: "axbcd" }).ok);
});

test("空模式不匹配任何东西（避免 contains 空串命中全部下载）", () => {
  for (const t of MATCH_TYPE_VALUES) {
    const r = modeRule(t, t === "regex" ? "" : "   ");
    assert.equal(
      testRule(r, { referrer: "https://a.com/x", filename: "x.jpg", url: "https://a.com/x.jpg" }).ok,
      false,
      `${t} 空模式不应命中`,
    );
    assert.equal(compileMatcher(r), null, `${t} 空模式应编译为 null`);
  }
});

test("坏正则不抛异常，只是不命中，并且 ruleError 能报出来", () => {
  const bad = modeRule("regex", "([unclosed");
  assert.doesNotThrow(() => findRule([bad], { referrer: "https://a.com/x", filename: "a.jpg" }));
  assert.equal(findRule([bad], { referrer: "https://a.com/x", filename: "a.jpg" }), null);
  assert.ok(ruleError(bad).startsWith("正则无效"));

  // 非正则模式下，任何字面量都是合法的
  assert.equal(ruleError(modeRule("prefix", "([unclosed")), "");
  assert.equal(ruleError(modeRule("prefix", "")), "匹配内容不能为空");
});

test("缺少 matchType 的老规则按正则处理（向后兼容）", () => {
  const legacy = {
    id: "old",
    name: "旧规则",
    enabled: true,
    matchOn: "referrer",
    pattern: "^https?://old\\.com/",
    flags: "i",
    folder: "{site}",
    onlyImages: true,
  };
  const n = normalizeRule(legacy);
  assert.equal(n.matchType, "regex");
  assert.equal(n.caseSensitive, false); // flags "i" => 不区分大小写
  assert.ok(testRule(legacy, { referrer: "https://old.com/a" }).ok);

  // 老数据里 flags 没有 i，就是区分大小写
  assert.equal(normalizeRule({ ...legacy, flags: "" }).caseSensitive, true);
});

test("normalizeRule 修补残缺字段", () => {
  const n = normalizeRule({ id: "x", pattern: "p" });
  assert.equal(n.matchOn, "referrer");
  assert.equal(n.matchType, "regex");
  assert.equal(n.caseSensitive, false);
  assert.equal(n.enabled, true);
  assert.equal(n.onlyImages, true);
  assert.equal(n.name, "未命名站点");

  const n2 = normalizeRule({ name: "  ", matchOn: "瞎写", matchType: "瞎写" });
  assert.equal(n2.matchOn, "referrer");
  assert.equal(n2.matchType, "regex");
  assert.equal(n2.name, "未命名站点");

  assert.deepEqual(normalizeRules(null), []);
  assert.equal(normalizeRules([{ id: "a" }, { id: "b" }]).length, 2);
});

// --------------------------------------------------------------------------- //
// findRule 顺序与 onlyImages
// --------------------------------------------------------------------------- //
const RULES = [
  modeRule("regex", "^https?://([^/]+\\.)?(pixiv\\.net|pximg\\.net)/", {
    id: "pixiv",
    name: "Pixiv",
    folder: "{site}/{yyyy}-{mm}",
    onlyImages: true,
  }),
  modeRule("contains", "cdn.example.com", {
    id: "generic-cdn",
    name: "CDN",
    matchOn: "url",
    folder: "杂项",
    onlyImages: true,
  }),
  modeRule("contains", "", { id: "disabled", name: "已禁用", enabled: false, folder: "不该命中" }),
];

test("findRule 跳过禁用规则、命中第一条", () => {
  const hit = findRule(RULES, {
    referrer: "https://www.pixiv.net/x",
    url: "https://i.pximg.net/a.jpg",
  });
  assert.equal(hit.rule.id, "pixiv");

  const hit2 = findRule(RULES, { referrer: "", url: "https://cdn.example.com/a.png" });
  assert.equal(hit2.rule.id, "generic-cdn");
});

test("findRule 对非图片不命中（onlyImages）", () => {
  const hit = findRule(RULES, {
    referrer: "https://www.pixiv.net/x",
    url: "https://a.com/f.zip",
    filename: "f.zip",
    mime: "application/zip",
  });
  assert.equal(hit, null);
});

// --------------------------------------------------------------------------- //
// 目标路径
// --------------------------------------------------------------------------- //
test("buildTarget 命中规则时归到站点/年月目录（关闭审核流程时）", () => {
  const item = {
    referrer: "https://www.pixiv.net/artworks/123",
    url: "https://i.pximg.net/img/2026/01/abc.jpg",
    filename: "abc.jpg",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(RULES, item), LEGACY);
  assert.equal(target, `Gallery/Pixiv/${thisYm()}/abc.jpg`);
});

test("buildTarget 保留原始文件名（含中文与空格）", () => {
  const item = {
    referrer: "https://www.pixiv.net/a",
    url: "https://i.pximg.net/x",
    filename: "我的 图 01.png",
    mime: "image/png",
  };
  const target = buildTarget(item, findRule(RULES, item), S);
  assert.ok(target.endsWith("/我的 图 01.png"), target);
});

test("buildTarget 从 URL 推断文件名（右键保存时没有 filename）", () => {
  const item = {
    referrer: "https://www.pixiv.net/a",
    url: "https://i.pximg.net/img/2026/01/xyz%20abc.jpeg?token=1",
    filename: "",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(RULES, item), S);
  assert.ok(target.endsWith("/xyz abc.jpeg"), target);
});

test("buildTarget 未命中时默认不干预", () => {
  const item = { referrer: "https://unknown.site/p", url: "https://unknown.site/a.jpg", filename: "a.jpg" };
  assert.equal(buildTarget(item, null, S), null);
});

test("buildTarget 未命中且 unmatched=folder 时归到 _其他", () => {
  const cfg = { ...S, unmatched: "folder" };
  const item = { referrer: "https://unknown.site/p", url: "https://unknown.site/a.jpg", filename: "a.jpg" };
  const target = buildTarget(item, null, cfg);
  assert.match(target, /^Gallery\/_其他\/\d{4}-\d{2}\/a\.jpg$/);
});

test("buildTarget 用自定义根目录", () => {
  const cfg = { ...LEGACY, galleryRoot: "图库" };
  const item = {
    referrer: "https://www.pixiv.net/a",
    url: "https://i.pximg.net/i.jpg",
    filename: "i.jpg",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(RULES, item), cfg);
  assert.match(target, /^图库\/Pixiv\/\d{4}-\d{2}\/i\.jpg$/);
});

test("buildTarget 规则未指定目录时回退到 defaultFolder（{host} 可用）", () => {
  const cfg = { ...LEGACY, defaultFolder: "{host}/{yyyy}" };
  const rules = [modeRule("regex", "^https?://([^/]+\\.)?pixiv\\.net/", { folder: "" })];
  const item = {
    referrer: "https://www.pixiv.net/a",
    url: "https://i.pximg.net/i.jpg",
    filename: "i.jpg",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(rules, item), cfg);
  assert.match(target, /^Gallery\/pixiv\.net\/\d{4}\/i\.jpg$/);
});

test("buildTarget 用捕获组构造路径", () => {
  const rules = [
    modeRule("regex", "^https?://([^/]+)/album/(\\d+)", { folder: "{$2}/{yyyy}-{mm}" }),
  ];
  const item = {
    referrer: "https://foo.com/album/42?x=1",
    url: "https://foo.com/i.jpg",
    filename: "i.jpg",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(rules, item), LEGACY);
  assert.match(target, /^Gallery\/42\/\d{4}-\d{2}\/i\.jpg$/);
});

test("buildTarget 用 $0/{match} 构造路径（字面量模式也有东西可用）", () => {
  const rules = [
    modeRule("contains", "album/", { folder: "{match}_{site}", matchOn: "referrer" }),
  ];
  const item = {
    referrer: "https://foo.com/album/42",
    url: "https://foo.com/i.jpg",
    filename: "i.jpg",
    mime: "image/jpeg",
  };
  const m = findRule(rules, item);
  assert.equal(m.matched, "album/");
  const target = buildTarget(item, m, LEGACY);
  // "album/" 里的斜杠会被当作路径分隔符，逐段清洗后再拼回来
  assert.match(target, /^Gallery\/album\/_M\/i\.jpg$/);
});

// --------------------------------------------------------------------------- //
// 审核流程（inboxMode）：命中的下载一律先进待审区
// --------------------------------------------------------------------------- //
test("开启审核流程时，命中的下载全部进 _inbox，不再按规则算目录", () => {
  const item = {
    referrer: "https://danbooru.donmai.us/posts/1234567",
    url: "https://cdn.donmai.us/original/a1/b2/c3d4.jpg",
    filename: "c3d4.jpg",
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  const target = buildTarget(item, matched, S);
  assert.equal(target, `Gallery/${DEFAULT_SETTINGS.inboxFolder}/c3d4.jpg`);
});

test("审核流程下未命中任何规则时依然不干预", () => {
  const item = {
    referrer: "https://unknown.example/p",
    url: "https://unknown.example/a.jpg",
    filename: "a.jpg",
    mime: "image/jpeg",
  };
  assert.equal(buildTarget(item, null, S), null);
});

test("审核流程可以换待审区目录名与图库根目录", () => {
  const cfg = { ...S, galleryRoot: "图库", inboxFolder: "收件箱" };
  const item = {
    referrer: "https://danbooru.donmai.us/posts/1",
    url: "https://cdn.donmai.us/a/b/c.jpg",
    filename: "c.jpg",
    mime: "image/jpeg",
  };
  const target = buildTarget(item, findRule(DEFAULT_RULES, item), cfg);
  assert.equal(target, "图库/收件箱/c.jpg");
});

test("isInsideInbox 认得出待审区，且待审区也算在 Gallery 里", () => {
  assert.ok(isInsideInbox("Gallery/_inbox/a.jpg", S));
  assert.ok(isInsideInbox("Gallery\\_inbox\\a.jpg", S));
  assert.ok(isInsideInbox("Gallery/_inbox", S));
  assert.ok(!isInsideInbox("Gallery/Danbooru/a.jpg", S));
  assert.ok(!isInsideInbox("GalleryX/_inbox/a.jpg", S));
  // 待审区在 Gallery 内部，所以 isInsideGallery 也会拦下，避免二次加工
  assert.ok(isInsideGallery("Gallery/_inbox/a.jpg", S));
});

test("真实 danbooru 文件在审核流程下进待审区", () => {
  const item = {
    referrer: "https://danbooru.donmai.us/posts/1234567",
    url: "https://cdn.donmai.us/original/f4/8e/f48edf42392e5115064303c6cf053779.jpg",
    filename: REAL_DANBOORU_NAME,
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  assert.equal(
    buildTarget(item, matched, S),
    `Gallery/_inbox/${REAL_DANBOORU_NAME}`,
  );
});

// --------------------------------------------------------------------------- //
// Danbooru 场景（用户实际要测的）
// --------------------------------------------------------------------------- //
test("内置 Danbooru 规则可用且已启用", () => {
  const r = DEFAULT_RULES.find((x) => x.id === "builtin-danbooru");
  assert.ok(r, "缺少 builtin-danbooru");
  assert.equal(r.enabled, true);
  assert.equal(r.matchOn, "referrer");
  assert.equal(r.matchType, "prefix");
  assert.equal(r.pattern, "https://danbooru.donmai.us/");
});

test("Danbooru：帖子页下载的图片归到 Danbooru/年-月", () => {
  const item = {
    referrer: "https://danbooru.donmai.us/posts/1234567",
    url: "https://cdn.donmai.us/original/a1/b2/c3d4e5f6.jpg",
    filename: "c3d4e5f6.jpg",
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  assert.equal(matched.rule.id, "builtin-danbooru");
  assert.equal(matched.matched, "https://danbooru.donmai.us/");
  assert.equal(buildTarget(item, matched, LEGACY), `Gallery/Danbooru/${thisYm()}/c3d4e5f6.jpg`);
});

test("Danbooru：没有 referrer 时靠图片 CDN 规则兜住，且落在同一目录", () => {
  const item = {
    referrer: "",
    url: "https://cdn.donmai.us/original/a1/b2/c3d4e5f6.jpg",
    filename: "c3d4e5f6.jpg",
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  assert.equal(matched.rule.id, "builtin-danbooru-cdn");
  assert.equal(buildTarget(item, matched, LEGACY), `Gallery/Danbooru/${thisYm()}/c3d4e5f6.jpg`);
});

test("Danbooru：老帖的 /data/ 直链也归到同一目录", () => {
  const item = {
    referrer: "",
    url: "https://danbooru.donmai.us/data/original/ab/cd/abcd.jpg",
    filename: "abcd.jpg",
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  assert.ok(matched);
  assert.equal(buildTarget(item, matched, LEGACY), `Gallery/Danbooru/${thisYm()}/abcd.jpg`);
});

test("Danbooru：不相关的站点不会被误收", () => {
  const item = {
    referrer: "https://example.com/gallery",
    url: "https://example.com/a.jpg",
    filename: "a.jpg",
    mime: "image/jpeg",
  };
  const matched = findRule(DEFAULT_RULES, item);
  if (matched) {
    assert.ok(!matched.rule.id.startsWith("builtin-danbooru"));
  }
});

// --------------------------------------------------------------------------- //
// 其它
// --------------------------------------------------------------------------- //
test("isInsideGallery 防止自己下载的东西被二次加工", () => {
  assert.ok(isInsideGallery("Gallery/a/b.jpg", S));
  assert.ok(isInsideGallery("Gallery\\a\\b.jpg", S));
  assert.ok(isInsideGallery("Gallery", S));
  assert.ok(!isInsideGallery("GalleryX/a.jpg", S));
  assert.ok(!isInsideGallery("Downloads/a.jpg", S));
});

test("ruleFromUrl 生成前缀规则，且保留 www（否则永远匹配不到真实 referrer）", () => {
  const r = ruleFromUrl("https://www.pixiv.net/artworks/1", "Pixiv");
  assert.equal(r.name, "Pixiv");
  assert.equal(r.matchType, "prefix");
  assert.equal(r.matchOn, "referrer");
  assert.equal(r.pattern, "https://www.pixiv.net/");
  assert.ok(testRule(r, { referrer: "https://www.pixiv.net/artworks/9" }).ok);
  // 前缀是精确的，不带 www 的域名不会命中
  assert.ok(!testRule(r, { referrer: "https://pixiv.net/artworks/9" }).ok);

  // 不传名字时用去掉 www 的短域名当显示名
  assert.equal(ruleFromUrl("https://www.pixiv.net/a", "").name, "pixiv.net");

  // 无 www 的站点
  const d = ruleFromUrl("https://danbooru.donmai.us/posts/1", "Danbooru");
  assert.equal(d.pattern, "https://danbooru.donmai.us/");
  assert.ok(testRule(d, { referrer: "https://danbooru.donmai.us/posts/999" }).ok);

  // http 站点保留 http
  assert.equal(ruleFromUrl("http://old.example.com/a", "Old").pattern, "http://old.example.com/");

  // 带端口
  const p = ruleFromUrl("http://127.0.0.1:8080/x", "本地");
  assert.equal(p.pattern, "http://127.0.0.1:8080/");
  assert.ok(testRule(p, { referrer: "http://127.0.0.1:8080/a/b" }).ok);
});

test("ruleFromUrl 对无效 URL 返回 null", () => {
  assert.equal(ruleFromUrl("not-a-url", "x"), null);
  assert.equal(ruleFromUrl("", "x"), null);
});

test("patternPlaceholder 随匹配方式与对象变化", () => {
  assert.equal(patternPlaceholder("prefix", "host"), "danbooru.donmai.us");
  assert.equal(patternPlaceholder("suffix", "host"), "donmai.us");
  assert.equal(patternPlaceholder("contains", "url"), "donmai.us");
  assert.equal(patternPlaceholder("wildcard", "host"), "*.donmai.us");
  assert.ok(patternPlaceholder("regex", "referrer").includes("^https?://"));
  for (const t of MATCH_TYPE_VALUES) {
    for (const f of ["referrer", "url", "filename", "host"]) {
      assert.equal(typeof patternPlaceholder(t, f), "string");
      assert.ok(patternPlaceholder(t, f).length > 0);
    }
  }
});

test("默认设置与内置规则结构完整", () => {
  for (const k of ["enabled", "galleryRoot", "defaultFolder", "unmatched", "maxBatch"]) {
    assert.ok(k in DEFAULT_SETTINGS, `缺少默认设置 ${k}`);
  }
  assert.ok(Array.isArray(DEFAULT_RULES) && DEFAULT_RULES.length > 0);
  for (const r of normalizeRules(DEFAULT_RULES)) {
    assert.ok(r.id && r.name && r.pattern, `规则字段不全: ${JSON.stringify(r)}`);
    assert.ok(MATCH_TYPE_VALUES.includes(r.matchType), `matchType 非法: ${r.matchType}`);
    assert.equal(ruleError(r), "", `内置规则 ${r.id} 应无语法错误`);
  }
});
