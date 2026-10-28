// 用插件自己的 bridge.js 打一次真实往返（只用到 fetch 的那几个导出，不碰 chrome.*）
import { readFileSync } from "node:fs";
import { bridgePing, bridgeVerify, bridgeSend, openDesktop } from "../bridge.js";

const sb = process.argv[2];
const cfg = JSON.parse(readFileSync(sb + "/config/settings.json", "utf8"));
const port = cfg.bridge_port || 47821;
const token = cfg.bridge_token || "";

console.log("ping:", JSON.stringify(await bridgePing(port, token)));
console.log("verify:", JSON.stringify(await bridgeVerify(port, token)));

const record = {
  version: 2,
  ingest_id: "e2e:" + Date.now() + ":1",
  client_id: "ext-e2e",
  download_id: 1,
  download_state: "complete",
  download_key: "dl:1",
  filename: "ext_e2e.png",
  inbox_name: "ext_e2e.png",
  image_url: "https://cdn.donmai.us/original/aa/bb/ext_e2e.png",
  page_url: "https://danbooru.donmai.us/posts/888001",
  post_id: "888001",
  site: "danbooru",
  rule_id: "danbooru",
  tags: { artist: ["ext_artist"], copyright: ["ext_work"], character: ["ext_char"], general: ["solo"] },
  rating: "s",
  source: "https://www.pixiv.net/artworks/1",
  tag_string: "ext_artist solo",
  metadata_complete: true,
  manual_review: false,
  md5: "",
  sent_at: Date.now(),
};

const sent = await bridgeSend(port, token, record);
console.log("send:", JSON.stringify(sent));
const again = await bridgeSend(port, token, record);
console.log("resend(dup):", JSON.stringify(again));
const ui = await openDesktop(port, token, "review", "");
console.log("ui/open:", JSON.stringify(ui));
const bad = await bridgeSend(port, "wrong-token", record);
console.log("bad-token:", JSON.stringify(bad));
