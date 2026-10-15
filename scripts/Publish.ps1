[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/FloatCanvas.App/FloatCanvas.App.csproj') -Raw
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'A valid release version is required.' }
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$packageName = "FloatCanvas-$Version-win-x64"
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (((Get-Item -LiteralPath $distRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The dist directory must be a real workspace directory, not a symbolic link or junction.'
}

function Assert-DistPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = $distRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate a path outside the resolved dist directory: $absolute"
    }
    if ((Test-Path -LiteralPath $absolute) -and (((Get-Item -LiteralPath $absolute).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing to mutate a symbolic link or junction: $absolute"
    }
    return $absolute
}

$runId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Assert-DistPath (Join-Path $distRoot ".publish-$runId")
$stagingPackage = Assert-DistPath (Join-Path $stagingRoot $packageName)
$stagingZip = Assert-DistPath (Join-Path $distRoot ".publish-$runId.zip")
$finalPackage = Assert-DistPath (Join-Path $distRoot $packageName)
$finalZip = Assert-DistPath (Join-Path $distRoot ($packageName + '.zip'))
$backupPackage = Assert-DistPath (Join-Path $distRoot ".previous-$runId")
$backupZip = Assert-DistPath (Join-Path $distRoot ".previous-$runId.zip")
$newPackageInstalled = $false
$newZipInstalled = $false
$replacementCommitted = $false
Push-Location $projectRoot
try {
    # Isolate RID-specific lock files in ignored obj folders, preserving committed dependency locks.
    & dotnet publish 'src/FloatCanvas.App/FloatCanvas.App.csproj' -c $Configuration -r win-x64 --self-contained true -o $stagingPackage `
        "-p:Version=$Version" '-p:PublishTrimmed=false' '-p:PublishSingleFile=false' '-p:NuGetLockFilePath=obj/packages.publish.win-x64.lock.json'
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    foreach ($required in @('FloatCanvas.exe', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingPackage $required))) { throw "Self-contained package is missing $required" }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Docs/User-Guide.md') -Destination (Join-Path $stagingPackage 'User-Guide.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Docs/User-Guide.en.md') -Destination (Join-Path $stagingPackage 'User-Guide.en.md')
    $publicDocs = Join-Path $stagingPackage 'Docs'
    New-Item -ItemType Directory -Path $publicDocs -Force | Out-Null
    foreach ($guide in @('User-Guide.md', 'User-Guide.en.md')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $projectRoot 'Docs') $guide) -Destination $publicDocs
    }
    foreach ($publicFile in @('README.md', 'README.en.md', 'LICENSE', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $publicFile) -Destination $stagingPackage
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $stagingPackage 'licenses') -Recurse

    # Match runtime legal notices to the exact versions actually included by this publish.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stagingPackage 'FloatCanvas.runtimeconfig.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath 'src/FloatCanvas.App/obj/project.assets.json' -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $library.Name -match '(?i)NativeAssets\.macOS/') { continue }
        $licenseFolder = $library.Name.Replace('/', '-')
        if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $stagingPackage 'licenses') $licenseFolder))) {
            throw "No reviewed license directory exists for dependency $($library.Name). Collect it before publishing."
        }
    }
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    $runtimeNotices = @()
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $id = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
        $source = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder (Join-Path $id $framework.version)
            if (Test-Path -LiteralPath $candidate) { $source = $candidate; break }
        }
        if ($null -eq $source) { throw "Could not locate legal notices for included runtime $id/$($framework.version)" }
        $destination = Join-Path (Join-Path $stagingPackage 'licenses') ($id + '-' + $framework.version)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $files = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '(?i)^(license(\..*)?|third[-_]?party[-_]?notices(\..*)?)$' -or $_.Extension -eq '.nuspec' })
        if (@($files | Where-Object Name -Match '(?i)^license').Count -eq 0) { throw "Runtime license file missing: $source" }
        foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) }
        $runtimeNotices += [pscustomobject]@{ Package = $id; Version = $framework.version; Files = @($files.Name) }
    }
    $manifest = [pscustomobject]@{
        Product = '浮绘 FloatCanvas'; Version = $Version; RuntimeIdentifier = 'win-x64'; SelfContained = $true
        Configuration = $Configuration; PublishedAt = [DateTimeOffset]::Now.ToString('o'); RuntimeLegalNotices = $runtimeNotices
        Validation = 'Preview build. See User-Guide.md for platform support and feature limitations.'
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stagingPackage 'PACKAGE-INFO.json') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $stagingZip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # Keep the previous complete distribution recoverable until the new folder and ZIP both exist.
    if (Test-Path -LiteralPath $finalPackage) { Move-Item -LiteralPath (Assert-DistPath $finalPackage) -Destination (Assert-DistPath $backupPackage) }
    if (Test-Path -LiteralPath $finalZip) { Move-Item -LiteralPath (Assert-DistPath $finalZip) -Destination (Assert-DistPath $backupZip) }
    Move-Item -LiteralPath (Assert-DistPath $stagingPackage) -Destination (Assert-DistPath $finalPackage)
    $newPackageInstalled = $true
    Move-Item -LiteralPath (Assert-DistPath $stagingZip) -Destination (Assert-DistPath $finalZip)
    $newZipInstalled = $true
    $zipHash = (Get-FileHash -LiteralPath $finalZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $packageName.zip" | Set-Content -LiteralPath ($finalZip + '.sha256') -Encoding ASCII
    $replacementCommitted = $true
    foreach ($backup in @($backupPackage, $backupZip)) {
        if (Test-Path -LiteralPath $backup) {
            try { Remove-Item -LiteralPath (Assert-DistPath $backup) -Recurse -Force }
            catch { Write-Warning "New distribution is complete; a previous output could not be cleaned: $backup" }
        }
    }
    Write-Host "Application: $(Join-Path $finalPackage 'FloatCanvas.exe')"
    Write-Host "ZIP: $finalZip"
    Write-Host "SHA256: $zipHash"
}
catch {
    # Roll back only this invocation's completed replacement steps.
    if (-not $replacementCommitted) {
        if ($newZipInstalled -and (Test-Path -LiteralPath $finalZip)) { Remove-Item -LiteralPath (Assert-DistPath $finalZip) -Force }
        if ($newPackageInstalled -and (Test-Path -LiteralPath $finalPackage)) { Remove-Item -LiteralPath (Assert-DistPath $finalPackage) -Recurse -Force }
        if (Test-Path -LiteralPath $backupPackage) { Move-Item -LiteralPath (Assert-DistPath $backupPackage) -Destination (Assert-DistPath $finalPackage) }
        if (Test-Path -LiteralPath $backupZip) { Move-Item -LiteralPath (Assert-DistPath $backupZip) -Destination (Assert-DistPath $finalZip) }
    }
    throw
}
finally {
    foreach ($temporary in @($stagingRoot, $stagingZip)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath (Assert-DistPath $temporary) -Recurse -Force }
    }
    Pop-Location
}
