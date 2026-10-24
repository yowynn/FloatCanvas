# 更新日志 / Changelog

## [Unreleased]

- 保存及另存为完成后立即刷新文件绑定气泡，无需继续操作画布。
- Refresh the bound-file bubble immediately after Save or Save As completes.

- 历史画布支持双击列表项恢复；打开历史时关闭欢迎界面，成功恢复后显示画布并进入编辑模式。
- Restore canvas history by double-clicking an entry; close Welcome when opening history and enter editing with the restored canvas visible after success.

- 手动关闭工具配置后重置固定状态；退出编辑时记住打开的固定窗口并在下次进入时恢复，图层窗口始终按固定窗口处理。窗口层级遵循最近焦点及父子关系，标题按钮统一为帮助、固定、关闭。
- Reset tool-options pins on manual close; restore open pinned windows on the next editing session and treat Layers as pinned. Stack windows by recent focus and ownership, with Help, Pin and Close in caption order.

- 放大新画布二次确认气泡与文字，使用连续描边连接气泡和尾巴。
- Enlarge the new-canvas confirmation hint and text, joining its body and pointer with one continuous outline.

- 任务栏和托盘始终使用戴皇冠图标，仅按画布显隐切换睁眼／闭眼；悬浮头像继续区分编辑与穿透模式。
- Keep crowned taskbar and tray icons, switching only between open and closed eyes with canvas visibility; floating launchers retain their mode-specific artwork.

- 修复颜色二级面板点击所属面板时未收起的问题，保留父控件首次点击、内部编辑及子对话框；颜色面板可通过标题区域拖动。
- Fix secondary color panels staying open after a click in their parent panel, preserving the parent click, internal editing and owned dialogs. Color panels can be dragged by their title area.
- 移除预设与印章槽选中勾号；预设参数改动以选中框角落的小圆点提示，不再遮挡图标。
- Remove selected check marks from presets and stamp slots. Indicate modified preset parameters with a small dot at the selection-border corner instead of covering the preview.

- 清理预设撤销按钮移除后的颜色弹窗留白：空错误提示不再占位，真实错误按内容展开，修正后恢复紧凑高度。
- Remove empty footer space from color popups after preset-undo removal. Error messages expand only when needed and collapse again after correction.

- 移除工具、画布和颜色面板的独立“撤销预设更改”按钮与历史栈；预设的新建、删除、覆盖、重命名和排序统一加入当前画布的撤销／重做历史，支持 Ctrl+Z、Ctrl+Y 和 Ctrl+Shift+Z。
- Remove separate preset-undo buttons and stacks from tool, canvas and color panels. Preset creation, deletion, overwrite, rename and ordering now share the current canvas's undo/redo history through Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z.

- 官方与个人工具预设统一应用完整、确定的参数；旧个人预设缺少的新增参数恢复为默认值。官方预览固定，移除首次选用时继承颜色、线宽并保存为官方基线的行为，旧官方缓存不再影响应用；各工具当前工作状态继续独立保存。
- Apply complete, deterministic parameters for official and personal tool presets, using defaults for new parameters missing from older personal presets. Keep official previews fixed, remove first-use color and stroke-width inheritance, and ignore older official baseline caches while retaining each tool's independent working state.

- 将“深度优先遮挡”移到画布工具配置，仅在启用透视画布时显示；画布预设保存该开关，旧预设缺少时默认关闭。缺少校准时按参考屏幕与预设行列、格长生成默认几何，避免继承当前四角。应用仍解除网格调整锁，目标画布的非透视原点与重置屏幕偏好保持独立。
- Move Depth-priority occlusion to Canvas tool settings, visible only with perspective enabled. Save it in canvas presets, defaulting to off when missing. Generate default geometry from the reference display and preset counts/spacing when calibration is absent instead of inheriting current corners. Application still releases the adjustment lock; the destination's flat origin and reset-display preference remain independent.

- 优化半透明笔迹、荧光笔和形状的绘制，避免每个对象都进行全屏临时图层合成；复杂填充、描边与箭头仍保持整体透明度语义。
- Optimize translucent strokes, highlighters and shapes to avoid a full-screen temporary layer per element; preserve whole-element opacity for combined fills, outlines and arrowheads.

- 设置新增“自动／增强／普通”全屏覆盖模式，默认自动；强制模式跳过能力检测和自动回退，保存后重启生效。
- Add Automatic, Enhanced and Ordinary overlay settings. Automatic remains the default; forced modes bypass detection and fallback. Restart to apply.

- 全屏增强每次启动重新验证实际接口，不再因旧异常记录、诊断写入失败或未验证的系统版本号直接回退；探测超时后仍尝试实际初始化。移除界面超时、慢绘制或输入释放失败时主动结束进程的行为，由 Windows 和用户处理未响应应用；紧急快捷键仅请求正常释放输入。
- Recheck actual overlay capabilities on every launch instead of rejecting historical exits, unwritable diagnostics or unlisted OS builds. Attempt real initialization after an inconclusive probe. Remove process self-termination for slow or unresponsive UI and input-release failures; leave unresponsive-app handling to Windows and the user, and make the emergency chord request normal input release only.

- 统一编辑、保存与恢复的容量边界，避免合法操作生成无法重开的工程；检测外部文件变化，优化脏状态查询、撤销快照、历史预览和资源写入，并限制单次图片导入的内存预算。
- Align editing, saving and recovery limits so valid edits remain reopenable; detect external file changes and reduce dirty-state, undo, preview and asset-copy costs, with bounded image imports.

- 共享文字塑形、按词换行与双向光标映射，捕获探测失败可退避重试，局部动图与滤镜仅刷新受影响输出。
- Share text shaping, word wrapping and bidirectional caret mapping; retry transient capture probes and restrict animated/effect updates to affected outputs.

- 保留鼠标为主、快捷键加速的操作方式，Tab 继续展开／收起工具条，不遍历按钮或显示额外的焦点虚框；支持高对比主题、明确错误和锁定反馈、缺失字体提示及预设撤销。历史和印章列表按视口加载，窄窗口可滚动操作，欢迎页可继续最近工作，应用内可阅读离线帮助。
- Preserve mouse-led interaction with shortcuts for faster actions. Tab expands/collapses the toolbar without button traversal or additional dashed focus outlines. Add high-contrast styling, explicit validation/lock/font feedback and preset undo; bound history and stamp lists to the viewport, retain actions in narrow windows, expose recent work and provide offline help.

- 更新安装改用持久事务和独立原生恢复启动器，准备阶段可取消，保留受保护的失败恢复证据并限制成功更新缓存；配置目录别名共享单实例身份。
- Make updates recoverable through a durable transaction and native recovery launcher, with cancellable preparation, protected failure evidence and bounded completed-update caches; normalize profile aliases for single-instance ownership.

- 新增可预览、可选择日志的脱敏诊断导出及本安装的系统集成注销入口；发布依赖锁定并附带 CycloneDX 清单。正式发布必须具备受信签名身份；本地未签名验证包单独标识，不能作为正式更新安装。
- Add previewable, optional-log diagnostic exports and an installation-owned integration removal command; lock release dependencies and include a CycloneDX inventory. Formal releases require trusted signing identities, while unsigned local validation packages remain distinct and ineligible for automatic updates.

- 统一工具条快捷键角标的共享布局，修正 C／I／P 偏移；恢复画布按钮透视琥珀态并叠加蓝色工具选框，统一画布、放置模式、置图与截图的图标画幅和笔宽。
- Share toolbar shortcut layout to align C/I/P; restore amber perspective styling beneath the Canvas tool's blue selection border, and unify icon canvas size and stroke weight across Canvas, placement, import and capture.

- 托盘左键单击延迟进入编辑、双击切换画布显隐；右键继续打开原生菜单。加宽菜单并在右侧显示快捷键。
- Delay a tray single click to enter editing, use double-click to toggle canvas visibility, retain the native right-click menu, and widen it with right-aligned shortcut hints.

- 新建画布的二次确认气泡移到上排按钮上方，限制在头像皇冠高度以内，并以小尾巴指向原确认按钮。
- Place the new-canvas confirmation bubble above its upper-row button, within the mascot's crown height, with a pointer toward the confirmation target.

- 为主菜单各功能分组、欢迎页、设置和悬浮头像分配独立图标，消除不同功能复用画笔、窗口、属性或眼睛图标的情况；托盘同步相同命令图标。
- Give menu categories, Welcome, Settings and the floating logo distinct artwork; share each command's matching icon with the tray.

- “更多工具”在选中模糊或马赛克时显示该工具的实时图标，选择其他工具后恢复三点图标。
- Show the active Blur or Mosaic artwork on More tools; restore three dots when another tool is selected.

- 修复隐藏画布后清除、再次显示时旧内容短暂闪回：所有显示窗口先提交当前画面，再恢复可见。
- Refresh every retained display before showing it, preventing cleared hidden canvas content from flashing back.

- 修复工具配置与图层属性数值错误提示残留：纠正输入、离开输入区域或重新打开面板后清除过期提示，保留忙碌时尚未应用的属性草稿；统一移除滚动内容区域的键盘焦点虚框。
- Clear stale tool-option and layer-property errors after correcting an entry, leaving the input area or reopening the panel, while retaining unapplied property drafts during busy operations; remove the dashed keyboard focus outline from scrolling content areas.

- 修复文字工具抢占选框控制区域：文本内区编辑、边框移动、角点缩放、手柄旋转；编辑中的边框也可变换。文字框改宽高后重新适配字号与换行，消除旧自动换行留下的空高，保留手动空段落。
- Keep text editing inside the text interior and restore border movement, corner resizing and handle rotation, including during inline editing. Refit font size and wrapping after frame changes so removed automatic line breaks leave no empty height; preserve deliberate empty paragraphs.

- 添加 I 置图、P 截图、Ctrl+PageUp/PageDown 调节透明度，以及仅打开的 Ctrl+L 图层、Ctrl+, 设置、Ctrl+T 当前工具参数；补齐工具提示、菜单提示和 I/P 角标，属性提示标注 Enter。
- Add I for image import, P for capture, Ctrl+PageUp/PageDown for opacity, and open-only Ctrl+L for Layers, Ctrl+, for Settings and Ctrl+T for current tool options. Complete toolbar/menu hints, I/P corner legends and the Enter property hint.

- 修复原生画布在 Ctrl 快捷键松开后不刷新选框：复制保留原选择，粘贴与复制副本选中新图层；移除未公开的 A 工具别名，Ctrl+A 全选时保留当前工具。
- Restore selection handles immediately after releasing Ctrl on the native canvas. Copy keeps the original selection, paste and duplicate select new layers, unbound A no longer changes tools, and Ctrl+A retains the active tool.

- 以透视矩形中心的投影统一网格原点和中央把手，修复偶数行列时四边／四角脱离格线；远端网格按屏幕间距局部抽稀，保留近景格线，并在 Skia 展开前移除无法分辨的虚线／点线周期，避免天际线附近长路径拖慢校准。
- Align the grid origin and center handle with the projected rectangle center so even-count edges and corners stay on the grid. Thin compressed spans by screen spacing while retaining near lines, and remove unresolvable dash/dot spans before Skia expands their long paths near the horizon.

- 绘图范围按全部屏幕、仅主屏排列，多屏时列出具体屏幕；按设备保存选择，断开回退主屏、重连恢复，并随屏幕布局更新列表，保留已打开设置中的未保存范围选择。恢复重置透视的屏幕选择，单活动屏时禁用下拉。
- Order drawing scope as All screens, Primary screen only, then individual screens. Save device identity, fall back to primary on disconnect, restore on reconnect, and refresh lists after topology changes while retaining pending scope edits. Restore reset-display choices and disable the dropdown for a single active screen.

- 配置兼容读取会忽略未知字段／枚举并为缺失、不可解析或越界字段提供局部默认值；后续修改其他设置仍保留原始未知数据。损坏配置在启动前提供“退出／备份并修复配置”，自动备份全部配置后只重置出错文件；启动错误的完整堆栈保留在日志。主菜单新增“系统与调试”，可打开安装和用户数据目录。
- Read configuration compatibly with local defaults for missing, unreadable or out-of-range values while retaining unknown fields and enums through later saves. Damaged configuration offers Exit or Backup and repair before native session startup, backs up all configuration and resets only the unreadable file. Keep startup stack traces in logs and add System and diagnostics folder shortcuts.

- 统一官方尺度：文字 36、符号／序号／Emoji 72、自由形状至少一边 108。序号按图形大小自动适配文字，使用图形／文字独立色板；Emoji 实际随边框拉伸。形状在工具和图层属性中可链接边框与填充颜色，并随预设、工程和恢复数据保存。
- Standardize official sizing at 36 for text, 72 for symbols, numbers and emoji, and a 108-pixel side for free shapes. Fit numbered labels to badge geometry with separate shape/text palettes, stretch emoji artwork with its frame, and persist linked outline/fill colors in tool presets, projects and recovery data.

- 将全屏增强作为可选后端：启动前由独立进程探测，接口缺失、初始化失败或上次异常退出时回退到普通桌面模式。运行中安全释放输入后重建桌面界面，保留画布、资源、文件绑定及撤销记录；增加 `--desktop-mode` 与 `--retry-fullscreen` 启动入口。
- Make fullscreen enhancement optional: probe it in an isolated startup process and use ordinary desktop mode after missing interfaces, initialization failures or an unclean prior exit. After safely releasing input, rebuild desktop surfaces while preserving documents, assets, file bindings and undo history; add `--desktop-mode` and `--retry-fullscreen` launch options.

- 进入编辑的松键等待期间保留穿透悬浮头像，工具条实际出现时再隐藏头像，避免两者都不可见的间隔。
- Keep the pass-through logo visible while waiting for entry keys to be released, then hide it when the editing toolbar appears, avoiding a gap with neither visible.

- 系统打开／保存文件框期间暂停覆盖输入，保留同一编辑会话；关闭或取消后恢复绘画，显式退出则不再恢复。不再拦截系统文件框的前台激活，某些游戏可能因此离开全屏。
- Suspend overlay input while a system Open/Save dialog runs, retain the editing session, and resume drawing on return or cancellation. An explicit exit cancels restoration. File dialogs use normal foreground activation, which may take some games out of fullscreen.

- 修复进入覆盖编辑时提前接管输入、退出后残留按下状态的问题：在触发消息处理结束后结合异步与线程队列状态，等待仍按下的键盘按键和鼠标按钮释放，再申请输入委托。
- Fix premature input takeover and residual pressed states when entering overlay editing. After the triggering message returns, combine asynchronous and thread-queue state and wait for held keyboard keys and mouse buttons to be released before requesting input delegation.

- 编辑与穿透模式的头像双击统一为画布显隐，保留模式与工具条折叠状态。单击继续延迟执行右键对应动作：编辑态打开主菜单，穿透态只进入编辑。工具条折叠改为按 `Tab` 或使用主菜单第一项“收起工具条／展开工具条”；不在头像旁新增按钮，输入框、菜单与对话框保留自身的 `Tab` 行为。
- Unify logo double-clicks across edit and pass-through modes to toggle canvas visibility while retaining mode and toolbar expansion. A delayed single click still mirrors immediate right-click: open the main menu while editing, or only enter editing from pass-through. Fold/unfold the toolbar with `Tab` or the first main-menu item, **Collapse toolbar / Expand toolbar**, without adding buttons beside the logo; input fields, menus and dialogs retain their own `Tab` behavior.

- 穿透浮动头像为编辑头像的 75%，改为双手捧皇冠、无笔，移除文字遮罩；编辑态继续戴冠。两组均以睁眼／闭眼表示画布显隐，托盘图标同步状态。
- Keep the pass-through mascot at 75% scale, holding her crown in both hands without a pen or text badge. Editing keeps the crown on her head. Both pairs use open/closed eyes for visibility, with matching tray states.

- 将画布调整改为普通互斥工具：单击或按 `C` 选中并显示蓝框，再次选择打开参数板，位置、关闭与固定规则统一；非透视常显参考网格并支持空白处径向拖动调节格长，透视自动显示校准框与手柄。
- Make Canvas an exclusive tool: click or press `C` to select it with a blue border, then select it again for options with shared positioning, dismissal and pinning. Flat mode shows a persistent reference grid with radial sizing from empty space; perspective shows calibration handles immediately.

- 网格原点为非透视时的主屏中心、透视时的中央整体移动手柄。非透视将行列数合并为精确“网格大小”，单位与矩形宽高一致；单纯开关保留行列数和校准，显式改格长才按主屏推导新格数与参考框，覆盖旧形变。重置默认围绕主屏并保留精确格长，多活动屏幕时可从下拉菜单选择重置屏幕。
- Anchor flat grids at the primary-screen center and perspective grids at the central move handle. Flat mode uses an exact Grid size in the same units as rectangle dimensions. Mode switches retain counts and calibration; explicit size edits derive new counts and a reference from the primary screen. Reset defaults to the primary screen with exact spacing; a dropdown selects another active display when multiple screens are in use.

- “调整时锁定网格大小”仅锁格长，原点仍随中央手柄变化，不再固定格线交点与图层锚点的关系。修复点线网格因短划线端帽导致几乎不可见的问题，普通绘制与深度合成中的点线均使用可见圆点；参考线继续采用黑白交替线段。
- Lock grid size while adjusting now retains spacing while the origin follows the central handle; it no longer fixes intersections to layer anchors. Fix nearly invisible dotted grids with round caps in ordinary and depth rendering; reference lines retain alternating black and white segments.

- 新工程格式 v7 保存精确非透视格长与原点，兼容读取 v1～v6；画布预设库 v3 保留精确格长与校准快照。应用透视预设解除调整锁后按投影格长同步平面尺寸，非透视预设保留精确保存值；旧工程和预设继续可读。
- Project format v7 retains exact flat spacing and origin and reads v1–v6; canvas preset library v3 retains exact spacing and calibration snapshots. Applying perspective presets releases the adjustment lock and synchronizes flat size with projection spacing; flat presets retain their exact saved size. Older projects and presets remain readable.

- 正常编辑时，鼠标进入透视禁区会以淡红色轻提示整个禁止区域，离开即隐藏，不进入导出。
- Softly highlight the whole unsafe perspective region in red while the pointer is inside it during editing. Hide the hint on leaving and exclude it from exports.

- 透视越界处理新增“应用并保留图层（慎用）”，保存原始图层、位置与组合，危险部分临时裁剪或隐藏；保存重开、撤销重做均保留，调回透视后恢复显示。弹窗仅突出需处理的图层数量，各选项说明收进关闭按钮旁的问号帮助。
- Add “Apply and keep layers (use with caution)” to perspective safety resolution. Preserve original layers, positions and groups through save/reopen and undo/redo; clip or hide unsafe parts until the perspective returns to safe space. Keep the dialog focused on the emphasized affected layer count and move option explanations into the help button beside Close.

- 加重贴面与立牌放置图标的线条，使工具条和菜单中的视觉分量与其他图标更一致。
- Increase Surface and Billboard placement icon stroke weight in both the toolbar and menus to match neighboring icons.

## [0.2.1] - 2026-10-21

0.2.1 放宽强透视的调整范围，并完善安全区内的绘制与图层处理；同时修复编辑窗口拖动、快捷键、印章预览与高 DPI 图层排序等问题。工程格式继续使用 v6。

Version 0.2.1 expands perspective adjustment with safe-area drawing and layer handling, and fixes tool-window dragging, shortcuts, stamp previews and high-DPI layer ordering. Projects continue to use format v6.

- 放宽四点透视校准，允许地平线进入屏幕，统一安全区网格／贴面／效果裁剪与输入保护；立牌顶部仍可越过天际线并选取。调整松手后提供取消、整组移回开阔安全位置或明确删除，保留原始内容和尺寸，整次一步撤销；预设及工程重开保留强透视。
- Allow four-corner calibration with the horizon on screen, sharing safe-domain clipping and input protection across grids, Surface content and effects. Billboard tops remain visible and selectable above the horizon. On release, cancel, move affected groups to open safe positions, or explicitly delete them; preserve content and logical size with one undo step. Presets and reopened projects retain strong perspective.

- 修复编辑态拖动或缩放图层、属性等辅助窗口时明显卡顿并意外退出编辑的问题；连续刷新的画面优先让输入得到处理，正常的原生窗口移动和缩放保持编辑会话。
- Fix severe lag and unintended exits from drawing mode when moving or resizing tool windows such as Layers and Properties. Continuous canvas refreshes yield to pending input, and normal native window movement and resizing retain the editing session.

- “放置方式”的工具提示只显示名称和当前模式，主菜单同步；两种模式共用居中的大菱形地面和同一人物轮廓，默认模式沿正方形自身轴向平躺投影、略微缩小，立牌形象立于中央、圆头略高于菱形尖点，并断开被遮挡的地面线条，保持单色线条风格；加宽“置入图片”图标。
- Show only the Placement name and selected mode in its tooltip, synchronized with the main menu. Both icons share a large central diamond and one figure outline. Default mode projects the figure flat along the square's own axes at a slightly smaller scale; Billboard mode stands it at the center with its round head just above the top corner and gaps in the occluded ground outline. Retain monochrome strokes and widen the image-import icon.

- 修复立牌及贴面／立牌混合的可编辑印章悬停预览偏移、裁切的问题，缩放时同步处理地面锚点与离地高度；已有印章无需重新保存。
- Fix shifted and clipped hover previews for editable billboard and mixed Surface/Billboard stamps by scaling ground anchors and elevation together with the artwork. Existing stamps do not need to be saved again.

- 为矩形、椭圆、符号、序号和 Emoji 的单击放置增加 6 个逻辑像素的拖拽容差，按起始屏幕 DPI 换算；轻微抖动保留预设尺寸，超过容差后自由定大小。
- Add a six-DIP drag tolerance to click placement for rectangles, ellipses, symbols, numbered markers and emoji, scaled for the starting monitor's DPI. Slight movement keeps the configured size; crossing the tolerance starts free sizing.

- 统一画布、工具条与各编辑面板的快捷键路由，补齐属性、图层、画布设置和印章库的主命令入口；保留文本、列表、滑块、下拉框和预设的本地操作。支持行内文字及图层重命名先提交再执行工程命令，修复 Alt／Win 组合误触发和长按工具键反复开关参数的问题。
- Unify shortcut routing across the canvas, toolbar and editing panels, including missing Properties, Layers, canvas settings and stamp-library command routes. Preserve local text, list, slider, dropdown and preset operations; commit inline text and layer renames before project commands, reject Alt/Win aliases, and prevent held tool keys from toggling options repeatedly.

- 修复属性双击配对及 Enter 单选／多选入口，补齐属性窗口的编组快捷键；Ctrl 仅隐藏选框并保留选择。新增中键临时移动、Ctrl+N 新建及 Ctrl+Backspace／Ctrl+Delete 清空；Win+空格切换输入法不再仅因 Win 按下就退出编辑。
- Fix property double-click pairing and Enter routing for single and multiple selections, and forward grouping commands from Properties while Ctrl hides only selection handles. Add temporary Move with the middle button, Ctrl+N for a new canvas, and Ctrl+Backspace/Ctrl+Delete to clear. Preserve editing when Win+Space switches input methods instead of exiting on Win keydown.

- 修复启动早期或工程转交失败时错误弹窗黑底黑字的问题；提前初始化主题并提供独立配色兜底，同时让工程转交确认在保存对话框之前完成。
- Keep early startup and project-handoff error dialogs readable with startup theme initialization and a fallback palette, and acknowledge queued project handoffs before save confirmation dialogs.

- 在前台窗口或窗口层级变化后检查并恢复画布的实际置顶顺序，保留输入层、工具窗口和对话框的相对顺序，并避开截图期间的临时隐藏状态。
- Restore the canvas's actual topmost order after foreground or window-order changes while preserving input, tool and dialog order and temporary capture visibility.

- 修复高 DPI 下图层拖动排序松手后偏离占位符的问题，统一屏幕像素与列表逻辑坐标转换，单层与多选排序均保持预览落点。
- Fix layer drops shifting away from the insertion marker at high DPI by converting Thumb's screen-pixel completion endpoint back to list coordinates for both single-layer and multi-layer reordering.

## [0.2.0] - 2026-10-19

0.2.0 正式发布，整合本轮预览版反馈，完善绘图、工程与历史画布流程。立牌和深度优先遮挡保留实验性标记并默认关闭。新工程格式为 v6，可读取 v1～v5；旧版应用不能读取新保存的 v6 工程。

Version 0.2.0 completes this preview cycle with drawing, project and canvas-history improvements. Billboard placement and depth-priority occlusion remain experimental and off by default. New projects use format v6 and read versions 1–5; older apps cannot read newly saved v6 projects.

- 优化开启“深度优先遮挡”后的画布调整：普通画面只合成各输出当前可见区域，并复用有内存上限的 CPU 工作缓冲，减少多屏拖动画布边时的重复计算与分配；保持原投影、网格和深度遮挡规则。
- Speed up canvas adjustment with Depth-priority occlusion by composing ordinary scenes only within each output's visible region and reusing bounded CPU scratch surfaces. This reduces repeated work and allocations during multi-monitor edge dragging while preserving projection, grid and occlusion rules.

- 新增默认关闭的“调整时锁定网格大小”：锁定显示网格格长与原点，保留原透视算法，倾斜四点调整时网格交点与图层锚点保持对齐；行列改为实时只读小数覆盖量。校准增加原点标记，普通／参考网格及四点预览共用格距；撤销、工程和恢复数据保留锁定，重置／应用预设解除锁定。v6 兼容读取 v1～v5。
- Add opt-in **Lock grid size while adjusting**, retaining fixed display-grid spacing and origin with the existing projection algorithm. Tilted calibration keeps intersections aligned with anchors; row/column coverage updates as read-only fractional values. Calibration adds an origin marker; ordinary/reference grids and four-point previews share spacing. Undo, projects and recovery retain the lock; reset/presets release it. Version 6 reads versions 1–5.

- 透视画布的图层窗口新增默认关闭的“深度优先遮挡”。立牌锚点深度优先，完全同深度沿用图层顺序；画布底色、网格和贴面沿等深线参与局部遮挡。统一空间选取、截图／导出、缓存与实时滤镜的有序后方采样；关闭透视保留选择。v6 工程保存此开关，兼容读取 v1～v5。
- Add opt-in Depth-priority occlusion to Layers for perspective canvases. Anchor depth orders billboards, with layer-order ties only at identical depth; canvas color, the grid and Surface content participate in partial occlusion. Spatial picking, captures/exports, cache routing and finite rear-scene live-filter sampling follow the same rules. Disabling perspective retains the choice. Version 6 stores the option and reads versions 1–5.

- 新增默认关闭的“立牌模式（实验性）”，与原“贴面”放置共存。透视下先预览并点击固定等深线，再沿用原工具输入；创建期间显示中心与屏幕竖直锚点连线，完成后辅助线随选中轮廓显示。主体移动地面锚点，中心手柄调整正负离地高度，角点以中心缩放；混合组合旋转只影响贴面成员。逻辑尺寸、描边和高度按同一深度比例显示，宽高由屏幕拖拽反算，描边预设 5 始终保留逻辑值 5。文件／位图插入及合并印章遵循新建方式，可编辑组合印章和内部复制保留各图层数据。v6 工程保存放置数据，兼容读取 v1／v2／v3／v4／v5，不承诺旧版应用读取新格式。
- Add opt-in Billboard mode (experimental) alongside Surface placement. In perspective, preview and fix an equal-depth line before using the original tool gesture; show the center and vertical anchor connection while drawing and with selection handles afterwards. Moving the body moves the ground anchor, the center handle changes signed elevation, and corner resizing uses the center. Mixed-group rotation affects only Surface members. Logical dimensions, strokes and elevation share one depth scale: drag sizes are inverse-mapped, while a stroke preset of 5 stays logically 5. Fresh images and flattened stamps use new-object placement; editable stamps and internal copies retain per-layer metadata. Version 6 retains placement data and reads v1/v2/v3/v4/v5, without promising older-app support for new files.

- 新增通用 Shift 拖动移动约束：以本次拖动最初起点为基准，仅沿画布 X／Y 轴移动，中途切换 Shift 不重设原点；立牌同样约束地面锚点，不沿屏幕或等深线方向。
- Hold Shift while moving to constrain movement to canvas X/Y axes through the original drag start; changing Shift mid-drag never resets that origin. Billboard movement constrains its ground anchor rather than screen or equal-depth directions.

- 固定按钮与显隐按钮统一二态强调，开启时同时改变图标、边框和背景。画布调整入口按透视状态显示完整强调，点击仍打开画布设置。
- Unify active pin and visibility feedback across icon, border and background. The canvas settings action reflects perspective with its full active style while continuing to open settings on click.

- 透视贴面矩形／椭圆截图选区按画布平面投影，关闭透视后恢复对应几何形状；自由与多边形套索保持屏幕轮廓。立牌截图先选深度，保留捕获像素并反算逻辑尺寸。
- Project Surface rectangle/ellipse captures from canvas-plane geometry so their intended shape returns with perspective disabled; freehand and polygon lassos retain screen contours. Billboard captures choose depth first, preserve captured pixels and derive logical dimensions.

- 绑定工程时在工具条上方显示随折叠的文件名气泡，标记未保存修改；右键可取消绑定及打开位置。印章槽右键仅保留重命名、覆盖、删除、取消固定四项维护操作；使用与使用可编辑源仍位于印章库，取消固定仅移除当前槽。
- Show a folding filename bubble with unsaved state above the toolbar; detach or locate the file from its context menu. Limit stamp-slot menus to Rename, Overwrite, Delete and Unpin; use and editable-source actions remain in the library, and Unpin affects only the clicked slot.

- 第二轮反馈：新增手动欢迎页入口和署名项目链接；浮动图标默认开启并可从主菜单、托盘及设置切换。统一固定按钮、恢复色板透明棋盘格、关闭 Tab 焦点轮转与焦点框，重绘固定像素取色光标；文字编辑框增加留白及独立粗光标，修复鼠标移动干扰闪烁。透视画布新增任意顺序四点定位与逐点网格预览，固定双行调整／重置按钮，并修复角点快速重复点击时的 Ctrl 拖动起拖。精简双语提示，调整欢迎页和设置宽度以避免英文意外换行。
- Round two: reopen a persistent welcome page from menus and open the project from its credits. Enable the floating logo by default with menu, tray and settings switches. Unify pin buttons, restore transparency checkerboards for color controls, disable Tab focus navigation and rings, and redraw the fixed-size eyedropper. Add text editing padding and a distinct caret; keep idle mouse movement from resetting its blink. Add unordered four-corner perspective placement with progressive previews, fixed two-row controls, and corner double-click dragging with Ctrl. Shorten bilingual hints and improve English layout.

- “将标注移回可见屏幕”仅恢复完全屏外内容，保留已可见和部分可见对象；按当前多屏范围选择最近可放置位置，分组保留间距，透视外观不变，整次一步撤销，重复操作不再偏移。
- Recover only wholly offscreen content, leaving visible and partly visible objects untouched. Use the nearest usable position across the active displays, retain editable group spacing and projected appearance, and make recovery one undo step with no repeated drift.

- 新增默认关闭的预览版更新开关，可在设置及更新面板持久修改；当前运行预览版也须主动开启才接收新预览版。没有可用更新时仍展示当前版本发行日志，日志获取不依赖可安装包。
- Add a persistent preview-update switch, off by default, in Settings and the update panel. Preview builds also require opt-in for newer previews. Show current-version release notes even when no update is available, independently of installable assets.

- 审查并补齐主菜单、嵌套菜单、画布操作、屏幕重置、印章槽位与托盘图标；移除通用方框占位，菜单工具显示对应图形，穿透菜单与工具条共用对号，检查更新使用循环箭头。
- Audit main, nested, canvas, display-reset, stamp-slot and tray menu icons. Remove generic box placeholders, use distinct tool symbols, share the toolbar check mark for pass-through, and add circular arrows for updates.

- 设置按四类组织，统一卡片、间距与固定底部操作；新增可选的穿透模式吉祥物。复用原图与显隐眼睛状态，支持延迟单击／即时右键进入绘画、双击切换显隐，以及带阈值的独立拖动和每次启动位置重置。
- Group settings into four categories with consistent cards, spacing and fixed footer actions. Add an optional pass-through mascot using the same artwork and visibility states, delayed single-click or immediate right-click entry, double-click canvas visibility, threshold-based independent dragging and position reset on restart.

- 统一预设、色板、印章和图层排序的拖动预览及蓝线双端圆点；修复非首项快照空白、同一空隙重复插入位置和边缘裁切。跟随图可移出原窗口，多图层预览显示数量，取消和完成均清理浮层与原项状态。
- Unify drag previews and blue insertion lines with endpoint dots across presets, swatches, stamps and layers. Fix blank non-first-item snapshots, duplicate gap positions and edge clipping. Previews follow beyond the source window, show multi-layer counts and clean up fully on completion or cancellation.

- Shift 统一临时反转形状、截图、裁剪和滤镜矩形区域的等比开关；画笔、荧光笔及自由套索支持一次拖动中的分段直线，直线／箭头端点和多边形套索支持八方向约束。按键时的指针位置随输入包传递，松键立即固定终点。
- Shift temporarily inverts aspect constraints for shapes, snapshots, crops and rectangular filter regions. Freehand, Highlighter and freehand lassos support straight segments within one drag; line/arrow endpoints and polygon lassos support eight-direction snapping. Key packets carry pointer positions so releasing Shift fixes the endpoint immediately.

- 图层等比偏好随工程和撤销保留，Shift 临时反转；文字默认自由缩放并在边框内调整字号和换行。属性内容优先、几何信息最后，支持固定、衍生弹窗保留，以及双击／多选 Enter 在鼠标附近打开。
- Persist layer aspect preferences with Shift inversion; resize text by fitting its font and wrapping inside the frame. Prioritize content in Properties, put geometry last, and support pinning, nested focus retention and pointer-adjacent opening from double-click or multi-selection Enter.

- 文字改为画布内输入、选择和光标编辑，支持旋转／透视位置映射；Shift+Enter 换行，Enter 提交并保留选中，失焦自动提交，Ctrl 点击强制新建文字层。移除旧文字输入窗口，序号改由属性面板编辑。
- Edit text, selections and carets directly on the canvas with rotation/perspective mapping. Shift+Enter adds a line; Enter commits and keeps selection; blur commits; Ctrl-click forces a new text layer. Remove the old text dialog and edit numbers through Properties.

- 颜色即时应用并保留预览透明度，关闭二级面板只记录最终颜色；色板命名保留父弹窗，取色支持指针颜色气泡及面板恢复。
- Apply colors live with alpha-preserving previews and one final recent-color entry; retain the parent popup during swatch naming and restore panels after eyedropper sampling with a live color bubble.

- 修复部分透视实时效果印章的提示预览被拉伸，按图像实际比例展示并更新水平／竖直比例提示。
- Preserve pixel aspect in projected live-effect stamp previews and report their actual horizontal/vertical display scales.

- preview.4 反馈：画布行列并排、透视平面重置分裂按钮和屏幕回退缓存；官方预设恢复默认平面，工具条反映透视状态，单角拖动支持吸附及 Ctrl／Shift。
- preview.4 feedback: paired row/column controls, a display-aware split reset button with persisted fallback, default planes for official presets, perspective toolbar state and corner snapping with Ctrl/Shift.

- 工具条保留灰色新建与右侧红色扫把，移除画布下拉和新建默认画布。新建按文件及修改状态确认；清空立即执行、可撤销且不入历史。历史过滤空白与重复版本，恢复保留原排序，设置新增历史上限（默认 60）。
- Keep gray New and a red broom for immediate, undoable Clear without a history entry; remove the canvas dropdown and default-canvas command. Confirm New according to file and edit state; filter blank/duplicate history, preserve restore ordering and add a configurable 60-version default history limit.

- 新配置的绘图范围默认使用“全部屏幕”，仍可选择“仅主屏”；已有保存的范围选择保持不变。范围在设置保存后立即生效，无需重启；显式启动参数仍优先决定本次运行范围。
- New configurations default to All screens, with Primary screen only still available; existing saved scope choices are retained. Saving the scope in Settings applies it immediately, without restarting. Explicit startup options still determine the scope for that run.

- 画布面板的“重置透视平面”分裂按钮可选择屏幕并立即重建参考平面，主按钮重复上次选择；默认使用主屏，失效选择回退默认。选择作为应用偏好跨启动保留，不属于预设或工程。中央四向把手或框内空白处可整体跨屏移动校准框；调整透视遇到安全边界时以琥珀色提示，并限制在有效范围内。
- The canvas panel's Reset perspective plane split button selects a display and immediately rebuilds the plane; its main action repeats the last choice. The default uses the primary screen and unavailable choices fall back to it. This is an application preference retained across restarts, independent of presets and projects. Move the entire frame across displays with the central four-way handle or its interior. Perspective adjustments show an amber warning at the safe boundary and remain within the valid range.

- 新保存或覆盖的个人画布预设包含四角位置和完整透视校准，应用时恢复保存状态；官方预设恢复默认平面并关闭透视。缺少校准的旧个人预设继续保留当前四角、应用面板参数；旧预设无法找回从未保存的四角，需重新校准后覆盖保存。个人预设不会根据重置屏幕偏好自动缩放或搬移校准。
- Newly saved or overwritten personal canvas presets include corner positions and complete perspective calibration, restoring their saved state. Official presets restore the default plane and disable perspective. Older personal presets without calibration preserve current corners while applying panel parameters. Older presets cannot recover corners they never saved; recalibrate and overwrite them. Personal presets do not automatically scale or move calibration to the reset-display preference.

- 九种官方符号预设的默认插入尺寸统一为 100×100，重新选择旧版使用过的官方预设时同样应用；个人预设和已有对象的尺寸保持不变。
- All nine official symbol presets now use a 100×100 default insertion size, including when reselecting an official preset used in an earlier version. Personal presets and existing objects keep their dimensions.

## [0.2.0-preview.4] - 2026-10-18

- 恢复画布显示时保留窗口层级，避免绘画内容短暂盖住工具条或面板；截图临时隐藏后的界面恢复使用同一保序方式。
- Restoring canvas visibility preserves window stacking so artwork does not briefly cover the toolbar or panels. Visuals temporarily hidden for capture use the same stacking-preserving restore.

- 新增画布背景与透视校准面板：背景颜色和透明度独立于标注透明度；可设置网格列行、显隐、颜色、透明度、线宽和线型。拖动四角、边缘或内部校准平面，形状、文字、图片与动图随之变换，截图按选定屏幕位置逆变换放置。新保存的 v3 工程保留画布设置，旧 v1／v2 工程仍可读取，缺少的设置使用默认值。
- Added canvas background and perspective settings. Background color and opacity are independent of annotation opacity; configure grid rows, columns, visibility, color, opacity, width and pattern. Calibrate by dragging corners, edges or the interior; shapes, text, images and animations follow the plane, and snapshots are inverse-projected into their selected screen position. New v3 projects retain canvas settings; v1/v2 projects remain readable and use defaults for missing settings.

- 画布面板左侧显示属性，右侧提供官方与个人预设；预设可保存背景、网格及透视开关，保留当前校准角点和原点，按新行列数重新定标，一次应用可一次撤销。个人预设支持覆盖、重命名、删除与排序。
- The canvas panel shows properties on the left and official/personal presets on the right. Presets save background, grid and projection preferences while preserving the current calibrated corners and origin, recalibrating for new row/column counts in one undo step. Personal presets support overwrite, rename, deletion and reordering.

- 校准时显示全屏单像素黑白交替参考线，预览自定义网格时减淡，退出校准后隐藏；列行滑块按整数跳转，线型选项带有图示。密集网格自动减少可见线条，修复某些透视下行列数从 7 增至 8 时整片网格突然抽稀的问题。
- Calibration displays single-pixel alternating black/white guides across the screen, dims them beneath the configured grid and hides them when calibration ends. Row/column sliders use integer steps, and line patterns have illustrated choices. Dense grids reduce visible lines automatically, fixing abrupt grid-wide thinning when a count increased from 7 to 8 under certain projections.

- 印章库新增按 1–9、0 排列的紧凑十格快捷栏，支持拖放或选槽后点击固定、清空与按槽位顺序逐个取消固定。搜索与操作同排，说明收进问号帮助，印章按完整列宽连续滚动，移除分页；列表顺序会保存，并显示首个固定槽序号和组合／图片标识。
- Added a compact stamp shelf ordered 1–9, then 0, supporting drag-and-drop or click-to-assign pinning, clearing and one-binding-at-a-time unpinning in shelf order. Search shares the action row and instructions are in Help. Stamps scroll continuously in complete columns without pagination; entries retain their order and show the first assigned slot and group/image type.

- 印章库按钮空闲时与“新建印章”一样使用灰底，从库中选用印章时显示缩略图和蓝色选框，切换工具或快捷槽后恢复。动图小缩略图静止，悬停大预览播放；含实时桌面效果的组合保留静态预览回退。
- The idle stamp-library button uses the same gray surface as New stamp, shows a thumbnail with a blue outline for a stamp chosen from the library, and resets when switching tools or shortcut slots. Small animated-stamp thumbnails remain still while hover previews animate; groups containing live desktop effects retain a static preview fallback.

- 个人预设、色板和印章拖动排序时显示本地半透明跟随图和目标插入线，拖放到印章快捷槽时高亮目标槽；取消拖动不触发点击应用。
- Reordering personal presets, saved colors and stamps shows a local translucent preview and destination insertion line; dropping onto a stamp shelf highlights the target slot. Cancelling a drag does not apply the item.

- 属性面板随选择自动刷新，有效数值与颜色实时生效并支持撤销；多选仅显示共同适用的属性，移除手动确认和刷新按钮。
- Properties follows selection automatically, applies valid values and colors immediately with undo support, and shows common applicable fields for multiple objects, without Apply or Refresh buttons.

- 主菜单与当前工具条同步，快捷键右对齐，调整工具条分组顺序，禁用裁剪仍有提示。移除实色工具的创建入口和旧截图草稿操作入口；旧工程中的实色效果和冻结截图保留读取兼容。设置入口统一命名为“设置”，快捷键角标开关只保留在设置中。
- Synchronized the main menu with current tools, right-aligned shortcut labels, reordered toolbar action groups and retained disabled-crop tooltips. Removed solid-redaction authoring and obsolete capture-draft actions while retaining older solid effects and frozen projects. The settings entry is now named Settings, with shortcut-badge preferences available there.

- 工具条清空按钮改为红色软毛扫把，底色尺寸与同排按钮一致，二次确认保持不变；印章图标强化握柄和压印底座。增强工具条和面板共用按钮的悬停反馈，明暗主题同步。
- The toolbar clear action now uses a red broom button with the same surface size as its neighbors and retains second-click confirmation. Stamp icons have a clearer grip and stamping base. Shared toolbar and panel buttons have stronger hover feedback in both themes.

- 隐藏画布时应用角色图标同步闭眼；睁闭切换仅改变眼部，其他像素与透明度保持一致。工具条、托盘和任务栏使用对齐的原图和一致的缩放方式，避免切换抖动。
- The mascot closes its eyes while the canvas is hidden. Only the eye areas change; the remaining artwork and transparency stay identical. Toolbar, tray and taskbar states use aligned originals and matching scaling to avoid jitter.

- 新增默认关闭的当前用户登录启动设置，以及 `.floatcanvas` 工程打开方式注册、命令行打开和运行中实例的文件转交。注册结果显示在设置按钮下方，已有 Windows 默认应用选择保持不变。
- Added optional per-user sign-in startup, off by default, plus `.floatcanvas` registration, command-line opening and project handoff to the running instance. Registration results appear beneath the settings button, and existing Windows default-app choices are preserved.

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
