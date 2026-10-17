# FloatCanvas 浮绘

[简体中文](README.md) | English

A Windows screen annotation tool. Draw over your desktop, place images and edit objects, then return to the application underneath while your annotations stay visible.

Current preview version: **0.2.0-preview.2**. Features and stability are still being refined. See the [changelog](CHANGELOG.md) for version changes and the [user guide](Docs/User-Guide.en.md) for instructions.

## Features

- Freehand pen, highlighter, lines and arrows, shapes, text, separate numbered markers, symbols and emoji; graphical official/custom presets, per-tool preset selections and edited parameters retained across restarts, and single-click shape creation at a default size.
- Import image files or clipboard images, or drag images directly onto the canvas; select objects again to move, rotate, resize, group or edit their properties.
- Two aligned toolbar rows with a shared visibility switch for tool and stamp shortcut hints; movable Tool options, Properties and Layers windows, with independent editing and canvas visibility.
- A stamp library with editable and flattened stamps, a ten-slot shelf, and `1`–`0` shortcuts.
- Region capture, cropping, blur, live pixelation and solid redaction; export PNG/JPEG or transparent annotations. The red **Clear canvas** button requires a second click to confirm and archives current work before clearing.
- Self-contained, editable `.floatcanvas` projects; automatic recovery saves, a blank canvas at startup, and manual workspace-history restoration.
- Light/dark themes and primary-screen scope by default, with an all-screens option.
- Simplified Chinese and English interfaces, with automatic Windows display-language selection.
- Check GitHub releases in the app, download and install updates, then restart automatically.

## Download and use

Windows x64 binaries are provided through the repository's [Releases](https://github.com/yowynn/FloatCanvas/releases), as a ZIP with a SHA-256 file. You can also build from source using the instructions below; a source checkout does not include packaged binaries.

Extract the package, keep all files together, and run `FloatCanvas.exe`. The default package excludes the .NET runtime and debug symbols (PDB files). It starts directly when a compatible **.NET 10 Windows Desktop Runtime x64** is installed.

If a compatible runtime is missing, the launcher displays a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then start FloatCanvas again. You can also find it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). Running FloatCanvas does not require the SDK.

Click the version at the bottom of the welcome screen to open **Check for updates**. It checks this project's GitHub releases automatically and always links to the [project page](https://github.com/yowynn/FloatCanvas). When an update is available, choose **Update now** to download, verify and replace the app, then restart automatically. No manual extraction or overwriting is needed. Keep the complete extracted ZIP in a local folder writable by your Windows account. Stable versions receive stable releases; previews can also receive newer previews. See [updating the app](Docs/User-Guide.en.md#check-and-install-updates).

| Shortcut | Action |
| --- | --- |
| `` Ctrl+` `` | Switch edit/pass-through; annotations remain visible while you use the application underneath |
| `` Ctrl+Shift+` `` | Hide canvas / Show canvas; keep the current edit/pass-through mode |
| `Esc` | Cancel the operation or close child/unpinned panels first; then enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` | Undo/redo |
| `Ctrl+S` / `Ctrl+O` | Save/open an editable project |

Click the app icon on the left of the toolbar for the main menu, double-click to expand/collapse, or drag it to move the toolbar. Settings and exit commands are available through the main menu and system tray. See the [user guide](Docs/User-Guide.en.md) for more.

Tool keys: `V` select, `E` eraser, `B` freehand, `H` highlighter, `L` line, `R` rectangle, `O` ellipse, `T` text, `N` numbered marker, `S` symbol and `J` emoji. `A` remains an arrow compatibility shortcut; `M` opens **More**, main-keyboard `-` opens the stamp library, and `+` saves the selection as a stamp. Letters remain normal text input while editing text.

## Languages and scope

Under **Main menu → Settings… → Language / 界面语言**, choose Automatic (`auto`), 简体中文 (`zh-CN`) or English (`en`). Save and restart to apply the change. Automatic follows the Windows display language: Chinese display languages use Simplified Chinese; other languages use English. Interface language is independent of annotation content, keyboard layout and input methods. [English](Docs/User-Guide.en.md) and [Chinese](Docs/User-Guide.md) guides are available.

The minimum system baseline is **Windows 10 1607 (build 14393) x64**, including LTSB 2016, LTSC 2019 and LTSC 2021; Windows 11 x64 is also targeted. Install a compatible .NET 10 Windows Desktop Runtime and keep Windows and the runtime updated. Microsoft's official support also depends on the Windows version, edition and lifecycle. Testing has mainly used Windows 11; earlier Windows 10/LTSB/LTSC versions still need testing on real systems. Primary-screen scope is the default. Complete cross-screen, mixed-DPI and dual-4K behavior remain under development. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

**Live desktop pixelation requires Windows 10 2004 (build 19041) or later.** Earlier systems use a static snapshot for newly created desktop pixelation; frozen-screenshot and image effects remain available. Existing live regions loaded from projects use solid coverage with a warning on earlier systems, retaining the original objects for editing on a supported system.

While live pixelation is visible, FloatCanvas excludes its canvas surfaces from system capture to prevent feedback. External screenshots may omit other annotations; use the in-app export for a complete composite. Removing or hiding the live regions restores normal capture behavior. Window detection in Windows Snipping Tool still has a known limitation.

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

Optional output on another drive: `./scripts/Publish.ps1 -OutputDirectory 'C:\FloatCanvas\dist' -ArtifactsPath 'C:\FloatCanvas\build'`. Packages and main build intermediates use those locations; small publish dependency lock files remain in ignored source `obj` directories.

## Release a source version

After pushing to a repository with GitHub Actions enabled, branch updates and pull requests run CI. To publish, push a `v<Version>` tag matching the application project's `Version`, or manually run the release workflow and select an existing tag. The workflow builds, tests, packages and smoke-tests the extracted app, then creates a Release with the ZIP and SHA-256 file attached. Changing the version alone does not publish a release.

## License

Maintained by **Heptayo**. The project code is licensed under [MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) / [licenses](licenses) for distributed dependencies.

FloatCanvas is independently implemented, drawing on published technical and interaction references from ppInk and ShareX.
