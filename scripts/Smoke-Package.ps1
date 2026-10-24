[CmdletBinding()]
param([string]$ZipPath, [string]$ExtractionRoot, [string]$ReportPath, [switch]$StaticOnly, [switch]$IsolateHotkeys)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/FloatCanvas.App/FloatCanvas.App.csproj') -Raw
    $ZipPath = Join-Path $projectRoot ('dist/FloatCanvas-' + [string]$project.Project.PropertyGroup.Version + '-win-x64.zip')
}
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
if ([string]::IsNullOrWhiteSpace($ExtractionRoot)) {
    $ExtractionRoot = Join-Path $projectRoot 'artifacts\package-smoke'
}
$runDirectory = Join-Path ([IO.Path]::GetFullPath($ExtractionRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $runDirectory)
$executables = @(Get-ChildItem -LiteralPath $runDirectory -Filter FloatCanvas.exe -Recurse -File)
if ($executables.Count -ne 1) { throw 'The distribution must contain exactly one FloatCanvas.exe.' }
$executablePath = $executables[0].FullName
$packageDirectory = $executables[0].DirectoryName
foreach ($required in @('LICENSE', 'CHANGELOG.md', 'README.md', 'README.en.md', 'User-Guide.md', 'User-Guide.en.md', 'FloatCanvas.Localization.dll', 'FloatCanvas.SystemOverlay.dll', 'FloatCanvas.runtimeconfig.json', 'PACKAGE-INFO.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $required) -PathType Leaf)) { throw "Package is missing $required" }
}
$documentationPrefix = $packageDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($document in @('README.md', 'README.en.md', 'User-Guide.md', 'User-Guide.en.md', 'Docs/User-Guide.md', 'Docs/User-Guide.en.md')) {
    $documentPath = Join-Path $packageDirectory $document
    foreach ($link in [regex]::Matches([IO.File]::ReadAllText($documentPath), '\]\(([^)]+)\)')) {
        $target = $link.Groups[1].Value
        if ($target -match '^(?:[a-z][a-z0-9+.-]*:|#)') { continue }
        $relativePath = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        $targetPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $documentPath) $relativePath))
        if (-not $targetPath.StartsWith($documentationPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $targetPath)) {
            throw "Packaged documentation link is unavailable: $document -> $target"
        }
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $packageDirectory 'PACKAGE-INFO.json') -Raw | ConvertFrom-Json
if ($manifest.Product -cne '浮绘 FloatCanvas' -or $manifest.RuntimeIdentifier -cne 'win-x64' -or $manifest.Configuration -cne 'Release') {
    throw 'The update manifest must identify the FloatCanvas Windows x64 Release package.'
}
$numeric = '(?:0|[1-9][0-9]*)'
$identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
if (-not [regex]::IsMatch([string]$manifest.Version, "\A$numeric\.$numeric\.$numeric(?:-$identifier(?:\.$identifier)*)?\z")) {
    throw 'The package version must follow the release tag contract.'
}
$packageName = "FloatCanvas-$($manifest.Version)-win-x64"
if ([IO.Path]::GetFileName($ZipPath) -cne "$packageName.zip" -or
    [IO.Path]::GetFullPath($packageDirectory) -cne [IO.Path]::GetFullPath((Join-Path $runDirectory $packageName))) {
    throw 'The ZIP filename and its single root directory must match the manifest version.'
}
$packagePrefix = $packageDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$allEntries = @(Get-ChildItem -LiteralPath $runDirectory -Recurse -Force)
if (@($allEntries | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    ($_.FullName -ine $packageDirectory -and -not $_.FullName.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase))
}).Count -ne 0) {
    throw 'The distribution must contain only one real package directory, without links or unrelated entries.'
}
$ownedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$declaredFiles = @($manifest.Files)
if ($declaredFiles.Count -eq 0 -or $declaredFiles.Count -gt 4096) { throw 'The update manifest must declare its owned files.' }
foreach ($file in $declaredFiles) {
    if ($file -isnot [string] -or [string]::IsNullOrWhiteSpace($file) -or $file.Contains('\') -or
        [IO.Path]::IsPathRooted($file) -or -not $ownedFiles.Add($file)) {
        throw 'The update manifest contains an invalid or duplicate relative filename.'
    }
}
$actualFiles = @($allEntries | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
    [IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/')
})
if (-not $ownedFiles.Contains('PACKAGE-INFO.json') -or -not $ownedFiles.SetEquals([string[]]$actualFiles)) {
    throw 'The update manifest must list every package file exactly once, including PACKAGE-INFO.json.'
}
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageDirectory 'FloatCanvas.dll')).ProductVersion
if ([string]::IsNullOrWhiteSpace($binaryVersion) -or $binaryVersion.Split('+')[0] -cne $manifest.Version) {
    throw 'The application binary version does not match the package manifest.'
}
if ($manifest.SelfContained -isnot [bool] -or $manifest.SelfContained) { throw 'The release manifest must declare SelfContained as false.' }
$runtime = Get-Content -LiteralPath (Join-Path $packageDirectory 'FloatCanvas.runtimeconfig.json') -Raw | ConvertFrom-Json
$requiredFrameworks = @($runtime.runtimeOptions.frameworks | Where-Object { $null -ne $_ })
foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $frameworks = @($requiredFrameworks | Where-Object { $_.name -eq $name })
    $frameworkVersion = $null
    if ($frameworks.Count -ne 1 -or -not [version]::TryParse([string]$frameworks[0].version, [ref]$frameworkVersion) -or
        $frameworkVersion.Major -ne 10 -or $frameworkVersion.Minor -ne 0) {
        throw "The framework-dependent package must require $name 10.0."
    }
}
if (@($runtime.runtimeOptions.includedFrameworks | Where-Object { $null -ne $_ }).Count -gt 0) {
    throw 'The framework-dependent package must not declare included runtime frameworks.'
}
$runtimeFiles = @(Get-ChildItem -LiteralPath $runDirectory -Recurse -File | Where-Object {
    $_.Name -in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll') -or $_.Extension -ieq '.pdb'
})
if ($runtimeFiles.Count -gt 0) { throw ('The distribution must not include runtime binaries or PDB files: ' + ($runtimeFiles.Name -join ', ')) }
if ($StaticOnly) {
    Write-Host "PASS static package contract: $ZipPath ($($ownedFiles.Count) owned files). No application was started."
    return
}
$profileDirectory = Join-Path $runDirectory 'profile'
if (Test-Path -LiteralPath $profileDirectory) { throw 'The package smoke profile must be fresh.' }
if ($IsolateHotkeys) {
    # Only this fresh smoke profile is seeded; a running user instance stays untouched.
    New-Item -ItemType Directory -Path $profileDirectory | Out-Null
    @{ Toggle = @{ Modifiers = 7; VirtualKey = 122 }; Hide = @{ Modifiers = 7; VirtualKey = 121 } } |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $profileDirectory 'settings.json') -Encoding UTF8
}
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executablePath
$start.WorkingDirectory = $runDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add('--smoke')
$start.ArgumentList.Add('--profile')
$start.ArgumentList.Add($profileDirectory)
# Run the apphost using the machine's existing runtime search configuration, without dotnet on PATH.
# Inherit DOTNET_ROOT and other runtime search variables unchanged. This is not a clean OS test.
$start.Environment['PATH'] = $env:SystemRoot + '\System32;' + $env:SystemRoot
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw 'Package smoke startup timed out.' }
    if ($process.ExitCode -ne 0) { throw "Package exited with code $($process.ExitCode)." }
    $appReport = Get-Content -LiteralPath (Join-Path $runDirectory 'artifacts\m1\app-smoke.json') -Raw | ConvertFrom-Json
    if (-not $appReport.Passed -or $appReport.CapturedInput) { throw 'Package did not initialize in pass-through mode.' }
    if ($appReport.MonitorScope -ne 'AllScreens' -or @($appReport.Monitors).Count -lt 1) {
        throw 'A fresh package profile must initialize all-screen scope with at least one active monitor.'
    }
    if ($appReport.LanguagePreference -ne 'auto' -or $appReport.InterfaceLanguage -notin @('zh-CN', 'en')) {
        throw 'A fresh package profile must resolve a supported automatic interface language.'
    }
    if (Test-Path -LiteralPath (Join-Path $profileDirectory 'errors.log')) { throw 'Package recorded an application error.' }
    $result = [pscustomobject]@{
        Passed = $true; Timestamp = [DateTimeOffset]::Now.ToString('o')
        Zip = $ZipPath; Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ExtractedExecutable = $executablePath; ExitCode = $process.ExitCode
        RequiredFrameworks = $requiredFrameworks; FrameworkDependent = $true; ManifestSelfContained = $manifest.SelfContained
        OwnedFileCount = $ownedFiles.Count; ManifestFilesComplete = $true; BinaryVersion = $binaryVersion
        Mode = $appReport.Mode; CapturedInput = $appReport.CapturedInput
        MonitorScope = $appReport.MonitorScope; ActiveMonitorCount = @($appReport.Monitors).Count
        LanguagePreference = $appReport.LanguagePreference; InterfaceLanguage = $appReport.InterfaceLanguage
        Monitors = $appReport.Monitors; AppLocalRuntimeFilesPresent = $false; PdbFilesPresent = $false
        DotnetOnChildPath = $false
        IsolatedTestHotkeys = [bool]$IsolateHotkeys
        RuntimeSource = 'Existing system .NET runtime, resolved by the apphost using inherited runtime search configuration.'
        Limitation = 'Fresh ZIP extraction on this Windows 11 machine using an existing installed .NET Desktop Runtime, with no dotnet on child PATH. This does not prove missing-runtime dialog behavior, Windows 10, or a clean OS.'
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $projectRoot 'artifacts\e2e\package-smoke.json' }
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "PASS package smoke: $ReportPath"
}
finally {
    try {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
    }
    finally { $process.Dispose() }
}
