# Gallery 浏览器插件（图片自动归类）

更新时间：2026-10-26 11:42（Asia/Tokyo）；本次文档整理时间，不代表重新运行或验收。

Chrome / Edge（Manifest V3）扩展。它在浏览器里接住图片下载，把来源页、帖子身份、标签和分辨率
整理成一条记录，**通过本地回环端口**推给 Gallery 桌面端，由桌面端做审核与归档。

桌面端只监听 `127.0.0.1`，并要求 `X-Gallery-Token`；端口和令牌都在桌面端
「设置 → 浏览器插件」里显示（可复制令牌）。

## 安装（开发者模式）

1. 打开 `chrome://extensions`（Edge 是 `edge://extensions`），右上角打开「开发者模式」。
2. 点「加载已解压的扩展程序」，选中仓库里的 `extension/` 目录。
3. 点扩展图标 →「设置」，把桌面端「设置 → 浏览器插件」里的**端口**和**令牌**粘进去，
   点「测试连接」，应显示已连接。

## 支持的站点

扩展设置 → 常规新增「接收浏览器下载的所有图片」，默认关闭。开启后所有网站的图片下载均可加入待归档队列；浏览器的保存位置和“另存为”流程保持原样。没有可靠元数据的图片标签、分级留空，交由桌面端审阅；不采集尚未下载的网页图片。修改后请保存设置；已安装的开发者扩展需先在扩展管理页重新加载。

| 站点 | 行为 |
| --- | --- |
| Danbooru 帖子页 / 其 CDN 直链 | 取标签、分级、来源，`inbox_name` 用原名（含 md5 时带上） |
| X / Twitter 帖子页与其 `pbs.twimg.com` 图 | 记录来源页、帖子 id、发帖账号，推文正文进作者评论；分辨率与目录手动填 |
| Pixiv 作品页 / 其 `i.pximg.net` 图 | 调作品页自己的 `/ajax/illust/{id}`（失败退回页面元素），取标题、画师、原图地址、作品说明（进作者评论）；分级全年龄 `g`、R-18 `q`、R-18G `e`；标签只保留库里已有的作品与角色，其余丢弃 |
| Pinterest pin 页与列表页 | 登录态读页面内嵌 pin 数据，未登录退回卡片链接；记 pin 页面、原图地址，作者写进画师，标题与描述不进标签 |
| 其他站点 | 走「接收规则」（设置页可增删、导出/导入） |

规则按 `referrer`（来源页）或 `url`（图片地址）匹配，模板支持 `{site}` `{yyyy}` `{mm}` 等占位符。
不确定某条下载会落到哪，可以用模拟器：

```powershell
node extension/tools/simulate.mjs                       # 内置示例场景
node extension/tools/simulate.mjs --show-rules           # 只看规则
node extension/tools/simulate.mjs --referrer "https://danbooru.donmai.us/posts/123" `
    --url "https://cdn.donmai.us/original/a/b/c.jpg" --filename "c.jpg"
```

## 推送协议（protocol 2）

桌面端 `Services/Bridge/BridgeServer.cs` 实现，插件侧在 `extension/bridge.js`。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/ping` | `{ok, app:"gallery", version, port, tokenRequired, received, pending, protocol:2}` |
| `GET` | `/tags` | 桌面端标签词表 `{works, characters, aliases}`，含中文名与繁体写法；Pixiv 标签按它过滤，缓存 24 小时 |
| `POST` | `/ingest` | 带 `X-Gallery-Token`；`{probe:true}` 只验证不落库；真实记录需 `download_state:"complete"`；返回 `{ok, persisted, queued, duplicate, ingest_id, name}` |
| `POST` | `/ui/open` | `{view:"library"\|"review", path}` 唤起桌面端窗口并定位 |
| `GET` | `/sources` | 待补来源清单 `{total, items:[{md5, name}]}`：只列 `source_url` 为空、且文件名带 danbooru md5 的图，按 md5 去重 |
| `POST` | `/sources` | 带 `X-Gallery-Token`；`{items:[{md5, page_url, source_url, post_id}]}` → `{received, updated}`。**只填空值，已有来源不覆盖** |

插件只在桌面端确认 `persisted` 且回显同一个 `ingest_id` 时才把记录从 outbox 删掉 —— 桌面端没收到就
下次启动接着投递（`bridge.js` 里的 `queueFlush`）。

## 补全图片来源

弹窗里的「补全图片来源」按文件名末尾的 32 位 danbooru md5 反查帖子，把 `page_url` / `source_url` /
`post_id` 回写桌面端。

- 为什么在浏览器里跑：Danbooru 前面有 Cloudflare 人机校验，命令行请求会拿到「Just a moment…」的等待页；
  只有带浏览器指纹与 cookie 的请求能过。
- 前提：先在本机浏览器正常打开 `danbooru.donmai.us` 通过校验；插件需要 `danbooru.donmai.us` 的 host 权限。
- 进度存在 `chrome.storage.local` 的 `sourceJob`：`{running,total,done,found,updated,missing,error}`。
  弹窗关掉不影响后台继续跑。
- 单个 md5 至少间隔 180ms，命中批次 40 条回写一次；被限流（429）或校验挡回时整批中止并写 `error`。
- 覆盖范围有限：只有文件名本身就带 md5 的图能查；时间戳名、纯数字名、Twitter media key 名都查不到。

## 维护边界

旧“69／70 项通过”只属历史记录，不代表当前版本或真实下载链路验收。本次（2026-10-26）仅整理文档，未运行扩展测试或真实下载链路；历史临时产物清理结果见 [归档文档](../ARCHIVE_REVIEW.md#验证边界)。规则模拟器仍是配置辅助工具；开发与自测边界见 [AGENTS](../AGENTS.md#一构建验证与改动边界)，浏览器安装、真实下载和入队仍需人工核对。
