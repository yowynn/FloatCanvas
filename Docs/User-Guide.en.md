# FloatCanvas user guide

[简体中文](User-Guide.md) | English

This guide covers the Windows x64 **0.4.0** source. Published packages are listed in [Releases](https://github.com/yowynn/FloatCanvas/releases), with version changes in the [changelog](../CHANGELOG.md). Earlier versions may have different interfaces and features. Save important work as a project file.

A drag that starts on the canvas stays with the canvas, even when it crosses the toolbar or ends on a button. Buttons are not triggered on release. A gesture that starts on the toolbar stays with the toolbar and does not draw on the canvas.

The interface supports automatic Windows language selection, Simplified Chinese and English. Choose a language in Settings, save and restart to apply it.

FloatCanvas keeps annotations and images visible over the screen. Enter edit mode to draw or adjust them. Switch to pass-through mode to click, drag and scroll in the application underneath while your annotations remain visible.

## Start and switch modes

Version 0.3.0 provides a Windows x64 ZIP; 0.3.1 introduces the EXE installer. Both unsigned formats have their own `.sha256` checksum file. Both use the same application files:

- **Installer:** run `FloatCanvas-<version>-win-x64-Setup.exe` and choose installation for yourself or all users. The initial default is the current user under `%LOCALAPPDATA%\Programs\FloatCanvas`; an all-users installation defaults to Program Files, requires administrator approval and creates entries for all users. You can choose another directory, but every intended user must have read and execute access to an all-users installation; a private account folder may not be suitable. Either scope offers an optional desktop shortcut and leaves login startup off.
- **ZIP:** extract it to a local folder writable by your Windows account, keep all files together, and run `FloatCanvas.exe`.

Copies used by the same Windows account share settings, presets, stamps and history under `%LOCALAPPDATA%\FloatCanvas` by default; an all-users installation keeps different users' data separate. Neither format includes the .NET runtime or debug symbols (PDB files). A compatible **.NET 10 Windows Desktop Runtime x64** must be installed.

**Installer workflow:**

1. Choose Simplified Chinese or English; the initial default follows the Windows display language. The standard wizard, runtime checks and error messages use the selected language.
2. Choose the installation scope and read the welcome page. Setup skips the runtime page when a compatible stable **.NET 10 Windows Desktop Runtime x64** is already present. An x86-only installation, another major version, a preview, the plain .NET Runtime or ASP.NET Core Runtime does not replace it.
3. If the runtime is missing, choose **Download and install .NET**. Setup downloads the correct runtime from Microsoft, verifies its integrity and runs its installer. Complete any Windows administrator prompt; installing the shared runtime can require administrator approval even when FloatCanvas itself is installed only for you. Setup checks the runtime again and continues automatically when it is available. A cancelled or failed download/install can be retried, and manual download and recheck remain available. If Windows needs a restart, save your work and restart yourself; Setup does not restart the computer automatically.
4. Read the license, choose the installation directory and whether to create a desktop shortcut, then confirm installation. The desktop shortcut is initially unchecked; a Start menu entry is created normally.
5. **Launch FloatCanvas** is selected on the completion page. Leave it selected and choose Finish to start the app, or clear it and open the app from the Start menu later.

Silent installation also checks the runtime. A missing runtime returns an error without writing application files or uninstall registration unless `/INSTALLRUNTIME` explicitly permits downloading and installing the shared runtime; Windows may still require administrator approval. `/CURRENTUSER` and `/ALLUSERS` select the installation scope, and `/NORESTART` prevents automatic restarts. Silent installation never starts FloatCanvas automatically.

For the **ZIP edition**, a missing compatible runtime causes the launcher to display a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then run `FloatCanvas.exe` again. You can also select it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). The SDK is not required; the plain .NET Runtime, ASP.NET Core Runtime and .NET Framework do not replace the required desktop runtime.

Choose **Settings → System → Fullscreen overlay mode**, save, and restart:

| Mode | Behavior |
| --- | --- |
| Automatic (default) | Prefer enhancement and fall back on current detection, initialization or recoverable runtime failures. |
| Enhanced | Initialize directly, without capability probes or automatic fallback. Report failures directly. |
| Ordinary | Use desktop windows without loading or checking enhancement. |

Automatic rechecks current interfaces on each launch. Historical exits, unwritable diagnostics and probe timeouts do not establish lack of support; an inconclusive probe is followed by actual initialization. A recoverable runtime input-interface failure in Automatic releases input before rebuilding desktop surfaces with the same canvas, assets, file bindings and undo history. The tray also offers **Switch to ordinary desktop mode**. Ordinary retains drawing and file operations but may not cover some fullscreen applications.

FloatCanvas does not terminate its own process for slow drawing, an unresponsive interface or an input-release failure. Windows and the user handle waiting for or closing an unresponsive application. In automatic enhancement, `Ctrl+Alt+Shift+F12` requests normal input release; a busy interface processes that request when it resumes, without forcibly closing FloatCanvas.

Opening Windows screen clipping or ShareX fullscreen capture/editor surfaces leaves editing and lets the capture interface cover the complete FloatCanvas picture. This prevents the live canvas from continuing to cover a frozen screenshot and its toolbar pixels. FloatCanvas releases editing input during capture and restores the live canvas afterward; click the floating launcher or use the editing shortcut to edit again. Automatic handoff supports these two capture products only; ordinary settings and non-fullscreen editors do not trigger it. For other capture tools that freeze the picture first, wait until their selection interface appears, then use the canvas visibility shortcut to hide the live canvas. Press the same shortcut again after completing or cancelling the capture to restore it. Ordering of FloatCanvas's own visible tools above the canvas is maintained separately.

To bypass enhancement, exit FloatCanvas and run `FloatCanvas.exe --desktop-mode`. This overrides the saved choice and lets you open Settings after forced enhancement fails. The legacy `--retry-fullscreen` option temporarily selects automatic detection. These options cannot be combined and do not change the saved mode or canvas data. Native crashes or hangs cannot be guaranteed to recover in place; unsaved content may be available in recovery history. Interfaces are initialized according to actual availability; validation on one system does not establish compatibility with every Windows version or fullscreen presentation path.

1. Choose **Start drawing** on the welcome screen, or press `` Ctrl+` `` to enter edit mode.
2. Press `` Ctrl+` `` again for pass-through. The toolbar disappears, annotations stay visible, and the application underneath receives input again.

Normal launches show the welcome screen in the middle of the screen and open a blank canvas without automatically showing earlier annotations or captures. Closing it, choosing Start drawing or entering edit mode with the toggle shortcut dismisses it for this session. When saved history is available, the welcome screen shows its latest date and a Continue from history entry. You can also click the app icon on the left of the toolbar and choose **File → Restore workspace history…**, or use the same command in the system tray. Opening history closes Welcome. Double-clicking an entry works like the Restore button. A successful restore shows the canvas and enters drawing mode.

The welcome screen labels the shortcuts **Drawing mode / pass-through mode** and **Hide canvas / Show canvas**, with the version and an author credit containing a red heart below. Hiding keeps your content. Canvas visibility is independent of edit/pass-through mode: entering edit mode does not reveal a hidden canvas, and showing the canvas keeps the current mode. While hidden, adjust the canvas or create new content with only the current draft visible. Confirming reveals the full canvas; cancelling keeps it hidden. Existing hidden layers cannot be selected or moved.

Annotations stay at screen coordinates within the current drawing scope. They do not follow web scrolling or objects in a game.

New configurations default to **All screens**. Choose **Primary screen only** to limit annotations and capture to that screen. Existing saved scope choices are retained. Switching to another application enters pass-through.

Open **Main menu → Settings…** and choose **All screens** or **Primary screen only** under **Drawing scope**; when multiple screens are connected, numbered **Screen 1**, **Screen 2**, etc. appear below a separator. Saving applies the change immediately, without restarting. The startup options `--primary-screen` or `--all-screens` override the scope for that run without changing the saved preference; do not use both together. A scope specified by a startup option remains in effect for that run. Open scope lists refresh when the display layout changes; temporary fallback preserves both saved and pending screen choices. Specific screens are saved by device identity: disconnecting temporarily falls back to primary, and reconnecting restores the chosen screen.

A specific-screen scope uses that screen's center as the canvas reference. **Primary screen only** and **All screens** use the primary-screen center, rather than the center of the combined desktop. Scope changes and opening a project on another screen retain the content's position relative to this reference: a rectangle at screen A's center appears at B's center when switching to B only, and at the primary-screen center when switching from B only to All screens. This does not rewrite object sizes, geometry or undo history. Editor windows use the same reference screen for their initial placement. Physical hot-plug and mixed-DPI behavior still require device acceptance. Use **Main menu → Canvas → Move annotations into view** for objects still entirely outside the active scope.

In edit mode, FloatCanvas receives normal window keyboard and mouse input. An unmapped letter should not be sent to the application underneath. Switching to another application hides editing tools and enters pass-through without removing annotations. If FloatCanvas cannot acquire the required input focus, it stays in pass-through and explains why.

Enhanced editing also isolates ordinary keys and FloatCanvas shortcuts, preventing underlying actions such as Explorer's Peek preview on Space. Text still uses the current input method. Alt+Tab, Windows shortcuts, lock keys and volume/media keys remain available; switching applications leaves edit mode.

Switching between FloatCanvas windows, completing a rename or closing a panel keeps edit mode and the toolbar. Closing a child panel on its own returns to an available parent window. Clicking the canvas closes related temporary child panels and unpinned panels together, then handles that canvas action normally. Pinned panels stay open. Modal dialogs, such as Rename or Delete confirmation, require confirmation or cancellation before drawing can continue.

Open pinned panels return on the next editing session. Manually closing tool options or canvas settings resets their pin; Layers always follows the pinned-window rule. Related child windows stay above their source, while peer panels follow recent focus.

When entering editing from another application, FloatCanvas waits for held keyboard keys and mouse buttons to be released before requesting input and showing the toolbar, avoiding residual pressed states in the application underneath. If the floating logo is enabled, it stays visible while waiting and hides when the toolbar appears.

With enhanced input, if keys are still reported as pressed after two seconds, that entry is cancelled while the current project and undo history are preserved. A tray notification and the floating logo's tooltip list the blocking keys. Press and release those keys, then click the logo or use the editing shortcut to retry. The hint clears after editing starts successfully. FloatCanvas does not synthesize releases to other applications or change the overlay mode for this condition.

Text is edited directly on the canvas. Changing windows, layers or tools, or entering pass-through, commits it to the canvas. `Esc` cancels the current text edit.

| Shortcut | Action |
| --- | --- |
| `` Ctrl+` `` | Switch between edit and pass-through |
| `` Ctrl+Shift+` `` | Hide canvas / Show canvas; keep the current edit/pass-through mode |
| `Tab` | Expand/collapse the toolbar while editing; this command is not triggered from input fields, menus or dialogs |
| `Esc` | Cancel the operation or close the color child panel, then an unpinned panel; finally enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` | Undo/redo; `Ctrl+Shift+Z` also redoes |
| `Ctrl+A` | Select all editable elements |
| `Ctrl+C` / `Ctrl+V` | Copy/paste |
| `Ctrl+D` | Duplicate selected elements with a small offset |
| `I` / `P` | Import images / select the capture tool |
| `Ctrl+PageUp` / `Ctrl+PageDown` | Increase / decrease canvas opacity by 5%; hold to repeat |
| `Ctrl+L` / `Ctrl+,` / `Ctrl+T` | Open Layers / Settings / current tool options; keep an open window open |
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

Holding `Ctrl` hides selection handles temporarily; releasing it restores them immediately without moving the mouse. Copy keeps the original selection; paste and duplicate select the new layers.

Shortcut ownership follows focus. Ordinary controls in the canvas, toolbar, Properties, tool options and canvas settings share drawing commands; arrows in Layers and the stamp library navigate their lists. Text fields, dropdowns, sliders and menus retain local typing, undo, clipboard and deletion. Preset items retain Enter/F2/Delete for applying, renaming and deleting presets. `Ctrl+S`/`Ctrl+O`/`Ctrl+N` remain available while typing, committing inline text or a layer rename first. In the stamp library, `Ctrl+N` saves the selection as a stamp, `Ctrl+F` searches, and Enter/F2/Delete act on its selected stamp; Esc first cancels slot selection, then closes the library. Settings, shortcut recording, confirmation dialogs and file dialogs keep their own commands.

Tools, window commands, New, Save and grouping run once per press. Arrows, deletion and undo/redo support holding the key. Alt/Win system combinations are never treated as ordinary drawing shortcuts.

Open **Main menu → Settings…** to change shortcuts or the light/dark theme. A registration conflict detected when saving displays a reason and preserves the previous working shortcut. Windows or another application may intercept a combination before the recorder receives it; choose another combination if the field does not change. The system tray also offers editing, visibility, settings and exit commands.

**Start FloatCanvas when signing in to Windows** is off by default. Enable it and save to start in the tray for the current user without entering edit mode. Clear it and save to turn startup off.

Canvas content, perspective grids and effect previews render on a dedicated worker. Selection frames, perspective handles and controls respond on the UI thread. Busy previews skip intermediate states and capture the latest value after each frame, retaining the final value after release. Complex previews may update later while their handles follow the current input.

Simple and complex canvases share this background mechanism. Completed content is retained, and updates transfer changed pixel blocks and redraw affected areas. Moving or clearing a selection frame alone does not redraw the canvas content. Animation timing and stamp hover previews are also prepared in the background.

**Prefer GPU acceleration** is enabled by default, including for existing settings. Live blur, pixelation and image blur/pixelation filters trigger the worker GPU path. When selected, the GPU renders the frame's shapes, text, images and filters. Light canvases, such as ordinary annotations and small animations, use CPU caching to avoid extra readback overhead. The UI combines completed preview pixels with current guides. Turn the option off to use the CPU for all rendering. Save to apply it without restarting; unavailable or failed GPU rendering falls back to the CPU. Switching leaves canvas content intact.

Settings shows the last frame's actual backend, its selection reason, the available GPU, the last draw time and GPU/CPU frame counts for this run. Idle canvases do not need continuous frames, and viewing the status does not trigger rendering. Detailed failure reasons are available in `diagnostics.log`.

GPU drawing does not mean the entire application runs only on the GPU. Image and animation decoding, desktop-capture readback and final window submission still involve CPU work; the GPU canvas also reads back into a window pixel buffer. Forcing light scenes onto this path may be slower. Performance gains depend on the device and canvas content.

Toggle shortcuts use Ctrl/Alt/Shift combinations. Win and system navigation combinations remain available to Windows. Pressing Win alone no longer immediately leaves editing; use `Win+Space` to switch input methods in controls and filename fields. Opening Start or actually switching to another application still releases input. `Ctrl+Alt+Esc` is reserved for emergency input release. An unresponsive editing UI or interrupted input ownership returns to pass-through; earlier drawing keys are not replayed into the application underneath.

Choose **Help → Open welcome page** from the main menu or use the system tray to reopen it. From editing, this first leaves edit mode and then brings Welcome to the foreground. Entering edit mode closes Welcome. Click its version to check for updates, or its credits to open the GitHub project.

## Settings and the floating logo

Settings are grouped into **General**, **Canvas & history**, **Shortcuts**, and **System**. Each category scrolls independently while Cancel and Save stay at the bottom. Cancel discards draft changes. An invalid history limit reveals its category and an explanation. Windows integration actions run only when their specific buttons are selected.

**Show floating logo** is on by default. Toggle it from the main menu, system tray, or **Settings → General**. In pass-through mode, a single click waits for double-click detection before entering drawing mode; right-click enters immediately. Neither opens the main menu. Double-click has the same behavior in both modes: show or hide the canvas and update the eyes, preserving the current mode and toolbar expansion. Showing or dragging the logo does not take focus from your current application.

The pass-through logo is 75% of the editing logo's size and carries a **Pass-through** indicator, so the modes remain distinguishable when the editing toolbar is collapsed. The indicator describes the current state and is not a button; open/closed eyes only indicate whether the canvas is visible/hidden.

Drag the logo to move it. Its position is kept during this run. Small pointer movements still count as a click or double-click.

In edit mode, pressing the right mouse button on the avatar opens the main menu immediately. When the menu is open, a left press closes it while remaining eligible for a double-click to toggle canvas visibility or a drag to move the toolbar. A single menu-dismissal click cannot reopen the menu later.

## Check and install updates

Click the version in the lower-left corner of the welcome screen to open **Check for updates**. The panel automatically checks [FloatCanvas GitHub releases](https://github.com/yowynn/FloatCanvas/releases), showing your current version, an available newer version and its release notes. Use **Check again** to refresh. A link to the [project page](https://github.com/yowynn/FloatCanvas) remains at the bottom of the panel.

For a current-user installation or ZIP copy, choose **Update now** when an update is available. FloatCanvas shows download progress, verifies the package, saves recovery data for your current canvas, exits, replaces the app and restarts automatically to resume your work. Settings, presets and canvas data are retained. Cancel the download or close the panel to keep using your current version. Download or verification failures do not replace the app; if replacement fails, it attempts to restore the previous version and reports the problem.

Current-user installations and ZIP copies need a **local folder writable by your Windows account**. They download the official ZIP; installed copies also refresh the Windows uninstall version and current file inventory, so files added by updates can still be uninstalled. ZIP updates preserve the existing uninstaller itself; changes to Setup or its uninstall interface require running the newer Setup. Keep every package file together. ZIP updates do not install the .NET runtime.

Launch the app and create shortcuts with `FloatCanvas.exe`; `FloatCanvas.Host.exe` is an internal host. Keeping an older version number in the ZIP folder name does not affect updates. If the panel names a missing or empty file, restore it from the complete official package of the **same version**, or extract a complete package into a new folder, then choose **Check again**. A startup update-failure notice identifies the version previously attempted and may refer to an earlier update. Keep its backups until you have confirmed that the app and your data work correctly.

**All-users installations** offer **Download and open Setup**. FloatCanvas downloads and verifies that version's installer, saves the current canvas, and lets Setup request administrator approval. The app exits only after the wizard confirms initialization; cancelling elevation or failing initialization leaves FloatCanvas running. Follow the wizard to update the existing machine installation. Keeping **Launch FloatCanvas** checked resumes work as the original user. The normal app and ZIP update worker are not elevated. If a newer release has only a ZIP, the panel explains the limitation and links to that release. A portable ZIP in a read-only, network or development directory still needs a complete published package in a writable local folder.

**Include preview releases** is off by default. Both stable and preview builds then offer only newer stable releases. Enable it to include newer previews; older versions are never installed. Save this preference directly in the update panel, or through **Settings → System**. Changing it in the update panel checks again. The switch is disabled while checking or downloading.

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

The mascot wears her crown while editing and holds it in both hands, without a pen or text badge, during pass-through. The new pair retains the original framing and other contours, changing only the eyes, hands and crown position and removing the pen. Open eyes mean the canvas is visible; closed eyes mean it is hidden. Only the eyes change within each pair. The tray also reflects mode and visibility.

A tray left click enters editing after the double-click interval. A double-click toggles canvas visibility. Right-click retains the tray menu, with shortcut hints on its right side.

The new-canvas confirmation bubble appears above its button, with a small pointer toward the same confirmation target. It stays within the mascot's crown height.

Menu categories, Welcome, Settings and the floating logo each have distinct icons. Matching tray commands use the same artwork.

More tools shows the active Blur or Mosaic icon. Selecting another tool restores its three-dot icon.

You can clear a hidden canvas. Showing it refreshes the retained display first, preventing old content from flashing back.

Invalid tool-option and layer-property entries retain the current effective value. Correcting the entry, leaving the input area or reopening the panel clears stale feedback and restores the current value. Busy operations retain unapplied property drafts so they can be applied afterwards. Scrolling content, buttons and sliders have no additional dashed keyboard focus outline; input-field editing borders and tool/preset selection states remain visible.

Main and context menus use symbols for their actions. **Switch to pass-through** shares the toolbar's **Done** check mark; **Check for updates** uses circular arrows. Stamp pin submenus and shelf actions also have matching icons.

The app icon is on the left, followed by two rows aligned in columns:

- **Upper row:** Select and Eraser, then ten entries: Freehand, Highlighter, Line, Rectangle, Circle, Text, Number, Symbol, Emoji and More. More contains only Blur and Pixelate; Text and Number have separate entries.
- **Lower row:** Stamp library and New stamp, then square stamp slots `1`–`0` beneath the ten drawing entries, with a status readout on the right. Empty slots have faint outlines, assigned slots have slightly stronger muted outlines, and the active shortcut stamp uses a blue outline.

The idle Stamp library button has the same gray background as New stamp. Using a stamp chosen from the library shows its thumbnail with a blue outline; switching to another tool or shortcut slot restores the gray background. Clicking the button still opens the library. Regular buttons and panel Close buttons have clearer hover and pressed backgrounds in both light and dark themes.

The upper-right controls are grouped in this order: the Canvas tool; import, capture and crop; Properties and Layers; undo/redo, blank canvas, opacity, visibility and Done. Separators divide these groups. Crop is enabled only for one selected editable image and still shows its tooltip when disabled. An open eye means the canvas is visible; a closed eye with an amber accent means it is hidden. The mascot icon also closes its eyes while the canvas is hidden; only its eyes change, and the tray and taskbar icons switch with it. The hover tooltip describes the next action. Open Blur and Pixelate through More. The opacity slider occupies two button widths. The status readout follows the light/dark theme, truncates long messages and scrolls on hover. The toolbar starts centered on the primary screen with the crown at the screen's top edge. Window positions, sizes and toolbar expansion are remembered for the current session and reset on restart; custom shortcuts and other preferences remain saved.

- **Click the app icon:** open the main menu. A single click waits for the system double-click interval to avoid conflicting actions; right-click opens the menu immediately.
- **Double-click the app icon:** show/hide the canvas and update its eyes; preserve editing mode and toolbar expansion.
- **Press `Tab` or choose the main menu's first item:** expand/collapse the toolbar. The item shows **Collapse toolbar** or **Expand toolbar** for the current state, with a `Tab` hint. The collapsed toolbar keeps only the icon; right-click it to reopen the menu and expand. `Tab` works during ordinary editing, does not repeat while held and does not move focus between buttons. Text editing, input fields, menus and dialogs do not trigger toolbar folding. Windows system file dialogs retain their native `Tab` navigation.
- **Drag the app icon:** move the toolbar and remember its position.
- **Click a tool icon:** select the tool. Hover for its name and shortcut; the main menu's **Drawing tools** and **Images and effects** sections also offer tool commands.
- **Select the current tool again:** click the same icon or press its shortcut again to open/close its options. Switching tools while options are open updates the panel for the new tool.
- **Press `Ctrl+T`:** open the current tool's options (Canvas opens canvas settings). An open window stays open; tools without an options window do nothing.

Dragging constrains the toolbar to the current drawing displays in real time. The expanded bar keeps both its icon and the full toolbar inside the actual display area; the collapsed bar keeps the whole icon inside. Primary-display or individual-display scope restricts it to that display. All screens allows it to cross continuous seams between adjacent displays, while excluding gaps between them. Expanding near an edge adjusts the position so the toolbar's right or bottom edge stays on screen. If the selected display area is smaller than the complete toolbar, the toolbar retains its size and keeps the icon accessible.

The options window uses the current tool name as its title, with a short description and parameters alongside presets. With the toolbar expanded, it initially opens above or below the bar. Drag it elsewhere afterwards; moving or collapsing the toolbar does not move or close it. When the toolbar is collapsed, press the current tool key again to open options near the pointer, within the screen bounds. An unpinned window closes when you click the canvas or another interface; pin it to keep drawing with options visible. Escape closes the color child panel first, then an unpinned tool window. Entering pass-through hides editing panels.

Lines share independent start/end arrowheads for plain, single-arrow and double-arrow combinations, with solid, dashed or dotted strokes. Set a rectangle's corner radius for rounded corners; lock the configured ratio, or choose a 1:1 square or circle preset. Symbols include triangles, stars, hearts, checks and crosses with editable colors. The emoji tool provides a gallery: click to insert at its configured Size, or drag to choose the size for that insertion. Size supports precise numeric entry and a larger slider range. Line-pattern and arrowhead choices include diagrams, and stroke width has a thickness preview. Closed shapes keep the full stroke width inside their outer contour. Fill and stroke occupy separate regions, so translucent colors do not stack there. Dashes cut gaps from the complete solid stroke; dotted strokes retain complete circles.

Tool keys: `V` select, `E` eraser, `B` freehand, `H` highlighter, `L` line, `R` rectangle, `O` circle, `T` text, `N` numbered marker, `S` symbol, `J` emoji and `C` Canvas. `A` has no tool binding; `Ctrl+A` only selects all and keeps the current tool. `M` opens More, main-keyboard `-` opens the stamp library, and `+` (the `=/+` key) saves the selection as a stamp. While editing text, these letters are treated as text input.

Shortcut hints share the upper-right frame corner in the upper row and the lower-left frame corner in the lower row. Canvas `C`, image import `I` and capture `P` use the same layout as the other tools. Adjacent icons share a consistent size and line weight. Turn off **Show tool shortcut hints** in Settings to hide letters, stamp-slot numbers and the `-`/`+` hints together. Shortcuts still work, and the preference is saved across restarts.

Draw lines and arrows by dragging; they have no single-click default length or angle. Rectangles, ellipses and symbols have default width and height: click empty space to create one at that size, centered on the click, or drag to choose its size. Rectangles, ellipses, symbols, numbered markers and emoji use a **six-DIP** drag tolerance, converted using the starting monitor's display scale. Slight movement within that distance counts as a click; reaching it starts drag sizing. The aspect chain preserves the configured ratio; hold `Shift` to temporarily invert the constraint. Image crops and rectangular filter regions use a 1:1 constraint. Rectangle/Circle captures use a free aspect ratio by default and constrain it to 1:1 only while `Shift` is held; capture options have no persistent aspect switch. Enter numeric options directly or drag the corresponding slider. Keys used inside text fields, dropdowns, the color spectrum and sliders operate those controls without switching tools or selecting stamps.

The symbol tool offers five official styles: Standard outline, Tinted fill, Solid fill, Dashed outline and Green emphasis. Each uses a triangle sample with a **72×72** default size. After selecting a style, click any shape in the shape picker to retain its colors, line pattern and size on that geometry. Personal presets and existing objects keep their shapes and dimensions.

For numbered markers and emoji, a click or slight pointer movement still inserts at the configured size, centered on the click. Drag to preview the size and release to create the object. The starting point forms one corner of the box, and you can drag in any of four directions. The whole object scales proportionally by default, keeping numbered markers circular and emoji at their original artwork ratio. Hold `Shift` while dragging to adjust width and height independently; releasing it immediately restores the proportional preview. Dragging changes only the new object, leaves the tool's configured size and the new layer's default aspect preference intact and commits one undo step.

Official text uses size 36; symbols, numbered markers and emoji use 72; rectangles and ellipses have a 108-pixel side. Number and emoji Size controls their geometric frame; label text fits automatically. Their tool options omit aspect locking; new instances lock aspect in layer properties by default. Unlock it there to stretch artwork independently in both directions. Numbered markers expose Shape color from the foreground palette and Text color from the background palette. Shapes offer Separate and Unified color styles with shape-specific previews. Unified combines outline and fill into one foreground Color control; returning to Separate restores the saved fill. The style is saved with presets, layers and recovery data.

### Tool presets and colors

Number badges offer filled, single-color outline and two-color outline styles. Both outline styles have a transparent interior and adjustable border width. Single-color outline uses the shape color for its border and label; two-color outline edits them separately. Switching styles preserves hidden colors. Tool options affect new badges; Properties can change several selected badges together. Projects and personal presets retain these choices. Existing badges keep their original colors and font-sizing rules.

Official and personal presets use matching rounded square preview tiles, five per row, with names in hover hints. Every tool has at least one official preset. All official presets are shown. My presets shows only the current tool, with room for at least four rows and a viewport that expands alongside taller tool options. Additional presets scroll inside this area without making the window taller.

A tool starts with its own default parameters on first use: official defaults use the palette's red (`#E03E3E`) foreground with transparent fill, except numbered markers, which use white label text. Each tool remembers its base preset and complete current parameters independently, retaining changes when you switch tools, close options, switch edit/pass-through mode or restart the app. State is saved locally, combining frequent edits over about 500 milliseconds; normal exit and update restarts wait for pending writes. Window positions still reset on startup, and the next number remains part of the canvas document.

Click a tile to apply and select it immediately. A tool always has one selected preset. Official and personal presets contain complete tool parameters: every application restores the preset's defined colors, stroke width, line pattern and other parameters. Official previews always show the fixed official definition; official baselines saved on first use in older versions no longer affect application or previews. If an older personal preset lacks a parameter added later, applying it resets that parameter to its default instead of inheriting the current value.

Editing parameters retains the selection; clicking the preset again restores its complete parameters. Temporary edits remain in that tool's current working state when you switch tools or restart. Only overwriting a personal preset changes its definition. Tool parameter changes do not alter existing objects or saved projects.

Use the plus button to save a new preset, or right-click a personal tile to overwrite, rename or delete it. Saving and renaming use the shared name dialog. Drag with the left mouse button to reorder; dragging and right-clicking do not apply a recipe. Deleting the linked personal preset falls back to the first official selection without changing any working tool parameters. The modified marker remains when those values differ from the official preset. Only explicitly selecting a preset applies its parameters.

Dragging personal tool/canvas presets, saved colors, stamps or layers shows a translucent preview that follows the pointer beyond the scrolling area and source window without taking focus or blocking input. Each gap has one blue insertion line with a dot at both ends, including at the first row and edges. Selected layers move together and the preview shows their count. Dropping onto a stamp slot highlights that slot instead. Release to commit, or press `Esc` or switch windows to cancel; dragging does not activate the item.

Choose colors in tool options or Properties. Foreground controls strokes or text; background controls fill for applicable shapes. The primary row has a rounded color-value button and ten quick colors in two rows. Clicking a quick color applies it directly. Small swatches have square corners and subtle outlines; recent and saved colors fit ten per row, with pixel-aligned spacing at the current display scale.

The color panel is an owned window with a draggable caption and Close button. It stays above its source and closes on focus loss. Its caption has no Pin button. Adjust colors on the left; recent colors and My swatches appear on the right, ten per row. Swatch naming dialogs belong to the color window and temporarily disable the windows underneath until confirmed or cancelled.

Open the value button for a saturation/value spectrum, hue strip, opacity and the 20 most recent colors. The spectrum provides more vertical room for picking colors. The hue strip uses a rainbow track and a slim lever. Press either to drag continuously; pressing the lever preserves its value. Drag the Foreground or Background title area to move the color panel. Valid colors immediately update the primary swatch, tool preview or selected objects. Closing the panel records only the final color once in recent colors, without a confirmation button. Swatches and tool previews retain alpha and blend with their underlying surface, without checkerboards. Clicking the parent options window closes only the color panel and lets that click act on the parent control. Tools based on strokes and fills use color opacity instead of a duplicate tool-opacity control.

My swatches saves named colors. Drag with the left mouse button to reorder, or right-click to overwrite, rename or delete a swatch. Scroll the fixed area to reach additional swatches. Save and rename dialogs keep the originating color panel open underneath. Custom input accepts `#RGB`, `#RRGGBB` or `#AARRGGBB`, such as `#FF2383E2`; `#00000000` is fully transparent. The eyedropper temporarily hides panels except the toolbar and shows a pointed cursor with a live color bubble. Click the canvas to confirm, or use `Esc` or right-click to cancel. Afterwards, the original panels return with the secondary color picker open.

Right-click the primary color field or the preview swatch left of the secondary panel's hex input to paste a color or choose a Copy format: `#RRGGBB`, `#AARRGGBB`, `rgb(R, G, B)`, `rgba(R, G, B, A)`, `hsl(H, S%, L%)`, or `hsla(H, S%, L%, A)`. RGB channels use integers from 0 to 255; HSL hue uses 0 to 360, with percentages for saturation and lightness; function Alpha uses 0 to 1. All six copied formats can be pasted unchanged. Formats without Alpha preserve the current opacity. Eight-digit hex always puts Alpha first. Paste is disabled for invalid clipboard text. Left-click the secondary preview to copy `#AARRGGBB` directly. Successful copies from either control briefly show Copied beside the swatch without waiting for hover. The primary color field's menu also has a separate Set to transparent action, setting `#00000000`. Opening the color menu or copying keeps the source Tool options, Properties or color panel open; copy receipts appear above that source.

Tool options affect newly created elements. Use Properties to change existing elements.

## Draw, select and adjust

- **Select:** click or marquee visible content; empty text space, transparent image regions and hollow shape interiors do not select. Shift-click to add or remove elements. A group selects as one object at the current editing level; enter it to select its child layers or groups, or hold `Alt` while clicking to select one member. The Stamp tool shows existing selection frames while newly placed stamps remain unselected.
- **Move and resize:** all tools except Eraser and Canvas show existing selection frames. A visible frame takes priority for left-button interaction, including with Blur, Pixelate, Crop, Capture and Text. Drag its body or border to move, a corner to resize, or the rotation handle to rotate. Hold `Ctrl` to hide and bypass the frame and use the current tool's own action.
- **Shift movement:** hold `Shift` while dragging the body to constrain movement to canvas X/Y axes through the original drag start. Pressing, releasing or pressing Shift again never resets that origin, and movement does not snap diagonally. With perspective enabled, canvas axes can differ from screen directions.
- **Rotate:** drag the rotation handle above the selection box. Hold `Shift` to snap in 15° steps. An ordinary single layer's frame rotates with it; perspective-placed snapshots use a canvas-plane bounding rectangle, and multiple layers use their combined bounding box.
- **Line endpoints:** select one line or arrow, then drag an endpoint.
- **Shift drawing:** in Freehand, Highlighter and freehand lassos, holding `Shift` previews a straight segment. Releasing it fixes the endpoint and resumes free drawing; repeat within one drag for multiple straight segments. Lines, arrows, double arrows, their endpoint adjustments and the next polygon-lasso edge snap to eight horizontal, vertical and 45° directions while `Shift` is held.
- **Text:** choose the text tool and click empty canvas space for an inline selection frame and caret. `Shift+Enter` adds a line; `Enter` finishes and keeps the object selected; `Esc` cancels this edit. Drag to select text and use normal caret keys, clipboard shortcuts and text undo. Clicking outside the current text frame commits it and starts a new text layer; `Ctrl`-click starts a new layer even inside the frame. Changing layers, tools or windows commits the edit. With the Text tool, double-click the glyphs of selected text to edit it again. Edit numbers through Properties.

Outside active text input, drag the selected text frame's body or border to move it, a corner to resize, or the rotation handle to rotate. Double-click text to enter editing: its interior then positions the caret or selects text, while the border remains available for transforms. Changing frame dimensions reflows the text and fits its font to the new frame, without retaining space from former automatic line breaks. Deliberate line breaks and empty paragraphs remain.
- **Numbered markers:** click to add and select the next number at its configured size, or drag to choose its size. The number advances only when placement completes; `Esc` or cancelling a drag does not skip a number. Hold `Ctrl` to create from inside an existing selection. The label stays on one line and shrinks to fit without changing the marker frame or the tool's Size setting. Deleting a marker does not renumber the others.
- **Erase:** the eraser removes whole elements. One erase gesture can be undone as one operation.

Closed shapes, text, numbers and emoji select the newly inserted object so you can move, scale or rotate it without switching tools; clicking empty space creates and selects the next object. Freehand, Highlighter and stamps remain unselected for continuous use. Lines, single arrows and double arrows require a drag to create and do not select themselves or show a new-stroke frame during or after drawing. Select an existing line manually to move it, adjust its endpoints or rotate it. Hold `Ctrl` to temporarily hide and bypass an existing selection frame and create at an overlapping position. Mouse-down determines whether the drag creates or adjusts an object; pressing or releasing `Ctrl` during the drag does not switch that action.

Each layer's aspect constraint is saved with the project. Hold `Shift` while dragging a selection handle to temporarily invert it. Unconstrained rectangles, ellipses and symbols support independent width and height changes while retaining stroke width and corner radii. Text defaults to unconstrained resizing: its font and wrapping adapt to the frame, which is retained during later text edits. Text and numbered markers offer a system-font dropdown with previews.

The Number tool has a starting number and a step. New canvases start at that number. When applying a number preset or changing the start, Next number follows the new start if it equals the old start; otherwise it stays unchanged. This equality rule applies even with existing layers or a zero step and never rewrites existing marker values. A zero step repeats the same number; a negative step counts backward. The step slider runs from -100 to 100 with zero centered. Start, Next number and layer-number sliders still begin at 1, while direct entry and icon scrubbing allow zero and negative values. Counters stay at their integer limit rather than wrapping. Next number on this canvas is saved independently in the document, can be edited and undone, and is not stored as a global counter in presets or application settings. Size changes the geometric frame of markers inserted with a click; a dragged marker takes its size from that gesture.

Every basic element remains a selectable object, including freehand lines, text, numbered markers and images.

**Properties** and **Layers** are separate windows. Open each from the toolbar or **Main menu → Windows**. Properties refreshes automatically when selection changes. Valid edits to position, size, angle, stroke, fill, opacity and text apply immediately; a short typing burst or leaving a field commits one undoable edit. Multiple selection is labeled accordingly and shows only properties applicable to every selected object. Mixed values appear empty; entering a value applies it to all selected objects.

Type-specific properties remain editable after creation: blur and pixelation strength, redaction color, symbol shape, and font size for numbers and emoji. For an image with multiple effects, choose the target from the Effects list before editing it; other effects and the original effect region remain unchanged.

These tool windows share a floating-panel layout. Drag an empty part of the header to move a panel, or drag an edge or the two diagonal lines in the bottom-right corner to resize it. Close it with the top-right button and reopen it from its original entry point. Panels do not minimize or maximize, including when you double-click the header. Properties prioritizes text content and other settings that are harder to change on the canvas, with position and size last. Only fields applicable to the entire selection appear; the status message stays visible at the bottom.

Double-click a canvas layer, or press `Enter` with one or more layers selected, to open Properties near the pointer. A panel that is already visible keeps its position. After clicking the canvas to dismiss an unpinned panel, a fresh double-click is required to reopen it. Double-clicking during inline text editing still selects text. Pin Properties with its header button to keep it open. An unpinned panel closes when focus leaves; nested color pickers and owned dialogs remain part of its focus stack. Closing the panel resets the pin.

An active pin emphasizes its icon, border and background, using the same two-state feedback as canvas visibility. The Canvas button's blue border marks the selected Canvas tool; its rectangle/trapezoid icon indicates perspective off/on. Perspective retains an amber fill and glyph, with an amber idle border that becomes blue when selected. Change perspective in its options panel.

Drag the X, Y, W, H, angle or stroke icon inside a numeric field horizontally to adjust it. External labels are descriptive and do not respond to dragging. `Shift` makes fine adjustments; `Ctrl` speeds them up. Release to commit one operation, or press `Esc` to cancel the drag. For one ordinary layer, X/Y refer to the top-left of its bounds in its parent group's local coordinates; without a parent, these are canvas-plane coordinates and differ from screen coordinates when projection is enabled. Image coordinates describe the body, so changing its outside border does not shift the displayed position. Ordinary objects use width and height along their own axes, including existing local scale but excluding the stroke; perspective-placed snapshots use their local plane-aligned body bounds without the border. Resizing through these fields preserves rotation; with proportional resizing, you can change only width or height. If the geometry has zero size along an axis, adjust its endpoints before changing that dimension. Arrowheads can be filled triangles, open arrows or diamonds; a head size of 0 scales automatically with stroke width.

Browse Layers like a file manager: the list shows only the current level's direct members. At Main canvas, it lists its direct layers and groups; inside a group, it lists only that group's layers and child groups. Each layer has a static type icon, with the six-dot handle vertically centered beside its name. Within one level, ordinary layers or groups higher in the list appear in front, and an ordinary group's contents occupy one consecutive block in the drawing order. Select several editable layers or groups and press `Ctrl+G` to group them, or `Ctrl+Shift+G` to ungroup. A new group collects the selection at its former frontmost position, preserving internal relative order; this can change overlaps with unselected layers.

Click a group's entry arrow, double-click its icon or non-text area, or select it and press `Enter` to switch to its list and edit its contents. Canvas selection then targets that group's child nodes, and newly drawn content belongs to that group. Breadcrumbs show the full path, such as **Main canvas > Group 1 > Group 2**; click any ancestor to return directly. The **Parent level** button, `Left` or `Backspace` goes up one level. `Esc` leaves one level before closing the window once you are back at the root. **Edit group contents** in Properties also enters the selected group.

Members retain local positions and transforms relative to their parent group. Moving, rotating or resizing the group carries all descendants without editing each member separately. For one selected member, Properties displays position and size in its parent group's local coordinates for precise local adjustments. For a group or multiple selected nodes, position and size describe the combined bounds on the canvas plane. Canvas dragging works with screen and canvas positions, then converts back to the member's local placement. Group and multiple-selection Properties offer **Together** and **Individually** under **Transform pivots**. Together rotates or scales around the common center; Individually uses each selected node's center. Every selected group remains one object: enter it and select its members to transform those separately. **Rotate by** is an increment for the current edit and resets to 0 after each committed change.

The eye and lock icons on the right change the node's own visibility and locking. Hiding or locking a group affects all descendants, while showing or unlocking it retains each member's original choices. A separately locked member cannot be edited directly but still follows its unlocked parent group's movement, rotation and resizing. Locking the parent also prevents direct edits to its descendants. Hidden or locked nodes remain selectable in the list.

Ordinary layers, groups and shared instances can all be renamed: double-click a painted letter in the name, or click an already selected name's lettering again. `F2` and the context menu also rename. Spaces, holes inside letters and icons are not name hits; double-clicking them retains the ordinary action, entering a group where applicable. New groups use numbered names such as Group 1 and Group 2; a placed stamp's group uses the stamp name. `Enter` or moving focus saves, `Esc` cancels, and an empty name restores the default. Names persist in projects, recovery and undo/redo. The header's **+** creates an empty group at the current editing level. Empty groups can be named, copied, reordered, saved and deleted; moving out their last member retains the group itself. Right-click for duplicate, delete, bring to front, send to back, group and ungroup. Use the **?** beside Close for operation help.

Drag the handle on the left of a row between rows to reorder, onto the middle of a group row to move inside, or onto an ancestor breadcrumb to move out. **Move to parent level** in the context menu moves out one level. Moving between groups preserves content's canvas position, although the new hierarchy and order may change overlaps. A group cannot move into itself or its descendants, and locked groups cannot accept moved nodes.

A group from an older project may show a `↕` marker for **Preserved legacy order**. Its members can still interleave with layers outside the group, so tree indentation alone cannot describe their overlaps. Opening, expanding, entering and saving preserve that order. Explicitly choose **Consolidate group** to collect its members into one consecutive block. This preserves internal relative order but can change overlaps with other content and can be undone. Actively dragging such a group or bringing it to front or back also consolidates it at the new group position.

Ordinary groups each own their contents. Copying an ordinary group or placing an ordinary editable stamp creates an independent copy. **Create shared definition** turns selected content into one project resource with multiple instances. Instances retain their own placement, visibility and locking while sharing internal content. Duplicating an instance within its project preserves sharing; pasting into another project includes complete definition and image dependencies.

Ordinary groups, groups shared within the project, and groups linked to external files all show their internal layers at the current level after entry. The group area identifies these three sources. External links show their actual file path, also included in Project resources. A group shared within the project is stored in that project without depending on an external file. An external link points to a `.floatcanvas` or `.floatprefab` on disk and retains an offline snapshot.

The group area describes only the group you have entered. Selecting a group on the main canvas does not show its configuration; selecting a child group after entering its parent does not switch the configuration. It offers two canvas buttons. Switching keeps the current group list and adds no undo step:

- **Temporary canvas** shows only the current group's content for editing. Ordinary groups retain their canvas coordinates and perspective, with outside layers and the root background hidden. Shared and external groups show their current editing draft.
- **Root project canvas** displays the complete root project and lets you select and edit the current group's contents, including unpublished nested edits. Selection frames, dragging, rotation, scaling and undo use the current instance's placement and the root canvas perspective. Other layers remain visible as context without leaving the current editing level. Both views edit the same content; switching neither publishes a shared or external draft nor opens another preview window. The overview of an external draft uses retained window-source images rather than the draft's live window bindings.

Use **Save as prefab** for an ordinary group to export its content and dependencies as a `.floatprefab` without first converting the group into a shared definition. Export leaves the original group intact; its position and container transform on the root canvas belong to instance placement. Shared groups can also be exported. **Detach as group** turns only the current shared or external instance into an ordinary group and opens its contents, leaving other instances and the external file intact. Unfinished drafts use the existing save/discard/cancel flow first.

Open **Project resources** from Layers to place, rename, edit, export or remove unused definitions. Double-click an instance icon or a non-text area, or choose **Edit shared definition** to enter its layer list and temporary canvas. **Done** publishes to every instance as one undoable change; **Cancel** retains the original. Use `Ctrl+Shift+S` to export the current draft as an independent `.floatprefab` while keeping the editing context and owner unchanged, including when publication conflicts.

**Link file** uses a `.floatcanvas` project or `.floatprefab` as a subproject and keeps a complete offline snapshot inside the owner. Missing sources retain their artwork; check, refresh or locate the source as needed. **Edit source project** preserves the source's canvas, background and file-version checks. Saving the source updates open references, and opening a project checks links. Active gestures and safety conflicts retain the accepted snapshot with an update available. **Make embedded** keeps sharing; detaching converts only the chosen instance into an ordinary group.

When a source is unavailable, you can still enter its accepted offline layer snapshot and switch between both canvas views. The group area identifies it as read-only, with rename, dragging and content edits disabled. Saving or leaving does not recreate the source or publish a new cached revision. Detach it as an ordinary group to edit independently.

Create a reusable file in either way:

- For a whole canvas, use File > Save project or Save as to save a `.floatcanvas` file.
- For an ordinary group, select or enter it and choose Save as prefab in the group area. For separate layers, create a shared definition in Project resources and export that definition.

On another canvas, choose Place on the toolbar or press `I` to add either file as a linked subproject at the current editing level, keeping the existing canvas. You can also hold a file drag in the file manager, use the edit/pass-through shortcut to enter editing, and release at the desired position; while already editing, drop directly. Images and projects may be mixed in one undoable batch. Failed reads and expired requests leave no partial batch. The filename names the new instance; renaming that layer does not rename the disk file or other instances.

Links retain offline snapshots. Project resources can edit or refresh the source or make the definition embedded; an instance can also be detached into an ordinary group. Older projects without a persistent resource identity must first be saved as a new file by this version before linking; placement does not rewrite the source. Projects and prefabs retain their internal Surface/Billboard structure as a positioned instance, rather than being flattened to an image.
Export a self-contained `.floatprefab` with its content and resources; portable exports remove external path bindings by default. The File menu and `FloatCanvas.exe --open "artwork.floatprefab"` import it into the current canvas while retaining existing content. Opening `.floatcanvas` through File > Open project, Windows association or `--open` still opens a project; Place and file drops add it to the existing canvas. Automatic Windows file association still applies to `.floatcanvas`.

Properties, Tool Options and Canvas settings share one numeric field: drag the icon inside its left edge for small adjustments near the starting point and faster changes farther away, type in the middle, or click and drag the bottom rail. Only embedded icons show the horizontal drag cursor and start numeric scrubbing; external names remain ordinary labels. The rail's common range does not limit scrubbing or typed values; each property's valid limits still apply. Mixed selections and automatic values can also be adjusted directly with the icon or rail. Position and Size use equal-width X/Y/W/H editors with the same fixed 36-DIP middle gap: empty for Position, containing the aspect chain for Size.

Clicking the circular thumb preserves its value; dragging keeps the offset from the press point. Clicking elsewhere on the rail adjusts immediately from that position. Clicking the icon, unit, frame or property label focuses the slider, so Left/Right adjusts by the property's step (one row or column for grid counts); clicking the input itself edits text. Dragged and refreshed values display at most three decimal places, with integer fields remaining whole numbers. Formatting preserves the full precision of existing data. Icon and rail drags preview continuously and restore the starting value on cancellation. Edits to layers and saved canvas settings commit as one undoable action on release.

Sliders use separate common ranges: ordinary strokes 0–32 px, highlighter 16–64 px, capture and image borders 0–16 px; text font size 8–144, Number and Emoji tool size 16–256; corner radius and arrowheads 0–64 px; blur strength 0.1–64, pixelation strength 1–64; number step −10–10. Layer width and height sliders span 1–1000 px, billboard elevation −1000–1000, and grid line width 0.5–8 px. Direct entry and icon dragging retain each property's original valid range. Switching tools or refreshing a panel never clamps stored values to the slider endpoints.

The default parameter area is 440 DIP with a fixed 96-DIP label column in both languages. Size, Position and Grid counts each have one group label and two editors; other ordinary properties occupy separate rows. Narrow windows stack pairs to preserve input space. Boolean names use the remaining width on the left and can wrap, with controls aligned to the right. All property booleans use switches. Mixed selections show a centered knob and dash; clicking chooses on for all selected layers, and clicking again turns them off. Concise ordinary labels scroll only when overflowing, pausing while pressed. When system animations are disabled, the static label and full tooltip remain available.

Ordinary windows, dialogs and titled panels use 12-DIP left and right content insets. Scrollbars take space only when visible, and the footer follows the same content edges.

The chain between width and height saves the aspect preference: a blue connected chain is locked, a broken chain is independent, and a dash indicates mixed layer preferences. Typing or dragging either locked dimension updates the other immediately. Cancelling restores both values, and layer edits are undoable. With mixed preferences, numeric resizing follows each layer's own aspect preference; clicking the chain applies one choice to all selected layers. Presets save the preference and dimensions; locked shapes are created with the configured ratio, including non-square ratios. Square and circle presets remain 1:1. New blur and pixelate layers inherit their tool's aspect preference. Hold `Shift` while drawing to temporarily release a stored lock, or to draw a 1:1 shape when unlocked, without changing the preference saved by the tool or new layer.

Each numeric field shows its unit on the right, including `px` for dimensions and stroke width, `°` for angles, `%` for opacity, and row, column or numbering indicators. Type only the number; units are separate from the editable text. Tool previews and panel refreshes preserve an active drag, with preset status refreshed on release.

Ordinary and boolean property names share the same 12-DIP font size, family and weight. Section titles use 13-DIP semibold text with no divider above the first section. Numeric anchors share a monoline icon family and the same concept always uses the same icon. Standard numeric, color, single-line text, dropdown and boolean rows are 34 DIP high; long boolean names may wrap, and galleries or multiline content can grow as needed.

Press `Esc` to close the secondary color window. While editing a color value or dragging hue, opacity or the spectrum, the first press cancels that adjustment and the next closes the window. Focusing the hex input without changing it still allows one-press dismissal. Closing does not also dismiss its source or leave editing.

Hiding Properties or changing selection commits valid text edits already shown on the canvas; unfinished numeric drags are cancelled and restored. Incomplete or invalid numbers are never written to objects; choosing another object loads its current properties automatically.

The canvas opacity slider at the upper right affects annotations and images, while the screenshot background keeps normal brightness. It uses a widening ribbon and hollow handle, highlighted only over the handle or during a drag. Press either the handle or rail to drag continuously; pressing the handle retains its value. Each drag creates one undo step, and cancellation restores its starting value. New canvas and visibility controls are beside it. Use the gray page-plus button for New canvas and the red broom beside it for immediate Clear. Hiding keeps your content and the current edit/pass-through mode. While hidden, new content previews only the current operation; committing shows the complete canvas.

If a display change leaves annotations wholly off screen, use **Main menu → Canvas → Move annotations into view**. Any content partly visible on a display in the active drawing scope stays in place. Wholly offscreen, visible and unlocked objects move to the nearest usable position. Groups receive one common canvas-plane translation, retaining all members' local positions and transforms, including hidden or locked descendants. The whole unit is skipped only when its root node or an ancestor is hidden or locked. Logical geometry and the screenshot crop stay unchanged; projected screen size can change with depth. Oversized content is made reachable without additional scaling. Recovery is one undo step, and repeated clicks do not keep shifting content.

### Canvas tool, grid and perspective

Canvas uses the ordinary `Esc` sequence: cancel an unfinished drag or four-point placement, dismiss unpinned settings, then enter pass-through when idle while retaining the selected Canvas tool.

Click **Canvas** on the toolbar or press `C` to select it. It is exclusive with the drawing tools; a blue border marks the current tool. Its rectangle/trapezoid icon indicates perspective off/on. Select Canvas again to open or close its options. Initial placement, reopening and the top-right pin follow the other tool-options panels. Closing the panel leaves the Canvas tool selected. While entering text, `C` types a letter; `Ctrl+C` still copies.

Selecting Canvas shows the reference grid by default, independently of **Show grid**. Without perspective, the origin is the **current reference-screen center**: the chosen screen for a specific-screen scope, or the primary screen for Primary only and All screens, never the center of the combined desktop. Drag from empty canvas space away from the origin to enlarge the squares, or toward it to shrink them. Each drag is one undo step; `Esc` cancels it. Starting on an existing object does not begin grid sizing.

The panel provides independent canvas background color and opacity. The default is transparent. Canvas color and the configured grid form the lowest layer on the canvas plane and share the toolbar's global opacity with annotations. With depth-priority occlusion enabled, they participate in spatial occlusion with billboards. In flat mode, document (0,0) remains the upper-left logical zero. The grid is independently centered on the primary display: its center stays on a grid intersection regardless of the perspective pin or layer translation. With Canvas selected, start a grid-size drag anywhere on the canvas, including over layer pixels; the whole drag is undoable. Flat mode displays one **Grid size** field: the side length of a square, in the same pixel units as rectangle width and height. Enter an exact decimal or use the slider. Manual values range from 1 to 1,000,000; smaller legal sizes in existing projects remain intact when opening the panel. New canvases restore the first official canvas preset by default. Turn on **Keep canvas settings when creating a canvas** under Settings → Canvas and history to retain the current background, grid, perspective and pin selection. Clearing annotations always retains the current canvas settings.

With perspective enabled, **Plane width** and **Plane height** determine logical-pixel dimensions and the aspect ratio with perspective off. The four corners determine the screen shape. Choose A4, A5, Letter, Legal, 1:1, 4:3, 16:9 or a custom ratio, and swap orientation. Paper options set only the ratio and preserve the longer logical edge; they do not convert millimeters to pixels. Enable the aspect lock to resize both dimensions together. Moving the corners retains fixed logical dimensions, so enlarging the frame also enlarges content and billboards.

The grid has independent spacing and origin X/Y. **Align grid origin to pin** explicitly resets its phase. Column and row coverage is read-only and may contain partial cells. Grid edits never change the projection, layer positions or billboard scale. Flat mode retains its own exact spacing and screen-centered origin; editing its grid no longer replaces dormant perspective calibration. Upgrading an older project preserves its matrix, material pin, lattice phase and displayed appearance.

**Preview rectification** temporarily captures the frame and compares the original with its unfolded result. Choose a paper ratio or enter dimensions. **Apply** changes the plane dimensions; **Apply and insert capture** also inserts the original-resolution screen capture. Turn perspective off to see it unfolded. The small preview bitmap never replaces the original capture. Cancelling, hiding the canvas, leaving editing or changing documents invalidates an unfinished result. Four corners alone cannot establish an object's true aspect ratio; supply a paper specification, custom ratio or known square cells.

**Calibrate from known cells** asks for one clear intersection, another along its horizontal grid axis, and another along its vertical axis. Enter the one or several cells spanned in each direction. Both pairs of frame edges must follow the grid axes. Check the fitted ratio, spacing and origin in the canvas preview, then apply them as one undo step. There is no need to count all cells across a large frame. Unresolved or inconsistent spans cannot be applied; cancel and select clearer intersections. Escape, right-click, hiding or leaving editing cancels the operation.

**Reset perspective** restores an unwarped 1:1 document view and clears flat-mode translation without rewriting layers. It retains the independent plane dimensions and grid spacing, centers the flat frame on the target screen and aligns the grid origin to the selected pin. With one screen it has no dropdown. With multiple screens, choose default **Primary display** or an explicit **Screen N**. Choosing saves the target; click the main button to reset. Missing targets fall back to the default. Reset does not change the drawing scope or logical document zero.

Select Canvas with perspective enabled to show the frame, central move handle and reference grid. The grid extends from its independent origin; frame edges may end within a cell. Logical (0,0) and the central handle have separate markers and roles. Drag the handle or empty frame interior to move it as a whole, including across displays under **All screens**. Corners and edges change perspective. Corners snap near adjacent horizontal or vertical axes; `Ctrl` bypasses snapping and `Shift` constrains movement through the drag origin. **Reference opacity** ranges from 0% to 100%; it lasts for the current run, resets to 100% after restart and is not saved. Reference lines are editing guides; the configured grid belongs to the plane and participates in depth composition. Opacity does not hide anchors or the move handle. Selecting another tool hides the frame and reference, retaining the configured grid when enabled. Losing focus or hiding the canvas cancels an unfinished drag.

Grid spacing is independent of the frame dimensions. Fractional coverage at edges is intentional.

The full-width **Position canvas with four points** button temporarily hides open windows except the toolbar. Click corners in any order: preview a line, then a triangle, then a quadrilateral with a grid. Its origin also uses the central handle. The fourth click commits one undo step; `Esc` cancels unfinished placement. Completion or cancellation restores previously visible windows without opening ones that were closed. Anchors remain adjustable while Canvas is selected; no Finish adjustment action is needed.

Moving the whole canvas changes only its screen position, preserving the perspective shape, grid spacing and pin selection. Translucent strokes and shapes preview continuously with their original opacity and overlap; there is no need to make them opaque first. Each adjustment remains one undo step, and cancellation restores the starting state.

The horizon may enter the screen. Concave or crossing corners, degenerate or noninvertible geometry and numerical instability constrain adjustment; reaching those limits turns the frame amber and shows a notice. The grid, canvas color and Surface content stop at a safe boundary, while safe areas outside the reference frame remain usable. Calibration marks the distant boundary with an amber dashed line. During normal editing, hovering over an unsafe area softly tints its extent red; returning to safe space or leaving the canvas removes it. The hint is excluded from exports. Unsafe creation or dragging displays a forbidden cursor; release rejects the creation or restores the pre-drag position. Freehand strokes break across unsafe areas while retaining safe fragments. Billboards validate their ground anchor and scale; their upper parts may extend above the horizon.

Adjustment keeps previewing with unsafe parts temporarily clipped. The confirmation dialog shows only the emphasized affected layer count; use the help button beside Close for option explanations. On release or the fourth placement point, affected layers trigger a choice: cancel this adjustment, apply and move layers back (recommended), apply and delete them, or apply and keep layers (use with caution). Keeping applies only the perspective: layer content, positions, groups, visibility and locks remain intact. Unsafe parts are clipped or temporarily hidden and reappear when the perspective returns to safe space, including after saving and reopening the project. Relocation prefers an open position inside the reference frame or screen, moving groups together and staggering independent layers while preserving original content and logical size. Hidden and locked layers are included. If no safe, usable destination is found, relocation is disabled; layers are never silently resized or deleted. The adjustment and selected action share one undo step; recovery snapshots retain the pre-adjustment state until a choice is made.

**Pin** uses a nine-point selector, defaulting to bottom left. It anchors subsequent plane-size or ratio changes: the selected point retains its logical and screen position. Selecting a different pin alone leaves content, grid phase and billboard scale unchanged. Use **Align grid origin to pin** to move the lattice phase explicitly. Logical (0,0), the pin and the central move handle have separate roles.

Grid colors use the foreground presets. Adjust color, opacity and line width, and select solid, dashed or dotted strokes from the illustrated list; dotted grids render visible round dots. Configured and reference grids share their origin and spacing and extend beyond the frame. Far grid spans with less than eight screen pixels of separation use hierarchical thinning, while resolvable near lines remain. Unresolvable dash and dot periods are omitted before expanding their plane-space paths. Reference, custom and four-corner preview grids share this policy. Dense grids still add CPU cost at high resolutions. Increase flat grid size or the independent perspective-grid spacing, or turn off **Show grid**. Perspective coverage counts are calculated from the plane extent and grid spacing; use three-intersection calibration to align an existing grid. Reference lines hide when you select another tool.

Canvas properties are on the left, with official and personal presets on the right. Use the plus button to save; personal presets support overwrite, rename, delete and drag reordering, and application is one undo step. Preset library v4 retains independent plane dimensions, perspective-grid spacing and origin, plus background, grid styling, exact flat spacing, depth-priority occlusion, pin, corners and complete calibration. The destination's flat-grid origin and the application's reset-display preference are outside preset data. Older v1–v3 libraries remain readable without rewriting their files on read.

Official presets disable perspective and restore default reference geometry. Older personal presets without calibration generate default reference geometry from the reference display and the preset's counts and spacing instead of inheriting the current corners. A missing depth-priority occlusion option defaults to off. Corners that an older preset never saved cannot be recovered automatically; recalibrate and overwrite it as needed. After a display-layout change, adjust again or choose a target screen for the reset.

Projection applies to shapes, text, images and animations. Default Surface objects follow the plane; experimental Billboards use the facing and depth rules below. Live blur and pixelation regions follow placement while sampling amounts remain in screen pixels. Fixed plane dimensions determine logical proportions and the corners determine projection; this does not recover a real camera. Recalibrate after a game's camera moves. New screenshots are inverse-projected into their selected screen positions and then follow later projection changes.

## Surface and Billboard placement (experimental)

**Surface** objects follow the canvas projection. **Billboard** objects face the screen, retain their own proportions and rotation, and scale with their ground-anchor depth. Billboard creation is off by default. Its switch is beside canvas adjustment in the toolbar and is also available from the main menu. Leaving it off retains existing creation workflows; the general Shift movement constraint and corrected Surface capture outlines apply independently.

The toolbar and main menu call this action **Placement**. Its tooltip shows only the action name and the current **Default mode** or **Billboard mode**; Default mode uses Surface placement. Enable canvas perspective, then use this button to switch to Billboard mode (experimental). The global switch affects only new objects, does not convert existing layers and is not stored separately in tool presets. Disabling perspective disables the switch and resets new-object placement to Default mode; existing billboard flags, anchors and elevation remain stored and resume projection when perspective returns. **Placement** and **Elevation** appear in Properties with perspective enabled.

### Create directly, then adjust the ground point

Billboard mode defaults to drawing directly in screen space, retaining each tool's gestures for shapes, strokes, text and captures. The frame's bottom midpoint becomes its ground point; move the body or center handle afterward to adjust position and elevation. An image frame's bottom does not necessarily match the pictured subject's feet.

A stroke can begin above the horizon, while its final frame bottom must reach safe ground. Pending ink appears at half opacity with a dashed frame. Finishing with unsafe ground cancels creation rather than keeping an earlier valid position. Image imports, bitmap pastes, external image drops and flattened stamps use one click to choose their center. Text reflows while typing and resolves its ground point when completed. Cancellation and changed editing scopes or projects reject late results.

Properties defaults to **Preserve current appearance**. Switching Surface/Billboard placement succeeds only when the current appearance has a safe representation; otherwise the layer stays unchanged. Clear that checkbox to use the original logical placement conversion.

### Fix a depth line first (advanced)

Enable **Billboards: fix a depth line first (advanced)** in the main menu's Canvas group for the following workflow. This application preference leaves existing layers unchanged.

1. Select a tool that creates layers. Move the pointer to preview an equal-depth line, then click to fix its depth. This stage shows only the line, without a point at the click, and does not begin drawing.
2. Use the original tool gesture: rectangles still drag from one corner to another; lassos, multi-point tools and text retain their completion rules. The fixed line stays visible, with a live outline center and a screen-vertical connection to the line.
3. The intersection becomes the ground anchor. As the outline center moves sideways, the candidate anchor moves along the same line without changing depth. The first click selects depth rather than the final anchor position.
4. A successful creation ends the equal-depth guide. The center, anchor and connection become selection guides and appear or hide with the selection outline. Choose depth again for the next creation.

In the advanced workflow, snapshots choose depth before the normal rectangle or lasso operation and retain the selected screen pixels. File imports, plain bitmap pastes, external image drops and flattened stamps prepare the image first, then use one canvas click to choose depth and a second to place the image center. Choosing depth creates no empty layer or undo entry. Switching tools, Esc or cancellation clears unfinished depth selection; failed asynchronous work and changed projects cannot receive a late image.

### Depth-priority occlusion (experimental)

Enable perspective in Canvas tool settings to expose **Depth-priority occlusion**, off by default. Billboards are ordered by their ground-anchor depth, with original layer order used only at exactly equal depth. Surface objects retain layer order within their common plane. The layer list does not reorder as anchors move, and Bring to front cannot override different spatial depths.

Surface content belongs to the common canvas plane. Equal-depth strips are composited between billboards, allowing one Surface object to cover part of a billboard while passing behind another part. Canvas color and the configured grid form the lowest ink on that plane and follow the same depth strips. Opaque canvas color hides billboard portions below the plane; translucent color blends over them. A grid alone covers only its actual strokes, leaving cell gaps transparent. At exactly equal depth, canvas color and grid stay beneath all objects. Global toolbar opacity is applied once after compositing canvas color, grid and objects. The desktop and background image remain backdrops; pictured objects have no independent depth. Negative elevation can hide some or all of a billboard, which remains selectable in Layers with its editing handles available.

The choice is saved in projects, recovery records and canvas presets and supports Undo. Disabling perspective hides and suspends it; enabling perspective restores the choice. Switching creation mode retains it. Applying a canvas preset restores its saved choice, defaulting to off when the parameter is missing. Screen captures and image exports use the same occlusion; plane-content stamps retain plane semantics. Live blur and pixelation sample only the completed scene behind their current depth fragment. Blur support does not read nearer fragments that have not yet been composited, avoiding cyclic sampling between intersecting filters. Depth-strip drawing can increase rendering cost on complex canvases.

Current projects retain this option. Older projects without it default to disabled. See the file section below for project-format compatibility.

### Logical dimensions and visible size

All billboards share the canvas depth scale. Direct screen creation preserves the completed appearance; moving the ground anchor changes visible size with depth. Properties width, height and stroke width are local values, combined with the layer transform to determine screen appearance. The advanced fixed-depth workflow converts drawing units at its chosen depth: at a scale of 0.25, a screen rectangle of 100×100 stores logical dimensions of 400×400, while a stroke preset of 5 appears as 1.25 in that workflow.

Moving farther away reduces visible dimensions, stroke width and elevation together while retaining their logical values. Disabling perspective displays logical size: that rectangle becomes 400×400 with stroke 5 at ordinary 100% view scale. It does not return forcibly to its initial screen size.

| Operation | Result |
| --- | --- |
| Drag the body | Move the ground anchor on the canvas plane; depth changes the projection, retaining logical dimensions, elevation and angle |
| Hold Shift during body movement | Constrain the anchor to canvas X/Y axes through the original drag start, independently of screen axes, equal-depth lines and artwork angle |
| Drag the center point | Use only screen-vertical motion to change elevation and move the entire layer up/down, retaining the anchor and logical dimensions |
| Drag the outer rotation handle | Rotate in the screen-facing plane while retaining the anchor and elevation |
| Drag a corner handle | Resize about the layer center while retaining the anchor and elevation; Shift temporarily inverts its aspect preference |
| Click the ground anchor or connection | Display-only references, without a second drag or rebinding action |

Elevation is signed: the center can pass through the anchor and continue down, without clamping at zero. At zero elevation the points coincide, but the center height handle remains usable. For one selected billboard, Properties X/Y locate its ground anchor in its parent group's local coordinates, or on the canvas plane when it has no parent. When an equal-depth line cannot intersect a screen-vertical line stably, the app reports that no anchor can be determined at that angle. Choose a valid location or adjust calibration; the app does not silently choose another depth.

### Groups, conversions and files

Editable group stamps, internal object copying, restored history and reopened projects retain the group hierarchy and every layer's placement, anchor, elevation and artwork, independently of the global switch. Editable stamps do not add a depth-selection step. Flattened image stamps capture the current visible appearance and become one fresh image, losing internal child depths. A group transform affects Surface content as well as billboard anchors and artwork; resizing also scales elevation. Each member's depth still determines its projected screen appearance. Depth never automatically changes the layer-list order.

With perspective enabled, Properties can convert an existing layer's placement. **Preserve current appearance** is selected by default: conversion succeeds only when the existing appearance has a safe representation; otherwise the layer stays unchanged. Clear the checkbox to retain original content and logical geometry while allowing the visible outline to change, without guaranteeing every screen corner is preserved. Images are not repeatedly resampled when moved or converted.

## Presentation

Choose **Keyboard demonstration** or **Mouse demonstration** from **More tools** or **Main menu → Presentation**. Both use the standard Tool options window, with parameters on the left and official or custom presets on the right. Drag a display region for the keyboard tool; the mouse tool needs no drawn region. The two tools have separate settings.

**Mouse demonstration** has an **Enable pointer effects** master switch, showing the cursor halo and clicks by default. Enable the halo, clicks and trail separately. Left, right and middle clicks each have their own enabled state, color, ring or disc shape, radius, ring width and duration. The halo has color and radius controls; the trail has color, width and duration controls. Each color's Alpha controls its transparency. Switching the master off hides all detailed controls; disabling a feature or an individual mouse button's click effect hides its corresponding controls. Switching it back on restores the previous preferences. Official presets cover the halo, clicks and trail, and custom presets retain your effect combinations. Presets preserve the current master-switch state when applied rather than storing it.

Pointer marks cover all displays served by FloatCanvas and stay at the cursor's physical screen position. They are independent of keyboard regions, canvas size, pan, perspective, groups and instances, and create no layer. Enabled pointer effects can continue while the canvas is hidden. Current pointer effects are personal preferences outside project files; changing their parameters neither modifies the layer being edited nor adds undo steps. Clicks and short trails never automatically become canvas ink.

With Mouse demonstration selected, canvas clicks, drags and wheel input only preview the effects: layer selection frames stay hidden and uneditable, and temporary middle-button selection pauses until you switch to another tool.

**Key display regions** record global keyboard input immediately after creation, including FloatCanvas's own controls, and continue during editing. Key layers have no individual enable switch: a visible layer records input. Hide or delete a layer to stop its key display. **Input scope** in Tool options applies to every key layer: choose **All apps**, **Other apps only** or **FloatCanvas only**. Keyboard observation stops when no key layer is visible or the whole canvas is hidden; pointer effects keep their separate master switch.

Choose **Key recording** or **Live keyboard** first. Key recording then offers **Simple** or **Realistic keycaps** appearance:

- **Key recording:** shows each complete action as one item. A shortcut such as `Ctrl+C` has no separate entry for the initial or remaining `Ctrl`. **Simple** uses text; **Realistic keycaps** gives each key an individual raised keycap. Key recording defaults to all physical keys, with a **Shortcuts** filter available. Held chords remain visible and fade together after release according to the configured duration.
- **Live keyboard:** displays a complete 104-key physical layout by default. Pressed keys change appearance and return to normal on release. Left and right modifiers, the two Enter keys, and numeric and navigation clusters respond independently. Language-layout and NumLock changes retain the same physical positions.

For Live keyboard, **Edit keyboard layout…** opens a secondary layout window. Click a key to select it and see its physical key name on the right. Drag to move it, or enter X, Y, width and height, measured in standard key widths. Change its displayed label, hide individual keys, use **Show all / Hide all**, or restore the standard keyboard. Hidden keys retain their positions and sizes, allowing game layouts with just WASD and Space. Enabled keys fit proportionally within the display region. Changes remain in a draft until Apply; Cancel changes nothing. Apply commits the layout once, while applying a restored standard keyboard uses the default layout.

These are physical key labels, not text read from an input field. When an application reports a password field, pressed state is cleared and keyboard recording pauses; the live keyboard returns to its normal keycaps while pointer effects can continue. Detection relies on the application's metadata and may miss custom input controls; it does not guarantee that every password entry is hidden.

In Tool options, mode-specific properties follow the mode selector: Key recording offers filtering, recent-item count, maximum width and duration, while Live keyboard offers layout editing. Shared alignment, font, size, foreground and background colors, and region dimensions appear below. The key display follows its layer's position, size, groups and perspective like other canvas content. Editing shows a faint dashed region boundary so an empty recording display can still be found, selected and adjusted; idle recording regions remain transparent in pass-through. Copies and shared instances each display the same global keyboard input at their own placements.

Projects, recovery files and `.floatprefab` files retain key styles, layouts and other layer settings without recent keys, clicks or trails. Ordinary PNG/JPEG exports and resource thumbnails show the live keyboard's normal layout without real pressed states or log input. To retain the displayed keys, use Snapshot with **Background + canvas** or **Canvas only**, freezing the previously displayed key frame into a static image. Global pointer marks are outside these canvas outputs and do not become a live recording inside the project.
## Stamp library and shelf

The library window has ten compact slots ordered `1`–`9`, then `0`. Drag an entry onto a slot to pin or replace it, or select a slot and then click an entry to assign it. A successful assignment clears the slot selection; a failed assignment leaves it selected for retry. Click the selected slot again to leave assignment mode. The slot's `×` clears its binding while keeping the stamp in the library. Search has an inset magnifying glass and shares a row with the action buttons; instructions are available from `?` beside Close. The default window width fits complete stamp columns, and all entries scroll in one continuous list. Drag entries to reorder the library; the order is saved. Each entry shows its group/image type and a pin badge with the first assigned slot number.

An unpinned entry has a **Pin** submenu for choosing a slot. A pinned entry instead has **Unpin**, which clears one binding at a time in shelf order (`1`–`9`, then `0`). The same stamp can occupy multiple slots.

Stamps reuse artwork you create. Select one or more elements and choose **New stamp** on the lower toolbar row, or use the save-selection action in the stamp library. Enter a name and choose a save mode. New stamps automatically fill the first empty shortcut slot; if all slots are full, the stamp remains available in the library:

| Save mode | Placed content |
| --- | --- |
| Editable group | Copy shapes, text, cropped images, animation, nested groups, styles and transforms; adjust the group together, enter it or `Alt`-click a member to edit it |
| Merge as image | Merge the current frame into one static image while retaining editable source objects in the library; choose **Use editable source** to place those original elements again |

Editable groups retain the original GIF/animated WebP data. Animation still plays when the library is reopened and the stamp is placed again. Small toolbar and library thumbnails remain static; ordinary animated stamps play in the larger hover preview. Groups containing live desktop blur or pixelation use their saved static thumbnail for previews, even when they also contain animation; placed objects keep their normal behavior. Choosing a stamp from the library keeps the library button highlighted and shows the selected stamp on it. Clicking that button still opens the library.

Search or scroll through the library, choose **Use** or double-click a thumbnail, then click the canvas to place it. Each click keeps the saved visual size and relative rotations, centered on the clicked position. No size-drawing gesture is needed, and repeated clicks place further copies. Each placement can be undone separately and leaves the copy unselected. Hover a slot or library entry to see its name, shortcut, larger preview and the preview-to-actual-size ratio. The larger editable-stamp preview uses plane coordinates, preserving the relative positions and elevation of Surface and Billboard artwork; the saved small thumbnail retains its original perspective appearance. New stamps fill the first empty shortcut slot automatically.

Select a library entry to rename it, overwrite it with the current canvas selection, or delete it. Ordinary stamps place independent copies; editing a copy or overwriting the library original does not update earlier copies. Save as **Shared prefab (synchronized within each project)** to make placements in one project share a definition. Completing a definition edit updates all its instances. Overwriting the library entry still leaves existing project content unchanged.

Pin a stamp to a slot labeled `1`–`9` or `0` to select it from the second toolbar row or its number key. The key selects the stamp; click the canvas afterwards to place it. Numbers remain text input while editing text. Unpinning clears only the shortcut; deleting a stamp also clears its pinned slots.

## Place images or projects, replace and crop images

Press `Ctrl+V` to paste a clipboard image, or choose **Place** on the toolbar to select one or more files. Place also accepts the linked `.floatcanvas` and `.floatprefab` files described above. New images are placed at the center of the currently visible canvas area. PNG, JPEG, BMP, GIF and WebP are supported. Transparency is retained; GIF and animated WebP retain their original data and play on the canvas.

You can also start dragging an image from a file manager or browser, keep holding it, and press the edit/pass-through shortcut (`` Ctrl+` `` by default) to open the canvas. Release the mouse at the desired position. The image is centered on the drop point; when dropping several images, subsequent images are offset slightly. The entire import can be undone in one step.

Dragging or pasting requires actual image data or supported image files. File import, copied/pasted or dropped files, and clipboard sources supplying original GIF/WebP data can retain animation. If the source supplies only PNG or Bitmap data, the result is static and the original animation cannot be recovered. A URL or web link alone is not an image import, and FloatCanvas does not fetch the original image online. If a browser does not provide the original animation, save the file and import it.

Hiding the canvas stops animation refresh; showing it continues playback. Pass-through keeps playback running. Playback does not edit the canvas, add undo steps or trigger recovery saves. Projects, recovery files and internal object copying retain animation data. Snapshots, PNG/JPEG exports and **Merge as image** stamps take one static frame at the time of the operation. Playback controls, playback-position persistence across restarts and animated export are not currently available.

An imported image is a separate object and does not automatically replace the background. Select it to move, rotate, resize, change opacity, or edit border width and color. Images and new captures expand the outline of every nontransparent pixel by the specified width, then paint the border color underneath the original image. Transparent PNGs follow the subject's silhouette; semitransparent areas blend with the color below them. Fully transparent images have no border. Animated borders follow each frame's silhouette. Position and size properties describe the image body. The selection frame and hit area include the full border and retain the source's transparent padding, so they do not jump as animation changes shape.

Image import still opens the file chooser directly, without a tool-options panel. Existing images gain no border by default. Borders already baked into older capture pixels remain unchanged; new capture borders can be edited in Properties or set to zero.

Image border masks are cached on demand, including individual frames already displayed by an animation. Changing the source, crop, effects, body dimensions or border width regenerates the mask on the next draw. Color and overall opacity changes reuse it. Masks match the actual display resolution and rebuild as needed after zoom or perspective changes, so a low-resolution mask is not stretched. A memory budget evicts older cached content, which is regenerated when needed.

- **Replace source image:** select one image and use **Main menu → Images and effects → Replace selected image…**. Its position, size, crop and effect regions are retained. Replacing a window source this way turns it into a static image; Undo restores the window source.
- **Crop image:** select only one editable, unlocked image, choose **Crop** after import and capture on the main toolbar, then drag the area to keep. Crop is disabled for no selection, multiple or mixed selections, and locked images; an empty selection does not crop the screenshot background. The original image asset is retained, and cropping can be undone. A window source opens its separate source-cropping window, described below.
- **Blur / Pixelate:** choose a tool from More and drag a region. On the live canvas it continuously processes the desktop and lower FloatCanvas objects, even when an image is selected. Move, resize or rotate the region, and save it in a project. Moving the region processes content at its new position. The corresponding effects on frozen screenshots and images remain static.
- Existing solid effects in older projects remain visible; solid-redaction authoring is no longer available. Image effects transform with their target image.

Live blur and pixelation require Windows 10 2004 (build 19041) or later to reliably exclude FloatCanvas's own windows. The application's minimum remains Windows 10 1607. Earlier systems create static snapshot effects for new desktop regions and show a notice. Existing live regions use a solid placeholder with a warning, retaining their original editable objects. Frozen-screenshot and image effects are unaffected by this requirement.

New live filters prepare a valid screen image before adding an object. Hiding the canvas, switching tools, leaving FloatCanvas or changing the document during preparation cancels insertion. Hiding stops capture; showing resumes from the same canvas's last valid image until a new frame arrives. A different document or display layout does not reuse that cache. Capture failures show a notice and retain a compatible last image when available, otherwise using solid coverage.

Live regions follow new desktop frames, targeting the display refresh rate up to 60 FPS. Actual speed depends on region size, hardware and system scheduling; frame-for-frame synchronization with the underlying application is not guaranteed. Export, image copying and merged stamps take a snapshot for that operation, while the project retains editable live regions. Effects and region transforms can be undone.

**External capture compatibility:** when the system supports session-local window exclusion, live filters exclude FloatCanvas only from its own capture, leaving visible annotations available to other capture tools. The application checks actual interface availability. Unsupported systems use compatibility capture, with a status message that third-party screenshots may omit the canvas. In that mode, use the in-app export for a complete composite, or remove or hide all live regions before capturing. This does not resolve the known Windows Snipping Tool window-detection limitation.

## Window sources

Choose **Main menu → Window sources → Add window source...**, search and select a window, then preview the full image and drag the region to keep, or choose **Use full image**. The list contains window information only; image preparation starts after selection. Cancelling creates no layer. Ordinary placement inserts into the current editing scope. Billboard placement also asks you to choose a canvas position; the advanced fixed-depth workflow remains available.

A window source is an image layer showing the selected window's continuing output. Move, rotate, resize it, or adjust opacity and border. Its initial size fits the source content's aspect ratio into the visible canvas. The default fit preserves that ratio inside the destination frame; source resizing keeps the destination frame unchanged and may leave empty space. It displays pixels only and does not forward canvas clicks or keystrokes to the source window.

Select one unlocked, real window-source image to use these actions in Properties or the **Window sources** menu:

| Action | Result |
| --- | --- |
| Rebind window | Explicitly choose a replacement window. Even the same title creates a new binding. Retains destination position and size, with one-step Undo |
| Crop source | Select a region from the full source image while keeping the destination frame unchanged. The project retains the full source image for later recropping |
| Freeze as image | Materialize the available image, crop, fit and image effects into a static image. Retain layer placement, size, opacity and the editable border. One Undo restores the window source |

**Pause source / Resume source** changes runtime state only, retaining the last image without adding undo steps. Multiple real window sources can be paused, resumed or frozen together; rebinding and cropping require one selected image. Shared instances show a source summary; enter the shared definition to change its source. Instances of one definition share one window source while retaining their own placements and transforms.

Properties shows Live, Paused, Source minimized, Source closed and capture-unavailable states. Capture pauses when the canvas is hidden, no reference is visible, or the source window is minimized or hidden. It resumes when possible; a closed source requires rebinding. Up to **four different live sources** run at once; additional sources show their saved image and a notice. Availability depends on the system and target application; protected content and some windows may be unavailable.

Projects, recovery files and prefabs retain source descriptions and an available full offline image. Cropping does not remove the rest of that image from these files. Live frames do not add undo steps or mark authored content as edited on every frame. Reopening, restoring, or moving content to another project does not automatically connect a window by title: the saved image appears until you explicitly **Rebind window**. Export or freeze an image to share the visible result. Canvas captures can use a previously displayed frame, falling back to the saved image when no live frame is available.

## Capture and place a static snapshot

Choose the toolbar's **Snapshot** tool or **Main menu → Canvas → Snapshot**. Select a region while the desktop is live. Once you finish, FloatCanvas captures that moment and inserts one static image at its original position and size on the current canvas. It stays on the same canvas, and the desktop outside the region remains live.

Select **Snapshot** again to open its options: selection shape, content source and an optional border. The defaults are a rectangle, background only and no border. These options and the current preset are retained with the tool state.

| Selection shape | Operation |
| --- | --- |
| Rectangle / Circle | Drag freely and release to finish; hold `Shift` for a square / perfect circle and release it to restore free proportions |
| Freehand lasso | Trace an outline; hold `Shift` for a straight segment, release `Shift` to resume the curve, then release the mouse to close it |
| Polygon lasso | Click to add vertices and move to preview the next edge; `Shift` snaps to eight directions; click the first vertex, double-click or press `Enter` to finish, and `Backspace` to remove the last vertex |

Circle is the selection mode's display name; free proportions can still produce an ellipse. Older tool states and personal presets cannot restore a persistent capture aspect lock, and reading them does not rewrite their files. A completed snapshot remains an editable image layer with its own aspect-lock property.

With perspective enabled and Surface placement selected, Rectangle and Circle selections lie on the canvas plane, so their preview follows the projection. Disabling perspective restores the corresponding rectangular or elliptical content outline. Freehand and polygon lassos always trace the pointer in screen space. Billboard snapshots default to a direct screen contour and preserve captured pixels. The advanced workflow can fix an equal-depth line first. Border width follows the selected creation workflow.

New perspective Surface snapshots use the smallest canvas-plane rectangle enclosing their capture contour and visible outside border. An unframed rectangular snapshot therefore matches its selection frame exactly; transparent corners introduced by storing the tilted region do not enlarge the frame. Projects, internal copying and cropping retain this contour. Older snapshots without a recorded contour keep their original image bounds.

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

ZIP copies and current-user installations register `.floatcanvas` files for the current user on normal startup; machine Setup registers the file type for all users. You can also use **Register .floatcanvas file type** in Settings. Double-click a project or choose FloatCanvas in Windows **Open with** to open it. If the app is already running, the project goes to that instance, with the existing save confirmation before replacing work. Existing Windows default-app choices are preserved; choose **Always** in Open with to make FloatCanvas the default. After moving the portable folder, run it from the new location to refresh file associations, and recheck the login startup setting. The command line also accepts `FloatCanvas.exe "project.floatcanvas"` or `FloatCanvas.exe --open "project.floatcanvas"`.

When FloatCanvas is closed, double-clicking a project starts the app, opens the project and enters edit mode. While the app is running, opening a project keeps the current mode: `Ctrl+O` or the menu keeps editing active, and double-clicking a project during pass-through replaces the canvas while staying in pass-through. A successful open always shows the canvas and opens the eyes. Cancelling or failing to open retains the previous visibility.

| Operation | Result | Can objects still be edited? |
| --- | --- | --- |
| Save/save project as | A `.floatcanvas` file with the current canvas, original images and effects | Yes; reopen it with **Open project** |
| Export composite image | A PNG/JPEG combining the background, annotations and images | The export is an image; the current project remains editable |
| Export transparent annotations | A transparent PNG containing only annotations and images | The export is an image; the current project remains editable |
| Copy selected objects | Editable FloatCanvas objects plus an interoperable image | Paste in FloatCanvas to keep editing; other apps usually receive an image |

New captures are saved as images on the current canvas; older separate capture drafts remain supported. Export does not clear content. Projects retain original images for later editing; use a composite image export when sharing a redacted result.

Projects use a format version independent of the application version; projects, recovery files and prefab packages now use **v15**. Older **v1–v14** projects remain readable and are migrated in memory without rewriting the source file. Projects retain group hierarchy, local transforms, group visibility and locking, drawing order, shared definitions and instances, linked-project snapshots, independent plane dimensions, perspective-grid spacing and origin, background, flat-grid size and origin, perspective, pin, flat translation and layer placement, including billboard anchors, elevation and depth-priority occlusion. Keyboard layers retain display styles and custom layouts; older keyboard regions migrate to their original key-log appearance. Window sources retain descriptions and offline images, without saving a live window connection. Legacy canvas migration preserves the exact matrix and material pin instead of recalibrating content; legacy groups retain their original interleaved overlaps. Missing placement flags mean Surface; missing depth preferences default to disabled. Saving writes v15, which older apps reject to avoid silently losing new keyboard layouts. Keep original files before exchanging with older versions or downgrading.

Committed edits trigger a recovery save after about one second. The App waits for pending recovery writes before exiting. Normal startup still uses a blank canvas. Choose **Main menu → File → Restore workspace history…** to select the latest or an earlier workspace and restore its live canvas and capture draft together. Restoring, cancelling or a failed restore keeps the edit/pass-through mode you started in. If you switch to another application or explicitly leave edit mode during restoration, FloatCanvas does not reclaim focus. Undo history is not retained across restarts.

The history list shows the save time and a canvas thumbnail to distinguish records with the same name. Thumbnails retain FloatCanvas's transparent annotation layer without capturing the desktop, toolbar or selection handles. Previews are generated on demand for older history without one. If a preview cannot be generated, **No preview** is shown and restoration remains available.

Default data paths:

Use **Main menu → System and diagnostics** to open the install directory or current user-data directory. Settings, working tool state, tool presets, canvas presets and colors tolerate unknown fields and enum values. Missing, unreadable or out-of-range fields use local defaults while other preferences remain available. Reading does not rewrite files; later unrelated edits retain unknown data and unchanged original values for other app versions. Legacy number presets retain their original badge color and white text.

Damaged JSON, duplicate fields or an unreadable structure offers **Exit** to retain every file or **Backup and repair**. Repair first copies all five configuration files into a timestamped `configuration-backups` subdirectory under the current user-data directory, then resets only the unreadable file. A failed backup stops repair. During normal use, a separate backup confirmation appears only when saving must replace an original value this version could not read; cancelling retains the original file. Projects, canvas content, stamps and recovery data are retained. Full technical errors stay in local logs.

- Settings: `%LOCALAPPDATA%\FloatCanvas\settings.json`
- Latest recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.floatcanvas`
- Previous recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.previous.floatcanvas`
- Workspace history: `%LOCALAPPDATA%\FloatCanvas\recovery\history`
- Tool presets: `%LOCALAPPDATA%\FloatCanvas\tool-presets.json`
- Canvas presets: `%LOCALAPPDATA%\FloatCanvas\canvas-presets.json`
- Per-tool current state and selected presets: `%LOCALAPPDATA%\FloatCanvas\tool-state.json`
- Recent colors and My swatches: `%LOCALAPPDATA%\FloatCanvas\colors.json`
- Stamp library: `%LOCALAPPDATA%\FloatCanvas\stamps`
- Runtime diagnostics: `%LOCALAPPDATA%\FloatCanvas\diagnostics.log`
- Unhandled exceptions: `%LOCALAPPDATA%\FloatCanvas\errors.log`

If something appears to fail, note the time and steps, and keep `diagnostics.log`; also keep `errors.log` if an unhandled exception occurred. Enter `%LOCALAPPDATA%\FloatCanvas` in File Explorer's address bar to find them. Logs record capture strategy, readiness and frame acquisition, selection bounds, display information, cancellation/failure reasons, versions and exception stacks. Relevant input includes its category, time and whether this app suppressed it, distinguishing trailing key events from actual external actions. Logs do not save keystroke content, continuous pointer trails, canvas images or annotation text; exception details may contain file paths. Each log keeps the current file and two older files, `.1` and `.2`, each limited to 1 MiB. Custom profiles keep logs in their own directory.

When manually restoring a workspace whose latest recovery is damaged, the App tries that workspace's previous version. A read failure is reported without replacing the original file. Exiting a blank startup canvas without editing does not overwrite the earlier recovery. Recovery helps retrieve recent work; save important work as your own project file as well.

Closing the toolbar or entering pass-through does not exit the App. To quit, use the system tray or **Main menu → Exit FloatCanvas**. Startup with Windows is disabled by default.

## Controls, messages and personal resources

FloatCanvas uses the mouse for drawing and selection, with shortcuts to speed up actions. Within the app, `Tab` and `Shift+Tab` do not traverse buttons or form controls, and no additional dashed keyboard focus outline is shown. `Tab` retains its expand/collapse toolbar command. Windows system file dialogs retain native keyboard navigation. When Windows High Contrast is enabled, the interface follows the system window, text and selection colors; the usual light/dark preference resumes when it is disabled. Selected presets and stamp slots use a border without an overlaid check mark. Changing parameters from the selected preset adds an inset triangular marker matching the selection border at its upper-right corner, leaving the preview intact. Errors include text and a warning symbol.

Creating, deleting, overwriting, renaming and reordering tool presets, canvas presets and My swatches share the current canvas's edit history. Use `Ctrl+Z` to undo and `Ctrl+Y` or `Ctrl+Shift+Z` to redo in chronological order, or use the toolbar and Edit menu. Switching canvases uses the destination canvas's history; history does not survive an app restart. Undoing creation removes that preset, and redo restores it. Swatch history leaves recent colors unchanged. Text editors, including color values and preset names, retain their local text undo. A blank or whitespace-only name disables Save until corrected.

Commands become unavailable when there is nothing to undo/redo or the selection does not meet their requirements; hover for the reason. The Inspector reports the number of locked objects in a mixed selection and explains that those objects must be unlocked before editing properties. When a font is missing, the font field retains its original name and identifies the rendering fallback. Choosing an installed font explicitly replaces the stored name.

Click the toolbar status display to read and copy the last 20 complete messages from this session. Long messages do not animate when Windows interface animations are disabled. History and stamp previews load for the visible area. The stamp library retains continuous scrolling and distinguishes an empty library, no search matches and failed previews, with an appropriate next action. Narrow tool and canvas settings place presets below the parameters.

## Help, diagnostics and uninstalling

**Main menu → Help** contains the bundled offline user guide, shortcuts and **Report an issue**. Reporting first displays the app version, operating-system version and writing guidance. The browser opens only when you choose **Open issue page**; no content is sent or uploaded automatically.

Choose **Main menu → System and diagnostics → Export diagnostic bundle…** to preview the app version, system information, execution mode and redacted logs. You can exclude logs before saving a local ZIP file. The application does not upload it; inspect the bundle before attaching it to a report. The bundle excludes canvases, projects, stamps and configuration files. Original logs remain in the user-data directory.

**Installed app:** exit FloatCanvas, then uninstall it through Windows **Installed apps**. The uninstaller uses the current file inventory to remove application files, shortcuts, the installation entry, and startup/file-opening registrations still pointing at that installation. This includes application files added by ZIP updates. It preserves registrations owned by another copy and Windows `UserChoice`.

Cleanup runs inside the native uninstaller without depending on .NET or the application host. Any cleanup failures list unfinished items so you can resolve file locks or permissions and retry; partial cleanup is not reported as complete.

**Personal data is kept by default.** The uninstall dialog lists discovered default profiles by account and full directory. Only explicitly selected and separately confirmed directories have their settings, presets, stamps and drawing history deleted. An all-users uninstall can select individual discovered profiles and does not automatically delete every account's data. These profiles may also be shared with the ZIP edition: exit every FloatCanvas instance and back up important content first. Projects saved elsewhere, unrelated files in the application directory and custom `--profile` directories are retained. Silent uninstall keeps personal data unless `/DELETEUSERDATA` explicitly requests deletion of the executing account's default profile; this does not mean all users' profiles.

**ZIP:** before deleting the application directory, exit FloatCanvas and run `FloatCanvas.exe --unregister-integration` from that directory to remove its current-user startup and file-opening registrations. Once the command completes, delete the application directory.

The ZIP unregister command preserves other installations' registrations, Windows `UserChoice` and personal data; deleting the application folder does not delete `%LOCALAPPDATA%\FloatCanvas`. A custom `--profile` keeps data in the selected directory.

Building from source requires **.NET SDK 10.0.402**, or a later patch allowed by the repository's `global.json`, plus **Visual Studio C++ Build Tools**, including MSVC and a Windows SDK, for the native component. Running a published package requires only the desktop runtime described earlier. Both ZIP and EXE installers are unsigned and do not require publisher keys or code-signing certificates. Downloads and updates still validate SHA-256, file sizes and package structure.

## Support and limitations

The minimum system baseline is **Windows 10 1607 (build 14393) x64**, including LTSB 2016, LTSC 2019 and LTSC 2021; Windows 11 x64 is also targeted. Windows 10 22H2 is no longer required to start the app. Features that need newer Windows versions use a fallback or show a notice without blocking basic drawing.

Install a compatible **.NET 10 Windows Desktop Runtime x64** available for your system, and keep Windows and the runtime updated. The app's minimum baseline is separate from Microsoft's support commitments, which also depend on the Windows version, edition and lifecycle; see [.NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Testing has still mainly used Windows 11. Earlier Windows 10/LTSB/LTSC versions need testing on real systems; source inspection and compatibility-branch tests cannot replace that acceptance work.

New configurations default to all screens, with a primary-screen-only option. Complete cross-screen, mixed-DPI, portrait-display, hot-plug, lock-screen recovery and dual-4K behavior are still being refined. Behavior may vary across devices and applications.

FloatCanvas is intended for ordinary desktop and fullscreen applications without requiring windowed or borderless mode in every case. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

System Open/Save As dialogs use normal Windows input and foreground activation. Closing or cancelling returns to FloatCanvas editing. Opening a file dialog may take some games out of fullscreen.

When using window detection in Windows Snipping Tool or another screenshot app, the drawing layer may become the capture target. To keep annotations in the result, use FloatCanvas's own capture or export commands. To capture only the application underneath, use the Hide canvas / Show canvas shortcut (default `` Ctrl+Shift+` ``) to hide the canvas, take the screenshot, then show it again. Pass-through keeps annotations visible. Canvas visibility keeps the current edit/pass-through mode; use the separate mode shortcut to change input behavior.

HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside the current scope.

When a file is bound, a speech bubble above the toolbar shows its name and a separate Unsaved badge for unsaved edits. Hover for the full path. It folds with the toolbar. Its context menu lists Revert changes, Open file location, a separator, then Detach file. Revert changes reloads the last saved content from the bound file and is disabled when there are no unsaved edits. Reverting creates one undo step: Ctrl+Z restores the state before reverting, and Ctrl+Y redoes the revert. Detaching keeps the current content and undo history as an unnamed canvas.

Right-click a filled toolbar stamp slot for exactly four maintenance actions: Rename, Overwrite, Delete and Unpin. Use and editable-source actions remain in the stamp library. Unpin affects only that slot; deleting the stamp removes every binding to it.

### New canvas, clear and history versions

The gray page-plus toolbar button starts a new canvas with no file association. It restores the first official canvas preset by default, or keeps the current settings when **Keep canvas settings when creating a canvas** is enabled. New unbound drawings require a second click on the same button. Empty canvases, unchanged history copies and clean saved projects start a new canvas in one click. Edited bound projects immediately offer Save and leave, Keep in history and leave, or Cancel. The red broom beside New immediately clears drawing objects with no confirmation or history entry. It retains the background, grid, calibration and file association, and supports Ctrl+Z. The project file changes only when you save again.

History compares content with the version present when you entered a canvas, then archives on switching, starting a new canvas or exiting. Temporary work is kept automatically; edited projects remain eligible after saving. Opening without edits, blank canvases (including changes only to canvas settings) and duplicate content add no versions. Restoring creates an unnamed copy, retains the original history timestamp and position, and never automatically overwrites the source project. Further edits can create a new version at the top of history. Background recovery also persists empty states separately.

History limit defaults to 60 and accepts integers from 0 to 1000; the slider covers 0–200. Zero disables archived history versions. Saving settings or adding history removes the oldest excess versions. Current workspace recovery and recovery quarantine are excluded. Save a project file for long-term retention.

Stamp tooltips preserve the preview image aspect. Stamps with projected live effects use a captured thumbnail, which can differ from nominal plane-space dimensions; the preview stays undistorted and reports separate horizontal and vertical scales when needed.
