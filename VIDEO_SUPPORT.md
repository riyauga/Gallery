# 视频接入（mp4）— 计划与交接

> 状态：代码与构建完成，**待用户手动播放验收**。每完成一步更新本文件的「进度」与「验证记录」。
> 验收标准（用户口径）：**能正常把 mp4 导入图库，并在图库主页看到缩略卡片、点开能正常播放。**

## 一、范围（已与用户确认）

| 项 | 决定 |
|---|---|
| 入口 | 只走**已添加图库根目录的扫描**；`待审核` 队列本期不接视频 |
| 格式 | 只 `mp4`（H.264/AAC），用系统解码 |
| 播放位置 | 现有全屏预览浮层（点缩略图弹出的那个） |
| 打开行为 | 自动播放，带声音 |
| 卡片 | 首帧封面 + 时长角标 |
| 必须可用 | 收藏、排序、搜索、删除、重命名、右键打开、标签编辑 |
| 本期不做 | 待审核/归档、NSFW 自动识别或模糊、整理/去重、多格式、完整播放器皮肤 |
| 播放控件 | 自绘播放条：左侧播放/暂停 + 下一个视频，右侧音量/设置/全屏；画面比例、速度、投屏收进设置菜单 |

## 二、硬约束（改代码前必读）

1. `Gallery.exe` 运行时会锁输出文件，构建前先关进程。
2. 交付构建只写 `bin\Debug\net9.0-windows10.0.19041.0\win-x64`，必须 0 警告 0 错误。
3. **不要**把 `.mp4` 加进 `ScannerService.ImageExts`：待审核导入（`LocalImageImport`）也共用这份白名单，加进去会让视频进入尚未适配的审核流程。扫描单独用 `MediaExts`。
4. 数据库当前 `schema_version=5`；旧库（v4）打开时先备份再迁移，v3 先走 v4 再走 v5，其他版本拒绝打开。
5. `images` 表同时承载标签、收藏、NSFW、整理等图片语义。视频入库后，图片专属批处理必须显式排除 `media_kind='video'`，不要让 ImageSharp 去解 mp4。
6. 视频播放会持有文件句柄：切图、关闭、删除、重命名前必须暂停并释放 `MediaSource`。
7. 助手不启动应用做 UI 验收；播放手感、拖动、长视频内存由用户手动确认。

## 三、进度

- [x] 0. 现状核对、范围确认、计划文档
- [x] 1. 数据层：schema v5、迁移、`ImageRow`/`QuerySpec`、扫描白名单与视频元数据
- [x] 2. 图库主页：媒体类型筛选接线、`PhotoItem` 字段、计数/空状态文案
- [x] 3. 缩略封面：系统封面抽取 + WebP 缓存 + 失败占位 + 时长角标
- [x] 4. 预览浮层：`VideoViewer`、自动播放、自绘播放条、切页/关闭释放、键盘、错误提示
- [x] 5. 图片功能兼容：右键复制、删除/重命名前释放视频、NSFW/分辨率标签排除视频
- [x] 6. 文档同步（README / IMAGE_LOADING / UI_CONVENTIONS）+ 构建 + 验证记录

> 代码与构建已完成；**实际播放手感、长视频、自绘播放条及各菜单项仍待用户手动验收**（助手不启动应用做 UI 验收）。

## 四、实施要点

### 1. 数据层

- `images` 新增：`media_kind TEXT NOT NULL DEFAULT 'image'`、`duration REAL`（秒）。
- 空库：`Services/LibrarySchema.sql` 直接建 v5。
- 旧库：`Services/LibraryMigrationV5.cs` 用 `VACUUM INTO` 备份 → 单事务 `ALTER TABLE` → `meta.schema_version=5`，幂等。
- `Db.cs`：v3→v4→v5，目标版本改为 5。
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
- 已知边界：整理与去重是显式维护操作，仍按原文件级逻辑处理视频（不额外解码）；本期没有为视频做整理/去重界面适配。

## 五、文件地图

| 文件 | 作用 |
|---|---|
| `Services/LibrarySchema.sql` | 新建库结构（v5） |
| `Services/Db.cs` | 版本判断与迁移入口 |
| `Services/LibraryMigrationV5.cs` | v4→v5 增量迁移（新增） |
| `Services/StoreService.cs` | 数据模型、查询筛选、Upsert |
| `Services/VideoInfo.cs` | 视频宽高/时长读取（新增） |
| `Services/ScannerService.cs` | 扫描白名单、视频元数据 |
| `Services/ThumbnailService.cs` | 视频封面抽取与缓存 |
| `Services/ImageLoader.cs` | 视频缩略图走封面路径 |
| `Models/PhotoItem.cs` | `IsVideo` / `DurationSeconds` |
| `Views/GalleryPage.xaml.cs` | 媒体筛选、映射、右键菜单、计数 |
| `Controls/PhotoTile.cs` | 时长角标、封面占位 |
| `Controls/Viewer/VideoViewer.cs` | 播放面与播放状态 API（新增）；播放条在 `PreviewOverlay` |
| `Controls/PreviewOverlay.xaml(.cs)` | 浮层内图片/视频切换、自绘播放条与生命周期 |
| `App.xaml.cs` | `--verify-video` 沙箱验收入口（新增） |
| `README.md` / `IMAGE_LOADING.md` / `UI_CONVENTIONS.md` | 用户与实现约定同步 |

## 六、验证记录

（每步完成后追加：构建结果、可复现检查、尚未验证项。）

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
- 构建产物只在约定目录，不要在其它位置留可运行副本。
