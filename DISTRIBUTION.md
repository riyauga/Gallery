# Gallery 1.0.0 分发说明

## 分发内容与依赖

版本由 `Gallery.csproj` 显式维护为 1.0.0，程序集和文件版本为 1.0.0.0。按 [项目规范](AGENTS.md#一构建验证与改动边界) 构建，分发约定输出目录的完整内容，不只复制 Gallery.exe。发布为 **x64 自包含**：`--self-contained true` 且 `WindowsAppSDKSelfContained=true`，.NET 9 与 Windows App SDK 运行时随包携带，用户无需预先安装。目标环境为 Windows 10 1809 及以上或 Windows 11 x64，仍需可用的 Microsoft Edge WebView2 Runtime（Win11 自带，Win10 缺失时需单独安装）。

## 发布形态

两种产物内容相同，均在 [Releases](https://github.com/riyauga/Gallery/releases) 提供：

| 产物 | 行为 |
|---|---|
| `Gallery-1.0.0-setup.exe` | Inno Setup 安装版，per-user 装到 `%LOCALAPPDATA%\Programs\Gallery`，不请求管理员权限；建开始菜单快捷方式、可选桌面图标、注册卸载项 |
| `Gallery-1.0.0-win-x64.zip` | 免安装版，解压得到单层 `Gallery-1.0.0\` 目录，双击 `Gallery.exe` 运行 |

打包步骤（发布目录与产物都写 `%TEMP%`，仓库内只保留 `bin\Debug` 交付构建）：

```powershell
dotnet publish Gallery.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o "$env:TEMP\ptv\publish"
# 把 publish 复制成 Gallery-1.0.0\、删掉 *.pdb，再压成 zip：
tar.exe -a -c -f Gallery-1.0.0-win-x64.zip Gallery-1.0.0
# 安装包：脚本在 tools\installer.iss，版本号与 Gallery.csproj 同步
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DSourceDir="$env:TEMP\ptv\publish" /DOutDir="$env:TEMP\ptv\dist" tools\installer.iss
gh release create v1.0.0 <两个产物> --title "Gallery 1.0.0" --notes-file <说明.md>
```

未做代码签名，首次运行会触发 SmartScreen 提示（更多信息 → 仍要运行）；安装向导为英文界面，官方 Unofficial 语言目录已无简体中文 isl。

程序包包含用户说明 `USER_GUIDE.md`、运行依赖、图标、Photos Fluent Icons 字体及可加载的 `extension/`。项目所有者于 2026-10-28 确认已取得该字体的分发授权；不据此推定其他项目也拥有该授权。扩展安装及配对见 [扩展说明](extension/README.md)。

不分发示例 JPG、示例 manifest、内置资料库、模型、个人配置、数据库、缩略图、WebView 用户目录、日志、资料包暂存或开发工具。代码保留的旧示例素材不代表发布内容；也不从它们恢复示例图库。编译源码路径映射为通用路径，避免调试符号携带开发机工作目录。

## 首次启动与已有用户

首次启动为空图库；用户主动添加本地目录、资料库包及模型。开机自启、浏览器桥接、按标签自动分目录默认关闭；固定归档目录必须由用户选择。已有 settings.json 中的明确设置及 folder_map.json 规则保留，不为升级覆盖它们。

个人设置、目录规则及个人备份位于当前用户 `%APPDATA%/Gallery`；数据库、缩略图、审核 WebView 数据及 crash.log 位于 `%LOCALAPPDATA%/Gallery`。后台诊断仍写 `%TEMP%/ptv/gallery-log.txt`。这些运行时数据不属于发布包，也不能把开发者现有用户数据目录复制给新用户。

资料库和模型为外部包，独立分发默认不包含任何包；开发者本机包继续保留在项目根目录。导入、移除与个人数据保护见 [资料包与个人规则](PACKS_AND_RULES.md)。本地图库暂时不可访问时保留索引；监听每约 3 秒检查已登记目录，恢复后重建监听并补扫，不移动或删除原图。该间隔不是所有设备的恢复时间承诺。

## 发布验证边界

构建必须 0 警告、0 错误。需检查输出目录实际内容，不能用 .gitignore 代替发布清单；普通增量构建不会自动删除历史遗留文件。分发前排除旧素材、日志与用户数据，保留运行依赖。自动化结果与人工验收分开记录；物理磁盘拔插、真实浏览器下载、外观和手感仍需用户验收。未执行任何上传或公开发布。

2026-10-28 已执行：构建 0 警告／0 错误；临时沙箱验证 26 项（默认值、配置兼容、空库、目录规则、资料移除与重新接入、损坏文件保护、桥接鉴权、微型 ONNX 推理／会话释放、模拟图库重连）；独立干净用户服务启动与关闭 5 项；现有扩展测试 70 项及 GET 配对码传递检查 3 项。测试未启动前台 UI，未访问真实图库数据库，未运行用户真实模型；不等同于真实设备或人工观感验收。

另已核对实际 Gallery.exe 的隐藏无窗口空库验证（.NET 9 程序入口）、126 个 PDB 文档路径映射、显式版本、模型路径大小写兼容，以及全部输出文件不含开发者用户名；最终输出清单排除旧样例、内置资料与个人数据。SourceLink 自动仓库指针已关闭。

2026-10-29 发布 v1.0.0 实测：自包含发布目录 247 MB（含 3 个 pdb 共 12 MB，发布包已剔除）；便携 zip 91 MB，解压 235 MB、单层 `Gallery-1.0.0\` 目录；安装包 64 MB。安装包已跑通 静默安装 → 启动 → 静默卸载 闭环：装后 239 MB，卸载后安装目录清空、无残留进程。未做代码签名，未在第二台机器验证，未做外观与手感验收。
