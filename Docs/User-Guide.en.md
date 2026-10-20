# FloatCanvas user guide

[简体中文](User-Guide.md) | English

This guide covers the Windows x64 release **0.2.1**. Earlier versions may have different interfaces and features; check the [changelog](../CHANGELOG.md). Save important work as a project file.

A drag that starts on the canvas stays with the canvas, even when it crosses the toolbar or ends on a button. Buttons are not triggered on release. A gesture that starts on the toolbar stays with the toolbar and does not draw on the canvas.

The interface supports automatic Windows language selection, Simplified Chinese and English. Choose a language in Settings, save and restart to apply it.

FloatCanvas keeps annotations and images visible over the screen. Enter edit mode to draw or adjust them. Switch to pass-through mode to click, drag and scroll in the application underneath while your annotations remain visible.

## Start and switch modes

For the packaged app, extract the Windows x64 ZIP for your version, keep all files together, and run `FloatCanvas.exe`. The default package excludes the .NET runtime and debug symbols (PDB files). It starts directly when a compatible **.NET 10 Windows Desktop Runtime x64** is installed.

If a compatible runtime is missing, the launcher displays a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then run `FloatCanvas.exe` again. You can also select it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). The SDK is not required; the plain .NET Runtime, ASP.NET Core Runtime and .NET Framework do not replace the required desktop runtime.

1. Choose **Start drawing** on the welcome screen, or press `` Ctrl+` `` to enter edit mode.
2. Press `` Ctrl+` `` again for pass-through. The toolbar disappears, annotations stay visible, and the application underneath receives input again.

Normal launches show the welcome screen in the middle of the screen and open a blank canvas without automatically showing earlier annotations or captures. Closing it, choosing Start drawing or entering edit mode with the toggle shortcut dismisses it for this session. To continue earlier work, click the app icon on the left of the toolbar and choose **File → Restore workspace history…**, or use the same command in the system tray. Restoration keeps the mode you started in: continue editing immediately when restoring from edit mode, or remain in pass-through when restoring from pass-through.

The welcome screen labels the shortcuts **Drawing mode / pass-through mode** and **Hide canvas / Show canvas**, with the version and an author credit containing a red heart below. Hiding keeps your content. Canvas visibility is independent of edit/pass-through mode: entering edit mode does not reveal a hidden canvas, and showing the canvas keeps the current mode. Tool windows remain usable while the canvas is hidden, but new content cannot be added.

Annotations stay at screen coordinates. They do not follow web scrolling or objects in a game.

New configurations default to **All screens**. Choose **Primary screen only** to limit annotations and capture to that screen. Existing saved scope choices are retained. Switching to another application enters pass-through.

Open **Main menu → Settings…** and choose **Primary screen only** or **All screens** under **Drawing scope**. Saving applies the change immediately, without restarting. The startup options `--primary-screen` or `--all-screens` override the scope for that run without changing the saved preference; do not use both together. A scope specified by a startup option remains in effect for that run. Complete cross-screen and mixed-DPI behavior is still being refined. Objects outside the active screen keep their coordinates; use **Main menu → Canvas → Move annotations into view** to bring them back.

In edit mode, FloatCanvas receives normal window keyboard and mouse input. An unmapped letter should not be sent to the application underneath. Switching to another application hides editing tools and enters pass-through without removing annotations. If FloatCanvas cannot acquire the required input focus, it stays in pass-through and explains why.

Switching between FloatCanvas windows, completing a rename or closing a panel keeps edit mode and the toolbar. Closing a child panel on its own returns to an available parent window. Clicking the canvas closes related temporary child panels and unpinned panels together, then handles that canvas action normally. Pinned panels stay open. Modal dialogs, such as Rename or Delete confirmation, require confirmation or cancellation before drawing can continue.

Text is edited directly on the canvas. Changing windows, layers or tools, or entering pass-through, commits it to the canvas. `Esc` cancels the current text edit.

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
| `Enter` | Open Properties for one or more selected layers; retain text completion while editing text |
| Hold the middle mouse button | Temporarily use Move; release to restore the previous tool |
| `Ctrl+N` | Start a new canvas after archiving current content and checking bound-project saves |
| `Ctrl+Backspace` / `Ctrl+Delete` | Clear the current canvas in one undo step; text fields retain their text-deletion keys |

Shortcut ownership follows focus. Ordinary controls in the canvas, toolbar, Properties, tool options and canvas settings share drawing commands; arrows in Layers and the stamp library navigate their lists. Text fields, dropdowns, sliders and menus retain local typing, undo, clipboard and deletion. Preset items retain Enter/F2/Delete for applying, renaming and deleting presets. `Ctrl+S`/`Ctrl+O`/`Ctrl+N` remain available while typing, committing inline text or a layer rename first. In the stamp library, `Ctrl+N` saves the selection as a stamp, `Ctrl+F` searches, and Enter/F2/Delete act on its selected stamp; Esc first cancels slot selection, then closes the library. Settings, shortcut recording, confirmation dialogs and file dialogs keep their own commands.

Tools, window commands, New, Save and grouping run once per press. Arrows, deletion and undo/redo support holding the key. Alt/Win system combinations are never treated as ordinary drawing shortcuts.

Open **Main menu → Settings…** to change shortcuts or the light/dark theme. A registration conflict detected when saving displays a reason and preserves the previous working shortcut. Windows or another application may intercept a combination before the recorder receives it; choose another combination if the field does not change. The system tray also offers editing, visibility, settings and exit commands.

**Start FloatCanvas when signing in to Windows** is off by default. Enable it and save to start in the tray for the current user without entering edit mode. Clear it and save to turn startup off.

**Prefer GPU acceleration** is enabled by default, including for existing settings. Live blur, pixelation and image blur/pixelation filters trigger the GPU path. When selected, the GPU renders that frame's entire canvas—shapes, text, images and filters—not just one filter. Light canvases, such as ordinary annotations and small animations, use CPU caching to avoid extra readback overhead. Turn the option off to use the CPU for all rendering. Save to apply it without restarting; unavailable or failed GPU rendering falls back to the CPU. Switching leaves canvas content intact.

Settings shows the last frame's actual backend, its selection reason, the available GPU, the last draw time and GPU/CPU frame counts for this run. Idle canvases do not need continuous frames, and viewing the status does not trigger rendering. Detailed failure reasons are available in `diagnostics.log`.

GPU drawing does not mean the entire application runs only on the GPU. Image and animation decoding, desktop-capture readback and final window submission still involve CPU work; the GPU canvas also reads back into a window pixel buffer. Forcing light scenes onto this path may be slower. Performance gains depend on the device and canvas content.

Toggle shortcuts use Ctrl/Alt/Shift combinations. Win and system navigation combinations remain available to Windows. Pressing Win alone no longer immediately leaves editing; use `Win+Space` to switch input methods in controls and filename fields. Opening Start or actually switching to another application still releases input. `Ctrl+Alt+Esc` is reserved for emergency input release. An unresponsive editing UI or interrupted input ownership returns to pass-through; earlier drawing keys are not replayed into the application underneath.

Choose **Open welcome page** from the toolbar menu or system tray to reopen it. A manually opened welcome page stays open when entering drawing mode; opening the startup welcome this way also keeps the existing window. Click its version to check for updates, or its credits to open the GitHub project.

## Settings and the floating logo

Settings are grouped into **General**, **Canvas & history**, **Shortcuts**, and **System**. Each category scrolls independently while Cancel and Save stay at the bottom. Cancel discards draft changes. An invalid history limit reveals its category and an explanation. Windows integration actions run only when their specific buttons are selected.

**Show floating logo** is on by default. Toggle it from the main menu, system tray, or **Settings → General**. In pass-through mode, click or right-click the logo to draw; double-click to show or hide the canvas and update its eyes. The logo does not take focus from your current application.

Drag the logo to move it. Its position is kept during this run. Small pointer movements still count as a click or double-click.

## Check and install updates

Click the version in the lower-left corner of the welcome screen to open **Check for updates**. The panel automatically checks [FloatCanvas GitHub releases](https://github.com/yowynn/FloatCanvas/releases), showing your current version, an available newer version and its release notes. Use **Check again** to refresh. A link to the [project page](https://github.com/yowynn/FloatCanvas) remains at the bottom of the panel.

When an update is available, choose **Update now**. FloatCanvas shows download progress, verifies the package, saves recovery data for your current canvas, exits, replaces the app and restarts automatically to resume your work. Settings, presets and canvas data are retained. Cancel the download or close the panel to keep using your current version. Download or verification failures do not replace the app; if replacement fails, it attempts to restore the previous version and reports the problem.

In-app installation requires the complete official Windows x64 ZIP extracted to a **local folder writable by your Windows account**. Keep all package files together. Read-only folders, network locations and development runs cannot install updates directly; move the complete published app to a suitable folder first. Updating does not install the .NET runtime for you.

**Include preview releases** is off by default. Both stable and preview builds then offer only newer stable releases. Enable it to include newer previews; older versions are never installed. Save this preference directly in the update panel, or through **Settings → Performance & system**. Changing it in the update panel checks again. The switch is disabled while checking or downloading.

When no update is available, the panel still shows the matching current version's published release notes, including for a current preview regardless of this switch. If no published notes exist, it explains that no notes were provided. Network errors and temporary GitHub limits display their status while retaining the project link.

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

Main and context menus use symbols for their actions. **Switch to pass-through** shares the toolbar's **Done** check mark; **Check for updates** uses circular arrows. Stamp pin submenus, shelf actions and canvas reset display choices also have matching icons.

The app icon is on the left, followed by two rows aligned in columns:

- **Upper row:** Select and Eraser, then ten entries: Freehand, Highlighter, Line, Rectangle, Ellipse, Text, Number, Symbol, Emoji and More. More contains only Blur and Pixelate; Text and Number have separate entries.
- **Lower row:** Stamp library and New stamp, then square stamp slots `1`–`0` beneath the ten drawing entries, with a status readout on the right. Empty slots have faint outlines, assigned slots have slightly stronger muted outlines, and the active shortcut stamp uses a blue outline.

The idle Stamp library button has the same gray background as New stamp. Using a stamp chosen from the library shows its thumbnail with a blue outline; switching to another tool or shortcut slot restores the gray background. Clicking the button still opens the library. Regular buttons and panel Close buttons have clearer hover and pressed backgrounds in both light and dark themes.

The upper-right controls are grouped in this order: canvas settings; import, capture and crop; Properties and Layers; undo/redo, blank canvas, opacity, visibility and Done. Separators divide these groups. Crop is enabled only for one selected editable image and still shows its tooltip when disabled. An open eye means the canvas is visible; a closed eye with an amber accent means it is hidden. The mascot icon also closes its eyes while the canvas is hidden; only its eyes change, and the tray and taskbar icons switch with it. The hover tooltip describes the next action. Open Blur and Pixelate through More. The opacity slider occupies two button widths. The status readout follows the light/dark theme, truncates long messages and scrolls on hover. The toolbar starts centered on the primary screen with the crown at the screen's top edge. Window positions, sizes and toolbar expansion are remembered for the current session and reset on restart; custom shortcuts and other preferences remain saved.

- **Click the app icon:** open the main menu. A single click waits for the system double-click interval to avoid conflicting actions; right-click opens the menu immediately.
- **Double-click the app icon:** expand/collapse the toolbar. The collapsed toolbar keeps only the icon.
- **Drag the app icon:** move the toolbar and remember its position.
- **Click a tool icon:** select the tool. Hover for its name and shortcut; the main menu's **Drawing tools** and **Images and effects** sections also offer tool commands.
- **Select the current tool again:** click the same icon or press its shortcut again to open/close its options. Switching tools while options are open updates the panel for the new tool.

The options window uses the current tool name as its title, with a short description and parameters alongside presets. With the toolbar expanded, it initially opens above or below the bar. Drag it elsewhere afterwards; moving or collapsing the toolbar does not move or close it. When the toolbar is collapsed, press the current tool key again to open options near the pointer, within the screen bounds. An unpinned window closes when you click the canvas or another interface; pin it to keep drawing with options visible. Escape closes the color child panel first, then an unpinned tool window. Entering pass-through hides editing panels.

Lines share independent start/end arrowheads for plain, single-arrow and double-arrow combinations, with solid, dashed or dotted strokes. Set a rectangle's corner radius for rounded corners; enable proportional drawing for squares or circles. Symbols include triangles, stars, hearts, checks and crosses with editable colors. The emoji tool provides a gallery: click to insert at its configured Size, or drag to choose the size for that insertion. Size supports precise numeric entry and a larger slider range. Line-pattern and arrowhead choices include diagrams, and stroke width has a thickness preview.

Tool keys: `V` select, `E` eraser, `B` freehand, `H` highlighter, `L` line, `R` rectangle, `O` ellipse, `T` text, `N` numbered marker, `S` symbol and `J` emoji. `A` remains an arrow compatibility shortcut; `M` opens More, main-keyboard `-` opens the stamp library, and `+` (the `=/+` key) saves the selection as a stamp. While editing text, these letters are treated as text input.

Shortcut hints appear at the upper right of upper-row buttons and lower left of lower-row buttons. Turn off **Show tool shortcut hints** in Settings to hide letters, stamp-slot numbers and the `-`/`+` hints together. Shortcuts still work, and the preference is saved across restarts.

Draw lines and arrows by dragging; they have no single-click default length or angle. Rectangles, ellipses and symbols have default width and height: click empty space to create one at that size, centered on the click, or drag to choose its size. Rectangles, ellipses, symbols, numbered markers and emoji use a **six-DIP** drag tolerance, converted using the starting monitor's display scale. Slight movement within that distance counts as a click; reaching it starts drag sizing. The aspect setting creates squares and circles; hold `Shift` to temporarily invert it. Rectangle/ellipse snapshots, image crops and rectangular filter regions follow the same aspect rule. Enter numeric options directly or drag the corresponding slider. Keys used inside text fields, dropdowns, the color spectrum and sliders operate those controls without switching tools or selecting stamps.

All nine official symbol presets use a **100×100** default size for click insertion. Reselecting an official symbol preset used in an earlier version also restores that default size. Personal presets and existing objects keep their dimensions.

For numbered markers and emoji, a click or slight pointer movement still inserts at the configured size, centered on the click. Drag to preview the size and release to create the object. The starting point forms one corner of the box, and you can drag in any of four directions. The whole object scales proportionally, keeping numbered markers circular and emoji at their original artwork ratio. Dragging changes only the new object, leaves the tool's configured size intact and commits one undo step.

### Tool presets and colors

Official and personal presets use matching rounded square preview tiles, five per row, with names in hover hints. Every tool has at least one official preset. All official presets are shown. My presets shows only the current tool, with room for at least four rows and a viewport that expands alongside taller tool options. Additional presets scroll inside this area without making the window taller.

A tool starts with its own default parameters on first use: official defaults use the palette's red (`#E03E3E`) foreground with transparent fill. Each tool remembers its base preset and complete current parameters independently, retaining changes when you switch tools, close options, switch edit/pass-through mode or restart the app. State is saved locally, combining frequent edits over about 500 milliseconds; normal exit and update restarts wait for pending writes. Window positions still reset on startup, and the next number remains part of the canvas document.

Click a tile to apply and select it immediately. A tool always has one selected recipe. Editing parameters keeps that selection and the official previews; only explicitly clicking the same recipe again restores its baseline. An official recipe initially keeps the current tool's colors and stroke width when applied; later clicks restore that selection's baseline. Official baselines and previews also persist across restarts, unaffected by subsequent temporary color changes. Personal recipes store the complete configuration, and temporary parameter changes do not overwrite their definitions. Tool parameter changes do not alter existing objects or saved projects.

Use the plus button to save a new preset, or right-click a personal tile to overwrite, rename or delete it. Saving and renaming use the shared name dialog. Drag with the left mouse button to reorder; dragging and right-clicking do not apply a recipe. Deleting the selected personal preset applies the first official preset.

Dragging personal tool/canvas presets, saved colors, stamps or layers shows a translucent preview that follows the pointer beyond the scrolling area and source window without taking focus or blocking input. Each gap has one blue insertion line with a dot at both ends, including at the first row and edges. Selected layers move together and the preview shows their count. Dropping onto a stamp slot highlights that slot instead. Release to commit, or press `Esc` or switch windows to cancel; dragging does not activate the item.

Choose colors in tool options or Properties. Foreground controls strokes or text; background controls fill for applicable shapes. The primary row has a rounded color-value button and ten quick colors in two rows. Clicking a quick color applies it directly. Small swatches have square corners and subtle outlines; recent and saved colors fit ten per row, with pixel-aligned spacing at the current display scale.

Open the value button for a saturation/value spectrum, hue strip, opacity and the 20 most recent colors. Valid colors immediately update the primary swatch, tool preview or selected objects. Closing the panel records only the final color once in recent colors, without a confirmation button. Swatches and tool previews retain alpha and blend with their underlying surface, without checkerboards. Clicking the parent options window closes only the color panel. Tools based on strokes and fills use color opacity instead of a duplicate tool-opacity control.

My swatches saves named colors. Drag with the left mouse button to reorder, or right-click to overwrite, rename or delete a swatch. Scroll the fixed area to reach additional swatches. Save and rename dialogs keep the originating color panel open underneath. Custom input accepts `#RGB`, `#RRGGBB` or `#AARRGGBB`, such as `#FF2383E2`; `#00000000` is fully transparent. The eyedropper temporarily hides panels except the toolbar and shows a pointed cursor with a live color bubble. Click the canvas to confirm, or use `Esc` or right-click to cancel. Afterwards, the original panels return with the secondary color picker open.

Tool options affect newly created elements. Use Properties to change existing elements.

## Draw, select and adjust

- **Select:** click an element, or drag a selection box from an empty area. Shift-click to add or remove elements. Grouped elements select together; hold `Alt` while clicking to select one member.
- **Move and resize:** drag an element to move it; drag a corner handle on its selection box to resize.
- **Shift movement:** hold `Shift` while dragging the body to constrain movement to canvas X/Y axes through the original drag start. Pressing, releasing or pressing Shift again never resets that origin, and movement does not snap diagonally. With perspective enabled, canvas axes can differ from screen directions.
- **Rotate:** drag the rotation handle above the selection box. Hold `Shift` to snap in 15° steps.
- **Line endpoints:** select one line or arrow, then drag an endpoint.
- **Shift drawing:** in Freehand, Highlighter and freehand lassos, holding `Shift` previews a straight segment. Releasing it fixes the endpoint and resumes free drawing; repeat within one drag for multiple straight segments. Lines, arrows, double arrows, their endpoint adjustments and the next polygon-lasso edge snap to eight horizontal, vertical and 45° directions while `Shift` is held.
- **Text:** choose the text tool and click the canvas for an inline selection frame and caret. `Shift+Enter` adds a line; `Enter` finishes and keeps the object selected; `Esc` cancels this edit. Drag to select text and use normal caret keys, clipboard shortcuts and text undo. Clicking outside the current text frame commits it and starts a new text layer; `Ctrl`-click starts a new layer even inside the frame. Changing layers, tools or windows commits the edit. With the text tool, click existing text to edit it again. Edit numbers through Properties.
- **Numbered markers:** click to add and select the next number at its configured size, or drag to choose its size. The number advances only when placement completes; `Esc` or cancelling a drag does not skip a number. Hold `Ctrl` to create from inside an existing selection. The number label stays on one line and shrinks to fit the badge without changing its size or the configured font size. Deleting a marker does not renumber the others.
- **Erase:** the eraser removes whole elements. One erase gesture can be undone as one operation.

Shapes, text, numbers and emoji select the newly inserted object so you can move, scale or rotate it without switching tools; clicking empty space creates and selects the next object. Freehand, Highlighter and stamps remain unselected for continuous use. Lines and arrows require a drag to create. Hold `Ctrl` to temporarily hide selection handles and create at an overlapping position. Mouse-down determines whether the drag creates or adjusts an object; pressing or releasing `Ctrl` during the drag does not switch that action.

Each layer's aspect constraint is saved with the project. Hold `Shift` while dragging a selection handle to temporarily invert it. Unconstrained rectangles, ellipses and symbols support independent width and height changes while retaining stroke width and corner radii. Text defaults to unconstrained resizing: its font and wrapping adapt to the frame, which is retained during later text edits. Text and numbered markers offer a system-font dropdown with previews.

The Number tool has a starting number and a positive increment. New canvases start at that number. Next number on this canvas is saved independently in the document, can be edited and undone, and is not stored as a global counter in presets or application settings. Font size changes the size of markers inserted with a click; a dragged marker takes its size from that gesture.

Every basic element remains a selectable object, including freehand lines, text, numbered markers and images.

**Properties** and **Layers** are separate windows. Open each from the toolbar or **Main menu → Windows**. Properties refreshes automatically when selection changes. Valid edits to position, size, angle, stroke, fill, opacity and text apply immediately; a short typing burst or leaving a field commits one undoable edit. Multiple selection is labeled accordingly and shows only properties applicable to every selected object. Mixed values appear empty; entering a value applies it to all selected objects.

These tool windows share a floating-panel layout. Drag an empty part of the header to move a panel, or drag an edge or the two diagonal lines in the bottom-right corner to resize it. Close it with the top-right button and reopen it from its original entry point. Panels do not minimize or maximize, including when you double-click the header. Properties prioritizes text content and other settings that are harder to change on the canvas, with position and size last. Only fields applicable to the entire selection appear; the status message stays visible at the bottom.

Double-click a canvas layer, or press `Enter` with one or more layers selected, to open Properties near the pointer. A panel that is already visible keeps its position. After clicking the canvas to dismiss an unpinned panel, a fresh double-click is required to reopen it. Double-clicking during inline text editing still selects text. Pin Properties with its header button to keep it open. An unpinned panel closes when focus leaves; nested color pickers and owned dialogs remain part of its focus stack. Closing the panel resets the pin.

An active pin emphasizes its icon, border and background, using the same two-state feedback as canvas visibility. The canvas settings button also shows a complete active state when perspective is enabled; clicking it still opens settings rather than directly toggling perspective.

Drag the label beside a numeric field, such as X, Y, width, height, angle or stroke width, horizontally to adjust it. `Shift` makes fine adjustments; `Ctrl` speeds them up. Release to commit one operation, or press `Esc` to cancel the drag. X/Y refer to the top-left of the element's bounds on the canvas plane and differ from screen coordinates when projection is enabled. Ordinary objects use width and height along their own axes, including existing scale but excluding the stroke; perspective-placed snapshots use their plane-aligned bounds. Resizing through these fields preserves rotation; with proportional resizing, you can change only width or height. If the geometry has zero size along an axis, adjust its endpoints before changing that dimension. Arrowheads can be filled triangles, open arrows or diamonds; a head size of 0 scales automatically with stroke width.

Elements higher in the layer list appear in front. Click an already selected layer name again, or press `F2`, to rename it. `Enter` or moving focus saves; `Esc` cancels; an empty name restores the default. Names persist in projects, recovery and undo/redo. Drag the handle on the left of a row to reorder it; use the eye and lock icons on the right to change visibility and locking. Right-click for duplicate, delete, bring to front, send to back, group and ungroup. Use the “?” beside the top-right close button for operation and shortcut help. Hidden or locked elements remain selectable in the list to change those states. Select several editable elements and press `Ctrl+G` to group them, or `Ctrl+Shift+G` to ungroup.

Hiding Properties or changing selection commits valid edits already shown on the canvas. Incomplete or invalid numbers are never written to objects; choosing another object loads its current properties automatically.

The canvas opacity slider at the upper right affects annotations and images, while the screenshot background keeps normal brightness. New canvas and visibility controls are beside it. Use the gray page-plus button for New canvas and the red broom beside it for immediate Clear. Hiding keeps your content and the current edit/pass-through mode. New elements cannot be added while hidden.

If a display change leaves annotations wholly off screen, use **Main menu → Canvas → Move annotations into view**. Any content partly visible on a display in the active drawing scope stays in place. Wholly offscreen, visible and unlocked objects move to the nearest usable position; groups retain spacing between editable members. Object sizes and the screenshot crop stay unchanged. Oversized content is made reachable without scaling. Recovery is one undo step, and repeated clicks do not keep shifting content.

Choose **Canvas** on the toolbar to open the canvas panel and set its background color and opacity. Background opacity is independent of the toolbar's annotation opacity; the background is transparent by default. A new canvas starts with an 8-column by 6-row square grid, sized to about 60% of the target display. Both New and Clear retain the current canvas settings. Reset the reference plane in the canvas panel.

**Reset perspective canvas** is a split button. Click its main action to execute the current choice, or use its arrow to choose the default option or an explicit numbered display. Choosing executes immediately and persists the application preference independently of projects and presets. With one active display only the default is shown; with several it uses the primary display. An unavailable choice falls back to the default, including after a drawing-scope change.

Properties appear on the left, with official and personal presets on the right. Save the current canvas settings with the plus button. Newly saved or overwritten personal presets include the background, grid, corner positions and complete perspective calibration; applying one restores its saved corners and perspective state in one undo step. Personal presets support overwrite, rename, delete and drag reordering. Calibration is restored as saved, without automatically scaling or moving it to the target display; recalibrate as needed after a display-layout change.

Official presets disable perspective and reset the default plane geometry. Older personal presets without calibration retain the current corners and recalculate for their row and column counts. Corner coordinates that an older preset never saved cannot be recovered automatically; recalibrate and overwrite that preset to restore the calibration on later use.

When the horizon enters the screen, the grid, canvas color and Surface content stop at a safe boundary. Safe areas outside the reference frame remain usable. Calibration shows the distant boundary as an amber dashed line. Unsafe creation or dragging displays a forbidden cursor; release rejects the creation or restores the pre-drag position. Freehand strokes break across unsafe areas while retaining safe fragments. Billboards validate their ground anchor and scale; their upper parts may extend above the horizon.

Adjustment keeps previewing with unsafe parts temporarily clipped. On release or the fourth placement point, affected layers trigger a choice: cancel this adjustment, apply and move layers back (recommended), or apply and delete them. Relocation prefers an open position inside the reference frame or screen, moving groups together and staggering independent layers while preserving original content and logical size. Hidden and locked layers are included. If no safe, usable destination is found, relocation is disabled; layers are never silently resized or deleted. Adjustment and layer changes share one undo step; recovery snapshots retain the pre-adjustment state until a choice is made.

Use **Place four corners**, beside Adjust, to click four corners in any order. Preview a line, then a triangle, then a quadrilateral with its grid. The fourth click commits one undo step; Esc cancels unfinished placement.

Enable **Enable perspective canvas**, then choose **Adjust perspective canvas**. Adjustment and reset actions hide when perspective is disabled. Corners snap near adjacent horizontal or vertical axes; Ctrl bypasses snapping and Shift constrains movement to an axis through the drag origin. The toolbar uses a rectangle or trapezoid to reflect the current perspective state; its click still opens canvas settings. Drag the central four-way handle or an empty area inside the frame to move it as a whole, including across displays when **All screens** is active. Corners and edges adjust perspective. The horizon may enter the screen. Adjustment is constrained only by concave or crossing corners, degenerate or noninvertible geometry, and numerical stability; reaching these limits turns the frame amber and shows a notice. Ordinary drawing pauses during calibration. A single-pixel reference grid with alternating black and white segments covers the screen, with the calibration frame and handles still visible. **Show grid** dims the reference and overlays your configured grid above it; ending calibration hides the reference and keeps only your enabled grid. Each drag is one undo step. Press `Esc` to cancel the current drag; press it again to end calibration. Losing focus or hiding the canvas cancels an unfinished drag.

Rows appear on the left and columns on the right. Enter values from 1 to 256 or use their sliders, which snap to whole numbers. Changes adjust the plane ratio and grid density while preserving the calibrated target corners. **Reset perspective canvas** restores the initial fitted plane on the selected display. Grid colors use the foreground presets; adjust color, opacity and line width, and choose a solid, dashed or dotted pattern from the illustrated list. The grid covers the plane beyond the calibrated frame and automatically reduces the number of visible lines when dense.

Projection applies to shapes, text, images and animations. Default Surface objects follow the canvas plane; experimental Billboards use the facing and depth rules below. Live blur and pixelation regions follow their placement while sampling amounts remain measured in screen pixels. Perspective scaling uses the equal-depth direction through the nearest corner; parallelograms keep the maximum-stretch direction at 1:1. This is a scale convention; it does not recover a real camera. Concave, crossing, degenerate or noninvertible calibrations are rejected; the horizon may enter the screen. Recalibrate after a game's camera moves. A newly placed screenshot is inverse-projected into its selected screen position, then follows later canvas projection changes. Version 6 projects save canvas and per-layer placement settings; missing fields in older v1/v2/v3/v4/v5 projects use defaults.

**Lock grid size while adjusting** is off by default. Enabling it captures the current grid spacing and origin in canvas coordinates without a visual jump. Four-corner adjustments keep the existing projection algorithm, while grid intersections remain aligned with anchors at the same canvas coordinates. Row and column fields become read-only coverage results that update during dragging, including fractional cells; these values do not feed back into calibration. Unlocking restores the original integer count controls and may rearrange the grid, without moving objects or anchors. Disabling perspective retains the lock; resetting or applying a canvas preset releases it. Version 6 projects and recovery data retain the lock; older projects default to unlocked.

During calibration, an **Origin** label identifies the corner originally designated top-left, in amber when locked. It represents row zero and column zero and follows the same corner identity during dragging and rotation. Grid spacing and origin remain fixed in canvas coordinates; their screen projection still changes, and the grid extends in both directions beyond the origin.

Dense grids at high resolutions can noticeably slow CPU rendering. Automatic line thinning does not guarantee smooth rendering at every setting. If drawing becomes slow, unlock the grid and reduce the row or column count or turn off **Show grid**; calibration guides hide automatically when calibration ends.

## Surface and Billboard placement (experimental)

**Surface** objects follow the canvas projection. **Billboard** objects face the screen, retain their own proportions and rotation, and scale with their ground-anchor depth. Billboard creation is off by default. Its switch is beside canvas adjustment in the toolbar and is also available from the main menu. Leaving it off retains existing creation workflows; the general Shift movement constraint and corrected Surface capture outlines apply independently.

The toolbar and main menu call this action **Placement**. Its tooltip shows only the action name and the current **Default mode** or **Billboard mode**; Default mode uses Surface placement. Enable canvas perspective, then use this button to switch to Billboard mode (experimental). The global switch affects only new objects, does not convert existing layers and is not stored separately in tool presets. Disabling perspective disables the switch and resets new-object placement to Default mode; existing billboard flags, anchors and elevation remain stored and resume projection when perspective returns. **Placement** and **Elevation** appear in Properties with perspective enabled.

### Choose depth, then use the original tool

1. Select a tool that creates layers. Move the pointer to preview an equal-depth line, then click to fix its depth. This stage shows only the line, without a point at the click, and does not begin drawing.
2. Use the original tool gesture: rectangles still drag from one corner to another; lassos, multi-point tools and text retain their completion rules. The fixed line stays visible, with a live outline center and a screen-vertical connection to the line.
3. The intersection becomes the ground anchor. As the outline center moves sideways, the candidate anchor moves along the same line without changing depth. The first click selects depth rather than the final anchor position.
4. A successful creation ends the equal-depth guide. The center, anchor and connection become selection guides and appear or hide with the selection outline. Choose depth again for the next creation.

Snapshots choose depth before the normal rectangle or lasso operation and retain the selected screen pixels. File imports, plain bitmap pastes, external image drops and flattened stamps prepare the image first, then use one canvas click to choose depth and a second to place the image center. Choosing depth creates no empty layer or undo entry. Switching tools, Esc or cancellation clears unfinished depth selection; failed asynchronous work and changed projects cannot receive a late image.

### Depth-priority occlusion (experimental)

Perspective canvases expose **Depth-priority occlusion** in the Layers window, off by default. Billboards are ordered by their ground-anchor depth, with original layer order used only at exactly equal depth. Surface objects retain layer order within their common plane. The layer list does not reorder as anchors move, and Bring to front cannot override different spatial depths.

Canvas color, the ordinary grid and Surface content belong to the common canvas plane. Equal-depth strips are composited between billboards, allowing one Surface object to cover part of a billboard while passing behind another part. Color and grid transparency is retained: opaque canvas color hides billboard portions behind the plane, while grid lines occlude only their actual painted pixels. The desktop and background image remain backdrops; pictured objects have no independent depth. Negative elevation can hide some or all of a billboard, which remains selectable in Layers with its editing handles available.

The choice is saved in projects and recovery records and supports Undo. Disabling perspective hides and suspends it; enabling perspective restores the choice. Switching creation mode or applying a canvas preset retains it. Screen captures and image exports use the same occlusion; plane-content stamps retain plane semantics. Live blur and pixelation sample only the completed scene behind their current depth fragment. Blur support does not read nearer fragments that have not yet been composited, avoiding cyclic sampling between intersecting filters. Depth-strip drawing can increase rendering cost on complex canvases.

Version 6 stores the option and reads versions 1–5. Older projects default to disabled; older applications cannot read the new format.

### Logical dimensions and visible size

All billboards share the canvas depth scale, without a separate first-placement 100% reference per object. Screen drag dimensions are divided by the depth scale to obtain stored logical dimensions. A stroke preset already specifies logical units and is projected directly. At a depth scale of 0.25, a screen rectangle of 100×100 stores logical dimensions of 400×400; a stroke preset of 5 remains 5, appearing as 1.25 throughout preview and after creation.

Moving farther away reduces visible dimensions, stroke width and elevation together while retaining their logical values. Disabling perspective displays logical size: that rectangle becomes 400×400 with stroke 5 at ordinary 100% view scale. It does not return forcibly to its initial screen size.

| Operation | Result |
| --- | --- |
| Drag the body | Move the ground anchor on the canvas plane; depth changes the projection, retaining logical dimensions, elevation and angle |
| Hold Shift during body movement | Constrain the anchor to canvas X/Y axes through the original drag start, independently of screen axes, equal-depth lines and artwork angle |
| Drag the center point | Use only screen-vertical motion to change elevation and move the entire layer up/down, retaining the anchor and logical dimensions |
| Drag the outer rotation handle | Rotate in the screen-facing plane while retaining the anchor and elevation |
| Drag a corner handle | Resize about the layer center while retaining the anchor and elevation; Shift temporarily inverts its aspect preference |
| Click the ground anchor or connection | Display-only references, without a second drag or rebinding action |

Elevation is signed: the center can pass through the anchor and continue down, without clamping at zero. At zero elevation the points coincide, but the center height handle remains usable. A billboard's Properties X/Y locate its ground anchor on the canvas plane. When an equal-depth line cannot intersect a screen-vertical line stably, the app reports that no anchor can be determined at that angle. Choose a valid location or adjust calibration; the app does not silently choose another depth.

### Groups, conversions and files

Editable group stamps, internal object copying, restored history and reopened projects retain every layer's placement, anchor, elevation and artwork, independently of the global switch. Editable stamps do not add a depth-selection step. Flattened image stamps capture the current visible appearance and become one fresh image, losing internal child depths. Rotating a mixed group changes only its Surface members; billboard anchors, elevation and individual angles all remain fixed, so their relative layout can change. Depth never automatically changes layer order.

With perspective enabled, Properties can convert an existing layer's placement. Conversion keeps original content and logical geometry while allowing its visible outline to change: a distorted Surface layer cannot simultaneously regain a front-facing shape and retain every screen corner. Images are not repeatedly resampled when moved or converted.

## Stamp library and shelf

The library window has ten compact slots ordered `1`–`9`, then `0`. Drag an entry onto a slot to pin or replace it, or select a slot and then click an entry to assign it. Click the selected slot again to leave assignment mode. The slot's `×` clears its binding while keeping the stamp in the library. Search has an inset magnifying glass and shares a row with the action buttons; instructions are available from `?` beside Close. The default window width fits complete stamp columns, and all entries scroll in one continuous list. Drag entries to reorder the library; the order is saved. Each entry shows its group/image type and a pin badge with the first assigned slot number.

An unpinned entry has a **Pin** submenu for choosing a slot. A pinned entry instead has **Unpin**, which clears one binding at a time in shelf order (`1`–`9`, then `0`). The same stamp can occupy multiple slots.

Stamps reuse artwork you create. Select one or more elements and choose **New stamp** on the lower toolbar row, or use the save-selection action in the stamp library. Enter a name and choose a save mode. New stamps automatically fill the first empty shortcut slot; if all slots are full, the stamp remains available in the library:

| Save mode | Placed content |
| --- | --- |
| Editable group | Preserve shapes, text, cropped images, animation, styles and transforms; adjust the group together or `Alt`-click a member to edit it |
| Merge as image | Merge the current frame into one static image while retaining editable source objects in the library; choose **Use editable source** to place those original elements again |

Editable groups retain the original GIF/animated WebP data. Animation still plays when the library is reopened and the stamp is placed again. Small toolbar and library thumbnails remain static; ordinary animated stamps play in the larger hover preview. Groups containing live desktop blur or pixelation use their saved static thumbnail for previews, even when they also contain animation; placed objects keep their normal behavior. Choosing a stamp from the library keeps the library button highlighted and shows the selected stamp on it. Clicking that button still opens the library.

Search or scroll through the library, choose **Use** or double-click a thumbnail, then click the canvas to place it. Each click keeps the saved visual size and relative rotations, centered on the clicked position. No size-drawing gesture is needed, and repeated clicks place further copies. Each placement can be undone separately and leaves the copy unselected. Hover a slot or library entry to see its name, shortcut, larger preview and the preview-to-actual-size ratio. The larger editable-stamp preview uses plane coordinates, preserving the relative positions and elevation of Surface and Billboard artwork; the saved small thumbnail retains its original perspective appearance. New stamps fill the first empty shortcut slot automatically.

Select a library entry to rename it, overwrite it with the current canvas selection, or delete it. Editing a placed copy does not automatically change the library original; use overwrite to update that original.

Pin a stamp to a slot labeled `1`–`9` or `0` to select it from the second toolbar row or its number key. The key selects the stamp; click the canvas afterwards to place it. Numbers remain text input while editing text. Unpinning clears only the shortcut; deleting a stamp also clears its pinned slots.

## Place, replace and crop images

Press `Ctrl+V` to paste a clipboard image, or choose **Image** to select one or more files. New images are placed at the center of the currently visible canvas area. PNG, JPEG, BMP, GIF and WebP are supported. Transparency is retained; GIF and animated WebP retain their original data and play on the canvas.

You can also start dragging an image from a file manager or browser, keep holding it, and press the edit/pass-through shortcut (`` Ctrl+` `` by default) to open the canvas. Release the mouse at the desired position. The image is centered on the drop point; when dropping several images, subsequent images are offset slightly. The entire import can be undone in one step.

Dragging or pasting requires actual image data or supported image files. File import, copied/pasted or dropped files, and clipboard sources supplying original GIF/WebP data can retain animation. If the source supplies only PNG or Bitmap data, the result is static and the original animation cannot be recovered. A URL or web link alone is not an image import, and FloatCanvas does not fetch the original image online. If a browser does not provide the original animation, save the file and import it.

Hiding the canvas stops animation refresh; showing it continues playback. Pass-through keeps playback running. Playback does not edit the canvas, add undo steps or trigger recovery saves. Projects, recovery files and internal object copying retain animation data. Snapshots, PNG/JPEG exports and **Merge as image** stamps take one static frame at the time of the operation. Playback controls, playback-position persistence across restarts and animated export are not currently available.

An imported image is a separate object and does not automatically replace the background. Select it to move, rotate, resize or change opacity.

- **Replace source image:** select one image and use **Main menu → Images and effects → Replace selected image…**. Its position, size, crop and effect regions are retained.
- **Crop image:** select only one editable, unlocked image, choose **Crop** after import and capture on the main toolbar, then drag the area to keep. Crop is disabled for no selection, multiple or mixed selections, and locked images; an empty selection does not crop the screenshot background. The original image asset is retained, and cropping can be undone.
- **Blur / Pixelate:** choose a tool from More and drag a region. On the live canvas it continuously processes the desktop and lower FloatCanvas objects, even when an image is selected. Move, resize or rotate the region, and save it in a project. Moving the region processes content at its new position. The corresponding effects on frozen screenshots and images remain static.
- Existing solid effects in older projects remain visible; solid-redaction authoring is no longer available. Image effects transform with their target image.

Live blur and pixelation require Windows 10 2004 (build 19041) or later to reliably exclude FloatCanvas's own windows. The application's minimum remains Windows 10 1607. Earlier systems create static snapshot effects for new desktop regions and show a notice. Existing live regions use a solid placeholder with a warning, retaining their original editable objects. Frozen-screenshot and image effects are unaffected by this requirement.

New live filters prepare a valid screen image before adding an object. Hiding the canvas, switching tools, leaving FloatCanvas or changing the document during preparation cancels insertion. Hiding stops capture; showing resumes from the same canvas's last valid image until a new frame arrives. A different document or display layout does not reuse that cache. Capture failures show a notice and retain a compatible last image when available, otherwise using solid coverage.

Live regions follow new desktop frames, targeting the display refresh rate up to 60 FPS. Actual speed depends on region size, hardware and system scheduling; frame-for-frame synchronization with the underlying application is not guaranteed. Export, image copying and merged stamps take a snapshot for that operation, while the project retains editable live regions. Effects and region transforms can be undone.

**External capture compatibility:** when the system supports session-local window exclusion, live filters exclude FloatCanvas only from its own capture, leaving visible annotations available to other capture tools. The application checks actual interface availability. Unsupported systems use compatibility capture, with a status message that third-party screenshots may omit the canvas. In that mode, use the in-app export for a complete composite, or remove or hide all live regions before capturing. This does not resolve the known Windows Snipping Tool window-detection limitation.

## Capture and place a static snapshot

Choose the toolbar's **Snapshot** tool or **Main menu → Canvas → Snapshot**. Select a region while the desktop is live. Once you finish, FloatCanvas captures that moment and inserts one static image at its original position and size on the current canvas. It stays on the same canvas, and the desktop outside the region remains live.

Select **Snapshot** again to open its options: selection shape, content source and an optional border. The defaults are a rectangle, background only and no border. These options and the current preset are retained with the tool state.

| Selection shape | Operation |
| --- | --- |
| Rectangle / ellipse | Drag and release to finish; `Shift` temporarily inverts the aspect setting |
| Freehand lasso | Trace an outline; hold `Shift` for a straight segment, release `Shift` to resume the curve, then release the mouse to close it |
| Polygon lasso | Click to add vertices and move to preview the next edge; `Shift` snaps to eight directions; click the first vertex, double-click or press `Enter` to finish, and `Backspace` to remove the last vertex |

With perspective enabled and Surface placement selected, rectangles and ellipses are canvas-plane shapes, so their preview follows the projection. Disabling perspective restores the corresponding rectangular or elliptical content outline. Freehand and polygon lassos always trace the pointer in screen space. Billboard snapshots choose an equal-depth line first, then select a screen contour, derive logical dimensions and preserve captured pixels. Optional border presets use logical width and already reflect depth during preview.

Clicking does not create a default-sized snapshot, and selections with no area insert nothing. Self-intersecting lassos use the even-odd fill rule: areas reached by crossing an even number of outline edges remain transparent.

| Content source | Included in the image |
| --- | --- |
| Background only | The underlying desktop and applications, without FloatCanvas annotations or tool windows |
| Background + canvas | The underlying screen composited with currently visible FloatCanvas content |
| Canvas only (transparent) | FloatCanvas content alone, retaining transparency |

All three sources exclude the toolbar, panels, selection handles and selection outline. Pixels outside a non-rectangular selection remain transparent. In canvas-only mode, live filters use their current image while other empty areas stay transparent. A fully transparent result shows a notice instead of inserting an invisible image.

A black-and-white outline previews the selection without freezing the whole desktop. Pressing `Esc` during selection cancels only that selection and leaves editing active. With no operation in progress, `Esc` still dismisses panels or enters pass-through according to the usual rules. Cancellation, empty selections and capture failures leave existing content intact. System-protected areas may be unavailable; the app reports this.

The new image is selected automatically, while the capture tool stays active for further captures. Move, rotate, resize, crop or delete the image, or undo its insertion in one step. Hold `Ctrl` to start a new selection inside the currently selected object instead of adjusting that object. Optional borders become part of the image and transform with it.

When capturing the background, FloatCanvas retains the current editing and input state while excluding its interface from the result. Some interface surfaces may briefly disappear when necessary and return afterwards; capture does not exit and re-enter drawing. Each request captures the current desktop without reusing the previous snapshot. Static content does not require moving the mouse or changing the screen first.

Capture failures leave existing content intact and retain edit mode while FloatCanvas's input session remains healthy. Switching applications, explicitly leaving edit mode, hiding the canvas or switching/changing the current canvas cancels an in-progress capture without inserting a late image or reclaiming focus. Unexpected failures show the diagnostic log location to help investigate the cause.

Original annotations are always retained. **Background + canvas** and **Canvas only** create static copies; transparent copies placed over the originals may appear darker. Captured images do not follow later changes in the underlying app. Saving the project retains the editable image. Copy the selected image to obtain the snapshot, or export the canvas composite through the File menu.

Older projects and history containing a separate frozen capture draft remain readable, editable and exportable. Entering pass-through from an older draft retains it and returns to the live canvas.

## Save, export and recover

Normal startup registers `.floatcanvas` files for the current user. You can also use **Register .floatcanvas file type** in Settings. Double-click a project or choose FloatCanvas in Windows **Open with** to open it. If the app is already running, the project goes to that instance, with the existing save confirmation before replacing work. Existing Windows default-app choices are preserved; choose **Always** in Open with to make FloatCanvas the default. After moving the portable folder, run the app from its new location once to refresh registration. The command line also accepts `FloatCanvas.exe "project.floatcanvas"` or `FloatCanvas.exe --open "project.floatcanvas"`.

| Operation | Result | Can objects still be edited? |
| --- | --- | --- |
| Save/save project as | A `.floatcanvas` file with the current canvas, original images and effects | Yes; reopen it with **Open project** |
| Export composite image | A PNG/JPEG combining the background, annotations and images | The export is an image; the current project remains editable |
| Export transparent annotations | A transparent PNG containing only annotations and images | The export is an image; the current project remains editable |
| Copy selected objects | Editable FloatCanvas objects plus an interoperable image | Paste in FloatCanvas to keep editing; other apps usually receive an image |

New captures are saved as images on the current canvas; older separate capture drafts remain supported. Export does not clear content. Projects retain original images for later editing; use a composite image export when sharing a redacted result.

The current project format is **v6**. Older **v1/v2/v3/v4/v5** projects remain readable; new projects include canvas background, grid, perspective and layer placement, including billboard anchors, logical elevation, depth-priority occlusion and the grid-size lock. Missing placement flags in old files mean Surface. Older apps cannot read v6. Missing depth preferences default to disabled. Keep the original file when exchanging work with an older version.

Committed edits trigger a recovery save after about one second. The App waits for pending recovery writes before exiting. Normal startup still uses a blank canvas. Choose **Main menu → File → Restore workspace history…** to select the latest or an earlier workspace and restore its live canvas and capture draft together. Restoring, cancelling or a failed restore keeps the edit/pass-through mode you started in. If you switch to another application or explicitly leave edit mode during restoration, FloatCanvas does not reclaim focus. Undo history is not retained across restarts.

The history list shows the save time and a canvas thumbnail to distinguish records with the same name. Thumbnails retain FloatCanvas's transparent annotation layer without capturing the desktop, toolbar or selection handles. Previews are generated on demand for older history without one. If a preview cannot be generated, **No preview** is shown and restoration remains available.

Default data paths:

- Settings: `%LOCALAPPDATA%\FloatCanvas\settings.json`
- Latest recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.floatcanvas`
- Previous recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.previous.floatcanvas`
- Workspace history: `%LOCALAPPDATA%\FloatCanvas\recovery\history`
- Tool presets: `%LOCALAPPDATA%\FloatCanvas\tool-presets.json`
- Per-tool current state and official baselines: `%LOCALAPPDATA%\FloatCanvas\tool-state.json`
- Recent colors and My swatches: `%LOCALAPPDATA%\FloatCanvas\colors.json`
- Stamp library: `%LOCALAPPDATA%\FloatCanvas\stamps`
- Runtime diagnostics: `%LOCALAPPDATA%\FloatCanvas\diagnostics.log`
- Unhandled exceptions: `%LOCALAPPDATA%\FloatCanvas\errors.log`

If something appears to fail, note the time and steps, and keep `diagnostics.log`; also keep `errors.log` if an unhandled exception occurred. Enter `%LOCALAPPDATA%\FloatCanvas` in File Explorer's address bar to find them. Logs record capture strategy, readiness and frame acquisition, selection bounds, display information, cancellation/failure reasons, versions and exception stacks. Relevant input includes its category, time and whether this app suppressed it, distinguishing trailing key events from actual external actions. Logs do not save keystroke content, continuous pointer trails, canvas images or annotation text; exception details may contain file paths. Each log keeps the current file and two older files, `.1` and `.2`, each limited to 1 MiB. Custom profiles keep logs in their own directory.

When manually restoring a workspace whose latest recovery is damaged, the App tries that workspace's previous version. A read failure is reported without replacing the original file. Exiting a blank startup canvas without editing does not overwrite the earlier recovery. Recovery helps retrieve recent work; save important work as your own project file as well.

Closing the toolbar or entering pass-through does not exit the App. To quit, use the system tray or **Main menu → Exit FloatCanvas**. Startup with Windows is disabled by default.

## Support and limitations

The minimum system baseline is **Windows 10 1607 (build 14393) x64**, including LTSB 2016, LTSC 2019 and LTSC 2021; Windows 11 x64 is also targeted. Windows 10 22H2 is no longer required to start the app. Features that need newer Windows versions use a fallback or show a notice without blocking basic drawing.

Install a compatible **.NET 10 Windows Desktop Runtime x64** available for your system, and keep Windows and the runtime updated. The app's minimum baseline is separate from Microsoft's support commitments, which also depend on the Windows version, edition and lifecycle; see [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Testing has still mainly used Windows 11. Earlier Windows 10/LTSB/LTSC versions need testing on real systems; source inspection and compatibility-branch tests cannot replace that acceptance work.

New configurations default to all screens, with a primary-screen-only option. Complete cross-screen, mixed-DPI, portrait-display, hot-plug, lock-screen recovery and dual-4K behavior are still being refined. Behavior may vary across devices and applications.

FloatCanvas is intended for ordinary desktop and fullscreen applications without requiring windowed or borderless mode in every case. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

When using window detection in Windows Snipping Tool or another screenshot app, the drawing layer may become the capture target. To keep annotations in the result, use FloatCanvas's own capture or export commands. To capture only the application underneath, use the Hide canvas / Show canvas shortcut (default `` Ctrl+Shift+` ``) to hide the canvas, take the screenshot, then show it again. Pass-through keeps annotations visible. Canvas visibility keeps the current edit/pass-through mode; use the separate mode shortcut to change input behavior.

HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside the current scope.

When a file is bound, a speech bubble above the toolbar shows its name and a separate Unsaved badge for unsaved edits. Hover for the full path. It folds with the toolbar; right-click to detach the file or open its location. Detaching keeps the current content and undo history as an unnamed canvas.

Right-click a filled toolbar stamp slot for exactly four maintenance actions: Rename, Overwrite, Delete and Unpin. Use and editable-source actions remain in the stamp library. Unpin affects only that slot; deleting the stamp removes every binding to it.

### New canvas, clear and history versions

The gray page-plus toolbar button starts a new canvas with the current settings and no file association. New unbound drawings require a second click on the same button. Empty canvases, unchanged history copies and clean saved projects start a new canvas in one click. Edited bound projects immediately offer Save and leave, Keep in history and leave, or Cancel. The red broom beside New immediately clears drawing objects with no confirmation or history entry. It retains the background, grid, calibration and file association, and supports Ctrl+Z. The project file changes only when you save again.

History compares content with the version present when you entered a canvas, then archives on switching, starting a new canvas or exiting. Temporary work is kept automatically; edited projects remain eligible after saving. Opening without edits, blank canvases (including changes only to canvas settings) and duplicate content add no versions. Restoring creates an unnamed copy, retains the original history timestamp and position, and never automatically overwrites the source project. Further edits can create a new version at the top of history. Background recovery also persists empty states separately.

Maximum saved history versions defaults to 60 and accepts integers from 1 to 10000. Saving settings or adding history removes the oldest excess versions. The current canvas and recovery quarantine are excluded. Save a project file for long-term retention.

Stamp tooltips preserve the preview image aspect. Stamps with projected live effects use a captured thumbnail, which can differ from nominal plane-space dimensions; the preview stays undistorted and reports separate horizontal and vertical scales when needed.
