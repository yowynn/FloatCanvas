# FloatCanvas 浮绘

<div align="center">

[简体中文](README.md) · **English**

</div>

A Windows screen annotation tool. Draw over your desktop, place images and edit objects, then return to the application underneath while your annotations stay visible.

![FloatCanvas preview showing a perspective grid, route annotations and the floating toolbar](https://github.com/yowynn/FloatCanvas/releases/download/v0.4.1/FloatCanvas-0.4.1-preview.png)

The current source version is **0.4.2**. See [Releases](https://github.com/yowynn/FloatCanvas/releases) for published versions and downloads, the [changelog](CHANGELOG.md) for important changes, and the [user guide](Docs/User-Guide.en.md) for instructions. Billboard placement and depth-priority occlusion are off by default.

The toolbar separates gray New and red-broom Clear. New confirms according to file and edit state; Clear is immediate, undoable and adds no history. A folding filename bubble shows the bound project. Stamp-slot context menus contain four maintenance actions: Rename, Overwrite, Delete and Unpin.

## Features

- Three root canvases: Foreground, Main and Background, with Main selected initially. Foreground and Background stay in screen space; only Main uses perspective. Composition always follows Background, Main, then Foreground, with mouse presentation above foreground content, following overall canvas visibility and opacity.

- Freehand pen, highlighter, lines and arrows, shapes, text, separate numbered markers, symbols and emoji; official/custom presets apply complete tool parameters, using defaults for new parameters missing from older presets. Per-tool preset selections and edited parameters persist across restarts, with single-click shape creation at a default size.
- Refine freehand strokes as you draw. The highlighter supports a rotatable chisel tip and ink buildup within a stroke. Drag with the line tool to fit a half-ellipse, or use solid, dashed, dotted and dash-dot patterns. Select a rectangle to adjust its corner radius on the canvas.
- Text offers plain, outlined and label styles. Number badges support digits, letters, Roman numerals, Chinese numerals and custom sequences. Choose built-in symbols or extract vector outlines from layers. Browse and search emoji with skin tones and recent choices, and download three optional fonts on demand.
- Import PNG, JPEG, BMP, GIF and WebP through files, the clipboard or drag and drop. GIF/animated WebP retain their original data and play; move, rotate, resize, group and edit image objects. Images and new captures support outward borders following their visible pixel contours, including each animation frame.
- Two aligned toolbar rows with a shared visibility switch for tool and stamp shortcut hints; movable, pinnable Tool options, Properties and Layers windows. Property edits apply immediately, with numeric entry, drag adjustment and segmented choices. Color and parameter drags can be cancelled; preset and swatch management shares canvas undo/redo.
- Browse layers from Main canvas with breadcrumbs, static type icons and a list of the current level; enter a group to see its direct members. Supports nested groups, hierarchy drag and drop, group transforms, and whole-selection or individual rotation and scaling. In Individual transforms, assigning X/Y places each selected item at that coordinate. Multi-selection supports batch opacity assignment; group and shared-instance opacity multiplies child opacity while preserving each child’s own setting. Place shared prefabs and subprojects repeatedly, edit their content together, or detach an ordinary group. Self-contained `.floatprefab` files and external links retain their required content, including offline snapshots when links are unavailable. Import Content and file drops accept `.floatcanvas` and `.floatprefab`; placed stamp groups inherit the stamp name, and group and instance names can be renamed directly.
- Ordinary groups, groups shared within the project and external file links share an internal layer list and persistent Independent canvas / Root project canvas choices. Independent editing uses the current root or group's own origin, perspective, grid and background preview; the root view shows the complete scene while editing only the current level. The group area shows storage and external paths, exports ordinary groups directly as prefabs, and detaches shared or external instances as ordinary groups.
- Editing and canvas visibility are independent. Create while hidden with a preview of only the current operation; commit to reveal the complete canvas or cancel to keep it hidden. Existing hidden layers cannot be selected or modified.
- Canvas is a tool exclusive with the drawing tools: click it or press `C` to select it with a blue border, then select it again for its options. Document (0,0) is the upper-left logical zero. The flat grid stays on a fixed reference-display center intersection; drag anywhere, including over layer pixels, to resize it. With perspective, the nine-point pin, logical origin and central move handle have separate roles.
- Independent canvas background color and opacity, four-point or four-line perspective setup, solid/dashed/dotted grids and canvas presets. Plane size and proportions are independent of grid density, with paper ratios such as A4, a rectification preview, and grid calibration from a local perspective rectangle and its horizontal/vertical cell counts. Calibration previews use a fully opaque black-and-white marching-ants reference grid; density edits do not resize content. Surface objects follow projection; experimental Billboards are drawn or placed directly on screen, then adjusted through their ground position and elevation. The advanced fixed-depth workflow remains available.
- Strong perspective can include the horizon on screen, with grids and Surface content clipped to a safe boundary. Hovering over an unsafe area while editing softly tints its extent red. On release, cancel, relocate, delete, or keep the original layers so adjusting the perspective back restores their display (use with caution), in one undoable operation.
- The spatial nine-point pin defaults to bottom left and fits complete odd-count grids. Solve each adjustment from the fixed reference and final parameters, without accumulated deformation. **Keep grid size while adjusting** fixes logical spacing and pin phase so layers remain aligned to their original grid lines; corners move freely while exact fractional coverage updates automatically. Flat grid spacing is saved independently without resetting stored perspective calibration.
- Enable perspective in Canvas tool settings to expose Depth-priority occlusion, interleaving billboards and Surface content by anchor depth and using layer order at identical depth. Canvas color and the grid participate at the ground-plane depth and remain below objects at equal depth; overall opacity applies after composition. The option is off by default and affects runtime preview and picking only. It does not dirty the project or persist in projects or canvas presets.
- Hold `Shift` while moving to constrain movement to a canvas X/Y axis through the original drag start. A billboard's body moves its anchor; its center handle adjusts elevation vertically on screen, including negative elevation, and corner resizing uses the center as its origin.
- Reopen Welcome from the main menu or tray. Pass-through shows a smaller mascot holding her crown in both hands, without a pen. Wearing the crown means editing; holding it means pass-through. Open/closed eyes indicate a visible/hidden canvas. Click to enter editing, double-click for visibility, or disable the floating mascot in Settings.
- Editable, flattened and shared-prefab stamps, with search, continuous scrolling, drag reordering and a ten-slot shelf ordered `1`–`9`, then `0`. Pin by dragging onto a slot or selecting a slot and clicking an entry. New stamps use `.floatprefab` and their own canvas settings for previews; older libraries remain readable. Ordinary stamps copy independent content; shared stamps synchronize within one project. Editable content retains animation.
- Rectangle, Circle, freehand lasso and polygon lasso captures place a static image at the selected position, using the background, background + canvas, or transparent canvas content. Global Region capture freezes and dims the current screen for selection, inheriting capture shape and style while always using the visible screen image. Completing a selection shows the canvas, enters editing and inserts the capture without switching the current tool; cancellation preserves the previous state. Surface selections follow the canvas plane; Billboard captures preserve their screen appearance. Includes single-image cropping, live blur and pixelation, and PNG/JPEG export. New canvas and Clear current canvas are separate actions; clearing supports Undo and retains the file association.
- Experimental window sources display an explicitly selected window as an image layer, with source cropping, rebinding, pause and freezing as an ordinary image. Instances of one definition share a source, with up to four different live sources at once. Projects retain full offline images and require explicit rebinding after reopening.
- Keyboard demonstration offers Simple recording, Realistic keycaps and Live keyboard styles. Edit multiple layout keys at once, override styles per key, and capture keyboard keys, mouse buttons, wheel directions and gamepad buttons. Visible keyboard layers keep recording. Mouse demonstration shows halos, clicks and trails that follow canvas visibility and opacity.
- Shapes and number badges offer filled and outlined styles. Multi-selection shares compatible primary and secondary color properties. Switch preset palettes, set transparency or restore the color from when the picker opened.
- Self-contained, editable `.floatcanvas` projects retain all three roots; `.floatprefab` retains main-canvas content and its independent preview settings. Both formats support Windows file-type registration. Full projects open in the current instance with unsaved-work protection; prefabs insert at the current editing location. Inserting either format imports only main-canvas content. **Revert changes** in the filename bubble reloads the last saved content and supports Undo. Automatic recovery saves, a blank canvas on normal startup, and manual workspace-history restoration. History filters blank and duplicate versions; its configurable limit defaults to 60. New canvas detaches the file association; Clear preserves it and supports Undo. Optional sign-in startup is off by default.
- Light/dark themes and all-screen scope for new configurations, with primary-screen-only and individual-display options. Switching screens preserves document geometry and undo history.
- Fullscreen overlay capability is detected automatically by default. Use the tray menu to switch between Ordinary and Enhanced modes while keeping the canvas, file association and undo history. Windows screen clipping and ShareX fullscreen captures receive display and input handoff, with the canvas restored afterward.
- GPU preference is on by default: eligible filter scenes use the GPU, while light canvases use CPU caching. Depth-priority occlusion currently uses CPU composition. Settings shows the last frame's actual backend and selection reason; unavailable or failed GPU rendering falls back to the CPU.
- Simplified Chinese and English interfaces, with automatic Windows display-language selection.
- Check GitHub releases in the app; current-user installations and ZIP copies update in place and restart, while all-users installations update through Setup.

Billboard creation is off by default and affects only new objects. Disabling perspective resets new-object placement while preserving existing billboards. Projects, prefabs and recovery files use **v21**. Previously published formats migrate in memory on read. Keep an original copy before saving in the new format if you need to downgrade or exchange files with older versions.

Animation requires the original file or encoded data. Clipboard sources supplying only PNG/Bitmap produce a static image; FloatCanvas does not fetch the original online. Hiding pauses refresh and pass-through keeps playback running. Snapshots, exports and merged image stamps take a static frame; animated export and playback controls are not available.

## Download and use

Windows x64 binaries are provided through the repository's [Releases](https://github.com/yowynn/FloatCanvas/releases). Both unsigned formats below have their own `.sha256` checksum file:

- **Installer: `FloatCanvas-<version>-win-x64-Setup.exe`** lets you choose installation for yourself or all users. The initial default is the current user under `%LOCALAPPDATA%\Programs\FloatCanvas`; an all-users installation defaults to Program Files and requires administrator approval. If you change the all-users directory, choose a folder that every intended user can read and execute. It creates Start menu and Windows uninstall entries for the selected scope, with an optional desktop shortcut. It does not enable login startup automatically.
- **ZIP: `FloatCanvas-<version>-win-x64.zip`** lets you manage the application directory yourself. Extract it to a local folder writable by your Windows account, keep all files together, and run `FloatCanvas.exe`.

Both formats contain the same application files and exclude the .NET runtime and debug symbols (PDB files). Copies used by the same account share `%LOCALAPPDATA%\FloatCanvas` by default; an all-users installation still keeps each user's data separate. Uninstalling retains personal data by default, with an explicit account-and-directory choice to delete settings, presets, stamps and history. Projects saved elsewhere and custom profiles are retained. A compatible **.NET 10 Windows Desktop Runtime x64** is required. You can also build from source using the instructions below; a source checkout does not include packaged binaries.

The installer offers Simplified Chinese and English, initially follows the Windows display language, and lets you choose manually. It skips the runtime page when a compatible runtime is already present. Otherwise choose **Download and install .NET**: Setup downloads the correct x64 desktop runtime from Microsoft, verifies and installs it, then checks again and continues automatically. Windows may require administrator approval even for a current-user FloatCanvas install. Setup does not restart Windows automatically; failures allow a retry or the official download page. **Launch FloatCanvas** is selected on the completion page and can be cleared.

For the ZIP edition, a missing compatible runtime causes the launcher to display a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then start FloatCanvas again. You can also find it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). Running FloatCanvas does not require the SDK.

Click the version at the bottom of the welcome screen to open **Check for updates**. It checks this project's GitHub releases automatically and always links to the [project page](https://github.com/yowynn/FloatCanvas). Current-user installations and ZIP copies update through the official ZIP and restart automatically; installed copies also refresh their uninstall version and file inventory. All-users installations download and verify the new Setup package, then use its wizard with administrator approval. FloatCanvas saves the current canvas and exits only after Setup confirms it is ready. After installation, leave **Launch FloatCanvas** checked and choose Finish to resume work as the original user. Updates default to stable releases; enable **Include preview releases** to opt in. Current-version notes remain available when there is no update. See [updating the app](Docs/User-Guide.en.md#check-and-install-updates).

| Shortcut | Action |
| --- | --- |
| `` Ctrl+` `` | Switch edit/pass-through; annotations remain visible while you use the application underneath |
| `` Ctrl+Shift+` `` | Hide canvas / Show canvas; keep the current edit/pass-through mode |
| `Tab` | Expand/collapse the toolbar while editing; text fields, menus and dialogs keep their own key behavior |
| `Esc` | Cancel the operation or close child/unpinned panels first; then enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` (or `Ctrl+Shift+Z`) | Undo/redo canvas edits and personal preset operations in order |
| `Ctrl+S` / `Ctrl+O` | Save/open an editable project |

Click the app icon on the left of the toolbar to open the main menu after a short delay, or right-click to open it immediately. Double-click the logo in either edit or pass-through mode to show/hide the canvas, and drag it to move it. Press `Tab` or use the main menu's first item, **Collapse toolbar / Expand toolbar**, to fold or unfold the toolbar; right-click still opens the menu when folded. Clicking or right-clicking the pass-through logo only enters editing and does not open the main menu. Settings and exit commands are available through the main menu and system tray. See the [user guide](Docs/User-Guide.en.md) for more.

Default tool keys: `V` select, `E` eraser, `B` freehand, `H` highlighter, `L` line, `R` rectangle, `O` circle, `T` text, `N` numbered marker, `S` symbol, `J` emoji, `C` Canvas and `P` Region capture. `I` opens **Import Content** and `M` opens **More**. Arrow styles and shape variants are selected through tool options or presets. Tap a tool key to switch, or hold it to use the tool temporarily and release to restore the previous tool. Letters remain normal text input while editing text.

Stamp Library uses the backtick key (labelled `~`); Create Stamp uses the equals key (labelled `+`). Neither requires Shift. The two keys to the right of `0` are labelled `-` and `+`. `F4` opens current tool options; `Enter` opens selected-layer properties; `F2` renames. Drag with the middle button or Space + left button to pan; Ctrl + wheel zooms. The wheel alone or with Shift does not pan. Settings lets you customize tool and command shortcuts, and bind global Region capture and the Mouse demonstration master switch.

## Languages and scope

Under **Main menu → Settings… → Language / 界面语言**, choose Automatic (`auto`), 简体中文 (`zh-CN`) or English (`en`). Save and restart to apply the change. Automatic follows the Windows display language: Chinese display languages use Simplified Chinese; other languages use English. Interface language is independent of annotation content, keyboard layout and input methods. [English](Docs/User-Guide.en.md) and [Chinese](Docs/User-Guide.md) guides are available.

The minimum system baseline is **Windows 10 1607 (build 14393) x64**, including LTSB 2016, LTSC 2019 and LTSC 2021; Windows 11 x64 is also targeted. Install a compatible .NET 10 Windows Desktop Runtime and keep Windows and the runtime updated. Microsoft's official support also depends on the Windows version, edition and lifecycle. Testing has mainly used Windows 11; earlier Windows 10/LTSB/LTSC versions still need testing on real systems. New configurations default to all screens, with a primary-screen-only option. Complete cross-screen, mixed-DPI and dual-4K behavior remain under development. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

Choose the fullscreen overlay mode under **Settings → System**, then restart. Automatic falls back to ordinary desktop windows on current detection, initialization or recoverable runtime failures, retaining the canvas and undo history. Forced Enhanced has no automatic fallback; Ordinary does not load enhanced interfaces. If enhancement prevents startup, exit first and use `FloatCanvas.exe --desktop-mode` to open Settings. When the interface is genuinely stuck, releasing input still depends on its recovery; in-place recovery is not guaranteed. See [Start and switch modes](Docs/User-Guide.en.md#start-and-switch-modes).

**Live blur and pixelation require Windows 10 2004 (build 19041) or later.** Earlier systems use static snapshots for new desktop filter regions; frozen-screenshot and image effects remain available. Existing live regions loaded from projects use solid coverage with a warning on earlier systems, retaining the original objects for editing on a supported system.

When the system supports session-local window exclusion, live filters exclude FloatCanvas only from its own capture, leaving the visible canvas available to other capture tools. Older systems use a compatibility path: external screenshots may omit the canvas while live blur or pixelation is visible. Use the in-app export for a complete composite, or remove or hide the live regions before capturing. Window detection in Windows Snipping Tool still has a known limitation.

Annotations stay at screen coordinates; they do not follow web scrolling, game cameras or objects. HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside the current scope.

Perspective calibration needs readjustment when the camera moves. Dense grids at high resolutions can noticeably slow CPU rendering; reduce the row or column count, or hide the grid.

## Build from source

Requires Windows x64, .NET SDK 10.0.402 (resolved according to `global.json`), and Visual Studio / Build Tools with **Desktop development with C++**, including the MSVC x64/x86 compiler and a Windows SDK. Both the native overlay and recovery launcher use this toolchain; the .NET SDK alone is insufficient.

Select that workload in Visual Studio Installer, or run the Microsoft Build Tools installer you downloaded:

```powershell
./vs_BuildTools.exe --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive --wait
```

Default tests include native input contracts without injected input. Actual game and device tests remain separate.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
dotnet run --project src/FloatCanvas.App -c Release
```

Build a Windows Release package without the .NET runtime or PDB files, and check its extracted startup. The version is read automatically from the application project. The startup check requires a compatible desktop runtime:

```powershell
./scripts/Publish.ps1
./scripts/Smoke-Package.ps1
./scripts/Publish-Installer.ps1 -ZipPath ./dist/FloatCanvas-0.4.2-win-x64.zip
```

The installer script uses the verified ZIP to produce `-Setup.exe` and its `.sha256` file in the same directory. It downloads a pinned Inno Setup compiler; no code-signing certificate is required. Adjust the example version to match your build.

Optional output on another drive: `./scripts/Publish.ps1 -OutputDirectory 'C:\FloatCanvas\dist' -ArtifactsPath 'C:\FloatCanvas\build'`. Packages and main build intermediates use those locations; small publish dependency lock files remain in ignored source `obj` directories.

## Release a source version

After pushing to a repository with GitHub Actions enabled, branch updates and pull requests run CI. To publish, push a `v<Version>` tag matching the application project's `Version`, or manually run the release workflow and select an existing tag. The workflow builds, tests, packages and smoke-tests the extracted app, then creates a Release with the ZIP, EXE installer and their individual SHA-256 files attached. Both formats are unsigned. Changing the version alone does not publish a release; existing releases and assets are not overwritten.

## License

Maintained by **Heptayo**. The project code is licensed under [MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) / [licenses](licenses) for distributed dependencies.

FloatCanvas is independently developed. Early versions took limited inspiration from ppInk and ShareX.
