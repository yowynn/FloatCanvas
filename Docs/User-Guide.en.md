# FloatCanvas user guide

[简体中文](User-Guide.md) | English

This guide covers the Windows x64 preview **0.2.0-preview.2**. Earlier versions may have different interfaces and features; check the [changelog](../CHANGELOG.md). Save important work as a project file.

A drag that starts on the canvas stays with the canvas, even when it crosses the toolbar or ends on a button. Buttons are not triggered on release. A gesture that starts on the toolbar stays with the toolbar and does not draw on the canvas.

The interface supports automatic Windows language selection, Simplified Chinese and English. Choose a language in Settings, save and restart to apply it.

FloatCanvas keeps annotations and images visible over the screen. Enter edit mode to draw or adjust them. Switch to pass-through mode to click, drag and scroll in the application underneath while your annotations remain visible.

## Start and switch modes

For the packaged app, extract the Windows x64 ZIP for your version, keep all files together, and run `FloatCanvas.exe`. The default package excludes the .NET runtime and debug symbols (PDB files). It starts directly when a compatible **.NET 10 Windows Desktop Runtime x64** is installed.

If a compatible runtime is missing, the launcher displays a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then run `FloatCanvas.exe` again. You can also select it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). The SDK is not required; the plain .NET Runtime, ASP.NET Core Runtime and .NET Framework do not replace the required desktop runtime.

1. Choose **Start drawing** on the welcome screen, or press `` Ctrl+` `` to enter edit mode.
2. Press `` Ctrl+` `` again for pass-through. The toolbar disappears, annotations stay visible, and the application underneath receives input again.

Each startup shows the welcome screen in the middle of the screen and opens a blank canvas without automatically showing earlier annotations or captures. Closing it, choosing Start drawing or entering edit mode with the toggle shortcut dismisses it for this session. To continue earlier work, click the app icon on the left of the toolbar and choose **File → Restore workspace history…**, or use the same command in the system tray. Restoration stays in pass-through; use the toggle shortcut to resume editing.

The welcome screen labels the shortcuts **Drawing mode / pass-through mode** and **Hide canvas / Show canvas**, with the version and an author credit containing a red heart below. Hiding keeps your content. Canvas visibility is independent of edit/pass-through mode: entering edit mode does not reveal a hidden canvas, and showing the canvas keeps the current mode. Tool windows remain usable while the canvas is hidden, but new content cannot be added.

Annotations stay at screen coordinates. They do not follow web scrolling or objects in a game.

The default drawing scope is **Primary screen only**, limiting annotations and capture to that screen. Applications on secondary screens can keep running; switching to another application enters pass-through.

Open **Main menu → Settings…** and change **Drawing scope** to **All screens** for the next launch. The startup options `--primary-screen` or `--all-screens` override the scope for that run without changing the saved preference; do not use both together. Complete cross-screen and mixed-DPI behavior is still being refined. Objects outside the active screen keep their coordinates; use **Main menu → Canvas → Move annotations into view** to bring them back.

In edit mode, FloatCanvas receives normal window keyboard and mouse input. An unmapped letter should not be sent to the application underneath. Switching to another application hides editing tools and enters pass-through without removing annotations. If FloatCanvas cannot acquire the required input focus, it stays in pass-through and explains why.

Uncommitted text is retained when focus moves away and is restored when you resume editing the same canvas. Explicit cancellation or switching to pass-through closes the text editor.

| Shortcut | Action |
| --- | --- |
| `` Ctrl+` `` | Switch between edit and pass-through |
| `` Ctrl+Shift+` `` | Hide canvas / Show canvas; keep the current edit/pass-through mode |
| `Esc` | Cancel the operation or close the color child panel, then an unpinned panel; finally enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` | Undo/redo; `Ctrl+Shift+Z` also redoes |
| `Ctrl+A` | Select all editable elements |
| `Ctrl+C` / `Ctrl+V` | Copy/paste |
| `Ctrl+D` | Duplicate selected elements with a small offset |
| `Ctrl+G` / `Ctrl+Shift+G` | Group/ungroup |
| `1`–`9`, `0` | Select the corresponding stamp from the ten-slot shelf, then click the canvas to place it |
| `M` / `-` / `+` | More tools / open stamp library / save selection as a stamp; use main-keyboard `-` and `+` |
| `Delete` | Delete selected, unlocked elements |
| Arrow keys / `Shift` + arrow keys | Move selected elements by 1/10 document pixels |
| `Ctrl+S` / `Ctrl+Shift+S` | Save/save the current project as another file |
| `Ctrl+O` | Open a project |

Open **Main menu → Settings…** to change shortcuts or the light/dark theme. A registration conflict detected when saving displays a reason and preserves the previous working shortcut. Windows or another application may intercept a combination before the recorder receives it; choose another combination if the field does not change. The system tray also offers editing, visibility, settings and exit commands.

Toggle shortcuts use Ctrl/Alt/Shift combinations. Win and system navigation combinations remain available to Windows; `Ctrl+Alt+Esc` is reserved for emergency input release. An unresponsive editing UI or interrupted input ownership returns to pass-through; earlier drawing keys are not replayed into the application underneath.

## Check and install updates

Click the version in the lower-left corner of the welcome screen to open **Check for updates**. The panel automatically checks [FloatCanvas GitHub releases](https://github.com/yowynn/FloatCanvas/releases), showing your current version, an available newer version and its release notes. Use **Check again** to refresh. A link to the [project page](https://github.com/yowynn/FloatCanvas) remains at the bottom of the panel.

When an update is available, choose **Update now**. FloatCanvas shows download progress, verifies the package, saves recovery data for your current canvas, exits, replaces the app and restarts automatically to resume your work. Settings, presets and canvas data are retained. Cancel the download or close the panel to keep using your current version. Download or verification failures do not replace the app; if replacement fails, it attempts to restore the previous version and reports the problem.

In-app installation requires the complete official Windows x64 ZIP extracted to a **local folder writable by your Windows account**. Keep all package files together. Read-only folders, network locations and development runs cannot install updates directly; move the complete published app to a suitable folder first. Updating does not install the .NET runtime for you.

Stable versions check only newer stable releases. Versions with a prerelease marker such as `preview` also check newer prereleases. Older versions are never installed. When there is no update, a network error or a temporary GitHub request limit, the panel shows the status and retains the project link so you can visit the releases page.

If your older version has no **Check for updates** entry, first download and install a release containing this feature from GitHub. Subsequent updates can be installed in the app.

## Interface language and input methods

Choose **Main menu → Settings… → Language / 界面语言**, save, and restart FloatCanvas to apply the change:

| Setting | Interface language at startup |
| --- | --- |
| Automatic (Windows) (`auto`, default) | Use Simplified Chinese for Chinese Windows display languages; fall back to English for other display languages |
| 简体中文 (`zh-CN`) | Simplified Chinese |
| English (`en`) | English |

Interface language does not translate or rewrite annotations, project text, user filenames or clipboard content. It does not change your Windows keyboard layout or input method. You can enter Chinese with an English interface or English with a Chinese interface; no particular input method or double-pinyin scheme is required.

The App's language selection applies to its own toolbar, menus, settings, messages, file-dialog titles and file-type filters. Windows-owned **Open**, **Save** and **Cancel** buttons may still use the Windows display language. Changing the App's language does not change Windows language settings.

## Toolbar and tool options

The app icon is on the left, followed by two rows aligned in columns:

- **Upper row:** Select and Eraser, then ten entries: Freehand, Highlighter, Line, Rectangle, Ellipse, Text, Number, Symbol, Emoji and More. More provides pixelation and other tools; Text and Number have separate entries.
- **Lower row:** Stamp library and New stamp, then square stamp slots `1`–`0` beneath the ten drawing entries, with a status readout on the right. Empty slots have faint outlines, assigned slots have slightly stronger muted outlines, and the active shortcut stamp uses a blue outline.

The upper-right controls provide undo/redo, import, capture, properties, layers, Clear canvas, canvas opacity, visibility and Done. Open pixelation and other effects through More. The opacity slider occupies two button widths. The status readout follows the light/dark theme, truncates long messages and scrolls on hover. The toolbar starts centered on the primary screen with the crown at the screen's top edge. Window positions, sizes and toolbar expansion are remembered for the current session and reset on restart; custom shortcuts and other preferences remain saved.

- **Click the app icon:** open the main menu. A single click waits for the system double-click interval to avoid conflicting actions; right-click opens the menu immediately.
- **Double-click the app icon:** expand/collapse the toolbar. The collapsed toolbar keeps only the icon.
- **Drag the app icon:** move the toolbar and remember its position.
- **Click a tool icon:** select the tool. Hover for its name and shortcut; the main menu's **Drawing tools** and **Images and effects** sections also offer tool commands.
- **Select the current tool again:** click the same icon or press its shortcut again to open/close its options. Switching tools while options are open updates the panel for the new tool.

The options window uses the current tool name as its title, with a short description and parameters alongside presets. With the toolbar expanded, it initially opens above or below the bar. Drag it elsewhere afterwards; moving or collapsing the toolbar does not move or close it. When the toolbar is collapsed, press the current tool key again to open options near the pointer, within the screen bounds. An unpinned window closes when you click the canvas or another interface; pin it to keep drawing with options visible. Escape closes the color child panel first, then an unpinned tool window. Entering pass-through hides editing panels.

Lines share independent start/end arrowheads for plain, single-arrow and double-arrow combinations, with solid, dashed or dotted strokes. Set a rectangle's corner radius for rounded corners; enable proportional drawing for squares or circles. Symbols include triangles, stars, hearts, checks and crosses with editable colors. The emoji tool provides a gallery and inserts at its configured Size, with precise numeric entry and a larger slider range. Line-pattern and arrowhead choices include diagrams, and stroke width has a thickness preview.

Tool keys: `V` select, `E` eraser, `B` freehand, `H` highlighter, `L` line, `R` rectangle, `O` ellipse, `T` text, `N` numbered marker, `S` symbol and `J` emoji. `A` remains an arrow compatibility shortcut; `M` opens More, main-keyboard `-` opens the stamp library, and `+` (the `=/+` key) saves the selection as a stamp. While editing text, these letters are treated as text input.

Shortcut hints appear at the upper right of upper-row buttons and lower left of lower-row buttons. Turn off **Show tool shortcut hints** in the main menu or Settings to hide letters, stamp-slot numbers and the `-`/`+` hints together. Shortcuts still work, and the preference is saved across restarts.

Draw lines and arrows by dragging; they have no single-click default length or angle. Rectangles, ellipses and symbols have default width and height: click empty space to create one at that size, centered on the click, or drag to choose its size. Proportional mode or `Shift` creates squares and circles. Enter numeric options directly or drag the corresponding slider. Keys used inside text fields, dropdowns, the color spectrum and sliders operate those controls without switching tools or selecting stamps.

### Tool presets and colors

Official and personal presets use matching rounded square preview tiles, five per row, with names in hover hints. Every tool has at least one official preset. All official presets are shown. My presets shows only the current tool, with room for at least four rows and a viewport that expands alongside taller tool options. Additional presets scroll inside this area without making the window taller.

A tool starts with its own default parameters on first use: official defaults use the palette's red (`#E03E3E`) foreground with transparent fill. Each tool remembers its base preset and complete current parameters independently, retaining changes when you switch tools, close options, switch edit/pass-through mode or restart the app. State is saved locally, combining frequent edits over about 500 milliseconds; normal exit and update restarts wait for pending writes. Window positions still reset on startup, and the next number remains part of the canvas document.

Click a tile to apply and select it immediately. A tool always has one selected recipe. Editing parameters keeps that selection and the official previews; only explicitly clicking the same recipe again restores its baseline. An official recipe initially keeps the current tool's colors and stroke width when applied; later clicks restore that selection's baseline. Official baselines and previews also persist across restarts, unaffected by subsequent temporary color changes. Personal recipes store the complete configuration, and temporary parameter changes do not overwrite their definitions. Tool parameter changes do not alter existing objects or saved projects.

Use the plus button to save a new preset, or right-click a personal tile to overwrite, rename or delete it. Saving and renaming use the shared name dialog. Drag with the left mouse button to reorder; dragging and right-clicking do not apply a recipe. Deleting the selected personal preset applies the first official preset.

Choose colors in tool options or Properties. Foreground controls strokes or text; background controls fill for applicable shapes. The primary row has a rounded color-value button and ten quick colors in two rows. Clicking a quick color applies it directly. Small swatches have square corners and subtle outlines; recent and saved colors fit ten per row, with pixel-aligned spacing at the current display scale.

Open the value button for a saturation/value spectrum, hue strip, opacity and the 20 most recent colors. Changes are drafts until the child panel closes or loses focus, when they apply once without a confirmation button. Clicking the parent options window closes only the color panel. Tools based on strokes and fills use color opacity instead of a duplicate tool-opacity control.

My swatches saves named colors. Drag with the left mouse button to reorder, or right-click to overwrite, rename or delete a swatch. Scroll the fixed area to reach additional swatches. Custom input accepts `#RGB`, `#RRGGBB` or `#AARRGGBB`, such as `#FF2383E2`; `#00000000` is fully transparent. Activate the eyedropper and click the canvas to sample a color, or press `Esc` to cancel.

Tool options affect newly created elements. Use Properties to change existing elements.

## Draw, select and adjust

- **Select:** click an element, or drag a selection box from an empty area. Shift-click to add or remove elements. Grouped elements select together; hold `Alt` while clicking to select one member.
- **Move and resize:** drag an element to move it; drag a corner handle on its selection box to resize.
- **Rotate:** drag the rotation handle above the selection box. Hold `Shift` to snap in 15° steps.
- **Line endpoints:** select one line or arrow, then drag an endpoint.
- **Text:** choose the text tool and click the canvas. Enter multiple lines and choose **Done** or press `Ctrl+Enter`. Double-click text in selection mode to edit it again.
- **Numbered markers:** click to add and select the next number; hold `Ctrl` to insert from inside an existing selection. The number label stays on one line and shrinks to fit the badge without changing its size or the configured font size. Deleting a marker does not renumber the others.
- **Erase:** the eraser removes whole elements. One erase gesture can be undone as one operation.

Shapes, text, numbers and emoji select the newly inserted object so you can move, scale or rotate it without switching tools; clicking empty space creates and selects the next object. Freehand, Highlighter and stamps remain unselected for continuous use. Lines and arrows require a drag to create. Hold `Ctrl` to temporarily hide selection handles and create at an overlapping position.

Without proportional constraints, rectangles, ellipses and symbols support independent width and height changes. Secondary edits change their actual geometry while retaining stroke width and corner radii, rather than stretching a raster image. Text and numbered markers offer a system-font dropdown with previews.

The Number tool has a starting number and a positive increment. New canvases start at that number. Next number on this canvas is saved independently in the document, can be edited and undone, and is not stored as a global counter in presets or application settings. Font size changes the actual size of new numbered markers.

Every basic element remains a selectable object, including freehand lines, text, numbered markers and images.

**Properties** and **Layers** are separate windows. Open each from the toolbar or **Main menu → Windows**. With one element selected, edit its position, size, angle, stroke, fill, opacity and applicable text settings in Properties, then choose **Apply properties**. The whole application of properties can be undone in one step.

These tool windows share a floating-panel layout. Drag an empty part of the header to move a panel, or drag an edge or the two diagonal lines in the bottom-right corner to resize it. Close it with the top-right button and reopen it from its original entry point. Panels do not minimize or maximize, including when you double-click the header. Properties shows position and size, appearance, and text or numbering in one continuous view, with only fields applicable to the selected object. Scroll when needed; **Apply properties**, **Reload selection** and the status message stay visible at the bottom.

Drag the label beside a numeric field, such as X, Y, width, height, angle or stroke width, horizontally to adjust it. `Shift` makes fine adjustments; `Ctrl` speeds them up. Release to commit one operation, or press `Esc` to cancel the drag. X/Y refer to the top-left of the element's screen-aligned bounding box. Width and height are measured along the element's own axes, including existing scale but excluding the stroke. Resizing through these fields preserves rotation; with proportional resizing, you can change only width or height. If the geometry has zero size along an axis, adjust its endpoints before changing that dimension. Arrowheads can be filled triangles, open arrows or diamonds; a head size of 0 scales automatically with stroke width.

Elements higher in the layer list appear in front. Click an already selected layer name again, or press `F2`, to rename it. `Enter` or moving focus saves; `Esc` cancels; an empty name restores the default. Names persist in projects, recovery and undo/redo. Drag the handle on the left of a row to reorder it; use the eye and lock icons on the right to change visibility and locking. Right-click for duplicate, delete, bring to front, send to back, group and ungroup. Use the “?” beside the top-right close button for operation and shortcut help. Hidden or locked elements remain selectable in the list to change those states. Select several editable elements and press `Ctrl+G` to group them, or `Ctrl+Shift+G` to ungroup.

Hiding Properties retains unapplied input. After selecting another element, return to the original element to apply its draft, or choose **Reload selection** to load the newly selected element.

The canvas opacity slider at the upper right affects annotations and images, while the screenshot background keeps normal brightness. Clear canvas and visibility controls are beside it. **Clear canvas** has a red background and a delete icon. The first click changes it to a confirmation checkmark and shows a red confirmation bubble with white **Clear canvas?** text; click again to clear the canvas and restore the original button. A quick double-click also confirms. Clicking elsewhere, pressing `Esc` or leaving edit mode cancels confirmation without clearing content. Clearing archives the current workspace first; use **Restore workspace history** to retrieve it. Hiding keeps your content and the current edit/pass-through mode. New elements cannot be added while hidden. If a display change leaves annotations outside the visible screen, use **Main menu → Canvas → Move annotations into view**. This moves visible, unlocked objects while preserving their spacing and the screenshot crop.

## Stamp library and shelf

Stamps reuse artwork you create. Select one or more elements and choose **New stamp** on the lower toolbar row, or use the save-selection action in the stamp library. Enter a name and choose a save mode. New stamps automatically fill the first empty shortcut slot; if all slots are full, the stamp remains available in the library:

| Save mode | Placed content |
| --- | --- |
| Editable group | Preserve shapes, text, cropped images, styles and transforms; adjust the group together or `Alt`-click a member to edit it |
| Merge as image | Place one image while retaining editable source objects in the library; choose **Use editable source** to place those original elements again |

Search or page through the library, choose **Use** or double-click a thumbnail, then click the canvas to place it. Each click keeps the saved visual size and relative rotations, centered on the clicked position. No size-drawing gesture is needed, and repeated clicks place further copies. Each placement can be undone separately and leaves the copy unselected. Hover a slot or library entry to see its name, shortcut, larger preview and the preview-to-actual-size ratio. New stamps fill the first empty shortcut slot automatically.

Select a library entry to rename it, overwrite it with the current canvas selection, or delete it. Editing a placed copy does not automatically change the library original; use overwrite to update that original.

Pin a stamp to a slot labeled `1`–`9` or `0` to select it from the second toolbar row or its number key. The key selects the stamp; click the canvas afterwards to place it. Numbers remain text input while editing text. Unpinning clears only the shortcut; deleting a stamp also clears its pinned slots.

## Place, replace and crop images

Press `Ctrl+V` to paste a clipboard image, or choose **Image** to select one or more files. New images are placed at the center of the currently visible canvas area. PNG, JPEG, BMP and WebP are supported; transparent images retain their transparent areas.

You can also start dragging an image from a file manager or browser, keep holding it, and press the edit/pass-through shortcut (`` Ctrl+` `` by default) to open the canvas. Release the mouse at the desired position. The image is centered on the drop point; when dropping several images, subsequent images are offset slightly. The entire import can be undone in one step.

The source must provide actual image data or supported image files. A URL or web link alone cannot be imported as an image. Websites and browsers may provide different drag content, so direct dragging is not guaranteed for every website. If dragging is unavailable, use **Copy image** and paste, or save the image and import the file.

An imported image is a separate object and does not automatically replace the background. Select it to move, rotate, resize or change opacity.

- **Replace source image:** select one image and use **Main menu → Images and effects → Replace selected image…**. Its position, size, crop and effect regions are retained.
- **Crop image:** select the image, choose **Crop**, and drag the area to keep. The original asset is retained, and cropping can be undone.
- **Blur / solid redaction:** choose a tool from More or Main menu → Images and effects. When the selection contains exactly one image, effects target that image even if text or shapes are also selected. Drag inside the image and unlock it first if needed. With no images or multiple images selected, a frozen screenshot uses its background; on the live canvas, blur retains an image of that moment and redaction creates a covering shape.
- **Pixelation:** on the live canvas, choose Pixelate from More and drag a continuously updating screen region. It includes the desktop and FloatCanvas objects below it, even when an image is selected. Move, resize or rotate the region and save it in a project. In frozen screenshots, pixelation remains a static effect on an image or the background.

Image cropping works on exactly one unlocked image in the selection. If several images are selected, reduce the selected images to one; unlock it before cropping if necessary.

Live desktop pixelation requires Windows 10 2004 (build 19041) or later to reliably exclude FloatCanvas's own windows. Earlier systems can still draw, import images and capture the screen. New desktop pixelation uses a static snapshot with a notice and does not continuously follow the desktop. Pixelation, blur and solid redaction on frozen screenshots or images are unaffected by this version requirement. Existing live regions loaded from projects use solid coverage with a warning on earlier systems; the original live objects remain unchanged instead of silently converting project content.

On systems that support live pixelation, visible regions update continuously. If desktop capture becomes unavailable, the region is covered by a solid color. Export, image copying and merged stamps use a snapshot taken for that operation; the project retains an editable live region. Static image effects move and transform with their target image. Effects and cropping can be undone.

**External capture limitation:** while live pixelation is visible, FloatCanvas excludes its display and input surfaces from system capture to avoid sampling its own output. External screenshots may omit other annotations. Use the in-app export for a complete composite; removing or hiding all live regions restores normal capture behavior. This does not resolve the known Windows Snipping Tool window-detection limitation.

## Capture and return to the live canvas

1. Choose **Capture and edit screen…**. This freezes the desktop into a separate screenshot draft for cropping, redaction and export, while retaining your live annotations. After the desktop freezes, drag to select a capture region. You can also capture the current drawing scope directly from **Main menu → Canvas**.
2. Continue using the same tools for drawing, images, text and effects. Existing annotations are copied into a separate screenshot draft.
3. Use **Crop** to change the screenshot region. If an image is selected, cropping applies to that image instead; clear the image selection before cropping the screenshot canvas.
4. Use **Main menu → File → Export composite image…** for PNG/JPEG, or press `Ctrl+C` with no elements selected to copy the composite result.
5. Use **Main menu → Canvas → Return to live canvas** to restore the original canvas. **Resume capture draft** reopens the draft.

Editing a screenshot does not change the original live canvas. Pressing `` Ctrl+` `` during screenshot editing retains the draft, returns to the live canvas and enters pass-through so you do not operate the desktop through a frozen picture.

When restoring a screenshot draft or after a display-layout change, FloatCanvas moves the screenshot view onto the primary screen only if its crop lies completely outside every active drawing screen. If any part remains visible, its position is kept. The original image, crop and committed annotations are unchanged.

If the previous screenshot has unsaved changes, starting another capture offers save as, discard or cancel. A failed capture preserves existing content. System-protected areas may be masked; the App reports this and lets you keep editing the rest of the screenshot.

## Save, export and recover

| Operation | Result | Can objects still be edited? |
| --- | --- | --- |
| Save/save project as | A `.floatcanvas` file with the current canvas, original images and effects | Yes; reopen it with **Open project** |
| Export composite image | A PNG/JPEG combining the background, annotations and images | The export is an image; the current project remains editable |
| Export transparent annotations | A transparent PNG containing only annotations and images | The export is an image; the current project remains editable |
| Copy selected objects | Editable FloatCanvas objects plus an interoperable image | Paste in FloatCanvas to keep editing; other apps usually receive an image |

The live canvas and screenshot draft are saved separately. Export does not clear content. Projects retain original images for later editing; use a composite image export when sharing a redacted result.

The current project format is **v2**. The app can still open **v1** projects, while newly saved projects use v2. Older FloatCanvas versions using the v1 format explicitly reject v2 projects, preventing a save that silently loses new information such as groups or stroke patterns. Keep the original v1 file if you need to exchange projects with an older version.

Committed edits trigger a recovery save after about one second. The App waits for pending recovery writes before exiting. Normal startup still uses a blank canvas. Choose **Main menu → File → Restore workspace history…** to select the latest or an earlier workspace and restore its live canvas and capture draft together, in pass-through. Undo history is not retained across restarts.

Default data paths:

- Settings: `%LOCALAPPDATA%\FloatCanvas\settings.json`
- Latest recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.floatcanvas`
- Previous recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.previous.floatcanvas`
- Workspace history: `%LOCALAPPDATA%\FloatCanvas\recovery\history`
- Tool presets: `%LOCALAPPDATA%\FloatCanvas\tool-presets.json`
- Per-tool current state and official baselines: `%LOCALAPPDATA%\FloatCanvas\tool-state.json`
- Recent colors and My swatches: `%LOCALAPPDATA%\FloatCanvas\colors.json`
- Stamp library: `%LOCALAPPDATA%\FloatCanvas\stamps`

When manually restoring a workspace whose latest recovery is damaged, the App tries that workspace's previous version. A read failure is reported without replacing the original file. Exiting a blank startup canvas without editing does not overwrite the earlier recovery. Recovery helps retrieve recent work; save important work as your own project file as well.

Closing the toolbar or entering pass-through does not exit the App. To quit, use the system tray or **Main menu → Exit FloatCanvas**. Startup with Windows is disabled by default.

## Support and limitations

The minimum system baseline is **Windows 10 1607 (build 14393) x64**, including LTSB 2016, LTSC 2019 and LTSC 2021; Windows 11 x64 is also targeted. Windows 10 22H2 is no longer required to start the app. Features that need newer Windows versions use a fallback or show a notice without blocking basic drawing.

Install a compatible **.NET 10 Windows Desktop Runtime x64** available for your system, and keep Windows and the runtime updated. The app's minimum baseline is separate from Microsoft's support commitments, which also depend on the Windows version, edition and lifecycle; see [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Testing has still mainly used Windows 11. Earlier Windows 10/LTSB/LTSC versions need testing on real systems; source inspection and compatibility-branch tests cannot replace that acceptance work.

Primary-screen scope is the default. Complete cross-screen, mixed-DPI, portrait-display, hot-plug, lock-screen recovery and dual-4K behavior are still being refined. Behavior may vary across devices and applications.

FloatCanvas is intended for ordinary desktop and fullscreen applications without requiring windowed or borderless mode in every case. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

When using window detection in Windows Snipping Tool or another screenshot app, the drawing layer may become the capture target. To keep annotations in the result, use FloatCanvas's own capture or export commands. To capture only the application underneath, use the Hide canvas / Show canvas shortcut (default `` Ctrl+Shift+` ``) to hide the canvas, take the screenshot, then show it again. Pass-through keeps annotations visible. Canvas visibility keeps the current edit/pass-through mode; use the separate mode shortcut to change input behavior.

HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside v1.
