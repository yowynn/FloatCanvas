# 更新日志 / Changelog

## 未发布 / Unreleased

- 新增画布背景与透视校准面板：背景颜色和透明度独立于标注透明度；网格支持列行、显隐、颜色透明度、线宽和线型设置。拖动角点、边缘或内部校准平面，网格密集时自动减少可见线条；校准适用于形状、文字、图片和动图，截图按屏幕位置逆变换放置。新工程保存画布设置，旧 v1／v2 工程使用默认值。
- Added a canvas background and perspective calibration panel. Background color and opacity are independent of annotation opacity; configure grid rows and columns, visibility, color and opacity, line width and pattern. Drag corners, edges or the interior to calibrate the plane; dense grids thin automatically. Projection applies to shapes, text, images and animations, and screenshots are inverse-projected into their screen position. New projects save canvas settings; older v1/v2 projects use defaults.

- 校准参考网格使用全屏单像素黑白交替细线，预览自定义网格时进一步减淡，退出校准后隐藏参考线。画布面板复用前景色预设、图示线型及输入＋滑块控件；列行滑块按整数跳转。
- Calibration guides use single-pixel alternating black and white segments across the screen and dim beneath the configured grid preview, and guides disappear when calibration ends. The canvas panel reuses foreground presets, illustrated line patterns and input-plus-slider controls, with integer steps for rows and columns.

- 修复特定透视下列数或行数从 7 增至 8 时网格突然隔线消失的问题；远处压缩不再触发全屏抽稀，只跳过整个可见跨度均挤在一起的线条。
- Fixed grid lines suddenly disappearing when a row or column count increased from 7 to 8 under certain projections. Compression at a distant corner no longer thins the entire grid; only lines crowded along their whole visible span are skipped.

## [0.2.0-preview.3] - 2026-10-18

- 实时滤镜创建先等待有效画面，显隐保留同一画布的最后有效缓存，消除等待新帧时的灰色闪框；准备中取消不新增对象或撤销记录。实时采集改为新帧通知唤醒并只处理最新合格帧，目标跟随显示刷新率、最高 60 FPS，不承诺与下层应用同帧。
- Live filters wait for valid pixels before insertion and retain the same canvas's last valid cache across visibility changes, avoiding gray flashes while awaiting a new frame. Cancelling preparation adds no object or undo entry. Capture wakes on new-frame notifications and processes only the newest valid frame, targeting the display refresh rate up to 60 FPS without promising synchronization with the underlying application's frames.

- 实时滤镜优先使用会话级窗口排除，避免将画布从其他截图软件中一并排除；按实际接口能力检测，旧系统保留兼容路径与说明。动图复用两侧静态标注缓存，动图及桌面滤镜的逐帧更新不再重复刷新面板。
- Live filters prefer session-local window exclusion so the canvas remains available to other capture tools. Actual interface availability is checked; older systems retain the compatibility path and a notice. Animated images reuse cached static annotations on either side, and animation/live-desktop frames no longer repeatedly refresh panels.

- 新增默认开启的“优先使用 GPU 加速”设置：滤镜场景在 GPU 上绘制整幅画布，轻量场景保留 CPU 缓存以减少读回开销。保存后无需重启即可生效，GPU 不可用或失败时回退 CPU；设置显示最近一帧实际后端、选择原因、设备、绘制耗时及帧数，不因查看状态而触发绘制。解码、采集读回和窗口提交仍包含 CPU 工作，已有配置保留兼容。
- Added **Prefer GPU acceleration**, enabled by default. Filter scenes render the entire canvas on the GPU, while light scenes retain CPU caching to avoid readback overhead. Changes apply on save without restarting, and unavailable or failed GPU rendering falls back to the CPU. Settings shows the last frame's actual backend, selection reason, device, draw time and frame counts without triggering rendering. Decoding, capture readback and window submission still involve CPU work; existing settings remain compatible.

- 工具条显隐按钮以睁眼表示可见、琥珀色强调的闭眼表示隐藏，悬停提示显示下一步操作。
- The toolbar visibility button shows an open eye when visible and a closed eye with an amber accent when hidden; its hover tooltip describes the next action.

- 实时画布新增动态模糊，与动态马赛克一样持续处理桌面及更低图层对象，移动区域会重新采样；模糊边缘使用额外采样范围。工程、恢复与可编辑印章保留动态区域，导出和合并图片使用当时的静态结果。实时滤镜需要 Windows 10 2004；较早系统继续静态降级，应用最低基线仍为 Windows 10 1607。
- Added live blur alongside live pixelation on the live canvas. Both process the desktop and lower objects and resample when moved; blur samples beyond its visible edge. Projects, recovery and editable stamps retain live regions; exports and merged images use a static result. Live filters require Windows 10 2004, with static fallbacks on earlier systems and the application minimum unchanged at Windows 10 1607.

- GIF 与动态 WebP 可从原始文件、编码剪贴板数据、文件复制粘贴或拖放导入并播放；工程、恢复、内部对象复制及可编辑组合印章保留原始动图，重开印章库后盖出仍播放。PNG／Bitmap 来源只能得到静态图，不按网址联网追原图。隐藏画布停止刷新，穿透仍播放；播放不产生撤销或自动保存，按需解码使用有界缓存。截图、PNG／JPEG 导出和合并图片取操作时一帧，缩略图静态，暂无播放控制、播放进度持久化或动画导出。
- GIF and animated WebP play when imported from original files, encoded clipboard data, copied/pasted files or drag and drop. Projects, recovery, internal object copying and editable group stamps retain the original animation, including stamps placed after reopening the library. PNG/Bitmap sources remain static; URLs are not fetched for originals. Hiding stops refresh while pass-through continues playback. Playback adds no undo or recovery writes and uses bounded on-demand decoding. Snapshots, PNG/JPEG exports and merged images take one current frame; thumbnails remain static. Playback controls, position persistence and animated export are not available.

- “更多”收拢为模糊和马赛克；实色遮盖保留主菜单与原快捷键入口。裁剪移至主工具条导入、截图之后，仅单选一张可编辑且未锁定图片时启用；无选、多选、图文混选和锁定状态禁用，不在空选时裁剪背景。
- More now contains Blur and Pixelate. Solid redaction retains its main-menu and existing shortcut entries. Crop is on the main toolbar after import and capture, enabled only for one selected editable, unlocked image. It is disabled for empty, multiple, mixed or locked selections and does not crop the background on an empty selection.

- 背景截图保留当前编辑和输入会话，不再先退出绘制再恢复，也不依赖外部窗口名单决定返焦。优先从采集中排除浮绘界面；必要时仅短暂隐藏视觉界面，旧系统保留兼容路径。每次重新采集当前桌面，静止画面无需人为触发更新。采集失败时，健康的编辑会话继续保留；真正外切、退出、隐藏或画布变化会取消插入，不抢回前台。
- Background snapshots retain the current editing and input session instead of exiting and re-entering drawing, without external-window allowlists for focus restoration. FloatCanvas first excludes its interface from capture, briefly hiding visual surfaces when necessary and retaining a compatibility path for older systems. Each request captures the current desktop, including static content without requiring an artificial update. Capture failures retain a healthy editing session; actual application switches, exit, hiding or canvas changes cancel insertion without reclaiming focus.

- 新增有大小上限的本地运行诊断与异常日志，记录采集方式、准备和取帧阶段、取消／失败原因及异常堆栈，并区分被抑制的按键尾流与真正外部输入。意外失败提示日志位置，主动取消仍保持安静。
- Added bounded local diagnostic and exception logs for capture strategy, readiness and frame acquisition, cancellation/failure reasons and exception stacks, distinguishing suppressed trailing key events from actual external input. Unexpected failures show the log location; intentional cancellation remains quiet.

- 截图改为实时画布上的原位静像：先选矩形、椭圆、自由套索或多边形套索，完成后才采集并作为普通图片插入，不再新建独立冻结草稿。可选仅背景、背景＋画布、保留透明度的仅画布及图片边框；保持截图工具便于连续操作。原有对象、旧冻结工程和历史草稿保留。
- Capture now places a static snapshot on the live canvas: select a rectangle, ellipse, freehand lasso or polygon lasso before capture, then insert an ordinary image without creating a separate frozen draft. Choose background only, background + canvas, or canvas only with transparency, plus an optional image border. The capture tool stays active for consecutive captures. Original objects, older frozen projects and history drafts are retained.

- 序号与 Emoji 新增拖动确定插入尺寸，实时预览并整体等比缩放；单击仍按配置尺寸居中插入，拖动不改写工具配置。松开后一次提交，取消不跳序号；保留选中后调整及 Ctrl 重叠创建。
- Numbered markers and emoji can now be sized by dragging, with a live preview and proportional scaling of the whole object. Clicking still inserts at the configured size, centered on the click, and dragging leaves tool settings intact. Release commits one operation; cancellation does not skip a number. Selection editing and Ctrl-based overlapping creation remain available.

- 修复恢复历史画布并在保存确认中选择“不保存”后意外退出绘制的问题。恢复、取消或失败保留原编辑／穿透模式；中途切到其他应用或主动退出时不抢回前台。历史列表新增保存时间和透明画布缩略图，仅保留浮绘自身标注；旧历史按需生成预览，预览不可用不影响恢复。损坏图片导致可选缩略图生成失败时，也不会阻止有效恢复存档的保存。
- Fixed restoring workspace history unexpectedly leaving edit mode after choosing **Don't save**. Restoration, cancellation and failures retain the original edit/pass-through mode without reclaiming focus after you switch applications or explicitly leave editing. History now shows save times and transparent canvas thumbnails containing only FloatCanvas annotations. Older history gets previews on demand, and unavailable previews do not prevent restoration. A failed optional thumbnail, including corrupt image data, no longer blocks saving otherwise valid recovery data.

- 统一内部窗口与弹层的焦点交接，修复印章重命名后关闭印章库可能使工具条消失、意外退出绘制的问题。点击画布会同时收起相关临时子层和未固定面板，并保留原始画布操作；固定面板保留，模态对话框仍需确认或取消。切换到其他应用仍释放输入，迟到的异步回调不抢回前台。
- Unified focus handoffs between app windows and popups, fixing the toolbar disappearing and editing ending unexpectedly after renaming a stamp and closing the library. Clicking the canvas dismisses related temporary child panels and unpinned panels together while retaining the original canvas action. Pinned panels remain open, and modal dialogs still require confirmation or cancellation. Switching to another application releases input; late asynchronous callbacks do not reclaim focus.

- 修复按住 Ctrl 从已有选框内连续绘制时，新图形未创建、原对象反而被移动的问题。拖动从按下鼠标时确定为创建或调整，中途松开 Ctrl 不改变本次操作。
- Fixed Ctrl-drawing from inside an existing selection moving the selected object instead of creating a new one. Each drag keeps the create-or-adjust action chosen at mouse-down, even when Ctrl is released before the mouse button.

- 修复通过 `=`／`+` 快捷键新建印章时，触发按键或长按重复字符覆盖默认名称的问题。工具条、画布和工具参数窗口入口均保留默认名称；松开快捷键后可正常输入。
- Fixed `=` / `+` stamp-creation shortcuts replacing the default name with the triggering character or its held-key repeats. Toolbar, canvas and tool-options entry points retain the default name; normal typing works after releasing the shortcut.

## 0.2.0-preview.2 — 2026-10-17

- Windows x64 最低启动基线降至 Windows 10 1607（内部版本 14393），包含 LTSB 2016、LTSC 2019 和 LTSC 2021，移除原先 22H2 的过高门槛。较早系统采用兼容的 DPI 与截图路径；动态桌面马赛克仍需 Windows 10 2004，新建区域在旧系统使用静态截图效果，已有动态区域以实色覆盖并提示。冻结截图和图片效果继续可用；较早 Windows 10 实机验证仍待补充。
- Lowered the Windows x64 startup baseline to Windows 10 1607 (build 14393), including LTSB 2016, LTSC 2019 and LTSC 2021, removing the overly restrictive 22H2 requirement. Earlier systems use compatible DPI and capture paths. Live desktop pixelation still requires Windows 10 2004; newly created regions use static snapshots on earlier systems, and existing live regions use solid coverage with a warning. Frozen-screenshot and image effects remain available. Testing on earlier Windows 10 systems is still pending.

- 各工具的基础预设与完整当前参数自动保存，切换工具、关闭参数窗口、切换模式及退出重启不再还原；从工具条重新进入箭头或圆角矩形时也保留修改。官方基线与预览同样持久保留，显式重选预设才恢复基线。临时调整不覆盖用户预设或已有对象，窗口布局仍每次启动重置。
- Each tool now saves its base preset and complete current parameters across tool switches, options-window closure, mode changes and app restarts, including when returning to arrows or rounded rectangles through the toolbar. Official baselines and previews persist too; explicitly reselect a preset to restore its baseline. Temporary changes leave personal preset definitions and existing objects intact, while window layouts still reset on startup.

- 修复三位及更长序号换行、超出标记的问题；数字按单行自动缩小适配，保留标记大小与设置字号，画布和导出一致。
- Fixed three-digit and longer numbered labels wrapping or overflowing their badges. Labels shrink to fit on one line, keeping the marker size and configured font size, consistently on the canvas and in exports.

- 官方工具默认前景色改为色板红（`#E03E3E`），填充保持透明；已有对象、用户预设和已保存的工具参数不变，界面选中强调色仍为蓝色。
- Official tool defaults now use the palette's red (`#E03E3E`) foreground with transparent fill. Existing objects, personal presets and saved tool parameters are retained; the interface selection accent stays blue.

- 工具条“新建画布”改为红底删除图标的“清空画布”：首次点击显示确认对号和红底白字的“确定清除吗？”气泡，再次点击执行，也可快速双击。点击其他位置、按 Esc 或退出编辑取消确认；保留清空前归档和历史恢复。
- Replaced the toolbar's New canvas action with a red **Clear canvas** delete button. Click once for a confirmation checkmark and a red **Clear canvas?** bubble with white text, then again to clear; a quick double-click also works. Click elsewhere, press Esc or leave edit mode to cancel. Archiving before clearing and workspace-history restoration are retained.

## 0.2.0-preview.1 — 2026-10-17

本版本更新：

- 欢迎页版本号可打开检查更新面板，自动查询本项目的 GitHub Release，并常显项目链接。支持下载进度、取消、校验和应用内安装；完整 ZIP 位于用户可写的本地目录时，可自动替换程序、重启并恢复画布。正式版只接收正式更新，预览版也接收更新的预发布版本。

- 修复首次切换工具时继承其他工具临时颜色和线型的问题；各工具独立初始化并保留自身参数，官方预览不随临时编辑变化。统一官方与个人预设标题下方的留白。

- 统一各级小色块的直角弱描边，并按当前 DPI 对齐尺寸与间距；颜色填满通用圆角按钮内侧。二级色板每排十色，官方与个人工具预设每排五项，滚动条出现后仍保留完整列宽。个人预设区域最少四排，并随参数面板高度伸展；更多预设在区域内滚动。

- 默认快捷键使用 Ctrl＋抑音符号切换编辑／穿透，Ctrl＋Shift＋抑音符号切换画布显隐，保留已有自定义绑定。编辑与显隐独立，隐藏时不能添加内容。每次启动显示精简欢迎页与红色爱心署名，窗口布局在重启后复位。
- 两排紧凑工具条保留选择／橡皮与十项绘图入口，下排提供印章库、新建印章和十格快捷槽；上下留白与两排间距统一为 2 DIP，保持顶边位置并收紧底部，液晶屏与下排对齐，按钮和槽位统一为 3 DIP 圆角。空槽、已绑定槽与选中槽的边框逐级增强，空槽和已绑定槽使用较轻的中性轮廓，选中蓝框保持清楚。上排角标位于右上，下排位于左下，所有角标可统一显隐；M 打开更多，主键盘 -／+ 打开印章库／保存选择为印章。
- 提高工具条快捷键角标的清晰度，修复 150% 缩放时角标被裁剪，缩窄底色遮罩以保留相邻边框；统一绘图工具图标的有效视觉大小，并改善印章槽边框和缩略图的辨识度。
- 工具参数改为可自由拖动的独立窗口，标题使用当前工具名。初次打开靠近工具条，工具条收起时可在鼠标附近召唤；移动或收起工具条不移动面板。Esc 逐层关闭颜色子面板与未固定面板。修复子面板关闭、命名和主题应用导致父面板或工具条消失的问题。
- 官方与我的预设统一为小方形预览，悬停显示名称；我的预设只显示当前工具并在独立区域内滚动，每个工具至少有一个官方预设。点击立即应用，参数修改保留选中，再点可还原；支持左键拖排、右键管理和统一命名窗口，删除选中项回到首个官方预设。
- 颜色主行新增两排十色直达，色值按钮使用完整颜色底与对比文字。二级面板保留色谱、色相、透明度与最近 20 色，关闭时自动应用；新增可命名、拖排和右键管理的我的色板。颜色控件、数值和下拉输入不触发绘图快捷键。
- 自由画笔、荧光笔和印章绘制后不自动选中；线条及箭头仅拖动创建。形状、文字、序号和表情支持连续插入后直接调整，Ctrl 可重叠创建。线型与箭头选项增加图示，线宽增加视觉预览。
- 矩形、椭圆和符号的非等比调整修改真实几何并保留线宽与圆角；符号支持默认宽高。文字及序号提供系统字体选择与预览，序号字号影响实际尺寸，支持开始值、步进和独立保存在画布中的当前计数；表情使用“大小”与更大的滑块范围。
- 实时画布新增动态马赛克区域，从更多工具打开，持续处理其下的桌面和浮绘元素，可移动、旋转、缩放并保存工程；采集不可用时实色覆盖。截图及图片保留静态效果，导出、图片复制和合并印章使用操作时快照。存在可见动态区域时，外部截图可能不包含其他标注；应用内导出保留完整合成，删除或隐藏动态区域后恢复正常捕获。
- 清空入口改为新建画布，先归档当前工作区，再从空白开始；可从历史恢复。正常启动保持空白，提交内容自动保存恢复数据；修复退出时取消恢复写入可能引发异常的问题。
- 使用简化的小图灵托腮握笔透明图标，PNG／ICO 按主体等比撑满。角色向左上放大，皇冠超出条身并覆盖左侧圆角；支持单击菜单、双击展开／收起与拖动工具条。状态屏使用灰绿／暗棕的计算器式配色，长信息悬停滚动。
- 属性与图层使用统一圆角悬浮面板、匹配的帮助／关闭图标和双斜线缩放角，移除最小化与最大化，修复 Windows 11 圆角重复描边。属性连续显示适用字段，底部应用／重载保持可见；图层支持显示、锁定、排序、重命名、编组与解组。
- 新增可编辑及合并图片印章、保留的源对象、搜索管理、十格数字快捷栏和大图悬停预览。新印章自动填入首个空槽；放置保持保存尺寸。
- 工程格式升级为 v2，继续读取 v1，新保存使用 v2；旧版明确拒绝不兼容格式，避免重存丢失新信息。改善系统勿扰及 ShareX 窗口识别兼容；Windows 自带截图的窗口识别仍有已知限制。

Changes in this version:

- Click the welcome-screen version to check this project's GitHub releases, with a persistent project link. Added download progress, cancellation, verification and in-app installation. Complete ZIP packages in a writable local folder can replace the app, restart and restore the canvas automatically. Stable versions receive stable updates; previews also receive newer prereleases.

- Fix first-use tools inheriting another tool's temporary color and line pattern. Tools initialize independently and retain their own state; official previews remain stable during edits. Match the spacing below official and personal preset headings.

- Small color swatches share square corners, subtle outlines and DPI-aware pixel spacing. Color fills the inside of rounded value buttons. Secondary palettes fit ten colors per row, and tool presets fit five tiles, including when scrollbars are present. The personal preset viewport accommodates at least four rows and expands alongside taller tool options; additional presets scroll within it.

- Default shortcuts use Ctrl plus backtick for edit/pass-through and Ctrl+Shift plus backtick for visibility, retaining custom bindings. Editing and visibility are independent; hidden canvases cannot accept new content. A concise welcome with a red-heart credit appears on startup, and window layout resets after restart.
- The compact two-row toolbar includes Select/Eraser, ten drawing entries, stamp management and ten stamp slots. Top, middle and bottom inner spacing is 2 DIP, tightening the bottom while keeping the top fixed; the status display aligns with the lower row, and buttons and slots use matching 3 DIP corners. Slot outlines progress from faint empty slots through moderate assigned slots to the unchanged selected blue frame. Hints sit at the upper right or lower left and can all be hidden together. M opens More; main-keyboard -/+ opens the stamp library or saves the selection as a stamp.
- Improved toolbar shortcut readability, fixed clipped hints at 150% scaling, and narrowed their background masks to preserve neighboring frames. Balanced the visible size of drawing-tool icons and improved stamp-slot outlines and thumbnails.
- Tool options use a freely movable window titled with the current tool. It initially opens near the toolbar, or near the pointer when the bar is collapsed; toolbar movement or collapse leaves it in place. Escape closes the color child panel before an unpinned tool window. Fixed child-panel, naming and theme actions dismissing parent panels or the toolbar.
- Official and personal presets share compact preview tiles with names on hover. Personal recipes are filtered by tool and scroll within their own viewport; every tool has an official default. Click to apply, retain selection while editing, and click again to restore. Added left-drag reordering, context management and a shared name dialog; deleting the selected personal recipe applies the first official one.
- Color inputs offer ten quick colors beside a full-color value button with contrasting text. The secondary picker retains spectrum, hue, opacity and the 20 most recent colors, applying drafts when it closes. My swatches supports naming, reordering and context management. Input in color, numeric and dropdown controls does not trigger drawing shortcuts.
- Freehand, Highlighter and stamps remain unselected after drawing; lines and arrows require dragging. Shapes, text, numbers and emoji support consecutive insertion and immediate adjustment, with Ctrl for overlapping creation. Line patterns and arrowheads include diagrams; stroke width has a visual preview.
- Non-proportional rectangle, ellipse and symbol editing changes actual geometry while retaining stroke width and corners. Symbols have default dimensions. Text and numbered markers offer system-font previews; marker font size affects actual dimensions, with a starting value, increment and document-owned current counter. Emoji uses a Size label and a larger slider range.
- Added live pixelation regions through More. They continuously process the desktop and FloatCanvas elements underneath, support transforms and project saving, and use solid coverage when capture is unavailable. Screenshots and image effects remain static; export, image copying and merged stamps use operation-time snapshots. While live regions are visible, external capture may omit other annotations; use in-app export for the complete composite. Removing or hiding live regions restores normal capture behavior.
- Replaced Clear with New canvas, archiving the current workspace before starting blank. Restore it through workspace history. Normal startup stays blank, with automatic recovery saves for committed edits. Fixed an exception when shutdown cancels a recovery write.
- Added the simplified transparent Little Turing mascot, fitted proportionally in PNG/ICO. It extends above and left of the bar, covering its left corners, with click-menu, double-click collapse and drag movement. The scrolling status display uses calculator-inspired gray-green and dark-brown colors.
- Properties and Layers share rounded floating panels, matching help/close icons and a small diagonal resize grip. Removed minimize/maximize and duplicate Windows 11 corner outlines. Properties keeps applicable fields together and Apply/Reload visible; layers support visibility, locking, ordering, naming, grouping and ungrouping.
- Added editable and flattened stamps, retained editable sources, library search/management, ten number-key slots and larger hover previews. New stamps fill empty slots automatically and retain their saved placement size.
- Upgraded projects to v2 while retaining v1 reading support; older apps reject incompatible formats to prevent data loss. Improved Do Not Disturb and ShareX window-detection compatibility. Window detection in Windows Snipping Tool still has a known limitation.

## 0.1.1-preview.9 — 2026-10-16

- 默认 Windows Release 包不再附带 .NET 运行时，并排除调试符号文件（PDB），减小下载与解压体积。
- 启动需要兼容的 .NET 10 Windows Desktop Runtime x64。缺少时显示 .NET 安装提示，用户可前往微软下载并安装，完成后再次启动浮绘。
- 发布时仅保留简体中文和英文的依赖语言资源。

- The default Windows Release package no longer bundles the .NET runtime and excludes debug symbols (PDB files), reducing download and extracted size.
- A compatible .NET 10 Windows Desktop Runtime x64 is required. If it is missing, a .NET installation prompt links to Microsoft's download page; install the runtime, then start FloatCanvas again.
- Dependency language resources are limited to Simplified Chinese and English when publishing.

## 0.1.1-preview.8 — 2026-10-16

- 直接向画布拖入图片，取代临时“拖入图片…”窗口。拖住图片时按切换快捷键展开画布，松手后图片中心对准落点；多图略微错开，可整体撤销。
- 文件选择和剪贴板粘贴继续在可见画布中央插入图片。网页链接不作为图像导入，浏览器拖入取决于来源提供的图像内容。

- Drag images directly onto the canvas instead of using the temporary **Drag in images…** window. Hold the image, use the toggle shortcut to open the canvas, and release to center it on the drop point. Multiple images are offset slightly and can be undone together.
- File-picker and clipboard imports remain centered in the visible canvas. Web links are not imported as images; browser dragging depends on the image content provided by the source.

## 0.1.1-preview.7 — 2026-10-15

- 修复操作画布后工具条无法点击的问题。
- 透明 PNG 图片导入时保留原始图片精度。

- Fixed the toolbar becoming unclickable after interacting with the canvas.
- Preserve original image precision when importing transparent PNG images.

## 0.1.1-preview.6 — 2026-10-15

- 自定义快捷键后，工具提示、辅助访问名称和已打开的欢迎说明同步显示当前组合。
- 使用指南补充被系统或其他应用截获的快捷键如何处理。

- Tooltips, accessibility names and an open welcome screen now show the current custom shortcuts.
- The user guides explain combinations intercepted by Windows or another application.

## 0.1.1-preview.5 — 2026-10-15

- 新增临时“拖入图片…”窗口，可在操作桌面时向原画布或截图草稿导入图片。
- 图层列表可单独收起，保留属性面板中的输入。
- 改善自动发布失败后的任务重试，保持构建产物与发布内容一致。

- Added a temporary **Drag in images…** window for importing into the original canvas or capture draft while using the desktop.
- The layer list can be collapsed independently while retaining property input.
- Improved workflow retries after a release failure while keeping publication tied to its verified build artifact.

## 0.1.1-preview.4 — 2026-10-15

- 裁剪及图片效果要求单选一张未锁定图片，避免误改截图背景。
- 从外部剪贴板粘贴图片后自动切换到选择工具，可直接移动图片。
- “移回可见屏幕”保留锁定、隐藏对象及截图裁剪框，维持对象间距。
- 截图草稿完全落在活动屏幕外时，自动把视图平移回主屏，保留原图与标注。

- Cropping and image effects require one unlocked image, preventing unintended edits to the screenshot background.
- Pasting an external clipboard image switches to selection so it can be moved immediately.
- Moving annotations into view preserves locked and hidden objects, object spacing and the screenshot crop.
- Screenshot drafts completely outside active screens are brought back onto the primary screen without changing the source image or annotations.

## 0.1.1-preview.3 — 2026-10-15

- 编辑文字或序号时，若目标随后被锁定或删除，提交会保留输入并说明原因；解锁或撤销删除后可重试。

- When a text or number target is locked or deleted while its editor is open, submitting keeps the draft and explains the problem. Unlock the target or undo its deletion to retry.

## 0.1.1-preview.2 — 2026-10-15

- 新增自动、简体中文和英文界面语言设置，保存后重启生效。界面语言不改变用户内容或输入法。
- 提供中英文 README 和使用指南。
- 默认仅在主屏绘图和截图，也可在设置中选择全部屏幕。
- 改善编辑时的键盘接管、图片导入后的快捷键焦点及截图工具条显示。
- 提供项目 MIT 许可证和按版本 tag 打包发布的工作流。

这是预览版本。Windows 10、物理双屏、完整全屏应用兼容和长期稳定性仍需更多验证。

- Added Automatic, Simplified Chinese, and English interface settings, applied after restarting. Interface language does not change user content or input methods.
- Added Chinese and English READMEs and user guides.
- Drawing and capture use the primary screen by default; all-screen mode remains available in Settings.
- Improved keyboard ownership while editing, shortcut focus after importing an image, and capture toolbar visibility.
- Added the project's MIT license and a version-tag release workflow.

This is a preview release. Windows 10, physical dual-monitor use, broader fullscreen compatibility, and long-term stability need further validation.

## 0.1.1-preview.1 — 2026-10-15

- 增加主屏工作范围设置，保留标注的原始位置。
- 改善文字续编、截图处理、图像导出和编辑状态保留。

- Added primary-screen scope while preserving annotation coordinates.
- Improved text re-editing, capture processing, image export, and editing-state continuity.

## 0.1.0 — 2026-10-15

- 初始 Windows 预览：屏幕标注、工具条、对象编辑、贴图、截图与工程保存。

- Initial Windows preview with screen annotation, a floating toolbar, editable objects, images, capture, and project saving.
