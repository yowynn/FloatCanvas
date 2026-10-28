# 官方预设与工具命名设计

审阅日期：2026-10-28。本文记录官方内容、排序、名称及验证入口；完整参数以 `ToolPresetLibrary`、`CanvasPresetLibrary` 为准。

## 内容原则与兼容边界

- 每个工具族的第一项是最通用的标准范式，其后是有明确用途的常用组合。分类是内容设计规则，图库继续使用现有“官方预设 / 我的预设”布局，不增加分组控件。
- 标准范式使用软件前景色板红色 `#E03E3E`。文字、描边、序号底色及预留截图边框均如此；序号的白色文字、表情本色、透明画布背景属于用途本身。
- 常用组合使用完整、确定、互相独立的参数快照；切换预设不继承上一工具的颜色、尺寸、箭头、序号或其他参数。符号工具保留首项 `official.triangle` ID，移除重复的逐几何官方项并使用新的样式 ID；个人预设不删除。其他工具的已有官方 ID 保持。
- 浅红填充使用软件背景色板红色 `#FFDCDC`。黄色、绿色、蓝色、黑色分别取软件前景色板 `#CB912F`、`#448361`、`#2383E2`、`#252525`。
- 不迁移或覆盖个人预设；不修改通用模型的缺字段默认语义。旧个人预设仍按原兼容逻辑读取。
- 选择、橡皮擦、印章没有可区分的工具外观参数，因此只保留一个标准范式。印章内容仍由素材库决定，不用重复预设伪造不同效果。

## 当前 profile 的只读检查

首次设计时通过 `ApplicationPaths.DefaultProfileDirectory`、运行实例命令行和对应 `App.Startup` 诊断记录，确认用户实例使用：

`C:\Users\Wynne\AppData\Local\FloatCanvas`

实际读取的内容：

| 文件 | 内容 | 设计处理 |
| --- | --- | --- |
| `tool-presets.json` | 两个画笔记录：“测试”（实线、宽 27.651）、“测试2”（点线、宽 24.176） | 用户澄清：标为“测试”的内容不视为提供参考。因此没有复制其名称、颜色或完整参数作为官方内容。 |
| `canvas-presets.json` | 版本 3，空列表 | 没有可用画布示范。 |

首次检查没有发现可用的正式示范；全部方案按工具用途和现有生产模型独立设计。画笔的粗细与点线、图形的轮廓与填充、特效的强度、截图的形状与来源等采用同一种可推导方式。首次检查前后 SHA-256、大小和修改时间记录在 `.artifacts/official-presets-20261028/worktree-evidence/profile-read-before.json` 与 `profile-read-after.json`；上表只描述该次检查时的文件内容。

## 官方清单与顺序

共 18 个工具族、62 项官方预设：17 个普通工具族 57 项，另有画布 5 项。符号工具由原先 11 项精简为 5 种样式。下表每行的第一项为**标准范式**，其余依次为**常用预设**。尺寸、线宽与字号均为工具参数的逻辑像素值。未列出的字段由完整官方定义给出，不从当前工作状态继承。

| 工具 | 官方顺序（中文 / English） | 主要组合与用途 |
| --- | --- | --- |
| 选择 | 标准选择 / Standard selection | 标准指针选择；无外观变体。 |
| 橡皮擦 | 标准橡皮擦 / Standard eraser | 标准擦除；无可保存的粗细或范围模式。 |
| 画笔 | 标准画笔 / Standard pen；精细画笔 / Fine pen；粗笔标注 / Bold pen；点状画笔 / Dotted pen | 红色，宽度依次 4、2、12、24；末项点线，用于点状路径标注。 |
| 荧光笔 | 标准荧光笔 / Standard highlighter；黄色宽幅荧光笔 / Wide yellow highlighter；绿色荧光笔 / Green highlighter | 红色 24、黄色 32、绿色 24；保留渲染器自身的 35% 高亮混合，不重复降低颜色 alpha。 |
| 直线 | 标准直线 / Standard line；单向箭头 / Single arrow；双向箭头 / Double arrow；虚线 / Dashed line；点线 / Dotted line | 均为红色宽 4；无箭头、末端实心三角、双端实心三角、虚线、点线。 |
| 矩形 | 标准矩形 / Standard rectangle；圆角矩形 / Rounded rectangle；正方形 / Square；浅色填充矩形 / Tinted rectangle；实心矩形 / Solid rectangle | 标准 144×108、直角、红框宽 4；圆角半径 12；正方形 108×108 锁比例；浅红填充；统一红色实心。 |
| 圆形 | 标准圆形 / Standard circle；椭圆 / Ellipse；浅色填充圆形 / Tinted circle；实心圆形 / Solid circle | 首项 108×108 且锁比例，点击或拖动得到圆；椭圆 144×108 可自由调整；后两项为圆形浅红填充和统一红色实心。 |
| 文字 | 标准文字 / Standard text；醒目标题 / Prominent heading；正文注释 / Body note；居中标签 / Centered label | 红色 36 左对齐；红色 48 左对齐；黑色 24 左对齐；蓝色 28 居中。保留默认字体。 |
| 序号 | 标准序号 / Standard number；蓝色步骤 / Blue step badge；紧凑序号 / Compact number | 红底白字 72；蓝底白字 48；黑底白字 36；均从 1 开始、步长 1，使用当前前景/背景颜色角色。 |
| 符号 | 标准轮廓 / Standard outline；浅色填充 / Tinted fill；实心填充 / Solid fill；虚线轮廓 / Dashed outline；绿色强调 / Green emphasis | 统一用 72×72 三角形示范：红色宽 4、浅红填充、统一红色实心、红色虚线、绿色宽 6。选好样式后单击形状选择器，即可切换任一几何并保留其余全部参数。 |
| 表情 | 微笑 / Smile；赞许 / Thumbs up；完成 / Done；提醒 / Warning | 😀、👍、✅、⚠️，均为 72；保留原生表情颜色。 |
| 模糊 | 标准模糊 / Standard blur；轻度模糊 / Light blur；强效模糊 / Strong blur | 强度 12、6、24；用于不同程度的视觉弱化。 |
| 马赛克 | 标准马赛克 / Standard mosaic；细格马赛克 / Fine mosaic；粗格马赛克 / Coarse mosaic | 格块强度 12、6、24。 |
| 裁剪 | 自由裁剪 / Free crop；正方形裁剪 / Square crop | 自由比例、锁定 1:1；由实际图片局部坐标约束路径执行。 |
| 贴图 | 完全不透明 / Full opacity；半透明参考图 / Half-opacity reference；淡色描摹底图 / Faint tracing reference | 新图片不透明度 100%、50%、25%；适用新导入、粘贴和拖放的贴面及立牌图片。替换资源保留既有图片外观。 |
| 截图 | 矩形截图 / Rectangle capture；圆形截图 / Circle capture；含标注截图 / Capture with annotations；红框截图 / Red-border capture；自由套索截图 / Freehand lasso capture；多边形套索截图 / Polygon lasso capture | 首项矩形、仅背景、无边框；圆形；矩形且包含画布；矩形红框宽 4；自由套索；多边形套索。无边框组合仍保存红色边框色；全部官方项显式关闭持久等比，矩形／圆形仅按住 Shift 时临时 1:1。 |
| 印章 | 标准印章 / Standard stamp | 使用素材库选择的实际印章。 |
| 画布 | 透明画布 / Transparent canvas；白板 / Whiteboard；黑板 / Blackboard；方格纸 / Graph paper；透明点线网格 / Transparent dotted grid | 首三项关闭网格但留红色网格参数；白/深色背景；方格纸浅底蓝网格；透明背景红色点线网格。均为 8 列×6 行、线宽 1，默认关闭透视与深度优先遮挡。 |

符号形状选择器继续使用 `Symbol.*` 双语资源，五种样式与其余普通工具内容使用 `Recipe.*` 双语资源，画布沿用 `CanvasPreset.*`。工具显示名称与官方配方名称分离，今后调整配方名不会改动工具条显示名。

## 画板、画布与一步启用排查

项目当前没有独立的 Board/画板工具或图层模型。白板、黑板属于 `EditorTool.Canvas` 对应的 `SceneDocument.Canvas` 设置，由 `CanvasPresetLibrary` 应用；普通矩形等属于 `ToolPresetLibrary`。

| 检查项 | 处理结果 |
| --- | --- |
| 关闭的画布网格 | transparent、whiteboard、blackboard 均显式保存 `GridVisible=false`、色板红色、线宽 1、正行列数。只打开一次“显示网格”即可看到网格。原定义是半透明白或蓝灰，并非全透明，但没有满足预留红色的要求。 |
| 关闭的透视 | 官方应用保持 `ResetProjection=true`，生成有效参考四角、矩阵和正间距；打开透视一次即可生效。个人校准仍按原快照恢复。 |
| 截图边框 | 0 宽直接表达“无边框”，没有另一个开关；边框色保持不透明红色，单次提高宽度即可显示。 |
| 直线箭头 | None 表达关闭端点；箭头尺寸 0 有自动尺寸语义，不是无效尺寸。选一次端点样式即可显示。 |
| 图形填充 | 透明色直接表达“无填充”，没有独立填充开关；轮廓标准保持透明内填。实心组合使用 LinkColors，并保留有效浅红色 Fill，切回分离颜色即可使用。 |
| 可见内容与特效 | 有效描边、文字颜色、总体不透明度、字号、尺寸和特效强度均非零；高亮不重复降低 alpha。 |
| 贴图透明度 | 审查发现原新图插入路径没有消费 Image 工具的 Opacity。补通图片工作参数到新图创建及立牌路径，避免看似存在、实际无效的常用预设；替换图片保持原外观。 |

通用 `CanvasPresetParameters.GridColor` 默认值保持原样，避免改动旧个人预设省略该字段时的行为。

## 工具名称与新建个人预设名称

- L 工具统一显示“直线 / Line”，O 工具统一显示“圆形 / Circle”。快捷键仍为 L、O。
- `ToolDisplayName` 为不含快捷键的共享名称入口，覆盖工具条/提示/辅助技术名称、工具菜单、状态、参数窗标题和新建预设默认名。保留兼容资源入口的相同名称。
- 所有工具新建个人预设统一为“{本地化工具显示名}预设 / {localized tool display name} preset”，例如“直线预设 / Line preset”“圆形预设 / Circle preset”“画布预设 / Canvas preset”“贴图预设 / Image preset”。不附快捷键字母。
- 箭头、双箭头、圆角矩形、旧三角形别名使用当前工具族名称。用户已有预设名称不变。
- README 和中英使用指南同步工具入口名称；具体几何对象仍可称为“椭圆 / Ellipse”，截图圈选入口则统一称为“圆形 / Circle”。

## 验证与证据

证据目录：`.artifacts/official-presets-20261028/`。`worktree-evidence/` 保存首次独立工作树验证的原始材料，`main-integration/` 保存从 `H:\FloatCanvas` 整合后重新构建和运行的证据。自动检查使用临时 profile；有窗口的检查运行在隔离的 Windows 桌面，不替换或重启当前运行实例。

首次 68 项方案在工作树及主目录整合后的 Release 全解决方案构建均成功，**0 警告、0 错误**。以下为首次方案的历史验证：**15 组、2,186 项检查在两次验证中均全部通过**，各次日志与 `verification-summary.json` 分别保存于对应证据子目录。本轮精简与交互修改的验证见文末。

| 验证组 | 通过项数 |
| --- | ---: |
| official-preset-design | 352 |
| complete-presets | 211 |
| presets | 73 |
| canvas-presets | 204 |
| tool-state | 82 |
| gpu-settings（含本地化） | 29 |
| preview4-menu | 101 |
| toolbar2 | 399 |
| image-import | 80 |
| tool-icons | 52 |
| image-tools | 167 |
| preset-fallback | 82 |
| preset-isolation | 146 |
| tool-compatibility | 37 |
| tool-options | 171 |

主要入口：

- `--official-preset-design-only <证据目录>`：官方清单、首项顺序、中英切换、完整应用、独立快照、一步启用网格/透视、真实裁剪手势、个人旧文件读取保持原字节，以及中英预设图集/JSON 清单。
- `--complete-presets-only`、`--presets-only`、`--canvas-presets-only`、`--tool-state-only`：完整参数替换、旧文件默认、存储、画布及状态回归。
- `--gpu-settings-only`、`--preview4-menu-only`、`--toolbar2-only`：本地化、工具菜单、工具条入口及快捷键。
- `--image-import-only`、`--tool-icons-only`、`--image-tools-only`：三个贴图预设的实际导出 alpha、原始 PNG 保留、工作状态隔离、贴面/立牌、拖放、替换和撤销重做，及透明度图标差异。

主目录实际执行使用独立构建输出下的 Release 测试程序加 `--isolated-ui-desktop`：

```powershell
Set-Location H:\FloatCanvas
dotnet build FloatCanvas.slnx -c Release `
    --artifacts-path H:\FloatCanvas\.artifacts\official-presets-20261028\main-integration\build
& .\.artifacts\official-presets-20261028\main-integration\build\bin\FloatCanvas.AppTests\release\FloatCanvas.AppTests.exe `
    --isolated-ui-desktop --official-preset-design-only .artifacts\official-presets-20261028\main-integration
```

关键证据文件：

- `build-release.log`、`verification-summary.json`、各验证组 `.log` 和 `regression-group-*.json`。
- `official-presets-zh-CN.json` / `official-presets-en.json`：逐工具、逐顺序的 ID、名称、standard/common 分类及完整参数。
- `official-presets-zh-CN.png` / `official-presets-en.png`：使用真实生产图标渲染的清单图集，已逐图检查名称可读和图标输出。图标会按现有逻辑归一化部分字号/宽度；具体尺寸以参数清单为准。
- `toolbar/`：中英提示、明暗主题和 100% / 125% / 150% / 200% 工具条受控窗口渲染。
- `worktree-evidence/profile-read-before.json` / `profile-read-after.json`：首次检查的两个真实个人预设文件的 SHA-256、字节数和修改时间完全相同。
- `main-integration/integration-before.json`、`official-presets.patch`：迁移的 26 个已有文件修改、5 个新增文件和明确排除的既有并发改动。
- `main-integration/preservation-after.json`、`staged-before.patch` / `staged-after.patch`：主目录三份文档的暂存删除未变，UI-Layout、原生覆盖层、fixture README 的字节哈希未变，个人预设和原运行构建产物哈希未变。
- `main-integration/app-compile-items.json`、`localization-resource-items.json`：主目录项目评估中的实际编译项与本地化嵌入资源项，包含新增 ToolDisplayName 和 OfficialPresets。

## 主目录整合结果

2026-10-28，按用户要求已将全部实现放入 `H:\FloatCanvas`。主目录与工作树迁移前 HEAD 均为 `44618074eec5255b5013c043aede7b946d2af64f`，本次任务文件与主目录既有修改没有重叠。先以明确文件清单生成 patch 并通过 `git apply --check`，再迁移已有文件修改和新增文件；源码与已验证工作树的字节哈希一致，本报告追加主目录整合说明。

主目录原有三份 `Docs/Research` 文档的暂存删除、`Docs/UI-Layout.md`、`src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp`、`tests/FloatCanvas.FullscreenInputFixture/README.md` 均被排除在迁移 patch 之外，并通过最终哈希和暂存 patch 比对确认保留。

`scripts/Publish.ps1` 从自身所在的 `H:\FloatCanvas` 解析项目根并编译当前源码，不引用本任务独立工作树；SDK 自动包含新 `.cs`，Localization 项目的 `Resources/*.json` 包含新官方名称资源。用户直接从主目录运行原打包流程即可得到本次功能和同步后的中英使用指南，无需复制工作树文件。本文是项目审阅报告，按现有打包规则不加入运行 ZIP。

首次整合验证的输出仅写入 `main-integration/build/`，未替换 `src/FloatCanvas.App/bin/Release` 的运行产物；当时主目录运行实例 PID 5904 保持运行。此前工作树预览和原始测试证据已复制至 `worktree-evidence/`，首次主目录图集和参数清单位于 `main-integration/`。

## 后续调整：截图、图形样式与序号联动

2026-10-28，本轮修改直接在 `H:\FloatCanvas` 实施，当前清单为上文的 **18 个工具族、62 项预设**。

### 截图创建与图层编辑

- 截图配置不再显示“等比缩放”开关。矩形和圆形截图默认自由比例；只在按住 Shift 时临时约束为 1:1，释放立即恢复，无须额外移动鼠标。
- 平面和透视贴面的比例约束在画布平面中计算；立牌截图在屏幕选区中计算。套索拉直和多边形八方向约束保持。
- 旧个人预设或工具状态中的 `ConstrainAspect=true` 在应用到截图工具时归一为 false，截图圈选本身也只读取 Shift。新工作快照与个人保存入口输出 false；全部官方截图项显式为 false。不修改旧个人预设文件的原始值，通用字段仍服务于其他工具。
- 圈选按钮、提示、辅助技术名称及官方预设名称统一为“圆形 / Circle”；内部 `CaptureShape.Ellipse` 不变。名称不强制正圆，自由比例仍可圈选椭圆。
- 创建好的截图仍是图片图层，属性中的等比开关和宽高联动保留；裁剪、模糊、马赛克区域以及其他图形的等比能力保持。

### 多图形工具精简

对应当前 **符号工具**的形状选择器，五种样式统一以 72×72 三角形示范，按下列顺序展示：

| 类别 | ID | 名称 | 参数 |
| --- | --- | --- | --- |
| 标准范式 | `official.triangle` | 标准轮廓 / Standard outline | 色板红色、宽 4、实线、无填充 |
| 常用预设 | `official.symbol-soft` | 浅色填充 / Tinted fill | 红色轮廓宽 4、浅红填充 |
| 常用预设 | `official.symbol-filled` | 实心填充 / Solid fill | 统一红色填充，保留浅红备用填充色 |
| 常用预设 | `official.symbol-dashed` | 虚线轮廓 / Dashed outline | 红色、宽 4、虚线、无填充 |
| 常用预设 | `official.symbol-green` | 绿色强调 / Green emphasis | 色板绿色、宽 6、实线、无填充 |

原来八个重复几何项及“实心星形”“绿色对勾”两个专用形状组合不再单列。三角形、五角星、爱心、对勾、叉号、菱形、五边形、六边形和对话框均可在选择器中一步切换；回归逐一验证整套样式和尺寸保留，并能创建对应可编辑图层。矩形、圆形等独立工具预设不合并，个人预设不删除。

### 序号边界

| 调整前状态 | 应用序号预设或改变起始值后的当前序号 |
| --- | --- |
| 起始 100、当前 100，切换起始为 1 | 更新为 1 |
| 起始 100、当前 101，切换起始为 1 | 保持 101 |
| 已有图层但当前仍等于旧起始值 | 仍跟随新起始值 |
| 没有图层但当前不等于旧起始值 | 仍保留当前值 |
| 步长 0，已插入且当前仍等于旧起始值 | 仍按相等关系跟随 |

官方和个人预设、手填起始值、滑块和图标拖动使用同一规则。一次连续手势按开始时的相等关系决定，拖动途中经过当前值不会突然改变联动策略；取消恢复原起始值，已推进的计数保持。手势中切换画布、工具或在同一会话内撤销／替换文档后，旧回调不写入新上下文，同时核对会话与实际文档身份。

独立保留真正的序号起始基准，其他工具完整快照中的隐藏起始字段不会意外移动当前计数；切回序号工具仍按实际旧起始值判断。现有序号图层数值和步进规则不回写，当前序号仍随各自画布保存。自动同步当前序号仍标记文档变化供保存／恢复使用，但不会意外显示隐藏的画布；实际新增图形后仍按原规则显现。

本轮证据目录为 `.artifacts/preset-refinements-20261028/`，包含最初失败复现、独立构建、各组回归日志、当前 62 项双语图集和完整参数清单，以及截图面板中英明暗主题渲染。

### 本轮主目录验证结果

Release 全解决方案构建通过，**0 警告、0 错误**。**23 组、2,410 项检查全部通过**，当前结果以本轮 `verification-summary.json` 和 `final-regressions.json` 为准。

| 验证组 | 通过项数 |
| --- | ---: |
| capture-aspect-policy | 72 |
| number-start-link | 54 |
| symbol-style-presets | 93 |
| official-preset-design | 328 |
| complete-presets | 193 |
| presets | 61 |
| capture-selection | 61 |
| snapshot-image | 65 |
| preview4-shift | 164 |
| canvas-anchor-capture | 36 |
| signed-number | 57 |
| number-bounds | 12 |
| feedback4-options | 88 |
| numeric-controls | 184 |
| tool-options | 171 |
| hidden-tool-properties | 34 |
| segmented-choices | 348 |
| tool-state | 83 |
| preset-fallback | 82 |
| preset-isolation | 146 |
| tool-compatibility | 37 |
| gpu-settings（含本地化） | 29 |
| phase2-hidden-canvas | 12 |

所有应用检查运行于隔离 Windows 桌面和临时 profile；构建使用 `--artifacts-path H:\FloatCanvas\.artifacts\preset-refinements-20261028\build`，没有覆盖主目录原运行构建产物。本轮真实个人预设和工具状态文件的哈希、原有并发文件、Git 暂存内容以及运行实例均保持不变，见 `preservation-after.json`。

已实际查看 `capture-options-{zh-CN,en}-{light,dark}.png` 和当前双语预设图集，确认截图面板没有等比行，Circle 短标签、提示与辅助技术名称一致；五种符号样式可清晰区分。首次复现及后续拖动跨值、同会话文档替换的失败日志保留，临时诊断代码已清除。

编译和隔离测试能验证代码、图像输出与受控窗口行为；不等同于在真实显示器上的最终视觉验收。本次整体本地提交包含实现、必要测试和本报告；不涉及推送、发布或重启运行实例。
