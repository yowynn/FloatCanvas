#requires -Version 7.2
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$run = Join-Path $root ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$protector = Join-Path $PSScriptRoot 'Protect-Release.ps1'
$trustPath = Join-Path $run 'test-public-trust.json'
$zipPath = Join-Path $run 'FloatCanvas-1.0.0-win-x64.zip'
$manifestPath = "$zipPath.release.json"
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    @{ Keys = @(@{ KeyId = 'ephemeral-test'; PublicKeyPem = $rsa.ExportSubjectPublicKeyInfoPem() }); AuthenticodeSha256 = @() } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $trustPath
    function Write-Fixture([string]$DistributionTrust) {
        $stream = [IO.File]::Create($zipPath)
        try {
            $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                $entry = $archive.CreateEntry('FloatCanvas-1.0.0-win-x64/PACKAGE-INFO.json')
                $writer = [IO.StreamWriter]::new($entry.Open())
                try { $writer.Write((@{ DistributionTrust = $DistributionTrust } | ConvertTo-Json -Compress)) } finally { $writer.Dispose() }
            } finally { $archive.Dispose() }
        } finally { $stream.Dispose() }
        $payload = [Text.Encoding]::UTF8.GetBytes((@{ SchemaVersion = 1; Product = 'FloatCanvas'; Version = '1.0.0'; RuntimeIdentifier = 'win-x64'; FileName = 'FloatCanvas-1.0.0-win-x64.zip'; Size = (Get-Item -LiteralPath $zipPath).Length; Sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash } | ConvertTo-Json -Compress))
        @{ KeyId = 'ephemeral-test'; Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) } | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath
    }
    function Verify { & $protector -Mode VerifyManifest -ZipPath $zipPath -Version '1.0.0' -TrustPath $trustPath }
    function Reject([string]$Name, [scriptblock]$Action) {
        $rejected = $false
        try { & $Action } catch { $rejected = $true }
        if (-not $rejected) { throw "FAIL $Name" }
        Write-Host "PASS $Name"
    }
    Write-Fixture 'PublisherSigned'; Verify
    Write-Host 'PASS independently signed release manifest verified by publication script'
    [IO.File]::AppendAllText($zipPath, 'tampered')
    Reject 'modified ZIP refused' { Verify }
    Write-Fixture 'PublisherSigned'
    $envelope = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $envelope.Signature = [Convert]::ToBase64String([byte[]]::new(384)); $envelope | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
    Reject 'modified signature refused' { Verify }
    Write-Fixture 'UnsignedValidation'
    Reject 'validation archive refused even with a valid test signature' { Verify }
    Reject 'formal release blocked with empty installed trust' { & $protector -Mode Preflight }
    Write-Host 'All 5 publication trust checks passed; no private key was saved or certificate installed.'
} finally { $rsa.Dispose() }
