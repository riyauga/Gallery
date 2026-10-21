# UI 规范

本文件放**本项目对 WinUI 3 的通用约束**：新增界面前先读这里，改动 UI 时按这里的方式做，和既有页面保持一致。
（和 `README.md` 的区别：README 记录「复刻 Photos 的关键参数」，本文件记录「我们自己必须守的规则」。）

---

## 模态弹窗与退出

- 主 UI 线程的 `ContentDialog` 统一使用 `await Gallery.Services.DialogService.ShowAsync(dialog)`；默认同一时间只显示一个，已有弹窗或正在退出时返回 `ContentDialogResult.None`，不排队重放旧操作。宿主仍设置 `XamlRoot`、内容及按钮，并且只在明确返回 `Primary`／`Secondary` 时执行相应操作。
- `DialogService.IsShowing` 可在创建昂贵内容前检查；`Dismiss()` 供主窗口退出时关闭当前弹窗。它只管理主 UI 线程的模态框，不用于 `Flyout`、原生文件选择器或多 UI 线程窗口。编辑器的取消、动画和后台任务清理必须放在 `finally`。
- 新建、删除文件夹后的全根扫描放在后台；扫描串行锁与数据库锁分开，目录枚举、图片头读取不能包在全根数据库锁中。后台扫描进度和错误投递到 UI，退出后不再刷新控件或启动新的审核／文件夹任务。
- 主窗口退出先阻止新操作、取消模态框，等待审核整批任务及文件夹操作结束，再由服务层停止监听／桥接并关闭数据库。不能只等待批处理中当前那一个 `Task.Run`，也不能以数据库锁暂时空闲作为全部任务结束的证明。
- 本轮未启动或操作应用。需人工验证快速重复编辑、弹窗取消后再次打开、扫描期间浏览，以及归档／导入／清理过程中退出。

## 搜索输入框

待审核圆环隐藏时保存中心卡牌 ID 并停止拖动和惯性；返回后先使页面可见，按 ID 恢复中心，等待布局连续两帧稳定再入场。中心卡片先显现，上下两侧再同时由内向外逐层翻展成环；等待图片时保留已展开部分，不先闪出完整圆环。中心 220ms，中心启动后至少 140ms 展开第一层，其后层间至少 85ms，各层 300ms；图片就绪时整体约 0.7 秒，不含加载等待。完成后释放到相同的真实圆环布局，系统关闭动画时直接显示。旧视图的选择回声按代次过滤；圆环裁切不创建滚动容器，批量网格保留滚动。

待审核页的多选模式使用显式“批量审核／退出批量审核”入口，卡片显示勾号和边框，不依赖颜色区分选择。0／1／多选分别显示空状态、完整详情、标签与目录；多选编辑从空白开始，必须显式应用整套覆盖，不继承第一张或共同标签。发牌准备阶段保留原环形卡片作为等待层，真实网格隐藏；容器连续两帧稳定且视口卡片补齐后，直接对真实网格卡片施加圆环到收拢再发牌的动画。边框、圆角、阴影、图片区域及文件名／勾选框在动画内进入最终样式，结束不再替换卡片。动画使用 fill=both 保持延迟期间的起始帧；窗口变化更新终点。单卡 460ms、错峰最多 126ms，准备完成后的发牌最多约 586ms；遵循系统动画开关。批量模式的网格滚动复用 `ContinuousScrollBar`，以像素偏移同步网页，原圆环索引滚动条隐藏。普通单图审阅的手势与布局设置保留。批量模式按住鼠标左键拖动超过 4px 开始框选，边缘自动滚动；拖动预览勾选，松手更新右侧详情，Ctrl／Shift 追加，Esc 取消。点击网格间隙或页面非交互空白处取消全部勾选，按钮、编辑控件与框选松手保留选择。左侧容器工具行右边缘复用 SelectionDropDown 提供“小／中／大”，默认“中”，缩放档位切换保留勾选并按原首行索引保持滚动位置。

- 封装默认圆角取原生主题资源 `ControlCornerRadius`，再同步给内部控件；不得用 UserControl 默认的 0 覆盖原生圆角。内容及内部控件双向拉伸，按宿主分配尺寸布局，不通过放大宽度或负边距补偿边缘。本轮仅静态/编译核对，边缘完整性待人工确认。
- 顶部图库搜索、资料库、筛选、标签编辑与待审核标签输入统一使用 `Controls/SearchInputBox.cs`，基于原生 `AutoSuggestBox`，不另绘模板。默认 `AllowFocusOnInteraction=true`、`PreventAutomaticFocus=false`；文本编辑、选区、键盘、输入法及候选展示交给原生控件。普通资料编辑字段仍用 `TextBox`。
- `AutoSuggestBox` 是密封类，组件用 `UserControl` 内嵌原生控件，不继承或修改其模板。公共 API 转发 `Text`、`PlaceholderText`、`TextChanged`、`ItemsSource`、`QuerySubmitted`；显式聚焦用 `FocusSearch()`，尺寸、可用状态由宿主设置，圆角同步到内部控件。例如 `new SearchInputBox { PlaceholderText = "搜索标签", Width = 300 }`。转发属性不是依赖属性，当前仅供 XAML 初值和代码读写，不提供完整 AutoSuggestBox API 或绑定契约。事件 sender 为内部原生 AutoSuggestBox。宿主负责查询、防抖、过期结果过滤和提交；普通搜索不提供 `ItemsSource`，不额外弹出候选。标签搜索保留候选选择、键盘提交与输入新标签；`TextChanged` 使用 `AutoSuggestBoxTextChangedEventArgs`，候选查询仅处理 `UserInput`。
- 标题栏设置 `PreventAutomaticFocus=true` 并提供 `DefaultFocusAnchor`，保留原 Photos 主动聚焦判断，允许点击、Tab 和 `FocusSearch()`，拦截启动或内容卸载等非主动聚焦；内部文本编辑器已有焦点时允许其正常转移。其他搜索不启用此保护。旧 `PhotosSearchBox` 已移除，不再维护第二套控件。
- 含输入框的普通 `Flyout`，触发按钮必须允许交互聚焦。WinUI 会把目标按钮的 `AllowFocusOnInteraction` 传给 `FlyoutPresenter`；仅修改输入框或 Flyout 本身不足以替代宿主配置。当前 `FilterButton` 显式设置为 `True`，其他菜单按钮不变。
- 不统一查询范围：资料库查实体，筛选查已入库标签及其中英文名、别名；组件不访问数据库，不适用于普通资料编辑字段。
- 标签编辑器与评审页的角色搜索按身份键折叠：同一角色只给一个候选，优先使用图片数最多的已有标签；只有该身份没有任何标签时才追加词库实体。词库实体按身份合并中/英文 wiki 条目，并在同作品内按来源名与已有标签/同来源实体去重，避免同一角色出现「标签 + 实体」或跨 wiki 双份候选。搜索候选名称由「设置 → 标签 → 角色搜索候选」单独控制，固定为来源原名加作品名：英文如 `kafka_(honkai:_star_rail)`，中文如 `卡芙卡_(崩坏：星穹铁道)`；标签贴纸仍按「角色标签显示」和单标签覆盖。候选名先取标签/实体自己的来源名，缺失时在同一身份的所有实体里找，仍缺再退回另一种语言；作品名跟随实际使用的来源语言。两种来源名都缺失时显示 `标签名（来源缺失）`，没有绑定作品时显示 `（未绑定作品）`；无 `tag_profiles` 的标签不自动猜绑，进入人工待补列表。
- 本次未启动应用；需人工核对所有入口的点击输入、中文输入法、删除/清空、结果刷新、候选选择/提交，以及标题栏切页/关闭查看器的焦点保护和搜索快捷键。

## 输入焦点与标题栏命中

- 托盘隐藏／唤回不切换标题栏模式。仅在托盘隐藏后的首次激活，由 `RefreshCaptionAfterTrayHide()` 重算命中区域、读取真实鼠标屏幕坐标，经 `WM_NCHITTEST` 得到当前位置后发送一次 `WM_NCMOUSEMOVE`，同步系统按钮状态；不移动鼠标、不模拟点击。关闭、恢复及各类首个输入的有界 `caption` 日志暂留，红色残留是否消除待人工验证。
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

数值来源：48 与 Photos 对齐、也等于自绘时间线宽度（`README.md` 参数表）；16 是详情面板既有取值（`Controls/Viewer/DetailPanel.cs`）。原生浮层条的宽度随系统与主题变化，没有可引用的常量，所以上表用项目约定值，而不是从条宽推导。

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

### 现状清单（2026-10-19 审计；2026-10-20 补待审核圆环）

| 位置 | 滚动区 | 右侧留白（加在哪一层） | 判定 |
|---|---|---|---|
| `Views/GalleryPage.xaml:445` | 图库内容区（日期排序保留时间线；其他排序通过原模板接入连续条） | 内容 `Margin` 右 48 | 原留白不变 |
| `Views/SettingsPage.xaml:47` | 设置页整页 | `ScrollViewer.Padding` 右 48 | 合规（本轮未改动） |
| `Controls/Viewer/DetailPanel.cs:76` | 预览右侧「图片信息」 | 内容 `Padding` 右 16 | 合规（下限值，本轮未改动） |
| `Views/SettingsPage.xaml.cs:270` | 「最近错误」对话框 | `ScrollViewer.Padding` 右 16 | 已修 |
| `Controls/Review/ReviewPage.cs` `_detailScroll` | 评审页「图片详情」栏 | `ScrollViewer.Padding` 右 16 | 已修 |
| `Controls/Review/ReviewPage.cs` `_queueScroll` | 待审核圆环 | `ContinuousScrollBar` 16 宽，放在圆环与分割线之间的空隙 | 合规（2026-10-20 新增） |
| `Controls/Catalog/CatalogPage.cs:140` | 标签目录左列表 | `ListView.Padding` 右 16 | 已修 |
| `Controls/Catalog/CatalogPage.cs:217` | 标签目录右编辑栏 | `ScrollViewer.Padding` 右 16（父 `Border` 的 16 在滚动区外，无效） | 已修 |
| `Controls/Catalog/AlignAmbiguousDialog.cs:48` | 标签消歧对话框 | `ScrollViewer.Padding` 右 16 | 已修 |
| `Controls/FilterPanel.cs:86` | 筛选浮出层的结果列表 | `ScrollViewer.Padding` 右 16 | 已修 |
| `Controls/Nsfw/NsfwPreviewDialog.cs:39` | 重判定预览列表 | `ScrollViewer.Padding` 右 16 | 已修 |
| `Controls/Tags/TagEditorDialog.cs:159` | 标签编辑器整页 | `ScrollViewer.Padding` 右 16 | 已修 |

图库右侧那条自绘时间线滚动条（`TimelineScrollbar`，48 宽）按要求不动：它本来就在内容留白之后，不覆盖可点内容。待审核圆环的 `ContinuousScrollBar` 是 16 宽，放在圆环与分割线之间的列间距里，同样不压卡片。

`TimelineScrollbar` 的横杠移动保留 100ms 缓动；连续滚动算出相同目标时不重播，目标变化时保存当前显示位置作为新动画起点，并同步基础位置到目标，避免停止动画后退回旧位置。拖动及刷新等无动画调用仍立即定位。该调整仅作用于时间线标记，连续滚动、反向滚动、拖动和长跳表现待人工核对。

修完请把「已修」改成带日期的字样，并在下面补一行说明改了什么；发现新的滚动区照此表追加。

---

## 二、图片查看器的平移边界按图片自身算

- 规则：偏移夹在「图片盖住视口」的范围内——**图比视口大**就给 `(图片尺寸 - 视口尺寸) / 2` 的拖动余量（拖到边缘即停，不留空白）；**图比视口小**就固定居中，拖不动。
- 明确禁止的是老规则「图片中心不得超出画布」：它会让放大后的图片永远看不到右/下边缘。反过来，完全不设边界同样不行（能把图片拖飞、留一片空白）。
- 落点：`Controls/Viewer/PhotoCanvasState.cs` 的 `Clamp()` / `ClampAxis()`，由 `SetViewport`（非适应态）、`Actual`、`ZoomAt`、`Pan` 四处调用。**新增或改动缩放/平移入口时必须一并调用**，否则那条路径会绕开边界。
- 自查：改动后跑几何断言，至少覆盖「放大后拖到边缘正好贴边」「继续拖被夹住」「反方向拖到底不留空白」「图小于视口时拖不动或回中」。

---

## 三、待审核页交互与布局

- **滚动轴**：右侧、可发现（允许自动淡出，但不能完全没有指示）；连续值，不按页跳。点击轨道任意位置时，滑块和内容同步用短缓动滑到点击处（不是瞬时、不是 `LargeChange`）；拖动 1:1；松手吸附到整数。交互结束/停稳后再提交详情、加载清晰图。
- **圆环拖拽**：左键拖动带速度/方向惯性；可控、接近即时停；硬甩滚得远再慢慢停，不设固定 ±2 上限。大拖动不卡住卡片。
- **选中卡片**：不要固定外发光环；选中放大，滚出后恢复；进入/退出对称。
- **淡入淡出**：顶部/底部卡片滚动时淡出；图片加载完成淡入，不 pop。
- **分栏**：圆环与详情之间用 `PaneResizer` 风格把手；有 min/max；拖动时两侧同步 reflow；比例写入 `layout_options.review_split_ratio`。
- **计数**：顶部 `a / b` 单行、字号小于标题；不要底部 prev/next + 数字行；分页器只保留一处。
- **对齐**：批量操作右边缘对齐（一键隐藏预览 vs 一键忽略）；侧栏附加入口与同类入口对齐（导入 vs 图库加文件夹）；同类入口位置一致。
- **设置**：齿轮只在悬停按钮本身时出现；点击外部或 Esc 关闭；隐藏设置持久化（选中缩放、缓动、拖拽力度等）。
- **拖放**：只绑定目标容器（左侧卡片容器），不要整页 `AllowDrop`；提示层也在目标容器内。
- **导入**：默认保留原文件（copy）+ 递归子文件夹；重复项跳过；导入后刷新轮播；入口在「待审核」右侧并与图库新增入口对齐。
- **批量预览**：底部提供“一键显示/隐藏预览”，与“一键忽略”右对齐。

## 四、视觉、渲染与加载

- **景深**：非中心卡片用 scale / translateZ / rotateX + 逐级降低亮度表达深度；默认不要加高斯模糊；只有用户明确要求才加。
- **NSFW**：文本、按钮、遮罩层保持清晰；滤镜/压缩/缩放只作用于图片层，不作用于文字/按钮。
- **背景**：容器透明/融入 Mica，不写死背景色；卡片背景取图片平均色，不默认黑色；平均色从已加载缩略图采样。
- **渐进加载**：先低清/模糊占位，再清晰；受保护图在“显示预览”前只加载模糊版；清晰图只在停稳/揭示后加载。
- **预取**：覆盖整队或足够范围，长跳不空白；换窗口只换可见 `src`，不取消/重启加载；不抢可见请求。
- **性能**：不在动画/缩放内容上用运行时 `filter: blur()`；用预模糊/低清/亮度/遮罩；批量 host↔web 消息；不动画 WebView；快照、折叠或挂起不等于解除 WebView 的窗口位置通知订阅。
- **主题**：不要写死 `#202020` 等颜色；优先主题资源/透明；平均色只用于卡片内容层，不用于容器背景。

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

### 通用组件原则

- **位置**：通用控件放 `Controls/`，命名空间 `Gallery.Controls`，`public sealed`，与 `PaneResizer` / `TimelineScrollbar` 同级；不要埋在 feature 子目录。
- **提取**：保持原调用方默认行为；把差异做成属性（`Orientation` / `Step` / `Thickness` 等），不是只改目录/命名空间；补 XML 注释、用法示例、已知限制。
- **职责**：连续滚动条负责值/指针/动画及默认自动隐藏；宿主只报告内容活动、页面可用性并处理提交和业务，不维护重复的透明度或隐藏计时。事件用 `ValueChanged` / `InteractionStarted` / `InteractionCompleted` / `HoverChanged` 这类明确语义。
- **防回归**：初始未测量尺寸（NaN）不要参与“是否变化”比较；用 `double.IsFinite` 或显式初始化，避免 0 高/0 宽。
- **数据/设置**：持久化用现有 `layout_options` 或记录字段；不新增散落配置；导入保留源用 `import_keep_source`；去重按完整路径查队列和 `images.abs_path`；copy/move 的归档阶段对应 `copied` / `moved`。
- **依赖**：不新增 NuGet/依赖，优先现有 `ImageLoader` / `ThumbnailService` / SDK。
- **代码范围**：最小修改；不顺手重构、格式化、删无关旧代码；只清理本次新增的无用引用。

## 六、文案与文档

- **文案**：面向用户，无内部名称/路径/术语；描述不重复标题；句末不加 `。`；按钮用用户动作命名（如“用别的程序打开”）。
- **文档**：改共享组件/共享行为时同步更新 `README.md`、`UI_CONVENTIONS.md`、`ARCHIVE_REVIEW.md`、`IMAGE_LOADING.md`；修正过时描述，不只追加 changelog。
- **组件文档**：包含用途/不适用、默认值、公共 API、宿主职责、示例、限制（如 `Orientation` 会重置宽高）。
- **交付说明**：见 `AGENTS.md`；构建 0/0 不等于 UI 验收，UI 手感由用户确认。
- **文档落点**：项目规范在 `AGENTS.md`；UI 规则在本文件；加载细节在 `IMAGE_LOADING.md`；待审核页行为在 `ARCHIVE_REVIEW.md`；入口/总览在 `README.md`。


## 窗口拖动与标题栏

- 保留 48 DIP 顶部布局、系统窗口按钮及原有搜索／设置交互。
- `MainWindow.OnWindowSubclass` 只在交互移动循环内，对明确标记为不改变尺寸、Z 序、激活状态且 DPI 不变的位置消息做合并。通过 `DefWindowProc` 保留即时 `WM_MOVE`；退出循环时将最后一条完整位置消息交回框架，然后恢复网页。
- 实际缩放、框架变化、显示／隐藏、最大化及 DPI 变化不合并；跨 DPI 后当前循环恢复完整处理。不适合需要逐帧 `AppWindow.Changed` 位置事件的独立悬浮窗口；当前宿主在拖动时冻结待审核网页，松手后先同步位置再恢复。
- 该行为是主窗口内部实现，没有新增公共 API、可配置默认值或页面接入方式。宿主后续增加位置事件订阅时，应核对合并语义；不能全局丢弃 `WM_WINDOWPOSCHANGED`，也不能只隐藏网页便认定其通知停止。
