#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$CacheDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-dotnet-runtime'),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve only the supported stable desktop-runtime channel. The official
# release metadata pins SHA-512; the compiled installer uses the corresponding
# SHA-256, so the download is verified even if a redirect changes later.
$metadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60
if (-not $Version) { $Version = [string]$metadata.'latest-runtime' }
if ($Version -notmatch '^10\.0\.(0|[1-9][0-9]*)$') { throw 'Expected a stable .NET 10 runtime version.' }
$releases = @($metadata.releases | Where-Object { $_.'release-version' -eq $Version })
if ($releases.Count -ne 1 -or [string]$releases[0].windowsdesktop.version -ne $Version) { throw 'The desktop runtime release is missing or ambiguous.' }
$files = @($releases[0].windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'windowsdesktop-runtime-win-x64.exe' })
if ($files.Count -ne 1) { throw 'Expected one official x64 desktop runtime installer.' }
$file = $files[0]
$filename = "windowsdesktop-runtime-$Version-win-x64.exe"
$url = [string]$file.url
$expectedUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$Version/$filename"
if ($url -cne $expectedUrl -or [string]$file.hash -notmatch '^[A-Fa-f0-9]{128}$') { throw 'Unexpected runtime URL or checksum in official metadata.' }

$cache = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (((Get-Item -LiteralPath $cache).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The runtime cache must not be a link or junction.' }
$installer = Join-Path $cache $filename
$sha512 = ([string]$file.hash).ToLowerInvariant()
if (Test-Path -LiteralPath $installer) {
    if (((Get-Item -LiteralPath $installer).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The cached runtime must not be a link.' }
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash -ine $sha512) { throw 'The cached runtime does not match Microsoft release metadata.' }
} else {
    $temporary = Join-Path $cache ("$filename." + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $url -OutFile $temporary -TimeoutSec 300
        $length = (Get-Item -LiteralPath $temporary).Length
        if ($length -le 0 -or $length -gt 268435456) { throw 'The runtime installer size is invalid.' }
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA512).Hash -ine $sha512) { throw 'The downloaded runtime does not match Microsoft release metadata.' }
        Move-Item -LiteralPath $temporary -Destination $installer
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
[pscustomobject]@{
    Version = $Version
    Url = $url
    Sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    Sha512 = $sha512
    InstallerPath = $installer
    MetadataUrl = $metadataUrl
}
