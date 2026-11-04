# 画布坐标、查看与转换 / Canvas coordinates, viewing and conversion

本规范说明工程格式18的画布边界及模块职责。操作说明见[用户指南](User-Guide.md#查看画布独立编辑与跨屏转换)。

## 状态归属

| 状态 | 所有者 | 保存位置 | 正式输出 |
| --- | --- | --- | --- |
| 图层局部几何、父级放置 | graph/instance | 工程 | 生效 |
| 画布Origin、透视、网格、背景 | 根画布或子工程header | 工程 | 按现有scope规则生效 |
| 创作范围与参考屏幕 | 根工程AuthoringReference | 工程 | 决定转换基准和默认整幅导出 |
| 平移、查看缩放 | 编辑上下文 | 本机CanvasViewStateStore | 不生效 |
| 实际桌面原点、监视器和DPI | 运行环境 | 运行时/显示设置 | 负责屏幕定位和控件尺度 |

保留像素度量。Windows DPI不是工程分辨率，编辑器缩放不是内容变换；不能在同步显示器时覆盖创作基准。左上角为画布零点，X向右、Y向下；桌面可有负坐标，独立画布也可保存非零Origin。

## 统一映射

`CanvasMapping`组合局部到父级、对应根投影和最终查看矩阵。输出查看为`V(p)=s*p+t`，`s>0`，范围1%～6400%。透视导航另有平面位移`T`，最终映射为`VHTp`；鼠标水平、垂直增量驱动画布平面的X、Y轴，不按屏幕方向反算抓取点。桌面原点在此后叠加，不进入C。绘制、输入逆变换、拾取、文字与控制点使用同一最终映射；控制点可点击尺寸按输出像素/DPI管理。原始投影安全域在查看之前计算，导航不能改变作品是否合法。

`GroupCanvasScene`选择当前范围及其独立header，剥离宿主放置用于独立编辑；总览保留实例放置。`CanvasPreviewRenderer`反算可见源范围，在实际输出面直接绘制矢量、文字及动态来源，非恒等视图绕过1x静态栅格缓存。查看改变不得写回graph或用已裁切的旧帧放大替代重绘。

全局鼠标演示保留真实屏幕坐标。窗口帧沿原来源地址更新；查看不能改写绑定意图。实时滤镜在作品位置采样桌面/下方作品，再随查看显示；真正应用后才按新的作品落点采样。

## 本机视角身份

本机存观察中心、缩放、透视平面位移和上下文，而不是绝对桌面偏移。键区分文件路径/资源身份、根、scope、总览/独立模式、外链来源、未保存工作区及历史版本。文件路径在本机规范化。外链以来源资源身份与路径连接在线编辑和离线快照。

没有持久资源ID的旧文件使用本机路径及画布类型关联视角，避免读取时新生成的临时ID打断记忆。用户明确保存升级后，复制视角到持久资源身份；读取或导航不升级源文件。解除文件绑定会复制到独立工作区命名空间，不覆盖原文件记录。

另存为复制视角并保留原文件记录；历史恢复复制缺失记录，不覆盖该历史版本已有视角。内容历史中的查看回执仅用于应用的Undo/Redo，不进共享工程；另存后回执映射到新命名空间。分辨率副本有新的根资源身份。工作区恢复通过本机绑定找回视角。

延迟写入、离开/退出刷新，使用原子文件替换。记录保留30天，最多1000项；损坏缓存降级到原始坐标和100%比例。首次独立打开不自动居中或适配；创建普通组时一次性初始化固定Origin，使内容位于主屏尺寸的子画布中央，不改变宿主放置。根工程自身同一根的独立／总览模式共用视角键和画布参数，仅切换其他根的可见性；嵌套来源仍区分宿主总览和来源独立视图。

画布工具选中即进入校准：平面左键移动网格原点、滚轮调整间距，透视显示校准框及手柄。按住空格临时导航，中键保留临时对象移动。独立浏览入口提供取景、还原、应用；非恒等视角显示橙色，菜单展开后入口再次点击执行还原并收起。取景命令参考主屏位置和大小；靠边平移分别计算各屏露出的真实边段。新视角栅格就绪前保留已呈现像素，不提交空白过渡帧、不取消正在进行的移动事务。穿透展示完整根工程及其正式映射，编辑上下文及本机视角继续保留。

## 两种真实转换

`CanvasRebase`负责完整候选，`CanvasViewApplication`负责作用范围，`CanvasResolutionConversion`负责像素尺度策略。两种操作共用安全基础，但改变的数据范围不同。

应用当前视图时，真实内容`p′=Cp`，完整画布映射`H′=CHC⁻¹`，因此`H′p′=CHp`。H包括Origin及画布校准；不能只改透视矩阵。直接内容根只变换一次，后代继承；不能再重复缩放叶宽高、字号和描边。同步变换网格、投影四角、钉点、背景及立牌高度。应用保留创作范围和组对外放置，不改Pivot、ImportOrigin、资源或链接身份作为补偿。

当前完整取景参与应用，包括显式居中或适配、缓存恢复和靠边平移。候选完整验证后，内容、校准和恒等视角原子提交，一次Undo/Redo恢复对应数据和视角。失败不发布部分内容、不夹值或删除图层。共享定义在离开层级时提交；外部链接只读，转为内部工程或普通组后才能修改。只读和组内根总览禁止应用。

分辨率转换以有效参考范围等比contain到目标屏幕并居中，修改真实像素尺度及创作参考，保留原始资产和依赖定义。隐藏/锁定内容一起变换。新增余区不纳入下次有效参考，避免比例往返累计缩小。转换不受查看缩放范围限制，但仍须通过数据、容量和投影安全检查。新根ResourceId标识未保存副本，首次保存进入Save As，不自动重定向源链接。

## 格式与输出

格式18新增可选`CanvasAuthoringReference`：`Frame`、`ReferenceScreenBounds`及可选`ContentReferenceBounds`。有效转换范围采用ContentReferenceBounds，缺省采用参考屏幕范围。克隆、恢复、冻结、内容比较和包持久化保留元数据。本机视角不进入工程内容指纹。旧文件使用保存的显示布局/Viewport推导，读取不改盘；低版本不能夹带新字段语义。

运行时Viewport与保存的Frame分开。普通PNG/JPEG按Frame重绘；显式视口截取使用自己的范围；平面内容导出保留既有语义。范围外内容保留。显示器变化只同步运行环境，不自动转换；全部屏幕以主屏为统一转换参考，不逐屏重排拓扑。

显式截图冻结当前显示范围、最终查看映射和上下文；作品随查看变换，实际桌面像素保持屏幕位置，实时滤镜保留作品位置采样。插入使用捕获时的最终逆映射，避免重复缩放。异步期间视角、原点、来源或祖先上下文改变则拒绝迟到结果。透视扭正也取样查看后的四角，预览展开时组合查看矩阵，持久化的平面尺寸和校准排除临时视角。

## English contract

Scene format18 separates authored geometry and canvas headers, the root authoring reference, local editor navigation, and runtime desktop placement. Pixel units remain unchanged. DPI, viewing zoom and authored resolution must never be conflated.

`CanvasMapping` owns the composed forward/inverse mapping. Perspective navigation translates along canvas-plane axes before projection, followed by positive output zoom/translation; the mapping is VHT. It excludes the OS desktop origin. Projection safety is evaluated before division. Preview redraws vectors/text at output resolution; picking, IME placement and calibration use the same mapping. Global pointer presentation remains in actual screen coordinates. Dynamic window bindings and desktop sampling remain authored while navigating.

Local views store observation center, zoom and perspective plane offset under document/scope/source identities, with atomic persistence and 30-day/1000-item retention. Save As copies records without changing the original; history contexts retain their own observations. View-only operations never dirty content or enter formal exports.

Explicit screenshots freeze the displayed scope and final viewing mapping. Artwork follows the view while desktop pixels stay in screen space; live filters retain authored sampling. Insertion uses the frozen inverse mapping, and stale view/origin/context results are rejected. Rectification samples the displayed quadrilateral without persisting the editor camera in the authored calibration.

Legacy files without persistent resource IDs use local path and canvas kind until an explicit save promotes the view to the persisted identity. Reading does not upgrade the source. Detaching a file forks its workspace view while retaining the original file's record.

Applying the complete view rebases content and calibration together (`p′=Cp`, `H′=CHC⁻¹`), commits a validated candidate and resets the camera in one content history entry. Parent placement, resource identities, Pivot and ImportOrigin are preserved. Read-only scopes and group root overviews cannot apply. Internal shared edits publish when leaving their level. External links are read-only until converted to internal content or detached.

Resolution conversion shares rebasing but changes the authored pixel basis, creates a new unsaved root identity and uses Save As. Uniform contain fitting retains aspect ratio and excludes added margins from subsequent conversion. Assets and dependency identities remain intact. Runtime monitor changes never auto-convert. Ordinary full exports use the saved authored frame rather than the mutable viewport. Rotation is deferred throughout this version.
