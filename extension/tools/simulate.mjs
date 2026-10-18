#!/usr/bin/env node
/**
 * 规则模拟器：不装插件也能验证「这条规则会不会命中、最终存到哪」。
 *
 *   node extension/tools/simulate.mjs                     # 跑内置的示例场景
 *   node extension/tools/simulate.mjs --show-rules        # 只列出规则
 *   node extension/tools/simulate.mjs \
 *        --referrer "https://danbooru.donmai.us/posts/123" \
 *        --url "https://cdn.donmai.us/original/a/b/c.jpg" \
 *        --filename "c.jpg"
 *
 * 默认用 rules.js 里的内置规则；也可以用 --rules <json 文件> 载入浏览器里
 * 导出的规则（插件设置页 →「导出设置与规则」）。
 */

import { readFileSync } from "node:fs";

import {
  DEFAULT_RULES,
  DEFAULT_SETTINGS,
  buildTarget,
  findRule,
  normalizeRules,
} from "../rules.js";

const DEMO_CASES = [
  {
    label: "Danbooru 帖子页 + CDN 图（最常见的正常情况）",
    item: {
      referrer: "https://danbooru.donmai.us/posts/1234567",
      url: "https://cdn.donmai.us/original/a1/b2/c3d4e5f6a7b8.jpg",
      filename: "c3d4e5f6a7b8.jpg",
      mime: "image/jpeg",
    },
  },
  {
    label: "从图片直链打开、没有 referrer（靠 CDN 规则兜住）",
    item: {
      referrer: "",
      url: "https://cdn.donmai.us/original/a1/b2/c3d4e5f6a7b8.jpg",
      filename: "c3d4e5f6a7b8.jpg",
      mime: "image/jpeg",
    },
  },
  {
    label: "老帖的 /data/ 直链",
    item: {
      referrer: "",
      url: "https://danbooru.donmai.us/data/original/ab/cd/abcdef.jpg",
      filename: "abcdef.jpg",
      mime: "image/jpeg",
    },
  },
  {
    label: "Danbooru 上的非图片文件（zip 图包）——只处理图片，应当放行",
    item: {
      referrer: "https://danbooru.donmai.us/posts/1",
      url: "https://cdn.donmai.us/original/x.zip",
      filename: "x.zip",
      mime: "application/zip",
    },
  },
  {
    label: "像但不是的域名（应当不命中）",
    item: {
      referrer: "https://donmai.us.evil.com/x",
      url: "https://donmai.us.evil.com/a.jpg",
      filename: "a.jpg",
      mime: "image/jpeg",
    },
  },
  {
    label: "完全无关的站点",
    item: {
      referrer: "https://example.com/gallery",
      url: "https://example.com/a.jpg",
      filename: "a.jpg",
      mime: "image/jpeg",
    },
  },
];

function parseArgs(argv) {
  const out = { settings: { ...DEFAULT_SETTINGS }, rules: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case "--referrer":
      case "-r":
        out.referrer = next();
        break;
      case "--url":
      case "-u":
        out.url = next();
        break;
      case "--filename":
      case "-f":
        out.filename = next();
        break;
      case "--mime":
        out.mime = next();
        break;
      case "--rules": {
        const raw = JSON.parse(readFileSync(next(), "utf-8"));
        out.rules = normalizeRules(Array.isArray(raw) ? raw : raw.rules || []);
        break;
      }
      case "--gallery-root":
        out.settings.galleryRoot = next();
        break;
      case "--show-rules":
        out.showRules = true;
        break;
      case "--help":
      case "-h":
        out.help = true;
        break;
      default:
        break;
    }
  }
  return out;
}

function showRules(rules) {
  console.log(`\n当前规则（${rules.length} 条，自上而下匹配，命中第一条即生效）：`);
  for (const r of rules) {
    const mark = r.enabled === false ? "✗" : "✓";
    console.log(
      `  ${mark} ${String(r.name).padEnd(20)} [${r.matchOn} · ${r.matchType}]  ${r.pattern}`,
    );
    console.log(`  ${" ".repeat(22)} -> ${r.folder}`);
  }
}

function run(label, item, rules, settings) {
  const matched = findRule(rules, item);
  const target = buildTarget(item, matched, settings);

  console.log(`\n● ${label}`);
  console.log(`  来源页 : ${item.referrer || "(空)"}`);
  console.log(`  图片   : ${item.url || "(空)"}`);
  if (matched) {
    const r = matched.rule;
    console.log(`  命中   : 「${r.name}」 [${r.matchOn} · ${r.matchType}] ${r.pattern}`);
    console.log(`  命中段 : ${matched.matched}`);
  } else {
    console.log(
      `  命中   : 无  ->  ${
        settings.unmatched === "folder"
          ? "归入「未命中」目录"
          : "不干预，留在浏览器默认下载位置"
      }`,
    );
  }
  console.log(`  存到   : ${target ? "下载/" + target : "（不改写）"}`);
}

function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.help) {
    console.log(
      "用法: node extension/tools/simulate.mjs [--referrer URL] [--url URL] [--filename NAME]\n" +
        "                                    [--mime TYPE] [--rules 导出的规则.json]\n" +
        "                                    [--gallery-root 目录名] [--show-rules]",
    );
    return 0;
  }

  const rules = args.rules || normalizeRules(DEFAULT_RULES);
  const settings = args.settings;

  if (args.showRules) showRules(rules);

  if (args.referrer || args.url || args.filename) {
    run(
      "命令行传入的用例",
      {
        referrer: args.referrer || "",
        url: args.url || "",
        filename: args.filename || "",
        mime: args.mime || "",
      },
      rules,
      settings,
    );
    console.log("");
    return 0;
  }

  if (!args.showRules) showRules(rules);

  console.log("\n" + "=".repeat(76));
  for (const c of DEMO_CASES) run(c.label, c.item, rules, settings);
  console.log("\n" + "=".repeat(76));
  console.log("\n想换成自己的规则：插件设置页 →「导出设置与规则」，然后");
  console.log("  node extension/tools/simulate.mjs --rules gallery-rules-xxx.json");
  console.log("");
  return 0;
}

process.exit(main());
