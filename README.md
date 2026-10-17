# 浮绘 FloatCanvas

简体中文 | [English](README.en.md)

Windows 屏幕标注工具。直接在桌面上绘画、贴图和调整对象，再切回下层应用操作，标注持续可见。

当前预览版本：**0.2.0-preview.1**。功能与稳定性仍在完善；各版本变化见[更新日志](CHANGELOG.md)，操作说明见[使用指南](Docs/User-Guide.md)。

## 功能

- 自由画笔、荧光笔、线条与箭头、形状、文字、独立序号、符号和 Emoji；图形化官方／自定义预设，支持按默认尺寸单击创建形状。
- 图片文件与剪贴板导入，也可将图片直接拖入画布；对象可重新选择、移动、旋转、缩放、编组和修改属性。
- 两排对齐的工具条，工具和印章快捷键角标可统一显隐；可拖动的工具参数板、属性与图层窗口，支持独立切换编辑状态与画布显隐。
- 可编辑或合并为图片的印章库，十格快捷栏和 `1`～`0` 快速选用。
- 区域截图、裁剪、模糊、动态马赛克与实色遮盖，导出 PNG／JPEG 或透明标注；新建画布前将当前工作归档到历史。
- 自包含的可编辑 `.floatcanvas` 工程；自动保存恢复数据，启动保持空白，按需手动恢复历史画布。
- 浅色／深色主题，默认仅主屏，可选择全部屏幕。
- 简体中文／英文界面，可自动跟随 Windows 显示语言。
- 应用内检查 GitHub Release，下载并安装更新后自动重启。

## 下载与使用

Windows x64 二进制包由仓库的 [Releases](https://github.com/yowynn/FloatCanvas/releases) 提供，附带 ZIP 和 SHA-256 校验文件。也可按下文从源码构建；源代码检出不包含打包产物。

解压预览包，保留全部文件，运行 `FloatCanvas.exe`。默认发行包不附带 .NET 运行时或调试符号文件（PDB）。电脑已有兼容的 **.NET 10 Windows Desktop Runtime x64** 时可直接启动。

若缺少兼容运行时，启动程序会显示 .NET 安装提示。点击 **Download** 前往微软官方下载页面，下载并运行 **Windows Desktop Runtime x64** 安装程序，完成后重新启动浮绘。也可从 [.NET 10 官方下载页](https://dotnet.microsoft.com/download/dotnet/10.0)选择该运行时；运行浮绘无需安装 SDK。

点击欢迎页底部的版本号打开“检查更新”；面板自动检查本项目的 GitHub Release，并始终提供[项目主页](https://github.com/yowynn/FloatCanvas)链接。有可用更新时，点击“立即更新”，应用将下载、校验、替换程序并自动重启，无需手动解压覆盖。请将完整 ZIP 解压到当前用户可写的本地目录。正式版只接收正式版本，预览版也可接收更新的预览版本；详见[更新说明](Docs/User-Guide.md#检查与安装更新)。

| 快捷键 | 操作 |
| --- | --- |
| `` Ctrl+` `` | 编辑／穿透切换；穿透时标注保留，下层应用可正常操作 |
| `` Ctrl+Shift+` `` | 隐藏画布／显示画布；保留当前编辑／穿透状态 |
| `Esc` | 取消当前操作、关闭子面板或未固定面板；最后返回穿透 |
| `Ctrl+Z`／`Ctrl+Y` | 撤销／重做 |
| `Ctrl+S`／`Ctrl+O` | 保存／打开可编辑工程 |

单击工具条左侧应用图标打开主菜单，双击展开／收起，拖动图标移动工具条。主菜单和系统托盘提供设置与退出入口；更多操作见[使用指南](Docs/User-Guide.md)。

工具键：`V` 选择、`E` 橡皮、`B` 自由画笔、`H` 荧光笔、`L` 直线、`R` 矩形、`O` 椭圆、`T` 文字、`N` 序号、`S` 符号、`J` Emoji。`A` 保留为箭头兼容键；`M` 打开“更多”，主键盘 `-` 打开印章库，`+` 保存选择为印章。输入文字时，字母用于正常输入。

## 语言与适用范围

在“主菜单 → 快捷键与主题…”的“界面语言 / Language”中，可选择自动（`auto`）、简体中文（`zh-CN`）或 English（`en`），保存后重启生效。自动模式跟随 Windows 显示语言：中文系使用简体中文，其他语言使用英文。界面语言与标注内容、键盘布局和输入法相互独立。提供[中文指南](Docs/User-Guide.md)和[English guide](Docs/User-Guide.en.md)。

面向 Windows 10 22H2／Windows 11 x64，当前预览主要在 Windows 11 使用；Windows 10 兼容性尚未确认。默认仅主屏，完整跨屏、混合 DPI 与双 4K 体验仍待完善。全屏应用的兼容性取决于具体应用和显示方式，不保证所有独占全屏场景。

存在可见动态马赛克时，为避免循环采样，系统截图会排除浮绘的画布层，外部截图可能不包含其他标注；请使用浮绘内的导出保留完整合成。删除或隐藏动态区域后自动恢复。Windows 自带截图的窗口识别仍有已知限制。

标注固定在屏幕坐标，不随网页滚动、游戏镜头或物体移动。HDR 色彩准确性、压感笔、触控、OCR、录屏、滚动截图、云同步和上传不在当前版本范围内。预览版仍在完善功能与稳定性。

## 从源码构建

需要 Windows x64 和 .NET SDK 10.0.402，补丁版本由 `global.json` 解析。

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
dotnet run --project src/FloatCanvas.App -c Release
```

生成不附带 .NET 运行时和 PDB 的 Windows Release 包，并检查解压启动；版本自动读取应用工程，运行检查需要已安装兼容的桌面运行时：

```powershell
./scripts/Publish.ps1
./scripts/Smoke-Package.ps1
```

可选跨盘输出：`./scripts/Publish.ps1 -OutputDirectory 'C:\FloatCanvas\dist' -ArtifactsPath 'C:\FloatCanvas\build'`；包和主要构建中间文件写入指定位置，少量发布依赖锁文件仍保留在源码的忽略目录 `obj` 中。

## 发布源码版本

推送到已启用 GitHub Actions 的仓库后，普通分支更新和 PR 会运行 CI。发布时，推送与应用工程 `Version` 一致的 `v<Version>` 标签，或手动运行发布工作流并选择已有标签；工作流会构建、测试、打包、检查解压启动，再创建附带 ZIP 与 SHA-256 文件的 Release。仅修改版本号不会自动发布。

## 许可证

由 **Heptayo** 维护。项目代码采用 [MIT 许可证](LICENSE)，发行依赖许可见[第三方声明](THIRD-PARTY-NOTICES.md)及 [licenses](licenses)。

浮绘独立实现，参考了 ppInk 与 ShareX 的公开技术和交互设计。
