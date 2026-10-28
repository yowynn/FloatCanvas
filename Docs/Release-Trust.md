# 发布包与完整性检查 / Packages and integrity checks

浮绘以无签名 Windows ZIP 包发布，不要求代码签名证书、发布签名密钥或独立签名清单。`PACKAGE-INFO.json` 将正式包标记为 `Unsigned`。下载、解压和应用内更新仍进行完整性与包结构检查。

FloatCanvas ships unsigned Windows ZIP packages. Publishing and updating do not require a code-signing certificate, publisher signing key or detached signature manifest. Release packages declare `Unsigned` in `PACKAGE-INFO.json`; download, archive and update integrity checks remain enabled.

## 发布内容 / Release assets

- `FloatCanvas-<version>-win-x64.zip`：完整应用、用户文档、许可证、依赖清单与 `PACKAGE-INFO.json`。
- `FloatCanvas-<version>-win-x64.zip.sha256`：ZIP 的 SHA-256 和准确文件名。
- ZIP 内的 `DEPENDENCIES.cdx.json`：基于锁定依赖生成的 CycloneDX SBOM。

Each release contains the complete ZIP and its `.sha256` companion. The ZIP includes application files, user documentation, licenses, a CycloneDX dependency inventory and an exact owned-file manifest. It requires the compatible .NET 10 Windows Desktop Runtime x64 installed on the system.

## 校验范围 / What is checked

发布流程从已有版本标签构建，核对标签、应用版本与提交一致；测试和发布使用锁定依赖。构建产物以不可覆写的 workflow artifact 传递给发布阶段。上传前再次校验 ZIP 的 SHA-256、文件名、内部版本、程序集版本、唯一根目录和完整文件清单，并确认远端标签仍指向同一提交。流程拒绝覆盖已有 GitHub Release 或资产。

The release workflow verifies the existing tag, application version and commit, uses locked dependencies and passes a unique build artifact to the publication job. Before publication it checks the ZIP checksum and package identity, validates its structure and file inventory, and rechecks the remote tag. Existing GitHub releases and assets are not overwritten.

客户端保留项目 GitHub 来源、HTTPS 下载地址、版本、架构、字节数与 SHA-256 检查；解压检查拒绝越界路径和非法包内容。安装保留事务备份与失败回滚，不将应用旁的无关文件当作包内文件删除。`UnsignedValidation` 本地验证包不能通过自动更新安装。

The updater retains the project's GitHub source checks, HTTPS download rules, version, architecture, byte-count and SHA-256 checks. Archive validation rejects unsafe paths and invalid package contents. Installation keeps transactional backups and rollback, and preserves unrelated files alongside the application. Local `UnsignedValidation` packages are not accepted for automatic installation.

SHA-256 用于发现下载内容损坏或与发行元数据不一致，不能单独证明发布者身份。来自同一来源的 ZIP、hash 或 GitHub digest 不是独立的发布者认证；当前发行包没有 Authenticode 或发布者签名验证。

SHA-256 detects corruption or a mismatch with release metadata. A ZIP and checksum from the same source, including a GitHub asset digest, do not independently authenticate the publisher. These releases have no Authenticode or publisher-signature verification.

## 本地构建与核对 / Local packaging and verification

使用 PowerShell 7.2+，在装有项目要求的 .NET SDK、MSVC 和 Windows SDK 的 Windows 环境执行：

```powershell
./scripts/Publish.ps1 -Configuration Release
./scripts/Smoke-Package.ps1 -ZipPath ./dist/FloatCanvas-0.3.0-win-x64.zip -StaticOnly -RequireRelease
Get-FileHash ./dist/FloatCanvas-0.3.0-win-x64.zip -Algorithm SHA256
Get-Content ./dist/FloatCanvas-0.3.0-win-x64.zip.sha256
```

将计算结果与 `.sha256` 中的 hash 比较。`-StaticOnly` 仅验证包内容，不启动应用；省略该参数可运行独立配置的启动 smoke。

Compare the computed hash with the `.sha256` file. Static smoke checks do not launch the application; omitting `-StaticOnly` also checks startup with a separate profile.

`-ValidationOnly` 保留本地验证用途，默认输出到 `dist-validation` 并标记 `UnsignedValidation`。正常发布默认输出到 `dist`，标记 `Unsigned`。两种模式均不签名；`-RequireRelease` 确保发布阶段不会误用本地验证包。

`-ValidationOnly` writes local test packages to `dist-validation` and marks them `UnsignedValidation`. Normal publication defaults to `dist` and marks packages `Unsigned`. Neither mode signs files; `-RequireRelease` prevents local validation packages from being published as releases.
