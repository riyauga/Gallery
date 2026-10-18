# Gallery 浏览器插件（图片自动归类）

Chrome / Edge（Manifest V3）扩展。它在浏览器里接住图片下载，把来源页、帖子身份、标签和分辨率
整理成一条记录，**通过本地回环端口**推给 Gallery 桌面端，由桌面端做审核与归档。

桌面端只监听 `127.0.0.1`，并要求 `X-Gallery-Token`；端口和令牌都在桌面端
「设置 → 浏览器桥接」里显示（可复制令牌）。

## 安装（开发者模式）

1. 打开 `chrome://extensions`（Edge 是 `edge://extensions`），右上角打开「开发者模式」。
2. 点「加载已解压的扩展程序」，选中仓库里的 `extension/` 目录。
3. 点扩展图标 →「设置」，把桌面端「设置 → 浏览器桥接」里的**端口**和**令牌**粘进去，
   点「测试连接」，应显示已连接。

## 支持的站点

| 站点 | 行为 |
| --- | --- |
| Danbooru 帖子页 / 其 CDN 直链 | 取标签、分级、来源，`inbox_name` 用原名（含 md5 时带上） |
| X / Twitter 帖子页与其 `pbs.twimg.com` 图 | 记录来源页、帖子 id、发帖账号；分辨率与目录手动填 |
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
| `POST` | `/ingest` | 带 `X-Gallery-Token`；`{probe:true}` 只验证不落库；真实记录需 `download_state:"complete"`；返回 `{ok, persisted, queued, duplicate, ingest_id, name}` |
| `POST` | `/ui/open` | `{view:"library"\|"review", path}` 唤起桌面端窗口并定位 |

插件只在桌面端确认 `persisted` 且回显同一个 `ingest_id` 时才把记录从 outbox 删掉 —— 桌面端没收到就
下次启动接着投递（`bridge.js` 里的 `queueFlush`）。

## 自测

```powershell
node --test extension/tests/*.mjs        # 69 个用例：规则、X 元数据、持久化队列
node extension/tools/e2e-bridge.mjs <桌面端 --dataroot 目录>
```

`e2e-bridge.mjs` 会读该沙箱 `config/settings.json` 里的端口和令牌，用插件自己的 `bridge.js`
对桌面端做一次真实往返：ping → probe → 投递 → 重投（重复幂等）→ `/ui/open` → 错令牌。
