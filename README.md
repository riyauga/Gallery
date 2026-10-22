# Gallery — 「照片」图库主页复刻

一个只做**观感与交互**的最小照片图库：C# / WinUI 3（与 Microsoft Photos 同栈），把图库主页的布局、状态与动画按规格复刻出来。

待审核深色页采用统一炭灰背景，圆环与网格卡片以中性渐变为底，图片平均色只作 10% 局部染色；圆环后层底板及描边逐渐融入页面。归档保留系统强调色，一键忽略使用普通中性按钮。画框尺寸、位置、环形滚动与加载契约不变；配色、浅色主题及往返切换表现待用户手动确认。

## 运行

词库入库统一使用 `TagCatalogService.CanonicalName`：不分来源处理 NFKC、空白、下划线及语义分类前缀，名称／别名／索引同步；作品键和实体分类也规范化，来源 URL 与实体键仍作追溯依据。导入策略为 `canonical-names-v3`，内容未变的旧词库也会按新规则重导；人工词库编辑和资料写入共用名称规则。身份整理结论保存在 `assets/tag_catalog/tag_identity.json`，保留来源实体，仅统一身份。归档直接写分类标签，移除先写 custom 再改分类的空副本来源。2026-10-21 数据整理报告及备份见本轮交付说明，UI 未由助手启动验收。

待审核圆环切走时保存中心卡牌 ID 并停止惯性；返回时先按 ID 定位、等待可见容器尺寸稳定，中心画框先显现，再由上下两侧同步按距离逐张翻展开成圆环。缩略图与错误状态随加载异步补入，不阻塞入场；入场可被滚轮、拖动或点击立即打断，另有 1.2 秒保底，系统关闭动画时直接显示最终布局，旧视图消息不回写本次选择。往返切换与多米诺入场的视觉效果待人工确认。

待审核页可点击“批量审核”：先保留原圆环，待布局稳定且视口卡片补齐后，由真实网格卡片完成发牌，文件名与勾选框随动画显现，结束不再换卡片；默认不选中；单击切换勾选，点击空白处取消全部勾选，也可按住左键拖动框选；“全选／取消全选”作用于当前全部待归档记录，左侧容器工具行右边缘的下拉框提供小／中／大卡片（默认中）。选一张保留完整详情；选多张只显示标签和归档目录，标签编辑从空白开始，标签应用、清空及分级后刷新预览时保留本页手动解除模糊的状态；离开页面或调整全局预览设置仍按原生命周期重置。“应用标签”按分类追加合并、忽略大小写去重，空分类保留原标签；“清空标签”清空所选图片的全部标签及分级，并清空编辑草稿。分级仍为单值，未填写时保留各图原值，明确选择后统一更新。分级按钮在多选时逐张运行模型并分别保存分级，保留各图其他标签；批量归档／忽略只处理勾选项；退出模式恢复原圆环位置。动画、网格滚动和真实归档效果待人工确认，本轮不启动应用。

稳定性维护（2026-10-21）：`crash.log` 已确认一次标签编辑框重复打开导致的未捕获异常；主窗口的所有 `ContentDialog` 现在统一经 `DialogService.ShowAsync(dialog)` 打开，已有弹窗时新请求按取消处理。新建、删除文件夹后的扫描移到后台，监听扫描不再持有覆盖整个根目录的数据库锁。退出前等待审核、文件夹操作、图库文件操作、桥接、监听、对账和词库任务，再关闭数据库。扫描枚举失败不执行全根缺失标记；正常目录链接仍支持，祖先循环停止遍历。上述修改经静态核对及构建检查，实际闪退、卡死和大图库手感仍待用户手动确认。

未捕获 XAML／后台线程异常写入程序旁 `crash.log`（时间、进程、来源及堆栈）；未观察任务异常与后台失败写 `%TEMP%\ptv\gallery-log.txt`，不会通过统一设置 `Handled` 掩盖异常。日志只能记录被这些托管入口捕获的错误，不能覆盖所有原生崩溃。

托盘唤回先显示、激活窗口，再按真实鼠标位置发送一次非客户区移动消息，刷新系统按钮的交互状态；不移动鼠标或模拟点击。此前切换标题栏模式及调整恢复顺序未能消除红色残留，已移除；新处理保留有界 `caption` 日志，视觉结果待人工验证。

```bash
cd D:\Projects\Other\Gallery
dotnet run
```

或直接运行已构建的：

```
bin\Debug\net9.0-windows10.0.19041.0\win-x64\Gallery.exe
```

- 需要 Windows 11；Windows App SDK 2.5 已自包含（`WindowsAppSDKSelfContained=true`），无需另装运行时。
- 内置 40 张示例图（`assets/`，程序生成，非版权素材），含 8 种宽高比与 1999–2026 的时间分布。

## 交互

| 操作 | 行为 |
|---|---|
| 单击缩略图 | 普通模式打开预览；选择模式切换该图勾选 |
| Ctrl + 单击 | 进入选择模式，独立切换该图，保留其他选中项 |
| Shift + 单击 | 按当前排序/筛选后的完整列表，从最近一次单图选择的锚点选到点击项；无锚点时仅选当前项 |
| Ctrl + Shift + 单击 | 保留已有选区，追加上述范围 |
| Ctrl+A | 进入选择模式，全选当前排序/筛选后的完整列表，包含屏幕外图片 |
| 方向键 / Shift+方向键 / Ctrl+Shift+方向键 | 左右按列表顺序跨排衔接，上下按相邻行画面位置移动，列表首尾不循环且保留选区 / 从固定锚点范围选择 / 追加范围；首次无焦点时只建立焦点 |
| Space | 切换焦点图勾选，更新选择锚点 |
| Enter / F2 / F3 | 打开焦点图 / 重命名焦点图 / 在资源管理器定位焦点图 |
| Delete | 选区优先，无选区时取焦点图；一次确认弹窗、默认取消，确认后移入回收站 |
| Ctrl+C / Ctrl+Shift+C | 选区优先、焦点兜底，复制文件列表 / 文件路径；单图像素复制通过右键“复制图片内容” |
| 拖动缩略图 | 从已选图片起拖时以复制语义拖出整个选区；多选时从未选图片起拖，改为只选中并拖出该图；缺失文件或模糊保护命中时取消整次拖出 |
| 点击窗口 XAML 内非交互空白 / Esc | 清空选区并退出选择模式；卡片、按钮、输入框、滚动条等交互区域不触发空白退出；查看器打开时 Esc 优先处理查看器 |
| 预览中 `Esc` / `←` / `→` | 关闭 / 上一张 / 下一张 |
| 右键缩略图 | 图库文件操作菜单；单图提供“复制图片内容” |
| 滚动/悬停 | 滚动时右侧时间线淡入，2 秒后淡出 |
| 拖动时间线轨道 | 按位置定位（标记无动画跟随） |
| 汉堡键 | 折叠/展开左侧导航（320 ↔ 48） |
| 导航「图库 / 收藏夹」 | 切换数据源，内容淡出 30ms / 淡入 20ms |
| 搜索框 | 按文件名过滤 |
| 命令栏 | 选择、排序、筛选、库类型与大小、更多（全选/不选） |
| 标题栏齿轮 | 打开设置页（整页替换内容区，标题栏与导航保留） |
| 设置 · 应用主题 | 浅色 / 深色 / Windows 默认，切换即时生效 |

> 幻灯片按钮按要求**已删除**（原版有，本项目不做）。

键盘焦点用画框外的 4 DIP 高亮主题色粗框标识，与画框间隔 2 DIP，不覆盖图片内容，按稳定图片 ID 跨虚拟化、排序和布局尺寸变化恢复；无有效焦点时优先取第一张已选图，否则取当前视口首张。焦点图离开视口时按必要距离滚入。窗口级预览按键仅在图库激活且无查看器、输入框或菜单/弹窗占用时分发；工具栏和导航保留原生方向键、Space，搜索框保留文本全选/复制。河流/网格跨行、扩选、查看器返回、确认删除、多文件粘贴及输入隔离待用户手动确认；本轮仅静态和构建检查。

## 与 Photos 对齐的关键参数

| 项 | 值 | 来源 |
|---|---|---|
| 标题栏高 | 48（系统三件套 48x48，`TitleBarHeightOption.Tall`） | 反编译常量 / 实测 |
| 导航宽 | 320（折叠 48） | `ExpandedOpenPaneLength` |
| 页面左右留白 | 32 / 48 | `GalleryViewGridMargin` / `GalleryViewGridContentMargin` |
| 头部内边距 | 32,12,32,18；外边距 0,16,16,8；圆角 6 | XAML |
| 方格三档 | 120/135/150、240/270/300、330/390/450 | `GalleryView.cs` |
| 河流行高 | 100/142/240、112/280/800、154/440/980 | 同上 |
| 间距 | 12 / 14 / 16 | `ImageGutterToDouble` |
| 河流通用约束 | 宽高比 clamp `[0.56, 3.0]`；末行不放大 | 布局算法 |
| 选中动画 | 167ms，`cubic-bezier(0,0,0,1)` | `GalleryTileContainer.xaml` |
| 悬停 / 按下底色 | 0ms 硬切 | 同上 |
| 按压缩放 | 0.98，100ms 线性 | `GalleryTileContainer.cs` |
| 时间线宽 / 箭头高 | 48 / 17 | `TimelineScrollbar.cs` |
| 时间线标记 | 26 × 3，圆角 2 | XAML |
| 时间线淡入 / 淡出 | 30ms / 200ms；2s 自动隐藏 | 同上 |
| 时间线标记移动 | 100ms；相同目标不重播，目标变化时从当前显示位置衔接；拖动仍直接定位，连续滚动效果待人工核对 | `TimelineScrollbar.cs` |
| 工具栏元素间距 | `CommandBarElementMargin = 2,0,2,0` | `CommandBarStyles.xaml` |
| 工具栏按钮宽 | 普通 52 / 带 chevron 62（`AppBarButtonWithChevronWidth`）/ 溢出 48 | `CommandBarStyles.xaml` |
| 选择按钮文字 | 可见「选择」/「取消选择」 | `GetSelectionText()` → `CommandBar_SelectSelectionText` / `_CancelSelectionText` |
| 选择按钮图标 | 勾选 `E73A` / 取消 `E711`，FontSize 12 | `IconService.GetCheckMarkIcon/GetCancelIcon` |
| 选择按钮底色 | `AppBarButtonHighlightedStyles.xaml`（灰底 + 描边） | 同上 |
| 下拉 chevron | `E70D`（ChevronDown ∨），**覆盖 WinUI 默认 `E974`** | `CommandBarStyles.xaml:21` |
| 排序类型 → 滚动条 | 日期维度 → 时间线；名字等非日期维度 → 原模板位置的通用连续条（`ScrollBarVisibility.Auto` 控制显隐） | `GalleryViewModel.EvaluateScrollbarType` / `ContinuousScrollBarPresenter` |
| 设置按钮 | 标题栏首，宽 40 高 48，图标 `E713` FontSize 16 | `TitleBarStyles.xaml: SettingsButtonWidth` |
| 标题栏按钮间距 | `CommandBarMargin = 12,0,12,0`；CommandBar 列 MinWidth 110 | `TitleBarStyles.xaml` |
| 设置页头 | margin `32,28,48,26`；图标 `E713` FontSize 20；标题 FontSize 24 | `Margins.xaml` / `GetSettingsHeaderIcon` |
| 设置页内容 | ScrollViewer padding `32,0,48,0`；StackPanel MaxWidth 958、Spacing 3 | `SettingsStyles.xaml` |
| 「个性化」副标题 | margin `1,24,0,6`，`BodyStrongTextBlockStyle` | 同上 |
| 应用主题卡片 | `ctc:SettingsCard`；Header「应用主题」/ Desc「选择要显示的应用主题」/ 图标 `E790` FontSize 16 | `SettingsPage.xaml` |
| 主题下拉 | `DropDownButton` + `MenuFlyout`，三项 `RadioMenuFlyoutItem`：浅色 / 深色 / Windows 默认 | 同上 |
| 切页 | 30ms 淡出 + 20ms 淡入 | `UnifiedGalleryPage.cs` |
| 内容区两层边距 | 外层 `GalleryViewGridMargin 32,0,0,0`、内层 `GalleryViewGridContentMargin 0,0,48,0` | `Margins.xaml`；滚动条因此贴窗口右缘 |
| 时间线圆点 | `TimelineIndicator`：同年内月份切换点，与年份文字共用距离过滤 | `TimelineLayout.CreateTimelineElementList` |

## 目录

```
Gallery/
├─ MainWindow.xaml(.cs)          窗口壳：标题栏 + NavigationView + Toast + 预览层
├─ Views/GalleryPage.xaml(.cs)   头部、日期头、命令栏、滚动区、选择与排序逻辑
├─ Controls/
│  ├─ PhotoGalleryPanel.cs       方格 / 河流布局（照搬 Photos 公式）
│  ├─ PhotoTile.cs               缩略图状态机与按压动画
│  ├─ TimelineScrollbar.cs       时间线滚动条
│  ├─ ContinuousScrollBar.cs     通用连续滚动条（待审核圆环）
│  └─ PreviewOverlay.xaml(.cs)   全窗预览
├─ Views/SettingsPage.xaml(.cs)  设置页（个性化 · 应用主题）
├─ ThemeService.cs               主题服务（SetTheme / GetThemeName）
├─ Models/PhotoItem.cs
├─ Services/Library.cs           读取 assets/manifest.json
└─ assets/                       40 张示例图 + manifest.json
```

## 通用控件

- 输入宿主规范：设置页空白点击将文本焦点交给中性锚点，保留输入框与其他交互控件的正常点击；标题栏 passthrough 区域从 DIP 转为当前缩放下的物理像素，并在尺寸/缩放变化后更新，不强制鼠标样式来掩盖命中错误。完整规则与人工核对项见 `UI_CONVENTIONS.md`，本轮尚未 UI 验收。
- 搜索封装默认沿用原生 `ControlCornerRadius`，内部控件按宿主尺寸拉伸，避免默认零圆角覆盖原生边缘；本轮边缘修正仍待用户目视确认。
- `Controls/SearchInputBox.cs`：所有搜索入口统一基于原生 `AutoSuggestBox`，覆盖顶部图库搜索、资料库、筛选、标签编辑与待审核标签输入。普通搜索不提供候选数据，标签输入保留候选及提交；查询由各宿主负责。组件默认允许交互聚焦；标题栏启用 `PreventAutomaticFocus`，承接原 `PhotosSearchBox` 的防抢焦点与显式搜索命令，旧类已移除。筛选 AppBarButton 同样允许聚焦，避免普通 Flyout 继承禁止聚焦的设置。普通资料编辑仍用 TextBox；点击、输入法、候选及焦点恢复待用户确认。
- `Controls/SelectionDropDown.cs`：按钮式单选下拉与设置页主题选择共用 `DropDownButton + MenuFlyout(Placement=Bottom) + RadioMenuFlyoutItem`，按钮外观、选中标记、动画与边界定位均交给原生菜单，不改 ComboBox 内部模板、不手算偏移。设置页、筛选、待审核状态和预览缩放全部接入；右键菜单的鼠标定位不变。2026-10-20 用户确认本次展示效果；后续新增文本单选下拉沿用此组件，不恢复已废弃的 `AnchoredComboBox` 内部定位方案。该确认不代表全部窗口尺寸、DPI 和键盘路径已逐项验收。
- `Controls/ContinuousScrollBar.cs`：可复用的连续滚动条。值始终是 `double`；拖动 1:1 跟手，点击轨道任意位置时滑块和内容一起短滑到点击处，不走原生 `ScrollBar` 的 `LargeChange`。支持竖向/横向、步长吸附、轨道/滑块厚度。待审核页用它替代原生滚动条。用法与约束见 `UI_CONVENTIONS.md`。
- `Controls/ContinuousScrollBarPresenter.cs`：全局接管 WinUI 原生条的模板内容，沿用原滚动条外层位置、尺寸和内容留白；设置、资料库、详情、列表、对话框和下拉菜单内的 `ScrollViewer` 自动接入。日期时间线仍独立保留。待审核网页调参面板由 DOM 报告几何/范围，WinUI 宿主叠加同一个连续条，网页继续负责内容滚动。实际尺寸与交互观感由用户手动确认。
- 自动隐藏统一由 `ContinuousScrollBar` 负责：活动时显示，悬停/拖动时保持，空闲 700ms 后淡出 200ms。WinUI 接入层、待审核圆环和网页调参条均使用该默认行为，宿主通过 `NotifyActivity()` 报告外部活动，通过 `HideIndicator()` 处理页面隐藏；不各自修改透明度或启动隐藏计时器。指示层隐藏不改变布局或命中范围。

## 标签身份与显示（2026-10-20）

- 角色身份以 `catalog_entities.identity_key` 为准：同一角色的简写/全名/译名、跨作品联动都指到同一个身份键；`catalog_memberships` 保留该身份的全部作品。
- 人工身份映射、标签状态和重复标签合并写在 `assets/tag_catalog/tag_identity.json`。标签状态存在 `meta['tag_state']`，不新增表、不改旧库 schema。
- 待审核贴纸（含批量编辑草稿）与标签编辑器直接输入的角色／作品原名，通过 `StoreService.ResolveTagLabels` 复用显示模式；未入库标签也可从按分类精确匹配的角色词库或作品词库读取名称，只读显示、不创建标签或写回身份。返回待审核页会刷新名称并保留输入草稿；查看器刷新名称前清理旧缓存，切回原名不再残留旧中文名。该轮仅构建与只读数据核对，实际贴纸切换待人工确认。

- 标签显示模式：`zh_full`（默认）/ `zh_short` / `alias` / `original` / `canonical`，在设置页「标签 → 标签显示」切换，仅作用于角色、作品的标签贴纸及评审页标签；其他分类不使用该全局设置。持久化到 `meta['tag_display_mode']`，单标签覆盖及人工显示名仍优先。缺中文名时回退英文来源名，再退标签原名；不改 sidecar 或搜索候选。
- 角色、作品搜索候选自动跟随输入语言，无语言设置：含汉字（包括中英混输）优先中文，纯英文、数字或符号优先英文；对应名称缺失时回退现有另一语言名称。角色仍附带同语言作品名，例如 `卡芙卡_(崩坏：星穹铁道)` 或 `kafka_(honkai:_star_rail)`；作品候选直接显示作品名，作品名称缺失时可使用同语言的已有别名。角色来源名先取标签／实体自身，缺失时从同身份实体补齐；缺来源或作品仍显式标记，不猜绑。搜索候选与标签贴纸独立，提交仍使用原标签名／实体键；旧 `tag_search_name_mode` 值不再读取。
- 标签编辑器和评审页搜索共用身份折叠：同一身份只返回一个候选，优先保留图片数最多的标签；只有该身份还没有任何标签时才追加词库实体。词库实体还会按身份合并中/英文 wiki 的同名条目，并在同作品内按来源名与已有标签/同来源实体去重，避免「标签 + 同角色实体」或跨 wiki 双份候选。`chalky`、`victor` 已按词库候选人工确认，外部作品/原创标签不自动链到同名角色，0 图标签不参与歧义列表。
- 命令行：`Gallery.exe --tag-repair` 只预演，`--tag-repair --apply` 写库（执行前自动备份）。报告写到 `%TEMP%\ptv\tag_repair.txt`。扫描、归档入库和标签编辑器保存后会后台补一次对齐。

## 项目规范

- 协作、构建、验证与交付边界：`AGENTS.md`
- UI 与交互约束：`UI_CONVENTIONS.md`
- 图片加载：`IMAGE_LOADING.md`
- 待审核与归档：`ARCHIVE_REVIEW.md`

## 已知差距（有意为之或未做）

1. **无虚拟化**：40 张全部创建。真实 Photos 用 `ItemsRepeater` + 自定义 `VirtualizingLayout`；本项目只复刻视觉与手感。
2. **无网格内日期分组**：Photos 会在滚动中插入日期分段标题，本项目只保留顶部固定日期头。
3. **图标**：用系统 `Segoe Fluent Icons` 近似，未内嵌 `PhotosIcons.ttf`（字体属微软版权素材）。
4. **查看器是覆盖层**，不是独立窗口；转场只做 150ms 淡入 + 0.98→1 缩放。
5. **标题栏**：未做 Photos 的标题栏配色与导入/设置按钮（按约定不做设置）。
6. **文件操作**：复制/移动/删除只弹提示，不碰磁盘。

## 工具

`tools/` 下是取证与验证脚本（不属于应用）：`shot.py` 截窗口、`shot_screen.py` 截全屏（能拍到弹出菜单）、`realinput.py` 发送真实鼠标/键盘、`wheel.py` 发送滚轮。

窗口保留原来的 48 DIP 自定义标题栏与系统按钮。交互拖动中，仅对尺寸、DPI、Z 序和激活状态不变的位置更新做合并；`WM_MOVE` 仍即时发送，松手后同步最终框架位置，再恢复待审核网页。缩放、最大化、显示／隐藏及 DPI 变化走完整处理。没有新增公开 API、长期设置或启动参数。定位依据和连续移动前后数据见 `ARCHIVE_REVIEW.md` 的窗口拖动记录。

## 坑（照着原版写也踩过的）

1. **WinUI 的 `Opacity` 归零会杀死合成层**：代码里置 0（或淡出到 0）之后，即使再设回 1 也画不出来。淡出终点用 `MinOpacity = 0.01`。
2. **`Opacity` 真归零之前不要留调试插桩**：`AnimateOpacity` 里写 `Opacity = to; return;` 这种短路，会让「淡出」变成瞬时归零，直接把整个控件打死且不可恢复（编译器只报 CS0162，不算错误）。
3. **`Popup` 一打开会让指针命中短暂离开本控件**：`TimelineArea` 立刻收到 `PointerExited`，气泡刚开就被收掉（拖动中因为 `_dragging` 才侥幸留住）。因此：气泡改为控件树内的 `Border` + `Translation` 定位，并给 `Exited` 加 180ms 抖动缓冲（原版 Hide 条件里本来就有计时器这一层）。
4. **跨 DPI 比较 UIA 矩形前先确认窗口所在显示器的缩放**：`BoundingRectangle` 是物理像素，拿 Photos 的数字按自己屏幕的缩放折算会整体偏 37.5%。稳妥做法是直接用原版 XAML 常量（`AppBarButtonWithChevronWidth = 62` 等），UIA 只用来复核。
5. **UIA 矩形不含 margin**：`CommandBarElementMargin` 的 2 DIP 间距不会体现在 `BoundingRectangle` 上，只能看截图。
6. **脚本化 hover 不可靠**：`SetCursorPos` 不产生 WinUI 的 `PointerEntered/Moved`（要用 `mouse_event(MOUSEEVENTF_MOVE)` 逐步移动）；`PrintWindow` 抓不到弹出层（用整屏 `ImageGrab`）。悬停观感最终以真机手动确认为准。
7. **窗口由工具启动会被会话回收**：验证脚本应把「启动 → 操作 → 抓图 → 结束」收敛在同一条命令里，用 `subprocess.Popen` 管进程，别指望跨命令常驻。
8. **UIA 的 `Name` ≠ 界面可见文字**：选择按钮的 `AutomationProperties.Name` 是「选择项目」，但可见 `Label` 是「选择」（来自 `GetSelectionText()`）。**猜文案时要看 XAML 绑定的属性名，别看 UIA 的 Name。**
9. **WinUI 默认 chevron 是 `E974`（ChevronRight ›）**：Photos 在 `CommandBarStyles.xaml` 里覆盖成 `E70D`（ChevronDown ∨）。不覆盖就是「›」而不是「∨」。
10. **非打包（`WindowsPackageType=None`）不能用 `ms-appx:///` 引用自带资源字典**：会直接以 `0xC000027B` 崩溃。把 `ThemeDictionaries` 内联进用它的控件 `Resources` 即可。
11. **原生 `ScrollBar` 点击轨道按 `LargeChange` 走**，且 `ValueChanged` 不能直接拿来做 1:1 跟手；需要“点哪到哪”时用 `Controls/ContinuousScrollBar.cs`。

## 标签与身份模型（2026-10-22，schema v4）

- 身份（`identities`）独立于词库条目存在：条目只是资料出处，条目改名或消失不影响身份编号；`identity_redirects` 记录归并后的旧键 → 有效身份，读取侧统一走重定向。
- 标签新增稳定键 `tag_key`（`tk_` 开头，创建时生成、之后不变），数字 `id` 与图片关联保持不变。草稿、归档 payload、sidecar、导出只应以 `tag_key` 识别标签，名称为辅。
- 标签与身份的关联统一存 `tag_identities`，`link_status` 三态：`linked`（已关联）/`pending`（待关联，可正常贴图）/`independent`（独立自定义，只有用户主动操作才关联）；`locked` 表示人工确认，自动对齐不得覆盖。
- `tags` 取消“分类内名称唯一”约束，角色/作品允许同名不同身份；其他分类的唯一性由应用层维持。
- 关联状态与显示模式分离：显示资料仍在 `tag_profiles` 与 `meta.tag_state`，身份只从 `tag_identities` 读取，避免多处状态互相覆盖。
- 迁移：v3 旧库在打开时自动升级，先用 `VACUUM INTO` 备份到 `<数据目录>\backups\library-v3-<时间>.db`，结构重建与数据修复同事务，失败回滚；迁移报告写入 `meta.migration_v4`（含备份路径、修复项、来源链接、核对结果）。
- 已应用且可复核的数据修复：绝区零露西的短名/全名条目合并到同一身份；鸣潮 3.4 联动露西与《赛博朋克：边缘行者》露西合并为同一身份，并补赛博朋克作品/角色来源条目。证据不足项不猜绑。
- 兼容限制：旧草稿/归档/sidecar 只有名称，读入时按名称解析；遇到同名不同身份会停在待关联，不会任取一条。旧版本程序写回时会丢弃 `tag_key`/身份引用，不能宣称无损。
- 人工验收（未自动验证）：同名角色候选是否可区分、独立标签是否在资料库更新后仍独立、待审核往返后身份是否保留、归档后筛选是否按实际作品标签计算。

## 自动对齐规则（2026-10-22）

- 只处理角色标签，且跳过：已锁（`tag_identities.locked`）、独立状态、旧人工/外部/原创状态、已有来源实体且未要求覆盖的标签。
- 名称必须唯一：词库候选按身份折叠后只能剩 1 个；多个候选一律记为“同名多实体”并跳过，不做 `LIMIT 1` 猜绑。
- 名称唯一还不够：候选身份的作品必须覆盖图片实际带的全部 work 标签（跨作品按成员表匹配，`genshin` 与 `genshin_impact` 这类命名差异做包含匹配）。覆盖不了就记为“作品冲突”并跳过，一个字都不写。
- 成功的自动对齐会同时写 `tag_identities`（source=auto-align，未锁），以后自动对齐先读锁和独立状态；自动结果不冒充人工确认。
- 这条规则是为修“词库缺这个作品时，错误的唯一候选被当成唯一匹配”而加；`hornet`（空洞骑士 vs 绝区零）、`scarlet`（妮姬 vs 火焰纹章）这类现在会被挡在门外。
