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

