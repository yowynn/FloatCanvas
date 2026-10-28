#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version,
    [string]$OutputDirectory,
    [string]$ArtifactsPath,
    [switch]$ValidationOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/FloatCanvas.App/FloatCanvas.App.csproj') -Raw
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'A valid release version is required.' }
if (-not $ValidationOnly) {
    if ($Configuration -ne 'Release') { throw 'Formal publication requires Release configuration. Use -ValidationOnly for local package validation.' }
}
$distRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $(if ($ValidationOnly) { 'dist-validation' } else { 'dist' })))
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$artifactsRoot = if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) { $null } else { [IO.Path]::GetFullPath($ArtifactsPath) }
$assetsPath = if ($null -eq $artifactsRoot) {
    Join-Path $projectRoot 'src/FloatCanvas.App/obj/project.assets.json'
} else {
    Join-Path $artifactsRoot 'obj/FloatCanvas.App/project.assets.json'
}
$packageName = "FloatCanvas-$Version-win-x64"
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (((Get-Item -LiteralPath $distRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The output directory must be a real directory, not a symbolic link or junction.'
}
$distRoot = (Resolve-Path -LiteralPath $distRoot).Path

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
if ($ValidationOnly -and ((Test-Path -LiteralPath $finalPackage) -or (Test-Path -LiteralPath $finalZip))) {
    $previousManifest = Join-Path $finalPackage 'PACKAGE-INFO.json'
    if (-not (Test-Path -LiteralPath $previousManifest -PathType Leaf) -or
        (Get-Content -LiteralPath $previousManifest -Raw | ConvertFrom-Json).DistributionTrust -cne 'UnsignedValidation') {
        throw 'A validation package cannot replace a release output. Choose a separate OutputDirectory.'
    }
}
$backupPackage = Assert-DistPath (Join-Path $distRoot ".previous-$runId")
$backupZip = Assert-DistPath (Join-Path $distRoot ".previous-$runId.zip")
$newPackageInstalled = $false
$newZipInstalled = $false
$replacementCommitted = $false
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'Test-PublishDependencies.ps1') -ProjectRoot $projectRoot
    # RID locks are reviewed alongside normal test locks; publishing cannot silently resolve a new graph.
    $publishArguments = @('publish', 'src/FloatCanvas.App/FloatCanvas.App.csproj', '-c', $Configuration,
        '-r', 'win-x64', '--self-contained', 'false', '-o', $stagingPackage,
        "-p:Version=$Version", '-p:UseAppHost=true', '-p:DebugSymbols=false', '-p:DebugType=None',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:NuGetLockFilePath=packages.win-x64.lock.json', '-p:RestoreLockedMode=true')
    if ($null -ne $artifactsRoot) { $publishArguments += @('--artifacts-path', $artifactsRoot) }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    # Keep startup recovery independent of the managed files being replaced.
    Move-Item -LiteralPath (Join-Path $stagingPackage 'FloatCanvas.exe') -Destination (Join-Path $stagingPackage 'FloatCanvas.Host.exe')
    $launcherBuild = Assert-DistPath (Join-Path $distRoot ".launcher-$runId")
    & (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -OutputDirectory $launcherBuild
    Copy-Item -LiteralPath (Join-Path $launcherBuild 'FloatCanvas.exe') -Destination $stagingPackage
    foreach ($required in @('FloatCanvas.exe', 'FloatCanvas.dll', 'FloatCanvas.Core.dll', 'FloatCanvas.Localization.dll',
        'FloatCanvas.Rendering.dll', 'FloatCanvas.Storage.dll', 'FloatCanvas.Windows.dll', 'FloatCanvas.SystemOverlay.dll', 'SkiaSharp.dll',
        'SkiaSharp.HarfBuzz.dll', 'HarfBuzzSharp.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll',
        'FloatCanvas.deps.json', 'FloatCanvas.runtimeconfig.json')) {
        $requiredPath = Join-Path $stagingPackage $required
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
            throw "Framework-dependent package is missing $required"
        }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stagingPackage 'FloatCanvas.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($null -ne $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) {
        throw 'Framework-dependent publish must not declare included runtime frameworks.'
    }
    $frameworkProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']
    if ($null -eq $frameworkProperty) { throw 'Framework-dependent publish must declare its required runtime frameworks.' }
    $requiredFrameworks = @($frameworkProperty.Value)
    if ($requiredFrameworks.Count -ne 2 -or @($requiredFrameworks.name | Sort-Object -Unique).Count -ne 2 -or
        'Microsoft.NETCore.App' -notin $requiredFrameworks.name -or 'Microsoft.WindowsDesktop.App' -notin $requiredFrameworks.name) {
        throw 'The package must require both .NET and Windows Desktop runtime frameworks.'
    }
    foreach ($framework in $requiredFrameworks) {
        $frameworkVersion = $null
        if (-not [Version]::TryParse([string]$framework.version, [ref]$frameworkVersion)) {
            throw "Required runtime framework has an invalid version: $($framework.name)"
        }
    }
    $publicDocs = Join-Path $stagingPackage 'Docs'
    New-Item -ItemType Directory -Path $publicDocs -Force | Out-Null
    foreach ($guide in @('User-Guide.md', 'User-Guide.en.md')) {
        $guidePath = Join-Path (Join-Path $projectRoot 'Docs') $guide
        Copy-Item -LiteralPath $guidePath -Destination $publicDocs
        # The root copies have no parent Docs directory for repository-relative links.
        $rootGuide = [IO.File]::ReadAllText($guidePath).Replace('(../CHANGELOG.md)', '(CHANGELOG.md)')
        [IO.File]::WriteAllText((Join-Path $stagingPackage $guide), $rootGuide, [Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Docs/Release-Trust.md') -Destination $publicDocs
    foreach ($publicFile in @('README.md', 'README.en.md', 'LICENSE', 'CHANGELOG.md')) {
        $sourceFile = Join-Path $projectRoot $publicFile
        if ($publicFile -in @('README.md', 'README.en.md')) {
            # The promotional image belongs to the source repository, not the runtime ZIP.
            $packageReadme = [IO.File]::ReadAllText($sourceFile) -replace '(?m)^!\[[^\r\n]*\]\(Docs/Images/floatcanvas-preview\.png\)\r?\n', ''
            [IO.File]::WriteAllText((Join-Path $stagingPackage $publicFile), $packageReadme, [Text.UTF8Encoding]::new($false))
        } else {
            Copy-Item -LiteralPath $sourceFile -Destination $stagingPackage
        }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $stagingPackage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $stagingPackage 'licenses') -Recurse
    if (-not (Test-Path -LiteralPath (Join-Path $stagingPackage 'licenses/Microsoft.NET.AppHost/LICENSE.TXT') -PathType Leaf)) {
        throw 'The native .NET apphost launcher license is missing.'
    }

    # Keep the reviewed licenses for the actual NuGet dependencies; the system supplies .NET.
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $library.Name -match '(?i)NativeAssets\.macOS/') { continue }
        $licenseFolder = $library.Name.Replace('/', '-')
        if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $stagingPackage 'licenses') $licenseFolder))) {
            throw "No reviewed license directory exists for dependency $($library.Name). Collect it before publishing."
        }
    }
    $manifest = [pscustomobject]@{
        Product = '浮绘 FloatCanvas'; Version = $Version; RuntimeIdentifier = 'win-x64'; SelfContained = $false
        DistributionTrust = $(if ($ValidationOnly) { 'UnsignedValidation' } else { 'Unsigned' })
        Configuration = $Configuration; PublishedAt = [DateTimeOffset]::Now.ToString('o'); RequiredFrameworks = $requiredFrameworks
        Validation = $(if ($Version.Contains('-')) { 'Preview build. See User-Guide.md for platform support and feature limitations.' }
            else { 'Release build. See User-Guide.md for platform support and feature limitations.' })
    }
    $components = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' -and $_.Name -notmatch '(?i)NativeAssets\.macOS/' } | ForEach-Object {
        $parts = $_.Name.Split('/')
        [ordered]@{ type = 'library'; name = $parts[0]; version = $parts[1]; purl = "pkg:nuget/$($_.Name)";
            hashes = @(@{ alg = 'SHA-512'; content = [Convert]::ToHexString([Convert]::FromBase64String($_.Value.sha512)).ToLowerInvariant() }) }
    })
    [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.6'; version = 1; components = $components } |
        ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $stagingPackage 'DEPENDENCIES.cdx.json') -Encoding utf8
    # NativeAssets packages ship PDBs as native runtime assets, independently of DebugSymbols.
    # Filter only this invocation's verified staging tree, never source/cache/previous packages.
    $stagingRoot = Assert-DistPath $stagingRoot
    $stagingPackage = Assert-DistPath $stagingPackage
    $stagedEntries = @(Get-ChildItem -LiteralPath $stagingPackage -Recurse -Force)
    if (@($stagedEntries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -ne 0) {
        throw 'Publish staging must not contain symbolic links or junctions.'
    }
    $stagingPrefix = $stagingPackage.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($symbol in @($stagedEntries | Where-Object { -not $_.PSIsContainer -and $_.Extension -ieq '.pdb' })) {
        $symbolPath = Assert-DistPath $symbol.FullName
        if (-not $symbolPath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a symbol outside this publish staging directory: $symbolPath"
        }
        Remove-Item -LiteralPath $symbolPath -Force
    }
    $packageFiles = @(Get-ChildItem -LiteralPath $stagingPackage -Recurse -File -Force)
    if (@($packageFiles | Where-Object { $_.Extension -ieq '.pdb' }).Count -ne 0) { throw 'The distribution must not contain debug symbols.' }
    $appLocalRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll', 'System.Private.CoreLib.dll',
        'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll', 'System.Windows.Forms.dll',
        'System.Windows.Forms.Primitives.dll', 'wpfgfx_cor3.dll', 'PenImc_cor3.dll', 'DirectWriteForwarder.dll')
    $unexpectedRuntimeFiles = @($packageFiles | Where-Object { $_.Name -iin $appLocalRuntimeFiles })
    if ($unexpectedRuntimeFiles.Count -ne 0) {
        throw "Framework-dependent package contains app-local .NET runtime files: $($unexpectedRuntimeFiles.Name -join ', ')"
    }
    # The updater owns only these files; unrelated files beside the application are preserved.
    # Write the manifest after filtering symbols, and include the manifest itself in its file list.
    $manifest | Add-Member -NotePropertyName Files -NotePropertyValue @(
        @($packageFiles | ForEach-Object { [IO.Path]::GetRelativePath($stagingPackage, $_.FullName).Replace('\', '/') }) + 'PACKAGE-INFO.json' | Sort-Object
    )
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
    foreach ($temporary in @($stagingRoot, $stagingZip, (Join-Path $distRoot ".launcher-$runId"))) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath (Assert-DistPath $temporary) -Recurse -Force }
    }
    Pop-Location
}
