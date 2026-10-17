# 更新日志 / Changelog

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
