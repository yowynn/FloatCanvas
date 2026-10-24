# 发布者签名与更新信任 / Publisher trust

当前仓库尚未配置正式发布公钥、代码签名证书或签名服务。`src/FloatCanvas.App/ReleaseTrust.json` 的白名单为空。正式发布被阻断，客户端不能自动安装缺少签名或不受信任的发行包；这不表示现有下载曾遭篡改，也不表示当前源码已经获得发行者认证。

Current production trust is deliberately unconfigured. Formal publishing fails closed, and the updater rejects unsigned releases or unknown signing keys. Source builds and tests remain available. A successful build is not publisher certification.

## 正式发布条件 / Formal release prerequisites

1. 取得真实代码签名身份并在受控签名机器提供其私钥。审核证书 SHA-256 指纹并提交到 `AuthenticodeSha256`。脚本只读取已有的 `CurrentUser/My` 身份，不创建证书、不安装根证书、不修改信任存储。
2. 在独立受控位置生成并保管至少 3072 位 RSA 发布清单密钥。只将公钥 PEM 与唯一 `KeyId` 提交到 `Keys`，并先通过可信渠道交付包含该公钥的客户端。不得把私钥提交仓库。首次从旧版升级需要可信的带签名完整包；旧版同源 hash 校验不会因此追溯变成发布者认证。
3. 提供环境变量 `FLOATCANVAS_RELEASE_KEY_ID`、`FLOATCANVAS_RELEASE_PRIVATE_KEY_PATH`、`FLOATCANVAS_SIGNING_CERT_THUMBPRINT`（已有证书的 SHA-1 选择指纹）、`FLOATCANVAS_TIMESTAMP_URL`（HTTPS RFC3161 服务）；可用 `FLOATCANVAS_SIGNTOOL_PATH` 指定 Windows SDK SignTool。公钥与证书 SHA-256 必须匹配仓库白名单，环境变量无法覆盖客户端信任。
4. 使用 PowerShell 7.2+ 执行 `scripts/Publish.ps1`。自有 `FloatCanvas*.exe` / `FloatCanvas*.dll`（包括子目录中的自有资源 DLL）采用 Authenticode SHA-256 与 RFC3161 SHA-256 时间戳签名，并经 Windows Authenticode 策略、时间戳存在性和证书白名单验证。第三方文件由完整 ZIP 的独立签名覆盖。

The release runner must already have the approved signing identity. The workflow currently has no usable identity configured and cannot publish. It does not provision certificates. A future signing-service integration must retain the same publisher and timestamp checks.

## 清单与离线核验 / Manifest and offline verification

`FloatCanvas-<version>-win-x64.zip.release.json` 包含 `KeyId`、base64 原始 UTF-8 `Payload` 和 base64 `Signature`。签名采用 RSA-PSS / SHA-256，覆盖原始 payload 字节；payload 绑定 schema 1、`FloatCanvas`、版本、`win-x64`、ZIP 文件名、字节数和 SHA-256。客户端使用内嵌公钥验证，之后才下载 ZIP，并继续检查 hash、路径、大小、解压与包清单。GitHub 的 digest 与 `.sha256` 仍必须一致。

离线核验时，从已经信任的源码版本取得公钥与脚本，执行：

```powershell
./scripts/Protect-Release.ps1 -Mode VerifyManifest -ZipPath ./FloatCanvas-1.0.0-win-x64.zip -Version 1.0.0
```

Verification requires no private key and rejects unknown keys, altered signatures, changed ZIP bytes, version/architecture/name/size mismatches, and unsigned validation packages. The trust configuration must come from an already trusted source; downloading a replacement public key beside an untrusted ZIP does not establish publisher identity.

## 轮换、撤销和验收 / Rotation, revocation and acceptance

轮换应先由现有受信任版本交付新公钥，再用新 KeyId 签后续版本。撤销应在可信客户端更新中移除旧 KeyId；尚未更新的离线客户端仍保留旧信任，需要通过可信渠道更换客户端。这里没有实时撤销服务，也不承诺旧版客户端立即感知密钥失窃。

`-ValidationOnly` 仅用于离线 package/smoke，默认输出到 `dist-validation`，包标记 `UnsignedValidation`，不生成发布签名，客户端和 Release workflow 拒绝把它作为正式发行。它不能覆盖已有签名发行的同一路径。

Tests use an ephemeral in-process RSA key; they do not create a production key or a certificate. Actual trusted-certificate signing, RFC3161 responses, expiration-with-timestamp policy, and the configured release service remain external acceptance requirements. Microsoft documents the relevant [SignTool signing and verification options](https://learn.microsoft.com/windows/win32/seccrypto/signtool) and [.NET RSA-PSS padding](https://learn.microsoft.com/dotnet/api/system.security.cryptography.rsasignaturepaddingmode?view=net-10.0).
