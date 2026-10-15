# 更新日志 / Changelog

## 0.1.1-preview.9 — 2026-10-16

- 默认 Windows Release 包不再附带 .NET 运行时，并排除调试符号文件（PDB），减小下载与解压体积。
- 启动需要兼容的 .NET 10 Windows Desktop Runtime x64。缺少时显示 .NET 安装提示，用户可前往微软下载并安装，完成后再次启动浮绘。

- The default Windows Release package no longer bundles the .NET runtime and excludes debug symbols (PDB files), reducing download and extracted size.
- A compatible .NET 10 Windows Desktop Runtime x64 is required. If it is missing, a .NET installation prompt links to Microsoft's download page; install the runtime, then start FloatCanvas again.

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
