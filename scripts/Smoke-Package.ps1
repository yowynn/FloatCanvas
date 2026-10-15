[CmdletBinding()]
param([string]$ZipPath, [string]$ExtractionRoot)
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
foreach ($required in @('LICENSE', 'CHANGELOG.md', 'README.md', 'README.en.md', 'User-Guide.md', 'User-Guide.en.md', 'FloatCanvas.Localization.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $required))) { throw "Package is missing $required" }
}
$profileDirectory = Join-Path $runDirectory 'profile'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executablePath
$start.WorkingDirectory = $runDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add('--smoke')
$start.ArgumentList.Add('--profile')
$start.ArgumentList.Add($profileDirectory)
# Verify app-local runtime selection without dotnet on PATH. This is not a clean OS test.
$start.Environment['PATH'] = $env:SystemRoot + '\System32;' + $env:SystemRoot
$start.Environment['DOTNET_ROOT'] = Join-Path $runDirectory 'no-installed-runtime'
$start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Package smoke startup timed out.' }
    if ($process.ExitCode -ne 0) { throw "Package exited with code $($process.ExitCode)." }
    $appReport = Get-Content -LiteralPath (Join-Path $runDirectory 'artifacts\m1\app-smoke.json') -Raw | ConvertFrom-Json
    if (-not $appReport.Passed -or $appReport.CapturedInput) { throw 'Package did not initialize in pass-through mode.' }
    if ($appReport.MonitorScope -ne 'PrimaryScreen' -or @($appReport.Monitors).Count -ne 1) {
        throw 'A fresh package profile must initialize only the primary monitor.'
    }
    if ($appReport.LanguagePreference -ne 'auto' -or $appReport.InterfaceLanguage -notin @('zh-CN', 'en')) {
        throw 'A fresh package profile must resolve a supported automatic interface language.'
    }
    if (Test-Path -LiteralPath (Join-Path $profileDirectory 'errors.log')) { throw 'Package recorded an application error.' }
    $runtime = Get-Content -LiteralPath (Join-Path $packageDirectory 'FloatCanvas.runtimeconfig.json') -Raw | ConvertFrom-Json
    $result = [pscustomobject]@{
        Passed = $true; Timestamp = [DateTimeOffset]::Now.ToString('o')
        Zip = $ZipPath; Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ExtractedExecutable = $executablePath; ExitCode = $process.ExitCode
        IncludedFrameworks = $runtime.runtimeOptions.includedFrameworks
        Mode = $appReport.Mode; CapturedInput = $appReport.CapturedInput
        MonitorScope = $appReport.MonitorScope; ActiveMonitorCount = @($appReport.Monitors).Count
        LanguagePreference = $appReport.LanguagePreference; InterfaceLanguage = $appReport.InterfaceLanguage
        Monitors = $appReport.Monitors; AppLocalRuntimeFilesPresent = (Test-Path -LiteralPath (Join-Path $packageDirectory 'coreclr.dll'))
        DotnetOnChildPath = $false
        Limitation = 'Fresh ZIP extraction on this Windows 11 machine with no dotnet on child PATH; SDK/runtime remain installed on the machine. This does not prove Windows 10 or a clean OS.'
    }
    $reportPath = Join-Path $projectRoot 'artifacts\e2e\package-smoke.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "PASS package smoke: $reportPath"
}
finally {
    try {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    }
    finally { $process.Dispose() }
}
