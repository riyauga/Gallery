# 视频接入（mp4）

更新时间：2026-10-26 11:42（Asia/Tokyo）；本次文档整理时间，不代表重新运行或验收。

> 代码已接入；真实播放／声音、长视频、菜单、跨屏与资源释放仍按下文待人工验收。全屏临时小窗口修复的用户确认不覆盖视频。共享窗口规则见 [UI_INTERACTION.md](UI_INTERACTION.md)。

## 一、范围（已与用户确认）

| 项 | 决定 |
|---|---|
| 入口 | 图库入口走**已添加图库根目录的扫描**；外部MP4可纯查看，不入库；`待审核` 队列不接视频 |
| 格式 | 只 `mp4`（H.264/AAC），用系统解码 |
| 播放位置 | 图库内为现有预览浮层；外部为独立查看窗口 |
| 打开行为 | 自动播放，带声音 |
| 卡片 | 首帧封面 + 时长角标 |
| 必须可用 | 收藏、排序、搜索、删除、重命名、右键打开、标签编辑 |
| 本期不做 | 待审核/归档、NSFW 自动识别或模糊、整理/去重、多格式、完整播放器皮肤 |
| 播放控件 | 自绘播放条：左侧播放/暂停 + 下一个视频，右侧音量/设置/全屏；画面比例、速度、投屏收进设置菜单 |

## 二、硬约束（改代码前必读）

构建、进程与自测授权统一见 [AGENTS](AGENTS.md#一构建验证与改动边界)。本节只列视频特有约束。

1. **不要**把 `.mp4` 加进 `ScannerService.ImageExts`：待审核导入（`LocalImageImport`）也共用这份白名单，加进去会让视频进入尚未适配的审核流程。扫描单独用 `MediaExts`。
2. 当前数据库为 schema v7；视频字段在 v5 引入。旧“当前 v5、其他版本拒绝”描述已过时；升级顺序由 `Db.Initialize` 和迁移实现决定，不能用视频阶段文档回退版本。
3. `images` 表同时承载标签、收藏、NSFW、整理等图片语义。视频入库后，图片专属批处理必须显式排除 `media_kind='video'`，不要让 ImageSharp 去解 mp4。
4. 视频播放会持有文件句柄：切图、关闭、删除、重命名前必须暂停并释放 `MediaSource`。
5. 助手不启动应用做 UI 验收；播放手感、拖动、长视频内存由用户手动确认。

## 三、状态

数据、扫描、封面、预览与文件生命周期已有接入；2026-10-22 的构建／沙箱记录保留在 [验证记录](#六验证记录)，不等于当前版本重新验证。真实播放等未验收范围仍见该节。

**计划与实现差异（2026-10-26 源码核对）**：范围表中的“不参与整理／去重”是视频阶段目标；`OrganizeService.ReadImages` 与 `RowDedupeService` 的记录查询没有按 `media_kind` 排除视频，现有维护命令仍可能处理视频记录。本次不修改代码，不把这项排除宣称为已实现。视频字段在 v5 引入，当前初始化目标已经是 v7。

## 四、实施要点

### 1. 数据层

- `images` 新增：`media_kind TEXT NOT NULL DEFAULT 'image'`、`duration REAL`（秒）。
- 空库：`Db.EnsureSchema` 在事务内读取 `Services/LibrarySchema.sql`（基础脚本标 v5），补 v6 的评论字段与 v7 皮肤结构，最终写 `schema_version=7`。
- 旧库：`Services/LibraryMigrationV5.cs` 用 `VACUUM INTO` 备份 → 单事务 `ALTER TABLE` → `meta.schema_version=5`，幂等。
- `Db.cs`：旧库按 v3→v4→v5→v6→v7 逐级迁移，最终版本检查使用 `LibraryMigrationV7.TargetVersion`；视频迁移只负责 v4→v5 这一步。
- `StoreService`：`ImageRow` 增加 `MediaKind`、`Duration`；`QuerySpec.MediaKind`（`null`/`image`/`video`）；`UpsertImage` 写入两列。
- `ScannerService`：新增 `VideoExts={".mp4"}`、`MediaExts`；视频元数据读取失败不阻断入库。
- 安全：`NsfwService` 的候选查询加 `media_kind='image'`；其它图片专属批处理只加过滤条件，不改行为。

### 2. 主页

- `FilterKind` → `QuerySpec.MediaKind`；删除 `videos ? empty : ...` 的占位分支。
- `PhotoItem` 增加 `IsVideo`、`DurationSeconds`；`SameItem` 同步比较。
- 计数：全图片时仍显示「N 张照片」，含视频时显示「N 个项目」。
- 视频不提供「复制图片内容」。

### 3. 封面与角标

- 封面走 `StorageFile.GetThumbnailAsync(ThumbnailMode.VideosView, edge, ResizeThumbnail)`，用 ImageSharp 转 WebP 落现有缓存（key 仍是 path|mtime|size|edge）。
- 系统接口的 `requestedSize` 上限是 1600；卡片很大时上游可能请求 2048/4096，`GetOrBuildVideo` 会先截到 1600 再取图和落缓存，避免直接抛 `ArgumentException` 导致封面为空。
- 取不到封面时卡片显示胶片占位图标，不阻断网格。
- 时长角标右下角，`m:ss` / `h:mm:ss`，仅视频显示。

### 4. 播放

- 新增 `Controls/Viewer/VideoViewer.cs`：`MediaPlayerElement` 只负责画面（`AreTransportControlsEnabled=false`），自管 `MediaPlayer`，`AutoPlay=true`、音量 1。
- `PreviewOverlay`：图片/视频互斥显示；视频跳过缩略图飞入动画（避免播放表面参与变换）；底部图片缩放条隐藏，改用自绘视频播放条。
- 播放条：顶部进度/时间；左侧播放/暂停、下一个视频；右侧音量（弹出窄竖直面板 + 竖直滑杆）、设置、全屏。下一个视频按当前预览列表顺序向后找第一个视频并跳过图片，找不到时按钮置灰。
- 播放条默认常显（未做自动隐藏）；进度/时间保留，草图只明确了按钮布局。
- 设置菜单：画面比例（适应窗口/填满窗口）、播放速度（0.5×–2.0×）、投放到设备。投放走 `CastingDevicePicker` + `MediaPlayer.GetAsCastingSource()`，失败只提示、不影响本地播放。
- 键盘：内容区 `←/→` 翻页、`Esc` 先退出全屏再关闭；视频内容区 `Space` 播放/暂停。按钮保留 Enter/Space，进度和音量滑块保留方向键；菜单、模态、文本输入及 Ctrl/Alt 组合不进入查看器快捷键。窗口加速器与冒泡按键共用 `PreviewOverlay.TryHandleKey`，视频条按钮保留原生键盘定位框，全屏操作栏支持 Tab 唤出且持有焦点期间不隐藏；删除/重命名/移动在真正执行文件操作前由 `GalleryPage.FileOperationStarting` 通知浮层释放视频。
- 失败：显示「无法播放，可能缺少系统编解码器」，仍可翻页/关闭。
- 整理／去重的计划与实际查询差异见 [状态](#三状态)，尚未做视频专属界面适配。

## 五、文件地图

| 文件 | 作用 |
|---|---|
| `Services/LibrarySchema.sql` | 基础结构脚本（v5）；新库最终 v7 由 `Db.EnsureSchema` 补齐 |
| `Services/Db.cs` | 版本判断与迁移入口 |
| `Services/LibraryMigrationV5.cs` | v4→v5 增量迁移（新增） |
| `Services/StoreService.cs` | 数据模型、查询筛选、Upsert |
| `Services/VideoInfo.cs` | 视频宽高/时长读取（新增） |
| `Services/ScannerService.cs` | 扫描白名单、视频元数据 |
| `Services/ThumbnailService.cs` | 视频封面抽取与缓存 |
| `Services/ImageLoader.cs` | 视频缩略图走封面路径 |
| `DataModels/PhotoItem.cs` | `IsVideo` / `DurationSeconds` |
| `Views/GalleryPage.xaml.cs` | 媒体筛选、映射、右键菜单、计数 |
| `Controls/PhotoTile.cs` | 时长角标、封面占位 |
| `Controls/Viewer/VideoViewer.cs` | 播放面与播放状态 API（新增）；播放条在 `PreviewOverlay` |
| `Controls/PreviewOverlay.xaml(.cs)` | 浮层内图片/视频切换、自绘播放条与生命周期 |
| `App.xaml.cs` | `--verify-video` 沙箱验收入口（新增） |
| `README.md` / `IMAGE_LOADING.md` / `UI_INTERACTION.md` | 用户与实现约定同步 |

## 六、验证记录

以下为 2026-10-22 历史记录，保留当时的 schema、输出与样本口径；本次文档整理未复跑。后续只补新的独有证据，不重复追加同类构建日志。

- 2026-10-22：现状核对完成。确认 `ScannerService.ImageExts` 无 mp4、`GalleryPage` 视频筛选返回空、预览浮层为自绘图片查看器、数据库锁定 v4、待审核导入共用图片白名单。
- 2026-10-22：代码完成，`dotnet build Gallery.csproj -c Debug` 0 警告 0 错误，输出 `bin\Debug\net9.0-windows10.0.19041.0\win-x64`。
- 2026-10-22：在 `--dataroot` 沙箱复制真实 v4 库后运行 `--verify-db`：`schema_version=5`，1080 张图完整，迁移备份 `local\backups\library-v4-*.db` 已生成；`--verify-query` 8 键排序/搜索均正常，说明迁移后的旧行可读。
- 2026-10-22：空沙箱生成 3 秒 H.264/AAC mp4 后运行 `--verify-video`：首次扫描 `added=1`，二次扫描 `added=0 updated=1`（幂等）；`videos=1 images=0`；入库字段 `kind=video duration=3 size=640x360`；系统封面已生成 WebP 且文件存在。
- 2026-10-22：自绘播放条完成；`dotnet build Gallery.csproj -c Debug` 0 警告 0 错误，输出 `bin\Debug\net9.0-windows10.0.19041.0\win-x64`。
- 2026-10-22：定位「大卡片视频封面不显示」：真实 4K 视频卡片请求 edge 2048/4096，Shell `GetThumbnailAsync` 只接受 ≤1600，抛出的 `ArgumentException` 被吞掉后返回空封面。已在 `GetOrBuildVideo` 截到 1600；用同一真实 mp4 重跑 512/1600/2048/4096 均生成 WebP（2048/4096 复用 1600 缓存）。构建 0 警告 0 错误。
- 2026-10-22：音量弹层改为窄竖直面板，标题「音量」在上，竖直滑杆自下而上调音量；`dotnet build Gallery.csproj -c Debug -t:Rebuild` 0 警告 0 错误。**未验证**实际弹层高度/位置和拖动手感。
- 2026-10-22：**未验证**：实际打开视频的自动播放/声音、自绘播放条按钮/菜单/进度拖动、下一个视频置灰、投屏、长视频拖动、切页释放文件、删除/重命名时的文件占用、不同 DPI/全屏表现。需用户用真实图库手动确认。

## 七、交接注意事项

- 不要把视频加进待审核链路；`LocalImageImport` 和 `ArchiveService` 本期保持图片语义。
- 改 `images` 查询时注意 `ReadImage` 按列名容错，新增列必须给默认值，避免旧行读出异常。
- 迁移必须可重入；已升到 v5 的库再次打开不得重复备份/改结构。
- 任何涉及播放器的文件操作，先 `Pause` 再释放，否则 Windows 会锁文件。
- 构建与交付边界见 [AGENTS](AGENTS.md#一构建验证与改动边界)。

## 外部纯播放

VideoViewer核心由独立窗口复用；其播放条不依赖标签与数据库，多个独立视频按新视频播放暂停其他的规则协调，关闭最终释放播放器。格式、导入排除、更新退出及验收边界见 [外部文件查看](FILE_VIEWER.md)。
