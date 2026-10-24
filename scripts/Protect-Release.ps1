#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Preflight', 'SignPackage', 'SignManifest', 'VerifyManifest')][string]$Mode,
    [string]$PackageDirectory,
    [string]$ZipPath,
    [string]$Version,
    [string]$ManifestPath,
    [string]$TrustPath = (Join-Path $PSScriptRoot '../src/FloatCanvas.App/ReleaseTrust.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$trust = Get-Content -LiteralPath $TrustPath -Raw | ConvertFrom-Json
if (@($trust.Keys).Count -eq 0) { throw 'No reviewed publisher key is configured in ReleaseTrust.json. Formal release is blocked.' }

function Get-PublisherKey([string]$Id) {
    $keys = @($trust.Keys | Where-Object { $_.KeyId -ceq $Id })
    if ($keys.Count -ne 1) { throw 'The release signing KeyId is not uniquely trusted.' }
    $rsa = [Security.Cryptography.RSA]::Create()
    $rsa.ImportFromPem($keys[0].PublicKeyPem)
    if ($rsa.KeySize -lt 3072) { $rsa.Dispose(); throw 'Release RSA keys must have at least 3072 bits.' }
    return $rsa
}
function Assert-SignedManifest {
    if ((Get-Item -LiteralPath $ManifestPath).Length -gt 32768) { throw 'The signed manifest is too large.' }
    $envelope = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $rsa = Get-PublisherKey $envelope.KeyId
    try {
        $payload = [Convert]::FromBase64String($envelope.Payload)
        if (-not $rsa.VerifyData($payload, [Convert]::FromBase64String($envelope.Signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Release signature verification failed.' }
        $value = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
        $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
        if ($value.SchemaVersion -ne 1 -or $value.Product -cne 'FloatCanvas' -or $value.Version -cne $Version -or
            $value.RuntimeIdentifier -cne 'win-x64' -or $value.FileName -cne "FloatCanvas-$Version-win-x64.zip" -or
            $value.Size -ne (Get-Item -LiteralPath $ZipPath).Length -or $value.Sha256 -ine $hash) { throw 'The signed release does not match this ZIP, version, architecture, size or hash.' }
    } finally { $rsa.Dispose() }
    Assert-PackageTrustMarker
}
function Assert-PackageTrustMarker {
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.GetEntry("FloatCanvas-$Version-win-x64/PACKAGE-INFO.json")
        if ($null -eq $entry -or $entry.Length -gt 4194304) { throw 'The signed ZIP package metadata is missing or too large.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $package = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($package.DistributionTrust -cne 'PublisherSigned') { throw 'Unsigned validation packages must never be signed or published as formal releases.' }
    } finally { $archive.Dispose() }
}
if ($Mode -eq 'VerifyManifest') {
    if ([string]::IsNullOrEmpty($ManifestPath)) { $ManifestPath = "$ZipPath.release.json" }
    Assert-SignedManifest
    Write-Host 'Publisher signature and ZIP identity verified.'
    return
}

# Signing inputs are provided externally; no keys/certificates or trust-store entries are generated here.
foreach ($name in @('FLOATCANVAS_RELEASE_KEY_ID', 'FLOATCANVAS_RELEASE_PRIVATE_KEY_PATH', 'FLOATCANVAS_SIGNING_CERT_THUMBPRINT', 'FLOATCANVAS_TIMESTAMP_URL')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Required signing configuration is missing: $name" }
}
$public = Get-PublisherKey $env:FLOATCANVAS_RELEASE_KEY_ID
$private = [Security.Cryptography.RSA]::Create()
try {
    $private.ImportFromPem([IO.File]::ReadAllText($env:FLOATCANVAS_RELEASE_PRIVATE_KEY_PATH))
    if ($private.KeySize -lt 3072 -or $private.ExportSubjectPublicKeyInfoPem() -cne $public.ExportSubjectPublicKeyInfoPem()) { throw 'The supplied private key does not match the reviewed publisher key.' }
    # Fail before touching output if this PEM contains no usable private key.
    $probe = [Text.Encoding]::UTF8.GetBytes('FloatCanvas release signing preflight')
    $null = $private.SignData($probe, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    $thumbprint = $env:FLOATCANVAS_SIGNING_CERT_THUMBPRINT
    if ($thumbprint -notmatch '\A[0-9a-fA-F]{40}\z') { throw 'A certificate SHA1 thumbprint is required to select the existing signing identity.' }
    $cert = Get-Item -LiteralPath "Cert:/CurrentUser/My/$thumbprint"
    $certHash = $cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    if (-not $cert.HasPrivateKey -or $certHash -inotmatch '\A[0-9a-f]{64}\z' -or $certHash -notin @($trust.AuthenticodeSha256)) { throw 'The available Authenticode signing identity is not the reviewed certificate.' }
    $timestamp = $null
    if (-not [Uri]::TryCreate($env:FLOATCANVAS_TIMESTAMP_URL, [UriKind]::Absolute, [ref]$timestamp) -or $timestamp.Scheme -ne 'https') { throw 'An HTTPS RFC3161 timestamp endpoint is required.' }
    $signtool = $env:FLOATCANVAS_SIGNTOOL_PATH
    if ([string]::IsNullOrWhiteSpace($signtool)) {
        $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" -File | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrEmpty($signtool) -or -not (Test-Path -LiteralPath $signtool -PathType Leaf)) { throw 'The Windows SDK SignTool is required for a formal release.' }
    if ($Mode -eq 'Preflight') { Write-Host 'Reviewed signing identities are available.'; return }
    if ($Mode -eq 'SignPackage') {
        $owned = @(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | Where-Object { $_.Name -like 'FloatCanvas*' -and $_.Extension -in @('.exe', '.dll') })
        if ($owned.Count -lt 2) { throw 'The application binaries are missing from the signing stage.' }
        foreach ($file in $owned) {
            & $signtool sign /sha1 $thumbprint /s My /fd SHA256 /tr $timestamp.AbsoluteUri /td SHA256 $file.FullName
            if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $($file.Name)" }
            & $signtool verify /pa /all /tw $file.FullName
            if ($LASTEXITCODE -ne 0) { throw "Authenticode/timestamp validation failed: $($file.Name)" }
            $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -cne $certHash) { throw "Untrusted or untimestamped binary: $($file.Name)" }
        }
        return
    }
    if ($Version -notmatch '\A\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?\z') { throw 'A release version is required.' }
    Assert-PackageTrustMarker
    if ([string]::IsNullOrEmpty($ManifestPath)) { $ManifestPath = "$ZipPath.release.json" }
    $payload = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ SchemaVersion = 1; Product = 'FloatCanvas'; Version = $Version; RuntimeIdentifier = 'win-x64'; FileName = "FloatCanvas-$Version-win-x64.zip"; Size = (Get-Item -LiteralPath $ZipPath).Length; Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json -Compress))
    $envelope = [ordered]@{ KeyId = $env:FLOATCANVAS_RELEASE_KEY_ID; Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($private.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) }
    $envelope | ConvertTo-Json -Compress | Set-Content -LiteralPath $ManifestPath -Encoding utf8NoBOM
    Assert-SignedManifest
} finally { $public.Dispose(); $private.Dispose() }
