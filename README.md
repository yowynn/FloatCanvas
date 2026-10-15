# 浮绘 FloatCanvas

简体中文 | [English](README.en.md)

Windows 屏幕标注工具。直接在桌面上绘画、贴图和调整对象，再切回下层应用操作，标注持续可见。

当前版本：**0.1.1-preview.9（预览）**。[使用指南](Docs/User-Guide.md) · [更新日志](CHANGELOG.md)

## 功能

- 自由画笔、荧光笔、直线、箭头、形状、文字和序号。
- 图片文件与剪贴板导入，也可将图片直接拖入画布；对象可重新选择、移动、旋转、缩放和修改属性。
- 编辑／穿透快捷键切换，隐藏画布，撤销／重做及图层管理。
- 区域截图、裁剪、模糊、马赛克、实色遮盖，导出 PNG／JPEG 或透明标注。
- 自包含的可编辑 `.floatcanvas` 工程和自动恢复。
- 浅色／深色主题，默认仅主屏，可选择全部屏幕。
- 简体中文／英文界面，可自动跟随 Windows 显示语言。

## 下载与使用

发布的 Windows x64 二进制包由仓库的 **Releases** 提供，附带 ZIP 和 SHA-256 校验文件。如果 Releases 尚无可用发行包，请按下文从源码构建。源代码检出不包含本地打包产物。

解压预览包，保留全部文件，运行 `FloatCanvas.exe`。默认发行包不附带 .NET 运行时或调试符号文件（PDB）。电脑已有兼容的 **.NET 10 Windows Desktop Runtime x64** 时可直接启动。

若缺少兼容运行时，启动程序会显示 .NET 安装提示。点击 **Download** 前往微软官方下载页面，下载并运行 **Windows Desktop Runtime x64** 安装程序，完成后重新启动浮绘。也可从 [.NET 10 官方下载页](https://dotnet.microsoft.com/download/dotnet/10.0)选择该运行时；运行浮绘无需安装 SDK。

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl+Alt+F` | 编辑／穿透切换；穿透时标注保留，下层应用可正常操作 |
| `Ctrl+Alt+Shift+F` | 隐藏／恢复全部画布 |
| `Esc` | 取消当前操作；没有进行中的操作时返回穿透 |
| `Ctrl+Z`／`Ctrl+Y` | 撤销／重做 |
| `Ctrl+S`／`Ctrl+O` | 保存／打开可编辑工程 |

更多操作见[使用指南](Docs/User-Guide.md)。工具条“更多”菜单和系统托盘提供设置与退出入口。

## 语言与适用范围

在“更多 → 快捷键与主题…”的“界面语言 / Language”中，可选择自动（`auto`）、简体中文（`zh-CN`）或 English（`en`），保存后重启生效。自动模式跟随 Windows 显示语言：中文系使用简体中文，其他语言使用英文。界面语言与标注内容、键盘布局和输入法相互独立。提供[中文指南](Docs/User-Guide.md)和[English guide](Docs/User-Guide.en.md)。

面向 Windows 10 22H2／Windows 11 x64，当前预览主要在 Windows 11 使用；Windows 10 兼容性尚未确认。默认仅主屏，完整跨屏、混合 DPI 与双 4K 体验仍待完善。全屏应用的兼容性取决于具体应用和显示方式，不保证所有独占全屏场景。

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

## 发布源码版本

推送到已启用 GitHub Actions 的仓库后，普通分支更新和 PR 会运行 CI。发布时，推送与应用工程`Version`一致的`v<Version>`标签，或手动运行发布工作流并选择已有标签；工作流会构建、测试、打包、检查解压启动，再创建附带ZIP与SHA-256文件的Release。仅修改版本号不会自动发布。这是工作流配置说明，不表示当前已完成远端CI或在线发布。

## 许可证

项目代码采用 [MIT 许可证](LICENSE)，发行依赖许可见[第三方声明](THIRD-PARTY-NOTICES.md)及 [licenses](licenses)。

浮绘独立实现，参考了 ppInk 与 ShareX 的公开技术和交互设计。
