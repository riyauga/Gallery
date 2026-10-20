# UI 规范

本文件放**本项目对 WinUI 3 的通用约束**：新增界面前先读这里，改动 UI 时按这里的方式做，和既有页面保持一致。
（和 `README.md` 的区别：README 记录「复刻 Photos 的关键参数」，本文件记录「我们自己必须守的规则」。）

---

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
| R7 | 需要 1:1 连续拖动、点击轨道任意位置、或宿主驱动的内容位置时，用 `Controls/ContinuousScrollBar.cs`；不要用原生 `ScrollBar` 再拦截指针（`LargeChange` 会打架） |

数值来源：48 与 Photos 对齐、也等于自绘时间线宽度（`README.md` 参数表）；16 是详情面板既有取值（`Controls/Viewer/DetailPanel.cs`）。原生浮层条的宽度随系统与主题变化，没有可引用的常量，所以上表用项目约定值，而不是从条宽推导。

### 通用连续滚动条（`ContinuousScrollBar`）

- 适用：圆环、照片条等「滚动条位置 = 内容连续位置」，需要拖动 1:1、点击轨道任意位置、宿主自己驱动内容位置的场景。
- 不适用：普通 `ScrollViewer` 的滚动区；那里仍用原生浮层条，按 R1–R6 留白。
- 默认：竖向命中区 16、轨道 2、滑块 6、滑块最短 28。待审核圆环把它放在圆环与分割线之间的列间距里，不压卡片。
- 宿主订阅 `ValueChanged` 把值直接映射到内容位置；`InteractionCompleted` 再提交详情/加载清晰图。宿主用 `Opacity` 控制显隐，控件本身不自动隐藏。
- `Step` 默认 1（松手吸附整数）；`Step = 0` 保留连续值。设置 `Orientation` 会重置控件默认宽高，自定义尺寸请在其后覆盖。

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
| `Views/GalleryPage.xaml:445` | 图库内容区（日期维度隐藏原生条、改用自绘时间线） | 内容 `Margin` 右 48 | 合规（本轮未改动，按要求保持原状） |
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

- **位置**：通用控件放 `Controls/`，命名空间 `Gallery.Controls`，`public sealed`，与 `PaneResizer` / `TimelineScrollbar` 同级；不要埋在 feature 子目录。
- **提取**：保持原调用方默认行为；把差异做成属性（`Orientation` / `Step` / `Thickness` 等），不是只改目录/命名空间；补 XML 注释、用法示例、已知限制。
- **职责**：组件只做值/指针/动画；显隐、提交、业务由宿主负责；事件用 `ValueChanged` / `InteractionStarted` / `InteractionCompleted` / `HoverChanged` 这类明确语义。
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
