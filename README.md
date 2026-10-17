# Gallery — 「照片」图库主页复刻

一个只做**观感与交互**的最小照片图库：C# / WinUI 3（与 Microsoft Photos 同栈），把图库主页的布局、状态与动画按规格复刻出来。

## 运行

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
| 单击缩略图 | 选中（单选） |
| Ctrl + 单击 | 多选切换 |
| 双击缩略图 | 打开预览 |
| 预览中 `Esc` / `←` / `→` | 关闭 / 上一张 / 下一张 |
| 右键缩略图 | 图库相关菜单（复制等为演示提示，不改磁盘） |
| 滚动/悬停 | 滚动时右侧时间线淡入，2 秒后淡出 |
| 拖动时间线轨道 | 按位置定位（标记无动画跟随） |
| 汉堡键 | 折叠/展开左侧导航（320 ↔ 48） |
| 导航「图库 / 收藏夹」 | 切换数据源，内容淡出 30ms / 淡入 20ms |
| 搜索框 | 按文件名过滤 |
| 命令栏 | 选择、排序、筛选、库类型与大小、更多（全选/不选） |

> 幻灯片按钮按要求**已删除**（原版有，本项目不做）。

## 与 Photos 对齐的关键参数

| 项 | 值 | 来源 |
|---|---|---|
| 标题栏高 | 48 | 反编译常量 / 实测 |
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
| 工具栏元素间距 | `CommandBarElementMargin = 2,0,2,0` | `CommandBarStyles.xaml` |
| 工具栏按钮宽 | 普通 52 / 带 chevron 62（`AppBarButtonWithChevronWidth`）/ 溢出 48 | `CommandBarStyles.xaml` |
| 选择按钮文字 | 可见「选择」/「取消选择」 | `GetSelectionText()` → `CommandBar_SelectSelectionText` / `_CancelSelectionText` |
| 选择按钮图标 | 勾选 `E73A` / 取消 `E711`，FontSize 12 | `IconService.GetCheckMarkIcon/GetCancelIcon` |
| 选择按钮底色 | `AppBarButtonHighlightedStyles.xaml`（灰底 + 描边） | 同上 |
| 下拉 chevron | `E70D`（ChevronDown ∨），**覆盖 WinUI 默认 `E974`** | `CommandBarStyles.xaml:21` |
| 排序类型 → 滚动条 | 日期维度 → 时间线；名字等非日期维度 → 原生条（`ScrollBarVisibility.Auto`） | `GalleryViewModel.EvaluateScrollbarType` |
| 切页 | 30ms 淡出 + 20ms 淡入 | `UnifiedGalleryPage.cs` |

## 目录

```
Gallery/
├─ MainWindow.xaml(.cs)          窗口壳：标题栏 + NavigationView + Toast + 预览层
├─ Views/GalleryPage.xaml(.cs)   头部、日期头、命令栏、滚动区、选择与排序逻辑
├─ Controls/
│  ├─ PhotoGalleryPanel.cs       方格 / 河流布局（照搬 Photos 公式）
│  ├─ PhotoTile.cs               缩略图状态机与按压动画
│  ├─ TimelineScrollbar.cs       时间线滚动条
│  └─ PreviewOverlay.xaml(.cs)   全窗预览
├─ Models/PhotoItem.cs
├─ Services/Library.cs           读取 assets/manifest.json
└─ assets/                       40 张示例图 + manifest.json
```

## 已知差距（有意为之或未做）

1. **无虚拟化**：40 张全部创建。真实 Photos 用 `ItemsRepeater` + 自定义 `VirtualizingLayout`；本项目只复刻视觉与手感。
2. **无网格内日期分组**：Photos 会在滚动中插入日期分段标题，本项目只保留顶部固定日期头。
3. **图标**：用系统 `Segoe Fluent Icons` 近似，未内嵌 `PhotosIcons.ttf`（字体属微软版权素材）。
4. **查看器是覆盖层**，不是独立窗口；转场只做 150ms 淡入 + 0.98→1 缩放。
5. **标题栏**：未做 Photos 的标题栏配色与导入/设置按钮（按约定不做设置）。
6. **文件操作**：复制/移动/删除只弹提示，不碰磁盘。

## 工具

`tools/` 下是取证与验证脚本（不属于应用）：`shot.py` 截窗口、`shot_screen.py` 截全屏（能拍到弹出菜单）、`realinput.py` 发送真实鼠标/键盘、`wheel.py` 发送滚轮。

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
