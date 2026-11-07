# UI 规范

更新时间：2026-10-26 11:42（Asia/Tokyo）；本次文档整理时间，不代表重新运行或验收。

本文件维护共享 UI、组件 API 与窗口规则；页面专属行为见 [ARCHIVE_REVIEW.md](ARCHIVE_REVIEW.md)，加载见 [IMAGE_LOADING.md](IMAGE_LOADING.md)。历史记录不构成当前授权或验收。

角色／皮肤的显示与改名只维护在 [归档文档](ARCHIVE_REVIEW.md#角色与皮肤v7)。

## 按任务读取

- 窗口：[全屏切换](#查看器全屏切换2026-10-25当前有效)、[最小尺寸](#主窗口最小尺寸)、[拖动与标题栏](#窗口拖动与标题栏)。
- 图库：[选择与文件操作](#图库选择与文件操作)、[输入焦点](#输入焦点与标题栏命中)、[平移边界](#二图片查看器的平移边界按图片自身算)。
- 共享组件：[模态弹窗](#模态弹窗与退出)、[搜索框](#搜索输入框)、[候选 Tab](#标签候选的-tab-导航)、[连续滚动条](#通用连续滚动条continuousscrollbar)、[二级下拉](#按钮锚定的二级下拉)。
- 设置：[页面与配置布局](#设置页的选项与识别配置布局)；视频播放见 [视频专题](VIDEO_SUPPORT.md#4-播放)，标签候选语义见 [身份专题](ARCHIVE_REVIEW.md#候选选择与实现边界)。
- 历史取证：[Photos 参考参数](#photos-参考参数非当前验收记录)；只在比较来源数值时读。

## 查看器全屏切换（2026-10-25，当前有效）

**目标与确认**：全屏帧直接接最大化详情帧，不能先出现裁切旧画面的普通小窗口。用户已反馈本次修复有效；该确认只覆盖当前复现场景，普通窗口位置还原、多屏/DPI、连续切换及视频不因此自动验收。

- 入口：`MainWindow.SetViewerFullscreen(bool full, Action applyViewerLayout)`；只用于共享 `PreviewOverlay`。回调同步改布局属性，不等待、不入队、不启动缩放动画；例如 `window.SetViewerFullscreen(full, () => ApplyViewerLayout(full))`。
- 切换期间临时设置本窗口 `DWMWA_TRANSITIONS_FORCEDISABLED=TRUE`，更新标题栏和查看器布局；退出复用进入前的 `OverlappedPresenter`。
- **关键修复**：进入前为最大化时，只在退出操作期间开启 `_restoringMaximizedViewer`。现有窗口子类处理 `WM_WINDOWPOSCHANGING`，先调用 `DefSubclassProc`；`IsZoomed` 为 false 时给移动/缩小请求加 `SWP_NOMOVE | SWP_NOSIZE`，让临时普通尺寸不落地；最终最大化状态的请求正常放行。窗口消息处理里不调用新的缩放接口。
- 退出明确调用 `Maximize()`，末尾统一 `RootGrid.UpdateLayout()` 一次；`finally` 必须解除边界拦截并恢复 DWM 动画。拦截不得扩散到普通拖动、缩放或最小化路径。
- `PreviewOverlay.MeasureOverride` 在测量子控件前按本轮可用尺寸决定分栏；图片沿用原视口、倍率和加载链路，无黑幕、淡回、计时器或跨帧等待。
- 诊断：`fullscreen exit` 的 `suppressedRestoreBounds`、`maximized`、`client` 用于复现后核对，不是视觉验收。

### 错误及已废弃记录

| 旧说法／方案 | 状态与原因 | 正确操作 |
| --- | --- | --- |
| 黑色 `FullscreenCurtain`、1% 透明度、120ms 淡回 | **已废弃**：掩盖中间画面且引入可见黑帧 | 不遮挡，阻止错误窗口边界落地 |
| 连续入队若干次即等于等待渲染帧 | **错误**：队列回调次数不代表呈现次数 | 不用递归入队判断画面完成 |
| 关闭 DWM 动画＋`UpdateLayout` 已保证无缝 | **结论错误**：消除了跳变仍可露出临时普通窗口；布局完成不等于已上屏 | DWM 仅辅助，最大化退出须执行上述边界拦截 |
| 缓存／复用 presenter 就能消除中间帧 | **不充分**：复用实例仍可能经过普通窗口状态 | 复用实例，同时拦截非最大化的中间几何变更 |
| 闪烁已经证明是 WinUI 合成层缺陷 | **未证实／误导**：截图明确显示后方应用从临时小窗口之外露出 | 先核对原生窗口边界与状态，不能直接归咎框架 |
| 延时、`RequestCommitAsync`、`DwmFlush`、无边框铺屏 | **未采用**：此前仅为候选，不是已验证修复 | 不写成当前操作规范，不用阻塞或延时替代定位 |
| “透明度为 0 必然永久停绘制” | **未经证实的历史推断**；现有打开／关闭动画仍使用 0 | 按具体控件生命周期查证，不全局强制改成 0.01 |

接口参考：[WM_WINDOWPOSCHANGING](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-windowposchanging)、[DWM 窗口属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)。


## 主窗口最小尺寸

主窗口缩放保护：参考本机 Photos（2026.11090.22001.0）正在运行窗口的原生最小跟踪尺寸，最小外框为 516×516 DIP（200% 时 1032×1032 物理像素），宽高分别限底、不锁定长宽比。`MainWindow.OnWindowSubclass` 在 `WM_GETMINMAXINFO` 中先调用默认处理，再仅补 `MinTrackSize`；每次查询按 `GetDpiForWindow` 向上取整换算物理像素。宿主沿用现有子类化安装／关闭移除，不新增公共 API 或设置；例如拖窄或拖矮均会在对应下限停止。只约束主窗口普通缩放，不修改查看器、独立浮窗、页面内部拖动或最大化边界；不新增图片解码／缓存调度。构建不替代手动拖边、跨 DPI、贴靠和最大化验收。

## 图库选择与文件操作

### 常用操作速查

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
| F5 | 重扫当前所在目录并刷新网格；停在「图库/全部」时重扫全部已添加目录。后台进行，不打断浏览，结束后用通知条报告新增/更新/移出数量 |
| 拖动缩略图 | 拖动时只跟随 96px 小缩略图；从已选图片起拖时以复制语义拖出整个选区（拖到资源管理器为复制），拖到侧栏目录则二次确认后批量移动；多选时从未选图片起拖，改为只选中并拖出该图；缺失文件或模糊保护命中时取消整次拖出 |
| 点击窗口 XAML 内非交互空白 / Esc | 清空选区并退出选择模式；卡片、按钮、输入框、滚动条等交互区域不触发空白退出；查看器打开时 Esc 优先处理查看器 |
| 预览中 `Esc` / `←` / `→` | 全屏时先退出全屏，再关闭 / 上一张 / 下一张；视频内容区 `Space` 播放或暂停 |
| 右键缩略图 | 图库文件操作菜单；非多选时提供“选择”进入多选；图片（视频除外）提供“设置为桌面背景”，超过 20 MB 只提示、不生效；单图提供“复制图片内容”（视频不提供） |
| 滚动/悬停 | 滚动时右侧时间线淡入，2 秒后淡出 |
| 拖动时间线轨道 | 按位置定位（标记无动画跟随） |
| 汉堡键 | 折叠/展开左侧导航（320 ↔ 48） |
| 导航「图库 / 收藏夹」 | 切换数据源，内容淡出 30ms / 淡入 20ms |
| 搜索框 | 按文件名过滤 |
| 命令栏 | 选择、排序、筛选、库类型与大小、更多（全选/不选） |
| 标题栏齿轮 | 打开设置页（整页替换内容区，标题栏与导航保留） |
| 设置 · 应用主题 | 浅色 / 深色 / Windows 默认，切换即时生效 |

无输入框、菜单、模态占用时才分发图库快捷键；焦点、候选 Tab 与原生控件键位见下文对应章节。幻灯片按钮按约定不提供。

### 选择与范围锚点

图库主页选择规则：普通模式点击图片打开预览，选择模式普通点击/ Ctrl 点击切换单张并保留其他项；Ctrl 也可直接进入选择模式。Shift 按当前排序/筛选后的完整数据列表选取锚点到点击项的连续范围，替换原选区；Ctrl+Shift 追加范围。锚点使用稳定图片 ID，普通单图选择更新锚点，连续 Shift 不移动锚点；无有效锚点时仅选择点击项，退出模式或取消全选清空锚点。范围不得只遍历可见 `PhotoTile`。

### 键盘分发与图片焦点

图库键盘统一由窗口 `PreviewKeyDown` 通过 `GalleryPage.HandleGalleryKeyAsync(args, contentFocus)` 分发，不再挂页面 `KeyDown`；异步命令在首个 await 前标记已处理。窗口负责图库可见性、查看器、输入框、Alt 组合及菜单/模态隔离，`HasContentKeyboardFocus(focused)` 区分图像内容和工具栏/滚动条等原生控件；后者保留自身方向键、Space 与 Enter；图库的 Enter、Delete、F2/F3 只在图片内容区接管。Ctrl+A/C/Ctrl+Shift+C 仍为非文本输入时的图库命令，不把 Ctrl+Enter、Ctrl+Space 等未定义组合当作裸键动作。Ctrl+A 对当前完整列表全选，Ctrl+F 保留搜索。Esc 仍用窗口加速器，菜单/模态及文本输入不抢键，查看器优先。

焦点与选区分开：稳定 ID 保存焦点，`PhotoTile.KeyboardFocused` 默认 false，只控制不参与命中的独立外边框：4 DIP 粗、外扩 6 DIP（与画框净距 2 DIP），圆角随外扩增加，使用高亮主题强调色；主题切换更新颜色，回收清空、宿主绑定恢复。普通方向键只移动焦点；左右按当前列表顺序前后移动：行末右移到下一排首张、行首左移到上一排末张，仅列表首尾停止且不循环；上下在相邻行找横向中心最接近项，同距按列表顺序，无相邻行时停止。首次无焦点时取首个已选项，否则当前视口首项，只建立焦点不再移动一步。Shift+方向键替换锚点范围，Ctrl+Shift 追加；Space 切换勾选并更新锚点。滚动视口向左/上/下各扩展 6 DIP，图库内容增加同量左/上/下边距抵消，图片原位置、可用布局宽度和间距不变；移动后按包含外框的真实视口坐标仅滚动必要距离；失效 ID 清除，下次重建焦点；鼠标点击更新逻辑焦点，关闭查看器保留选区及焦点。外框还要求图库激活且实际焦点位于图片内容区；Tab 离开、菜单/查看器接管或窗口内鼠标按下时隐藏，仅保留逻辑图片 ID。Tab 只切换区域，不恢复某张图片的提示；方向键等图库命令明确定位图片时再显示，不清空选区。鼠标全选与键盘 Ctrl+A 共用选择逻辑，但鼠标全选不强制转为键盘焦点。

### 文件操作与刷新

Enter/F2/F3 作用于焦点图。Delete、Ctrl+C、Ctrl+Shift+C 均选区优先、无选区时取焦点图；Delete 复用一次默认取消的回收站确认弹窗，Shift+Delete 不做永久删除。Ctrl+C 统一写入文件列表、Copy 语义，不解码图片；读取任一文件失败则整次取消并报告文件名、保留原剪贴板。Ctrl+Shift+C 复制同一目标集合路径，单图右键“复制图片内容”仍提供像素格式。F5 主动刷新：重扫当前所在目录后重建目录树与网格，停在「图库/全部」时扫全部已添加目录；扫描按根目录进行（缺失判定也是根级），因此在子目录里刷新的是它所在的根，避免把其它目录误标为缺失；后台串行执行、退出时中断，扫描中不阻塞浏览，结果用通知条报告新增/更新/移出数量。新增键盘入口仅由窗口主 UI 线程使用，不增加配置或数据库字段；不增加 Ctrl+X/V、Home/End、反选、撤销。移除文件夹成功后同样强制重建网格（即使当前就停在「图库/全部」，`SetDirectory` 的“目标没变就返回”不生效），被移除目录的图片立即从界面消失。上述交互与跨应用粘贴均待人工验证。

### 拖出与侧栏移动

图片按下后捕获指针，移动超过原有 12 DIP 阈值时先释放捕获，再启动系统拖放；快速移出卡片仍继续判定起拖。松手、取消、捕获丢失或卡片卸载时清理手势，松手／取消／卸载同时释放捕获；左上角 44×44 DIP 的收藏按钮区域继续不作为起拖点。桌面沙箱已验证快速移出卡片可进入系统拖拽启动事件，跨应用落点和长按手感仍须单独核对。

图库拖出规则：拖已选图片导出全部选中记录（包含屏幕外项目）；在选择模式拖未选图片，先清空原选区、选中起拖图，再只导出该图。文件列表须写入 `DragStartingEventArgs.Data`，允许和请求操作均为 Copy，不搬移源文件；缺失文件及模糊保护沿用原整批拒绝语义。点击窗口 XAML 内非交互空白通过根级已处理事件路由退出并清空；卡片、按钮、输入框、导航项、滚动条及分隔拖动把手不触发退出。Esc 继续使用窗口级加速器，查看器优先；`GalleryPage.ExitSelection()` 无选择时返回 false。只适用于图库/收藏夹/目录视图，不套用待审核的批量网格规则。修饰键、跨屏范围、拖出到外部程序及空白/Esc 的实际表现待用户手动确认。
拖入侧栏目录：应用内拖拽经过某个目录项时，该项直接借用自带的悬停视觉亮起（与鼠标悬停完全同一套 PointerStates，不另造底色或描边）；同一时刻只亮一项，选中项落回 `PointerOverSelected` 而非普通悬停，父项 DragLeave 不清除已被子项接管的亮起；离开、落到该项或拖拽结束即熄灭，目录项被移除时引用作废；外部文件拖入不亮起。状态由 `NavigationViewItemPresenter` 承载，改的是 presenter 而不是 item 本身。文件监听刷新目录树时复用已有导航项并原位更新计数，仅增删实际变化的目录项，避免在指针输入期间整树替换。

## 模态弹窗与退出

- 主 UI 线程的 `ContentDialog` 统一使用 `await Gallery.Services.DialogService.ShowAsync(dialog)`；默认同一时间只显示一个，已有弹窗或正在退出时返回 `ContentDialogResult.None`，不排队重放旧操作。宿主仍设置 `XamlRoot`、内容及按钮，并且只在明确返回 `Primary`／`Secondary` 时执行相应操作。
- `DialogService.IsShowing` 可在创建昂贵内容前检查；`Dismiss()` 供主窗口退出时关闭当前弹窗。它只管理主 UI 线程的模态框，不用于 `Flyout`、原生文件选择器或多 UI 线程窗口。编辑器的取消、动画和后台任务清理必须放在 `finally`。
- 新建、删除文件夹后的全根扫描放在后台；扫描串行锁与数据库锁分开，目录枚举、图片头读取不能包在全根数据库锁中。后台扫描进度和错误投递到 UI，退出后不再刷新控件或启动新的审核／文件夹任务。
- 主窗口退出先阻止新操作、取消模态框，等待审核整批任务及文件夹操作结束，再由服务层停止监听／桥接并关闭数据库。不能只等待批处理中当前那一个 `Task.Run`，也不能以数据库锁暂时空闲作为全部任务结束的证明。
- 本轮未启动或操作应用。需人工验证快速重复编辑、弹窗取消后再次打开、扫描期间浏览，以及归档／导入／清理过程中退出。
- 详情层（`PreviewOverlay`）是全窗遮罩，切页必须先收掉：`Nav_SelectionChanged` 一进来就调 `Preview.CloseImmediate()`，不走 300ms 飞回动画。飞回的终点是图库里那张格子，切页后它已不可见，动画只会和新页面抢时间。标题栏的待审核 chip 与「查看并归档」提示卡都经由 `Nav.SelectedItem` 进同一处，所以只需在这一处拦。
- 导航历史由窗口统一保存会话内最近 50 个位置：图库、收藏夹、资料库、待审核、侧栏文件夹和设置首页／二级页共用，重复位置不追加；后退后进入新位置会清除前方历史，失效的目录项回放时跳过。鼠标 XButton1／XButton2 和 Alt+←／→ 后退／前进，WinUI 内容区通过根级指针事件识别侧键；主窗口原生消息作为其他输入路径的入口。预览打开时后退先正常关闭预览、前进不切页，预览不入历史；弹窗、菜单或下拉框打开时侧键和历史快捷键不切页，保留现有编辑与取消行为。设置面板沿用自身滚动位置和后台任务，不因历史切换重建。前台沙箱模拟鼠标输入已验证图库／收藏夹、设置二级页往返和一个模态弹窗；实体鼠标、其他弹层及交互手感待人工验收。
- 详情面板的标签贴纸支持左键双击复制：复制的是**屏幕上看到的那段文字**（有中文名就是中文名，没有就是原名），不是库里的原名。宿主收到 `DetailPanel.TagCopied` 后弹提示；剪贴板失败只记日志，不打断预览。

## 搜索输入框

待审核圆环、工作台、喷涂与回收的完整行为只维护在 [ARCHIVE_REVIEW.md](ARCHIVE_REVIEW.md)；共享候选输入规则如下。

- 封装默认圆角取原生主题资源 `ControlCornerRadius`，再同步给内部控件；不得用 UserControl 默认的 0 覆盖原生圆角。内容及内部控件双向拉伸，按宿主分配尺寸布局，不通过放大宽度或负边距补偿边缘。本轮仅静态/编译核对，边缘完整性待人工确认。
- 顶部图库搜索、资料库、筛选、标签编辑与待审核标签输入统一使用 `Controls/SearchInputBox.cs`，基于原生 `AutoSuggestBox`，不另绘模板。默认 `AllowFocusOnInteraction=true`、`PreventAutomaticFocus=false`；文本编辑、选区、键盘、输入法及候选展示交给原生控件。普通资料编辑字段仍用 `TextBox`。
- `AutoSuggestBox` 是密封类，组件用 `UserControl` 内嵌原生控件，不继承或修改其模板。公共 API 转发 `Text`、`PlaceholderText`、`TextChanged`、`ItemsSource`、`QuerySubmitted`；显式聚焦用 `FocusSearch()`，尺寸、可用状态由宿主设置，圆角同步到内部控件。例如 `new SearchInputBox { PlaceholderText = "搜索标签", Width = 300 }`。转发属性不是依赖属性，当前仅供 XAML 初值和代码读写，不提供完整 AutoSuggestBox API 或绑定契约。事件 sender 为内部原生 AutoSuggestBox。宿主负责查询、防抖、过期结果过滤和提交；普通搜索不提供 `ItemsSource`，不额外弹出候选。标签搜索保留候选选择、键盘提交与输入新标签；`TextChanged` 使用 `AutoSuggestBoxTextChangedEventArgs`，候选查询仅处理 `UserInput`。
- 标题栏设置 `PreventAutomaticFocus=true` 并提供 `DefaultFocusAnchor`，保留原 Photos 主动聚焦判断，允许点击、Tab 和 `FocusSearch()`，拦截启动或内容卸载等非主动聚焦；内部文本编辑器已有焦点时允许其正常转移。其他搜索不启用此保护。旧 `PhotosSearchBox` 已移除，不再维护第二套控件。
- 含输入框的普通 `Flyout`，触发按钮必须允许交互聚焦。WinUI 会把目标按钮的 `AllowFocusOnInteraction` 传给 `FlyoutPresenter`；仅修改输入框或 Flyout 本身不足以替代宿主配置。当前 `FilterButton` 显式设置为 `True`，其他菜单按钮不变。
- 不统一查询范围：资料库查实体，筛选查已入库标签及其中英文名、别名；组件不访问数据库，不适用于普通资料编辑字段。

标签身份折叠、候选语言和贴纸解析见归档文档“标签身份与显示”；查询、防抖与提交仍由宿主负责。

## 标签候选的 Tab 导航

- `SearchInputBox.EnableSuggestionTabNavigation` 默认 `false`；仅待审核页与标签编辑器的作品、角色、作者、类型、自定义输入设为 `true`。分级及图库、筛选等其他搜索入口不启用。
- 用途：已显示且非空的候选列表支持真实键盘焦点。搜索框按无修饰 Tab 进入第一条，候选中 Tab 向后循环；Shift+Tab 向前，第一条退回搜索框，再按 Shift+Tab 可按系统顺序离开。Tab 导航不替换输入文字、不关闭列表；Ctrl/Alt 组合不拦截。无候选或 Esc 已关单时保留系统 Tab。
- 候选行的 Enter 使用原生 `QuerySubmitted`，宿主仍负责选中项的标签写入、清空 `Text` 和 `ItemsSource`；组件在宿主处理后恢复输入焦点。示例：`new SearchInputBox { EnableSuggestionTabNavigation = true }`，继续沿用现有 `TextChanged` 查询及 `QuerySubmitted` 提交。
- 实现依赖原生 AutoSuggestBox 的文本框与弹层 ListView；不提供独立查询、排序或存储。Tab 进入时把整棵原生候选视图临时转入独立 Popup，沿用原有位置、行模板及原生提交，结束时归还；避免 AutoSuggestBox 失焦强制关单。导航期间暂时关闭 `UpdateTextOnSelect`，完成后恢复。此轮仅静态与构建核对，Tab 循环、反向退出、Enter、Esc 和鼠标混用仍需人工验收。

## 设置页的选项与识别配置布局

新用户默认关闭开机自启、浏览器桥接和按标签自动分目录；已有配置文件中的明确值保留。分发与依赖见 [分发说明](DISTRIBUTION.md)。

设置采用首页加五个二级页面：

| 页面 | 内容 |
| --- | --- |
| 首页 | 主题、标签显示、敏感内容显示/模糊强度、托盘、自启、打开方式，以及五个分类入口 |
| 导入与归档 | 归档提醒、按标签分目录、历史保留、文件夹自动标签及层级 |
| 资料库 | 资料库包拖拽导入、已导入条目列表与行内移除确认 |
| 智能识别 | 模型、设备、导入自动识别、全库重新识别与进度、标签类型映射 |
| 浏览器插件 | 连接状态/开关、密钥、待处理记录、端口 |
| 存储与诊断 | 缩略图缓存清理、错误查看/清空 |

- 页头齿轮固定为 24 DIP；首页“设置”是不可点击的 `TextBlock`，进入二级页后在同一布局槽位换为原生 `Button`，按钮清零最小尺寸、内边距和边框，内容文字与首页标题共用 24 DIP 及 `TitleTextBlockStyle`。齿轮常显，一级标题位置/字号/间距保持一致，后方追加分隔符及同字号二级标题；无独立返回箭头。键盘进入二级页时聚焦“设置”按钮，鼠标进入不强制键盘焦点，返回仍恢复首页入口和滚动位置。
- 分类入口使用原生可点击 `SettingsCard`，整行可操作，右侧箭头表示进入，说明行显示当前状态摘要。沿用主题资源、卡片圆角和边框；页头及内容共享最大宽度 958 DIP 与原有边距，不增加独立侧栏或第三层导航。
- `SettingsPage` 内六个面板常驻同一可视树，以 `Visibility` 切换，共用 `SettingsScroll`；不新建 `Frame`、设置对象、识别服务或任务，不因返回取消或重启识别。默认显示首页，会话内分别保存各页滚动位置；二级页点击原位的“设置”按钮或按 Esc 回首页，恢复入口焦点和滚动位置；首页 Esc 不再处理，菜单/模态/文本输入继续优先。焦点来源沿用鼠标 Pointer/键盘 Keyboard，不手绘定位框。
- 导航由页面私有方法处理；`SettingsPage.TryGoBack()` 供窗口级 Esc 调用，已在首页时返回 false，窗口负责弹层/文本输入隔离；无新增持久配置或数据库字段。例如从首页点击“智能识别”进入模型与扫描配置，通过页头返回；全库扫描继续更新首页识别入口摘要。宿主继续复用一个 `SettingsPage`，应用退出仍由原有 `StopBackgroundWorkAsync()` 负责取消/等待任务。
- 简单选项沿用单行 `SettingsCard`；识别的两块复杂配置保留既有标题/操作/说明/选项的分层卡片。首页显示只影响当前偏好的项目，模型管理与批量任务不再放在“敏感内容”显示组下面。
- 未启动应用；页面观感、深浅主题、窄窗口、鼠标/键盘进入与返回、滚动位置恢复、识别过程中往返仍需人工验收。

- 识别模型只保留一个导入区域（拖压缩包／文件夹，或手动选目录），模型格式为 PixAI v1.0 的 `model.onnx + config.json`。四类勾选控制同一次推理的写入分类，不设置 WD／PixAI 分工或第二个模型入口；分级未勾选时主动扫描／应用预览不更改原分级。
- 同类模板沿用此排版规则：横向短标签单选／复选项按内容宽度紧凑排列，用容器间距控制选项之间的距离，不依赖控件默认最小宽度撑开布局。新增或修改同类选项组时一并核对；样式优先限定在页面或选项组，保留原生交互模板，不全局压缩图库勾选和其他用途控件。
- 设置页通过局部隐式样式继承原生 `DefaultRadioButtonStyle`／`DefaultCheckBoxStyle`，仅将 `MinWidth` 设为 0，让选项按内容宽度排列；横向单选／复选组统一间距 24 DIP，保留原生高度、焦点与选中反馈。「显示方式」及两组识别标签均适用，不扩展到图库勾选或其他弹窗。
- 「导入时自动识别」「重新识别」使用分层卡片：顶部图标、标题与右侧开关／操作按钮；说明、标签勾选和写入策略放在标题下方并左对齐。不将多行配置塞入普通 SettingsCard 的右侧内容槽。
- 「识别模型」「资料库」分别放在智能识别／资料库分组，使用整行 `Border`，不塞进 `SettingsCard` 右侧槽。标题下是 96 DIP 拖拽区，再下方是条目列表与状态。拖拽区用页面局部不透明底色（深色 #151515、浅色 #E3E3E3，高对比度跟随系统），低于外层卡片亮度；禁止重复叠加半透明卡片背景。拖入时只改变轮廓色，不抬亮底色。
- 手动选目录按钮在框内右上角，内缩 8 DIP，36×36、透明背景、无边框、16 DIP 的 E8F4 字形，与导航栏图库按钮一致。居中提示横向预留 48 DIP，避免和按钮重叠。**已废弃**：标题行外置导入按钮，现按框内右上角布局。
- 条目行左侧名称和说明，右侧 × 触发行内二级确认；确认键用 `AccentButtonStyle`。模型完整路径仅在条目文字的悬停提示中显示，不单独占行。导入／删除互斥，错误显示在实际操作分组；模型存在时禁用选目录入口。删除语义与支持格式见 [README.md](PACKS_AND_RULES.md#资料包热插拔)。
- 「导入与归档」的目录规则使用整行列表：启用勾选、条件／目标说明、上移／下移、编辑、删除；新增／编辑通过 `DialogService` 打开带标签类型、原名、相对目标和分级／画师选项的滚动表单。非法输入留在弹窗内显示原因，不先写入。首尾顺序按钮禁用；配置读坏时阻止保存。保存反馈明确“已有图片未移动”；个人备份恢复后刷新列表。数据与 API 的唯一合同见 [个人目录规则](PACKS_AND_RULES.md#个人目录规则)。
- 资料库设置另设「个人规则备份」卡片，提供 JSON 导入／导出；不把个人规则放进公共资料包条目列表。恢复弹窗明确区分「保留现在的设置」「采用备份中的设置」，默认取消，经 `DialogService` 统一打开，文件选择器绑定当前窗口。导入／导出与资料包操作互斥；后台操作不接管界面。数据格式及 API 见 [公共资料与个人规则](PACKS_AND_RULES.md#公共资料与个人规则)。
- 设置导入位置遵循 `PackFolders.Root`：开发输出向上找到 `Gallery.csproj` 后使用项目根目录，独立分发才使用程序目录；路径显示和删除边界都复用该值，不各自拼接 `bin` 路径。源码 `DataModels` 与运行时 `models` 分开，存储合同见 [资料包热插拔](PACKS_AND_RULES.md#资料包热插拔)。
- 包导入／移除 API 与后台操作职责见 [包管理 API](PACKS_AND_RULES.md#包管理-api-与宿主职责)；本节只维护设置布局。
- 沿用系统卡片背景、边框与圆角主题资源，内边距 16 DIP，图标列 20 DIP、列距 16 DIP；四个复选框取消默认最小宽度，间距 24 DIP，两组排列一致。
- 「已有标签」单独一行；进度／结果文字与其并排并允许换行，不再挤占顶部按钮区域。控件名称、事件和设置保存逻辑保持原有契约。预览无分级变化但有勾选分类的标签结果时，仍允许应用。
- 用户已确认当前分层卡片及紧凑选项的排版效果。助手已核对 XAML 与构建，未启动应用；窗口缩窄与 DPI 缩放、扫描进度和取消按钮出现后的布局仍需人工确认。

## 输入焦点与标题栏命中

- 标题栏按钮、图库工具栏、菜单项、收藏按钮及查看器顶栏/视频条按钮保留原生焦点视觉。鼠标点击可以获得焦点，但不得主动标成 `FocusState.Keyboard`；键盘导航保留系统定位框，不使用叠加层或修改按钮边框重画。文本输入的插入光标、编辑状态及控件悬停/按压反馈不属于键盘定位框，不因此隐藏。
- 主页 `PhotoTile` 及其内部收藏按钮固定 `IsTabStop=false`，不按可见图片逐个参与 Tab；卡片收藏仍按鼠标悬停显示。查看器的独立收藏按钮继续保留原生键盘操作。图片逻辑焦点由图库方向键建立，Enter 打开、Space 勾选；Tab 切区时隐藏图片定位框。此修改不改变选择、加载或收藏写库逻辑。
- 目录栏 `PaneResizer` 是单个 Tab 入口，使用系统焦点视觉；Left/Right 每次调整 8 DIP，在既有 MinLength/MaxLength（默认 180–500）内裁剪，复用 `WidthChanged` 和 `DragFinished` 保存宽度；鼠标按下按 Pointer 聚焦，原鼠标拖动流程保留，无新增配置或公共 API。
- `TimelineScrollbar` 是单个 Tab 入口，无项目时退出 Tab。上/下箭头按钮自身不单独参与 Tab；键盘焦点显示原生框并保持时间线可见。Up/Down 复用现有 177 DIP 滚动，PageUp/PageDown 按视口高度滚动，Home/End 到首尾；这些键只在时间线聚焦时生效，不能误入图库图片导航。鼠标按下使用 Pointer；隐藏定时器不淡出仍有键盘焦点的时间线。
- 查看器窗口加速器和冒泡按键共用 `PreviewOverlay.TryHandleKey(key)`：仅处理查看器内部焦点的裸键，无菜单/模态、文本输入或 Ctrl/Alt 组合占用；按钮、滑块和选择控件保留自身按键，Esc 可从非文本控件退出全屏/关闭查看器。返回 false 时宿主不得吞键。视频内容区 Space 播放/暂停；按钮上的 Space 仍激活按钮，滑块左右键仍调节滑块。
- 图片缩放条默认收起，鼠标悬停或查看器收到 Tab/Shift+Tab 时显示，键盘焦点/下拉/拖动占用期间保持；全屏顶栏和翻页箭头同样提供 Tab 入口，不能在仍持有焦点时被鼠标移出隐藏。图库卡片、原生按钮和查看器共用“焦点可存在、键盘定位框只服务键盘”的原则，文本插入光标不受影响。
- 查看器滚轮即缩放：以指针位置为锚点，向上滚放大、向下滚缩小，沿用缩放条同一套倍率与限幅，不再判断 Ctrl。原来「普通滚轮上一张／下一张、Ctrl+滚轮缩放」的 photos 语义已弃用，翻页只保留方向键与顶栏翻页按钮；`ImageViewer.NavigateRequested` 一并移除，不留下无人触发的翻页事件。
- `CustomCommandBar` 用于现有命令栏的菜单 UIA 分组及 Photos 焦点恢复，不用于自绘焦点或图片卡片。按本地反编译的 `CommandBarAccessibilityBehaviourFixer`：无弹层按钮点按后必要时补焦点、菜单打开聚焦首个可用项、非溢出入口的弹层关闭返回入口，统一使用 `FocusState.Programmatic`（原版枚举值 3），不能误写为 `Keyboard`（值 2）。Loaded/Unloaded 成对接线且避免重复订阅，系统负责最终焦点视觉；无新增公共 API、配置或自定义焦点默认值。宿主沿用原生样式并负责业务点击及菜单互斥，例如标题栏通过 `<controls:CustomCommandBar><AppBarButton ... /></controls:CustomCommandBar>` 承载设置入口（40×48、16 DIP 齿轮）。图库工具栏不再禁用按钮和菜单项的系统焦点视觉。
- 筛选弹层按 Photos `GalleryCommandBar` 在 Closing 记录 Target，下一次命令栏布局更新时校正焦点；入口不在溢出区或“更多”仍打开时返回入口，入口已收进关闭的溢出区时返回内部 `MoreButton`。记录仅消费一次，页面卸载时清空；其他互斥弹层已打开时不抢回焦点。子菜单打开只在入口不处于溢出区时收起命令栏，保留溢出菜单内部操作路径。
- 人工核对：纯鼠标点设置/待审核及开关菜单时不强制键盘框；鼠标打开再用 Esc 关闭属于混合输入，允许原生键盘提示，不能以是否有下拉箭头或“鼠标打开过”决定显隐。筛选 Esc 必须回正确入口，宽/窄窗口及收进“更多”后的返回均需人工核对；Tab/Shift+Tab、Enter/Space 打开菜单、方向键导航和 Esc 返回保留原生焦点提示；鼠标与键盘交替、深浅主题及高对比度仍需验证。此规则不替换图库图片已有的独立焦点外框。

- 托盘隐藏／唤回不切换标题栏模式。仅在托盘隐藏后的首次激活，由 `RefreshCaptionAfterTrayHide()` 重算命中区域、读取真实鼠标屏幕坐标，经 `WM_NCHITTEST` 得到当前位置后发送一次 `WM_NCMOUSEMOVE`，同步系统按钮状态；不移动鼠标、不模拟点击。关闭、恢复及各类首个输入的有界 `caption` 日志暂留，红色残留是否消除待人工验证。
- 收进托盘同时进入低功耗：释放图片像素缓存、预览层与 ONNX 会话，唤回沿用当前画面、按需重新解码；当前页、滚动位置、图库数据和后台服务不变。普通最小化不触发清理。实现细节见 [图片加载](IMAGE_LOADING.md#当前实现)。
- 统一控件不等于统一宿主行为。含文本编辑的页面须提供可聚焦的中性锚点：点击页面空白/说明文字时，将已有文本编辑焦点转移到锚点；不能只隐藏光标或清空文本。设置页使用 `SettingsRoot`，透明背景保证空白可命中，关闭系统焦点框；该锚点会参与 Tab 导航。
- 空白点击处理需识别原始事件来源及祖先，跳过输入框、候选输入、按钮、滑块/滚动条及开关，不拦截其点击、文本选区或候选操作。设置页接收已处理的指针事件，避免 ScrollViewer 吞掉空白点击，但不将事件标记为已处理，不改变保存时机。不默认扩展到其他页面或独立 Popup。
- `TransformToVisual` 与 `ActualWidth/Height` 得到的是 DIP；交给 `InputNonClientPointerSource.SetRegionRects` 前必须乘当前 `XamlRoot.RasterizationScale` 转成物理像素。边界用左上向下、右下向上取整，覆盖完整控件边缘。不得沿用旧源码中“SetRegionRects 使用 DIP”的错误注释，也不得通过强制 IBeam 光标掩盖命中区域错误。[微软标题栏交互区域示例](https://learn.microsoft.com/zh-cn/windows/apps/develop/title-bar#interactive-content)
- 标题栏仍使用原自定义布局；搜索框尺寸、标题栏/根布局尺寸、缩放比例变化后重新登记命中区域，隐藏后不得保留过期区域。保持现有托盘恢复入口的重算，不把普通窗口移动变成逐帧坐标刷新；缩放变化监听随根元素加载/卸载成对接线。
- 人工核对须包含：设置页点击输入→点击空白→再次输入、只读框选取复制、按钮/滑块不被抢焦点；首页搜索框左/中/右端悬停与点击、文本拖选、缩放/最大化/还原/跨 DPI 显示器及托盘恢复。编译通过不能代替这些验收，本轮未启动或操作应用。

## 一、滚动条与内容必须分离

### 为什么需要专门一条

WinUI 的竖向滚动条是**浮层**：它画在滚动内容之上，**不占布局宽度**。
于是「内容撑满滚动区」就等于「滚动条压在内容右侧」——文字被切掉、按钮点不到、输入框右缘被盖住。
滚动区容器自己不会替内容让位，**必须显式留白**。

注意作用层级：留白要加在**滚动区内部**（`ScrollViewer.Padding` 的右侧，或滚动内容根的 `Margin`/`Padding` 右侧）。
加在滚动区的**父容器**上无效——浮层条仍然压在内容上。

- 正确：`<ScrollViewer Padding="32,0,48,0">`、内容 `Margin="0,0,48,0"`、`new StackPanel { Padding = new Thickness(16, 14, 16, 16) }`
- 错误：父 `Border Padding="16"` 里套一个内容贴满的 `ScrollViewer`（右侧那 16 在滚动区外面）

### 规则

| # | 规则 |
|---|---|
| R1 | 任何会出现竖向滚动条的滚动区，内容右侧必须留出滚动条占位：**页面级（整页滚动）48**；**面板 / 对话框 / 浮出层内的滚动区 16**（16 是下限，只够放条本身，几乎没有间隙） |
| R2 | 留白加在滚动区内部（见上文），不要加在父容器上 |
| R3 | 用自绘滚动条替代原生条时（图库的时间线），留白取该控件宽度（现为 48），自绘条放在留白区内，不覆盖可点内容 |
| R4 | 横向滚动区同理，底部留出占位（本项目默认禁用横向滚动） |
| R5 | `ListView` / `GridView` 的条由内部 `ScrollViewer` 提供，用 `Padding`（如 `new Thickness(0, 0, 16, 0)`）给条目让位；条目模板的最右列不要贴边 |
| R6 | 不要用「看起来没压到」来判断：浮层条会淡出，只有明确留白才算分离 |
| R7 | 原生滚动区和宿主驱动的内容位置统一使用 `Controls/ContinuousScrollBar.cs` 的连续交互。WinUI 通过全局 `ScrollBar` 模板接入，原生条仅保留外层布局、范围与无障碍契约，不再接收轨道指针；不要叠加原生 `LargeChange` |

数值来源：48 与 Photos 对齐、也等于自绘时间线宽度（本文件 Photos 参考参数表）；16 是详情面板既有取值（`Controls/Viewer/DetailPanel.cs`）。原生浮层条的宽度随系统与主题变化，没有可引用的常量，所以上表用项目约定值，而不是从条宽推导。

### 通用连续滚动条（`ContinuousScrollBar`）

- 适用：圆环、照片条等「滚动条位置 = 内容连续位置」，需要拖动 1:1、点击轨道任意位置、宿主自己驱动内容位置的场景。
- 普通 `ScrollViewer`、列表、对话框及下拉菜单内部的滚动区：由 `App.xaml` 的全局 `ScrollBar` 模板使用 `ContinuousScrollBarPresenter` 自动接入，按 R1–R6 保留原内容留白。无需替换容器或逐页调整尺寸。
- 默认：竖向命中区 16、轨道 2、滑块 6、滑块最短 28。待审核圆环把它放在圆环与分割线之间的列间距里，不压卡片。
- 宿主订阅 `ValueChanged` 把值直接映射到内容位置；`InteractionCompleted` 再提交详情/加载清晰图。自动隐藏属于组件默认行为，各宿主不得再设置条的 `Opacity` 或维护单独的隐藏计时器。
- 所有连续条默认首次可滚动时显示、30ms 淡入，空闲 700ms 后用 200ms 淡出；悬停条本身或拖动/点击动画期间保持显示。显隐只影响内部指示层，隐藏终点为 0.01，透明命中区和原布局位置/尺寸不变，指针移入仍能唤醒。
- 实际值和范围变化会自动唤醒。宿主收到不一定改变偏移的滚轮、滚动按键或网页原生输入时调用 `NotifyActivity()`；页面隐藏时调用 `HideIndicator()` 停止显隐动画/计时并立即收起。卸载由组件自动清理，不新增可关闭自动隐藏的开关。
- `Step` 默认 1（松手吸附整数）；`Step = 0` 保留连续值。设置 `Orientation` 会重置控件默认宽高，自定义尺寸请在其后覆盖。
- `ThumbCrossOffset` 默认 0，可匹配原浮层细滑块的贴边位置；`MinimumThumbLength`、`ThumbThickness`、`TrackThickness` 沿用原公共属性。
- `CancelInteraction()` 停止动画、释放指针捕获且不吸附；有进行中的交互时发出 `InteractionCompleted`。宿主在外部滚轮/方向键接管、隐藏或卸载时调用。
- `ContinuousScrollBarPresenter` 是原生条的模板宿主，无额外页面 API。保留原 `ScrollViewer` 模板中滚动条的单元格、边距及显隐；按当前 SDK 资源读取 12 DIP 条宽、8 DIP 展开滑块、30 DIP 最短滑块及两端按钮保留区，紧凑态使用 2 DIP、32 DIP 最短滑块与原贴边偏移。直接 `ChangeView(..., disableAnimation: true)` 同步滚动，保留滚轮、键盘、虚拟化与外层原生条的无障碍范围。
- 网页调参面板保留 DOM `overflow:auto`，只隐藏其原生条；测量原滚动条占位后补回同等右内边距，宿主 Canvas 在对应 CSS 区域叠加同一个 C# 控件。CSS 像素按网页视口与 WebView 实际尺寸换算；滚动、展开、缩放触发合并后的几何消息。无布局占位的浏览器浮层条若也未暴露有效 CSS 宽度，则保留原生条，不猜测宽度；该模式尚未完成替换。
- 限制：模板接入面向当前项目的 `ScrollViewer`，不覆盖另行引入的 `ScrollView` 或独立原生窗口。网页目前仅接入待审核调参面板，不修改浏览器扩展页面。隐藏页与拖窗快照期间隐藏网页对应条并停止其动画。位置与尺寸已按模板/DOM 几何接线，仍需用户在实际窗口手动核验。

```csharp
var bar = new ContinuousScrollBar
{
    Orientation = Orientation.Vertical,
    Maximum = count - 1,
    ViewportSize = 9,
};
bar.ValueChanged += value => content.ScrollTo(value);
bar.InteractionCompleted += () => Commit();
```

### 自查方式

```bash
rg -n "ScrollViewer|VerticalScrollBarVisibility|SetVerticalScrollBarVisibility|ScrollBarVisibility.Auto" --glob "*.xaml" --glob "*.cs"
```

每个命中都要问一句：**这条滚动区的留白加在哪一层？** 只在滚动区外面留白的，算不合规。

图库右侧那条自绘时间线滚动条（`TimelineScrollbar`，48 宽）按要求不动：它本来就在内容留白之后，不覆盖可点内容。待审核圆环的 `ContinuousScrollBar` 是 16 宽，放在圆环与分割线之间的列间距里，同样不压卡片。

`TimelineScrollbar` 的横杠移动保留 100ms 缓动；连续滚动算出相同目标时不重播，目标变化时保存当前显示位置作为新动画起点，并同步基础位置到目标，避免停止动画后退回旧位置。拖动及刷新等无动画调用仍立即定位。该调整仅作用于时间线标记，连续滚动、反向滚动、拖动和长跳表现待人工核对。

上下箭头使用 Photos `TimelineScrollbarStyles.xaml` 同款专用 `RepeatButton` 模板：保留原矩形点击热区、布局和 50ms 长按重复；悬停只改变三角前景色，按下改变前景色并按 `ScrollBarButtonArrowScalePressed` 缩放，禁用色也跟随滚动条主题资源，背景不随状态高亮。模板在 `CreateArrow` 内部创建，不新增公共 API；宿主继续通过 `Attach` 接入图库，不适用于其他通用按钮。此项仅静态核对与构建，实际主题、悬停／按下及长按观感待人工确认。

---

## 二、图片查看器的平移边界按图片自身算

- 规则：偏移夹在「图片盖住视口」的范围内——**图比视口大**就给 `(图片尺寸 - 视口尺寸) / 2` 的拖动余量（拖到边缘即停，不留空白）；**图比视口小**就固定居中，拖不动。
- 明确禁止的是老规则「图片中心不得超出画布」：它会让放大后的图片永远看不到右/下边缘。反过来，完全不设边界同样不行（能把图片拖飞、留一片空白）。
- 落点：`Controls/Viewer/PhotoCanvasState.cs` 的 `Clamp()` / `ClampAxis()`，由 `SetViewport`（非适应态）、`Actual`、`ZoomAt`、`Pan` 四处调用。**新增或改动缩放/平移入口时必须一并调用**，否则那条路径会绕开边界。
- 手动核对（助手不新增／运行 UI 测试）：至少覆盖「放大后拖到边缘正好贴边」「继续拖被夹住」「反方向拖到底不留空白」「图小于视口时拖不动或回中」。

### 视频（mp4）预览

播放控件、自动播放、声音、下一个视频、时长角标及释放文件统一见 [视频文档](VIDEO_SUPPORT.md#4-播放)。共享输入隔离仍遵守本文件 [输入焦点与标题栏命中](#输入焦点与标题栏命中)。

## 四、视觉、渲染与加载

- 景深用缩放／透视／亮度表达，默认不加高斯模糊；NSFW 滤镜只作用于图片层，文字和按钮保持清晰。
- 先占位、停稳／主动揭示后加载清晰图；保护图不得提前向网页交付清晰像素。预算和预取见加载文档。
- 不在动画／缩放内容上使用运行时 `filter: blur()`；批量 host↔web 消息，不动画 WebView；折叠／快照不等于解除窗口位置订阅。
- 审阅页的炭灰底色、10% 平均色、标签反馈和黑洞动画只维护在归档文档，不在这里重复参数。

具体加载参数与当前实现见 `IMAGE_LOADING.md`。

## 五、通用组件与代码约定

### 按钮锚定的二级下拉

- 单选下拉统一使用 `SelectionDropDown`，实现直接复用主题下拉的 `DropDownButton + MenuFlyout + RadioMenuFlyoutItem`；菜单/筛选面板同样锚定触发按钮。右键菜单、托盘菜单继续按鼠标/托盘锚点定位，不属于按钮避让范围。
- 默认 `Placement=Bottom`，与主题下拉一致。按钮样式、菜单间距、边界翻向、限高滚动、动画、焦点及轻触关闭均由 WinUI 原生控件负责；不再修改 ComboBox 的 Canvas、内部动画或逐帧计算弹层坐标。
- 公共 API：`Items`（文本项或带 `Content/Tag` 的 `ComboBoxItem` 描述）、`SelectedItem`、`SelectedIndex`（默认 -1）、`PlaceholderText`（默认空）、`SelectionChanged`、`IsDropDownOpen`、`DropDownOpened/Closed`。宿主提供内容、尺寸和选择回调，例如 `<controls:SelectionDropDown MinWidth="140" SelectionChanged="OnSelectionChanged" />`，不自行调整弹层偏移。
- 当前设置两处、筛选匹配模式、待审核状态和预览缩放全部接入。`ComboBoxItem` 仅用作现有文本/Tag 描述，不作为弹层视觉元素；选中标记和键盘导航与上方主题单选菜单一致，而非复刻 ComboBox 的覆盖式列表交互。
- 限制：适用于当前文本/数值单选，不提供可编辑输入、ItemsSource、富内容模板或动态重排时保持选中对象等完整 ComboBox 能力；这些并非当前调用方契约。
- 确认与后续约束（2026-10-20）：用户确认本次展示效果，后续文本单选下拉统一沿用此组合。已废弃 `AnchoredComboBox`：不要通过改 `Canvas.Top`、复制按钮文字或删除 ComboBox 内部动画来修补定位。全部窗口尺寸、DPI、键盘及嵌套弹层边界仍未逐项验收，不把本次确认扩大为全场景验证。

```xml
<controls:SelectionDropDown SelectedIndex="0" SelectionChanged="OnSelectionChanged">
    <ComboBoxItem Content="照片" Tag="photos" />
    <ComboBoxItem Content="系统默认程序" Tag="default" />
</controls:SelectionDropDown>
```

### 右键菜单与拖拽

- 图库网格右键「选择」只在非多选状态出现，点击后进入多选并只选中当前这张；多选状态右键只保留可批量执行的命令。
- 「设置为桌面背景」对所有非视频文件显示，走 `SystemParametersInfo(SPI_SETDESKWALLPAPER)`；超过 20 MB 不提交，只在右下角 Toast 提示。格式是否可用由系统判定，失败同样提示。
- 拖拽外观统一为 96px 缩略图（`DragUI.SetContentFromSoftwareBitmap`，光标居中），不再使用整张卡片。外部拖出仍是复制语义；应用内拖到侧栏目录为移动，必须二次确认，支持批量，拖拽数据走自定义格式，不接收资源管理器拖入的文件。

### 通用组件原则

- **位置**：通用控件放 `Controls/`，命名空间 `Gallery.Controls`，`public sealed`，与 `PaneResizer` / `TimelineScrollbar` 同级；不要埋在 feature 子目录。
- **提取**：保持原调用方默认行为；把差异做成属性（`Orientation` / `Step` / `Thickness` 等），不是只改目录/命名空间；补 XML 注释、用法示例、已知限制。
- **职责**：连续滚动条负责值/指针/动画及默认自动隐藏；宿主只报告内容活动、页面可用性并处理提交和业务，不维护重复的透明度或隐藏计时。事件用 `ValueChanged` / `InteractionStarted` / `InteractionCompleted` / `HoverChanged` 这类明确语义。
- **防回归**：初始未测量尺寸（NaN）不要参与“是否变化”比较；用 `double.IsFinite` 或显式初始化，避免 0 高/0 宽。
- **数据/设置**：持久化复用所属功能的现有配置或记录字段；导入／去重／归档的数据合同见 [导入与归档数据约定](ARCHIVE_REVIEW.md#导入与归档数据约定)。
- **依赖**：不新增 NuGet/依赖，优先现有 `ImageLoader` / `ThumbnailService` / SDK。
- **文本复制**：查看器详情面板里的文件名、目录、来源网址、作者评论都用 `TextBlock.IsTextSelectionEnabled=true` 直接选中复制，不再挂单独的复制按钮；开启后这些文本块不参与「点空白退出多选」的判定（根级已按该属性排除）。

## 六、文案与文档

- 面向用户写文案，不暴露内部路径或术语；说明不重复标题；按钮以动作命名。反例（2026-10-26 已修）：资料库页曾把「会存到 D:\...\archive」「zenless-zone-zero-wiki · 1558 条 · 2026-10-15」「按摘要增量导入」直接写给用户——路径、来源 ID、实现机制都属于开发者视角。写法应是「拖入下载到的资料库文件，可以放多个」「绝区零 · 1558 条」。
- 文档维护、组件说明要素与验收规则统一见 [AGENTS](AGENTS.md#三文档与交付)。


## 应用窗口图标

应用沿用原始黑底白色 WinUI 字形，`tools/make_icon.py` 生成 `assets/Gallery.ico`；标题栏和侧栏继续使用原有 FontIcon。主窗口创建后显式调用 `AppWindow.SetIcon` 加载同一 ICO，供任务栏、Alt+Tab 和 Win+Tab 使用；这项设置独立于 EXE 的 `ApplicationIcon`。托盘从同一文件加载小图标。不能仅凭非零句柄认定显示已更新，应核对运行时图标像素和 Shell 提取结果；观感仍需用户确认。不要自动删除全局 Shell 图标缓存。

## 窗口拖动与标题栏

- 保留 48 DIP 顶部布局、系统窗口按钮及原有搜索／设置交互。
- `MainWindow.OnWindowSubclass` 只在交互移动循环内，对明确标记为不改变尺寸、Z 序、激活状态且 DPI 不变的位置消息做合并。通过 `DefWindowProc` 保留即时 `WM_MOVE`；退出循环时将最后一条完整位置消息交回框架，然后恢复网页。
- 实际缩放、框架变化、显示／隐藏、最大化及 DPI 变化不合并；跨 DPI 后当前循环恢复完整处理。不适合需要逐帧 `AppWindow.Changed` 位置事件的独立悬浮窗口；当前宿主在拖动时冻结待审核网页，松手后先同步位置再恢复。
- 该行为是主窗口内部实现，没有新增公共 API、可配置默认值或页面接入方式。宿主后续增加位置事件订阅时，应核对合并语义；不能全局丢弃 `WM_WINDOWPOSCHANGED`，也不能只隐藏网页便认定其通知停止。

## Photos 参考参数（非当前验收记录）

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
| 设置页头 | margin `32,28,48,26`；图标 `E713` FontSize 24；一级/二级标题 FontSize 24 | `Margins.xaml` / `GetSettingsHeaderIcon` |
| 设置页内容 | ScrollViewer padding `32,0,48,0`；StackPanel MaxWidth 958、Spacing 3 | `SettingsStyles.xaml` |
| 「个性化」副标题 | margin `1,24,0,6`，`BodyStrongTextBlockStyle` | 同上 |
| 应用主题卡片 | `ctc:SettingsCard`；Header「应用主题」/ Desc「选择要显示的应用主题」/ 图标 `E790` FontSize 16 | `SettingsPage.xaml` |
| 主题下拉 | `DropDownButton` + `MenuFlyout`，三项 `RadioMenuFlyoutItem`：浅色 / 深色 / Windows 默认 | 同上 |
| 切页 | 30ms 淡出 + 20ms 淡入 | `UnifiedGalleryPage.cs` |
| 内容区两层边距 | 外层 `GalleryViewGridMargin 32,0,0,0`、内层 `GalleryViewGridContentMargin 0,0,48,0` | `Margins.xaml`；滚动条因此贴窗口右缘 |
| 时间线圆点 | `TimelineIndicator`：同年内月份切换点，与年份文字共用距离过滤 | `TimelineLayout.CreateTimelineElementList` |

参数来自当时的本机 Photos 取证；实现与数值漂移时回到当前源码核对，不把参考表当成本项目能力清单。
