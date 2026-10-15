# FloatCanvas user guide

[简体中文](User-Guide.md) | English

This guide covers **0.1.1-preview.9** for Windows x64. Features and stability are still being refined; save important work as a project file.

A drag that starts on the canvas stays with the canvas, even when it crosses the toolbar or ends on a button. Buttons are not triggered on release. A gesture that starts on the toolbar stays with the toolbar and does not draw on the canvas.

The interface supports automatic Windows language selection, Simplified Chinese and English. Choose a language in Settings, save and restart to apply it.

FloatCanvas keeps annotations and images visible over the screen. Enter edit mode to draw or adjust them. Switch to pass-through mode to click, drag and scroll in the application underneath while your annotations remain visible.

## Start and switch modes

For the packaged app, extract the Windows x64 ZIP for your version, keep all files together, and run `FloatCanvas.exe`. The default package excludes the .NET runtime and debug symbols (PDB files). It starts directly when a compatible **.NET 10 Windows Desktop Runtime x64** is installed.

If a compatible runtime is missing, the launcher displays a .NET installation prompt. Click **Download** to open Microsoft's download page, download and run the **Windows Desktop Runtime x64** installer, then run `FloatCanvas.exe` again. You can also select it on the official [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). The SDK is not required; the plain .NET Runtime, ASP.NET Core Runtime and .NET Framework do not replace the required desktop runtime.

1. Choose **Start drawing** on the welcome screen, or press `Ctrl+Alt+F` to enter edit mode.
2. Press `Ctrl+Alt+F` again for pass-through. The toolbar disappears, annotations stay visible, and the application underneath receives input again.

Annotations stay at screen coordinates. They do not follow web scrolling or objects in a game.

The default drawing scope is **Primary screen only**, limiting annotations and capture to that screen. Applications on secondary screens can keep running; switching to another application enters pass-through.

Open **More → Settings…** and change **Drawing scope** to **All screens** for the next launch. The startup options `--primary-screen` or `--all-screens` override the scope for that run without changing the saved preference; do not use both together. Complete cross-screen and mixed-DPI behavior is still being refined. Objects outside the active screen keep their coordinates; use **Move annotations into view** to bring them back.

In edit mode, FloatCanvas receives normal window keyboard and mouse input. An unmapped letter should not be sent to the application underneath. Switching to another application hides editing tools and enters pass-through without removing annotations. If FloatCanvas cannot acquire the required input focus, it stays in pass-through and explains why.

Uncommitted text is retained when focus moves away and is restored when you resume editing the same canvas. Explicit cancellation or switching to pass-through closes the text editor.

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+F` | Switch between edit and pass-through |
| `Ctrl+Alt+Shift+F` | Hide/restore all annotations; hiding also releases input |
| `Esc` | Cancel the current operation first; otherwise enter pass-through |
| `Ctrl+Z` / `Ctrl+Y` | Undo/redo; `Ctrl+Shift+Z` also redoes |
| `Ctrl+A` | Select all editable elements |
| `Ctrl+C` / `Ctrl+V` | Copy/paste |
| `Ctrl+D` | Duplicate selected elements with a small offset |
| `Delete` | Delete selected, unlocked elements |
| Arrow keys / `Shift` + arrow keys | Move selected elements by 1/10 document pixels |
| `Ctrl+S` / `Ctrl+Shift+S` | Save/save the current project as another file |
| `Ctrl+O` | Open a project |

Open **More → Settings…** to change shortcuts or the light/dark theme. A registration conflict detected when saving displays a reason and preserves the previous working shortcut. Windows or another application may intercept a combination before the recorder receives it; choose another combination if the field does not change. Drag the toolbar to reposition it. The system tray also offers editing, visibility, settings and exit commands.

Toggle shortcuts use Ctrl/Alt/Shift combinations. Win and system navigation combinations remain available to Windows; `Ctrl+Alt+Esc` is reserved for emergency input release. An unresponsive editing UI or interrupted input ownership returns to pass-through; earlier drawing keys are not replayed into the application underneath.

## Interface language and input methods

Choose **More → Settings… → Language / 界面语言**, save, and restart FloatCanvas to apply the change:

| Setting | Interface language at startup |
| --- | --- |
| Automatic (Windows) (`auto`, default) | Use Simplified Chinese for Chinese Windows display languages; fall back to English for other display languages |
| 简体中文 (`zh-CN`) | Simplified Chinese |
| English (`en`) | English |

Interface language does not translate or rewrite annotations, project text, user filenames or clipboard content. It does not change your Windows keyboard layout or input method. You can enter Chinese with an English interface or English with a Chinese interface; no particular input method or double-pinyin scheme is required.

The App's language selection applies to its own toolbar, menus, settings, messages, file-dialog titles and file-type filters. Windows-owned **Open**, **Save** and **Cancel** buttons may still use the Windows display language. Changing the App's language does not change Windows language settings.

## Draw, select and adjust

Choose a pen, highlighter, line, arrow, double arrow, rectangle, rounded rectangle, ellipse, triangle, text, numbered marker or eraser from the toolbar's tool list. Adjust color and stroke width directly on the toolbar.

Common tool keys: `V` select, `B` freehand, `L` line, `A` arrow, `R` rectangle, `O` ellipse, `T` text, `N` numbered marker and `E` eraser. While editing text, these letters are treated as text input.

- **Select:** click an element, or drag a selection box from an empty area. Shift-click to add or remove an element from the selection.
- **Move and resize:** drag an element to move it; drag a corner handle on its selection box to resize.
- **Rotate:** drag the rotation handle above the selection box. Hold `Shift` to snap in 15° steps.
- **Line endpoints:** select one line or arrow, then drag an endpoint.
- **Text:** choose the text tool and click the canvas. Enter multiple lines and choose **Done** or press `Ctrl+Enter`. Double-click text in selection mode to edit it again.
- **Numbered markers:** each click adds the next number. Deleting a marker does not renumber the others.
- **Erase:** the eraser removes whole elements. One erase gesture can be undone as one operation.

Every basic element remains a selectable object, including freehand lines, text, numbered markers and images.

Choose **Properties and layers** to open the panel. With one element selected, edit its position, size, angle, stroke, fill, opacity and applicable text settings, then choose **Apply properties**. The whole application of properties can be undone in one step.

Colors accept `#AARRGGBB`, such as `#FF2383E2`; `#00000000` is a fully transparent fill. X/Y refer to the top-left of the element's screen-aligned bounding box. Width and height are measured along the element's own axes, including existing scale but excluding the stroke. Resizing through these fields preserves rotation; with proportional resizing, you can change only width or height. If the geometry has zero size along an axis, adjust its endpoints before changing that dimension. Arrowheads can be filled triangles, open arrows or diamonds; a head size of 0 scales automatically with stroke width.

Elements higher in the layer list appear in front. Click **Layers** to collapse or expand the list while retaining your property input. Hidden or locked layers can still be selected in that list to change their visibility/lock state or stacking order. Hiding the properties panel retains unapplied input. After selecting another element, return to the original element to apply its draft, or choose **Reload selection** to load the newly selected element.

The toolbar's overall opacity affects annotations and images, while the screenshot background keeps normal brightness. If a display change leaves annotations outside the visible screen, use **More → Move annotations into view**. This moves visible, unlocked objects while preserving their spacing and the screenshot crop.

## Place, replace and crop images

Press `Ctrl+V` to paste a clipboard image, or choose **Image** to select one or more files. New images are placed at the center of the currently visible canvas area. PNG, JPEG, BMP and WebP are supported; transparent images retain their transparent areas.

You can also start dragging an image from a file manager or browser, keep holding it, and press the edit/pass-through shortcut (`Ctrl+Alt+F` by default) to open the canvas. Release the mouse at the desired position. The image is centered on the drop point; when dropping several images, subsequent images are offset slightly. The entire import can be undone in one step.

The source must provide actual image data or supported image files. A URL or web link alone cannot be imported as an image. Websites and browsers may provide different drag content, so direct dragging is not guaranteed for every website. If dragging is unavailable, use **Copy image** and paste, or save the image and import the file.

An imported image is a separate object and does not automatically replace the background. Select it to move, rotate, resize or change opacity.

- **Replace source image:** select one image and use **More → Replace selected image…**. Its position, size, crop and effect regions are retained.
- **Crop image:** select the image, choose **Crop**, and drag the area to keep. The original asset is retained, and cropping can be undone.
- **Blur / pixelate / solid redaction:** select an image, choose the corresponding tool, and drag an area inside it. In a frozen screenshot, effects apply to the screenshot background when no image object is selected.

Cropping and image effects work on one unlocked image at a time. Select a single image or unlock it before using these tools.

Effects move and transform with their target image. They do not process the live desktop underneath. Undo can reverse effects and cropping.

## Capture and return to the live canvas

1. Choose **Capture**. After the desktop freezes, drag to select a capture region. You can also capture the current drawing scope directly from **More**.
2. Continue using the same tools for drawing, images, text and effects. Existing annotations are copied into a separate screenshot draft.
3. Use **Crop** to change the screenshot region. If an image is selected, cropping applies to that image instead; clear the image selection before cropping the screenshot canvas.
4. Use **More → Export composite image…** for PNG/JPEG, or press `Ctrl+C` with no elements selected to copy the composite result.
5. Use **More → Return to live canvas** to restore the original canvas. **Resume capture draft** reopens the draft.

Editing a screenshot does not change the original live canvas. Pressing `Ctrl+Alt+F` during screenshot editing retains the draft, returns to the live canvas and enters pass-through so you do not operate the desktop through a frozen picture.

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

Committed edits trigger a recovery save after about one second. The App waits for pending recovery writes before exiting. On the next launch, it attempts to restore the live canvas and screenshot draft, starting in pass-through. Undo history is not retained across restarts.

Default data paths:

- Settings: `%LOCALAPPDATA%\FloatCanvas\settings.json`
- Latest recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.floatcanvas`
- Previous recovery: `%LOCALAPPDATA%\FloatCanvas\recovery\workspace.previous.floatcanvas`

If the latest recovery is damaged, the App tries the previous version. A read failure is reported without replacing the original file. Recovery helps retrieve recent work; save important work as your own project file as well.

Closing the toolbar or entering pass-through does not exit the App. To quit, use the system tray or **More → Exit FloatCanvas**. Startup with Windows is disabled by default.

## Support and limitations

The current preview is primarily intended for Windows 11 x64. Windows 10 22H2 is a compatibility target; complete compatibility has not yet been confirmed.

Primary-screen scope is the default. Complete cross-screen, mixed-DPI, portrait-display, hot-plug, lock-screen recovery and dual-4K behavior are still being refined. Behavior may vary across devices and applications.

FloatCanvas is intended for ordinary desktop and fullscreen applications without requiring windowed or borderless mode in every case. Fullscreen compatibility depends on the application and display mode; not every exclusive-fullscreen scenario is guaranteed.

HDR color accuracy, pressure-sensitive pens, touch, OCR, recording, scrolling capture, cloud synchronization and uploads are outside v1.
