# FloatCanvas 浮绘

[简体中文](README.md) | English

A Windows screen annotation tool. Draw over your desktop, place images and edit objects, then return to the application underneath while your annotations stay visible.

Current version: **0.1.1-preview.9**. [User guide](Docs/User-Guide.en.md) · [Changelog](CHANGELOG.md)

## Features

- Freehand pen, highlighter, lines, arrows, shapes, text and numbered markers.
- Import image files or clipboard images, or drag images directly onto the canvas; select objects again to move, rotate, resize or edit their properties.
- Edit/pass-through shortcuts, canvas visibility, undo/redo and layers.
- Region capture, cropping, blur, pixelation and solid redaction; export PNG/JPEG or transparent annotations.
- Self-contained, editable `.floatcanvas` projects and automatic recovery.
- Light/dark themes and primary-screen scope by default, with an all-screens option.
- Simplified Chinese and English interfaces, with automatic Windows display-language selection.

## Download and use

Published Windows x64 binaries are provided through the repository's **Releases**, as a ZIP with a SHA-256 file. If no release package is available yet, build from source using the instructions below. A source checkout does not include local build artifacts.

Extract the package, keep all files together, and run `FloatCanvas.exe`. The default package excludes the .NET runtime and debug symbols (PDB files). It starts directly when a compatible **.NET 10 Windows Desktop Runtime x64** is installed.

If a compatible runtime is missing, the launcher displays a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then start FloatCanvas again. You can also find it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). Running FloatCanvas does not require the SDK.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F` | Switch edit/pass-through; annotations remain visible while you use the application underneath |
| `Ctrl+Alt+Shift+F` | Hide/restore the entire canvas |
| `Esc` | Cancel the current operation; otherwise enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` | Undo/redo |
| `Ctrl+S` / `Ctrl+O` | Save/open an editable project |

See the [user guide](Docs/User-Guide.en.md) for more. Settings and exit commands are available through the toolbar's **More** menu and the system tray.

## Languages and scope

Under **More → Settings… → Language / 界面语言**, choose Automatic (`auto`), 简体中文 (`zh-CN`) or English (`en`). Save and restart to apply the change. Automatic follows the Windows display language: Chinese display languages use Simplified Chinese; other languages use English. Interface language is independent of annotation content, keyboard layout and input methods. [English](Docs/User-Guide.en.md) and [Chinese](Docs/User-Guide.md) guides are available.

The target is Windows 10 22H2/Windows 11 x64. The current preview has mainly been used on Windows 11; Windows 10 compatibility is not yet confirmed. Primary-screen scope is the default. Complete cross-screen, mixed-DPI and dual-4K behavior remain under development. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

Annotations stay at screen coordinates; they do not follow web scrolling, game cameras or objects. HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside the current scope. Features and stability are still being refined in this preview.

## Build from source

Requires Windows x64 and .NET SDK 10.0.402, resolved according to `global.json`.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
dotnet run --project src/FloatCanvas.App -c Release
```

Build a Windows Release package without the .NET runtime or PDB files, and check its extracted startup. The version is read automatically from the application project. The startup check requires a compatible desktop runtime:

```powershell
./scripts/Publish.ps1
./scripts/Smoke-Package.ps1
```

## Release a source version

After pushing to a repository with GitHub Actions enabled, branch updates and pull requests run CI. To publish, push a `v<Version>` tag matching the application project's `Version`, or manually run the release workflow and select an existing tag. The workflow builds, tests, packages and smoke-tests the extracted app, then creates a Release with the ZIP and SHA-256 file attached. Changing the version alone does not publish a release. This describes the configured workflow, not a completed remote CI run or online publication.

## License

The project code is licensed under [MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) / [licenses](licenses) for distributed dependencies.

FloatCanvas is independently implemented, drawing on published technical and interaction references from ppInk and ShareX.
