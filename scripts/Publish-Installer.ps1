#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-build')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Split-Path -Parent $zip }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work, $output -Force | Out-Null
foreach ($path in @($work, $output)) {
    if (((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Installer build paths must not be links or junctions.' }
}
# This exact extracted payload is compiled into Setup; there is no second app build.
& (Join-Path $PSScriptRoot 'Smoke-Package.ps1') -ZipPath $zip -ExtractionRoot (Join-Path $work 'payload') -StaticOnly -RequireRelease
$manifests = @(Get-ChildItem -LiteralPath (Join-Path $work 'payload') -Filter PACKAGE-INFO.json -Recurse -File)
if ($manifests.Count -ne 1) { throw 'Expected one verified package manifest.' }
$payload = $manifests[0].DirectoryName
$manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
# Keep compiler input paths below legacy MAX_PATH even when evidence roots are long.
$shortPayload = Join-Path $work 'p'
if (-not $payload.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The verified payload must stay inside this build directory.'
}
Move-Item -LiteralPath $payload -Destination $shortPayload
$payload = $shortPayload
$ownedList = Join-Path $work 'FloatCanvas.install-files.txt'
[IO.File]::WriteAllLines($ownedList, [string[]]$manifest.Files, [Text.UTF8Encoding]::new($false))
if (-not $CompilerPath) { $CompilerPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$version = [string]$manifest.Version
$numericVersion = ($version -split '-', 2)[0] + '.0'
$name = "FloatCanvas-$version-win-x64-Setup"
$runtime = & (Join-Path $PSScriptRoot 'Get-DotNetRuntime.ps1') -CacheDirectory (Join-Path $WorkingDirectory 'runtime-cache')
$stagingOutput = Join-Path $work 'compiled'
New-Item -ItemType Directory -Path $stagingOutput -Force | Out-Null
$arguments = @('/Qp', "/DAppVersion=$version", "/DFileVersion=$numericVersion", "/DPayloadDirectory=$payload", "/DOwnedList=$ownedList", "/DOutputDirectory=$stagingOutput", "/DOutputName=$name",
    "/DRuntimeInstallerVersion=$($runtime.Version)", "/DRuntimeInstallerUrl=$($runtime.Url)", "/DRuntimeInstallerSha256=$($runtime.Sha256)",
    (Join-Path $PSScriptRoot 'installer/FloatCanvas.iss'))
& $CompilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$compiled = Join-Path $stagingOutput "$name.exe"
if (-not (Test-Path -LiteralPath $compiled)) { throw 'The installer compiler produced no executable.' }
$installer = Join-Path $output "$name.exe"
$hash = (Get-FileHash -LiteralPath $compiled -Algorithm SHA256).Hash.ToLowerInvariant()
# A failed compile leaves a previous installer and checksum intact.
Move-Item -LiteralPath $compiled -Destination $installer -Force
"$hash  $name.exe" | Set-Content -LiteralPath "$installer.sha256" -Encoding ASCII
Write-Host "Published unsigned installer: $installer"
[pscustomobject]@{ Installer = $installer; Sha256 = $hash; Zip = $zip; PayloadDirectory = $payload; Version = $version }
