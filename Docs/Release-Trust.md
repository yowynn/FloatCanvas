# 发布包与完整性检查 / Packages and integrity checks

浮绘以无签名 Windows ZIP 和 EXE 安装包发布，不要求代码签名证书、发布签名密钥或独立签名清单。两种包使用相同的程序文件，`PACKAGE-INFO.json` 将正式包标记为 `Unsigned`。下载、解压和应用内更新仍进行完整性与包结构检查。双格式发布从 0.3.1 开始，已发布的 0.3.0 ZIP 及历史资产不变。

FloatCanvas ships unsigned Windows ZIP and EXE installer packages containing the same application files. Publishing and updating do not require a code-signing certificate, publisher signing key or detached signature manifest. Release packages declare `Unsigned` in `PACKAGE-INFO.json`; download, archive and update integrity checks remain enabled. Dual-format publication starts with 0.3.1; the published 0.3.0 ZIP and historical assets remain unchanged.

## 发布内容 / Release assets

- `FloatCanvas-<version>-win-x64.zip`：完整应用、用户文档、许可证、依赖清单与 `PACKAGE-INFO.json`。
- `FloatCanvas-<version>-win-x64.zip.sha256`：ZIP 的 SHA-256 和准确文件名。
- `FloatCanvas-<version>-win-x64-Setup.exe`：从同一 ZIP 构建的安装程序，支持当前用户／所有用户范围、运行时下载引导、快捷方式及卸载。
- `FloatCanvas-<version>-win-x64-Setup.exe.sha256`：EXE 安装包的 SHA-256 和准确文件名。
- ZIP 内的 `DEPENDENCIES.cdx.json`：基于锁定依赖生成的 CycloneDX SBOM。

Each release contains the complete ZIP, an EXE installer built from that ZIP, and a `.sha256` companion for each format. The ZIP includes application files, user documentation, licenses, a CycloneDX dependency inventory and an exact owned-file manifest. Setup supports current-user or all-users installation, runtime download/install, shortcuts and uninstall. Both formats require a compatible .NET 10 Windows Desktop Runtime x64; it is downloaded on demand by Setup and is not embedded in either package.

## 校验范围 / What is checked

发布流程从已有版本标签构建，核对标签、应用版本与提交一致；测试和发布使用锁定依赖。安装包只使用已验证的 ZIP 内容，编译器固定为 Inno Setup 6.7.4，下载时校验固定 SHA-256。构建产物以不可覆写的 workflow artifact 传递给发布阶段。上传前再次校验 ZIP 与 EXE 各自的 SHA-256，以及 ZIP 的文件名、内部版本、程序集版本、唯一根目录和完整文件清单，并确认远端标签仍指向同一提交。流程拒绝覆盖已有 GitHub Release 或资产。

The release workflow verifies the existing tag, application version and commit and uses locked dependencies. The installer uses only the verified ZIP payload and a pinned Inno Setup 6.7.4 compiler whose download is checked against a fixed SHA-256. A unique build artifact passes the packages to the publication job. Before publication it checks both checksums, validates the ZIP's identity, structure and file inventory, and rechecks the remote tag. Existing GitHub releases and assets are not overwritten.

安装器构建时从微软 .NET 10 官方发布元数据选择稳定的 Windows x64 Desktop Runtime，核对下载文件的官方 SHA-512，再将确定的版本、HTTPS URL 和 SHA-256 固化到 Setup。用户点击运行时安装后，下载完成后和执行前均检查 SHA-256；命令行不能替换下载地址、校验值或可执行文件。运行时安装允许 Windows 请求管理员确认，检测成功才继续，不自动重启；静默安装必须显式传入 `/INSTALLRUNTIME` 才会安装共享运行时。这些完整性检查不改变浮绘自身无签名发行的规则。

During packaging, Setup selects a stable Windows x64 Desktop Runtime from Microsoft's official .NET 10 release metadata, verifies the download against its published SHA-512, and embeds that version, HTTPS URL and SHA-256. The runtime download is checked again before execution; command-line arguments cannot replace the pinned URL, hash or executable. Windows may request administrator approval. Setup continues only after runtime detection succeeds, never automatically restarts Windows, and requires explicit `/INSTALLRUNTIME` consent for silent runtime installation. These integrity checks do not change FloatCanvas's unsigned distribution policy.

客户端保留项目 GitHub 来源、HTTPS 下载地址、版本、架构、字节数与 SHA-256 检查；解压检查拒绝越界路径和非法包内容。当前用户／ZIP 更新保留事务备份与失败回滚，不将应用旁的无关文件当作包内文件删除，并为当前用户安装刷新卸载文件清单。`UnsignedValidation` 本地 ZIP 验证包不能通过自动更新安装。

The updater retains the project's GitHub source checks, HTTPS download rules, version, architecture, byte-count and SHA-256 checks. Archive validation rejects unsafe paths and invalid package contents. Current-user/ZIP updates keep transactional backups and rollback, preserve unrelated files alongside the application and refresh the current-user uninstall file inventory. Local `UnsignedValidation` ZIPs are not accepted for automatic installation.

所有用户安装通过同一项目发布中的 Setup 更新，单独验证其资产名称、版本、大小和 SHA-256，执行前再次校验并锁定文件。普通应用和 ZIP 更新助手不提权；Setup 自行请求管理员确认并发送就绪信号后，浮绘才退出，完成后可用原用户身份恢复画布。安装登记、系统集成和个人资料清理按明确范围处理；卸载器根据当前清单删除程序文件，个人资料默认保留，选择删除时逐目录确认，清理失败明确报告。

All-users installations update through Setup from the same project release, with separate asset-name, version, size and SHA-256 checks and a locked recheck before execution. Neither the normal app nor its ZIP worker is elevated. Setup requests administrator approval and signals readiness before FloatCanvas exits, then can resume the canvas as the original user. Registration, shell integration and personal-data cleanup respect their scopes. The uninstaller uses the current file inventory, retains personal data by default, confirms selected data directories and reports cleanup failures.

SHA-256 用于发现下载内容损坏或与发行元数据不一致，不能单独证明发布者身份。来自同一来源的 ZIP、hash 或 GitHub digest 不是独立的发布者认证；当前发行包没有 Authenticode 或发布者签名验证。

SHA-256 detects corruption or a mismatch with release metadata. A ZIP and checksum from the same source, including a GitHub asset digest, do not independently authenticate the publisher. These releases have no Authenticode or publisher-signature verification.

## 本地构建与核对 / Local packaging and verification

使用 PowerShell 7.2+，在装有项目要求的 .NET SDK、MSVC 和 Windows SDK 的 Windows 环境执行：

```powershell
./scripts/Publish.ps1 -Configuration Release
./scripts/Smoke-Package.ps1 -ZipPath ./dist/FloatCanvas-0.3.1-win-x64.zip -StaticOnly -RequireRelease
./scripts/Publish-Installer.ps1 -ZipPath ./dist/FloatCanvas-0.3.1-win-x64.zip
./scripts/Test-InstallerRuntime.ps1
./scripts/Test-InstallerBootstrap.ps1
./scripts/Test-InstallerLifecycle.ps1
Get-FileHash ./dist/FloatCanvas-0.3.1-win-x64.zip -Algorithm SHA256
Get-Content ./dist/FloatCanvas-0.3.1-win-x64.zip.sha256
Get-FileHash ./dist/FloatCanvas-0.3.1-win-x64-Setup.exe -Algorithm SHA256
Get-Content ./dist/FloatCanvas-0.3.1-win-x64-Setup.exe.sha256
```

将计算结果分别与各自 `.sha256` 中的 hash 比较，示例版本号应与本次构建一致。`-StaticOnly` 仅验证包内容，不启动应用；省略该参数可运行独立配置的启动 smoke。生成安装包不会安装或启动浮绘。

Compare each computed hash with its own `.sha256` file, and adjust the example version to match your build. Static smoke checks do not launch the application; omitting `-StaticOnly` also checks startup with a separate profile. Building the installer does not install or launch FloatCanvas.

`-ValidationOnly` 保留本地验证用途，默认输出到 `dist-validation` 并标记 `UnsignedValidation`。正常发布默认输出到 `dist`，标记 `Unsigned`。两种模式均不签名；`-RequireRelease` 确保发布阶段不会误用本地验证包。

`-ValidationOnly` writes local test packages to `dist-validation` and marks them `UnsignedValidation`. Normal publication defaults to `dist` and marks packages `Unsigned`. Neither mode signs files; `-RequireRelease` prevents local validation packages from being published as releases.
