#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$IsccPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-runtime-tests')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsccPath) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
$run = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$harness = Join-Path $PSScriptRoot 'installer/tests/RuntimeProbe.iss'
$results = [Collections.Generic.List[object]]::new()

function Compile-Probe([string]$Name, [string]$RootOverride = '') {
    $arguments = @('/Q', "/DProbeOutputDirectory=$run", "/DProbeName=$Name")
    if ($RootOverride) { $arguments += "/DRuntimeProbeRoot=$RootOverride" }
    & $IsccPath @arguments $harness *> (Join-Path $run "$Name-build.log")
    if ($LASTEXITCODE -ne 0) { throw "Runtime test harness failed to compile: $Name. See $run." }
    return (Join-Path $run "$Name.exe")
}
function Invoke-Probe([string]$Name, [bool]$Expected, [string]$ExpectedRoot, [hashtable]$Environment = @{}, [switch]$Parser, [string]$Executable = $probe) {
    $report = Join-Path $run "$Name.txt"
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', "/Report=$report", "/Expected=$([int]$Expected)", "/ExpectedRoot=$ExpectedRoot", "/LOG=$(Join-Path $run "$Name.log")")) {
        $info.ArgumentList.Add($argument)
    }
    if ($Parser) { $info.ArgumentList.Add('/Parser=1') }
    # The child's environment is isolated; the current shell and system remain unchanged.
    foreach ($key in @('DOTNET_ROOT_X64', 'DOTNET_ROOT')) { $info.Environment.Remove($key) | Out-Null }
    $info.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw "Runtime probe timed out: $Name" }
        # InitializeSetup deliberately returns False: no installer writes can occur.
        if ($process.ExitCode -ne 1 -or -not (Test-Path -LiteralPath $report)) { throw "Runtime probe did not finish its pre-install checks: $Name ($($process.ExitCode))." }
    }
    finally { $process.Dispose() }
    $record = @{}
    foreach ($line in [IO.File]::ReadAllLines($report)) {
        $parts = $line.Split('=', 2)
        if ($parts.Length -eq 2) { $record[$parts[0]] = $parts[1] }
    }
    if ($record.Passed -cne 'True') { throw "Runtime probe failed: $Name - $($record.Error)" }
    $results.Add([pscustomobject]@{ Name = $Name; Checks = [int]$record.Checks; Root = $record.Root; Version = $record.Version })
    Write-Host "PASS runtime prerequisite: $Name"
}
function Assert-FixturePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture mutation escaped its private test directory.' }
    return $full
}
function With-RemovedFixture([string]$Path, [scriptblock]$Action) {
    $source = Assert-FixturePath $Path
    $held = Assert-FixturePath (Join-Path $run ('held-' + [Guid]::NewGuid().ToString('N')))
    Move-Item -LiteralPath $source -Destination $held
    try { & $Action }
    finally { Move-Item -LiteralPath $held -Destination $source }
}

$probe = Compile-Probe 'RuntimeProbe'
# This uses the production registry/default-root path, with no fixture override.
Invoke-Probe 'actual-system-runtime' $true '' -Parser
$systemRoot = $results[0].Root
$dotnet = Join-Path $systemRoot 'dotnet.exe'
$listed = & $dotnet --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'The detected system runtime cannot list frameworks.' }
function Latest-Framework([string]$Name) {
    $versions = @($listed | ForEach-Object { if ($_ -match ('^' + [regex]::Escape($Name) + ' (10\.0\.(?:0|[1-9][0-9]*)) \[')) { [version]$Matches[1] } })
    if ($versions.Count -eq 0) { throw "A real stable .NET 10 $Name installation is required for integration verification." }
    return ($versions | Sort-Object -Descending | Select-Object -First 1).ToString()
}
$desktopVersion = Latest-Framework 'Microsoft.WindowsDesktop.App'
$coreVersion = Latest-Framework 'Microsoft.NETCore.App'
$empty = Join-Path $run 'empty-root'
$missing = Join-Path $run 'missing-root'
New-Item -ItemType Directory -Path $empty -Force | Out-Null
Invoke-Probe 'x64-environment-root' $true $systemRoot @{ DOTNET_ROOT_X64 = $systemRoot; DOTNET_ROOT = $empty }
Invoke-Probe 'generic-environment-root' $true $systemRoot @{ DOTNET_ROOT = $systemRoot }
Invoke-Probe 'missing-x64-root-falls-back' $true $systemRoot @{ DOTNET_ROOT_X64 = $missing; DOTNET_ROOT = $systemRoot }
Invoke-Probe 'existing-broken-x64-root-does-not-fall-back' $false $empty @{ DOTNET_ROOT_X64 = $empty; DOTNET_ROOT = $systemRoot }
Invoke-Probe 'existing-broken-generic-root-does-not-fall-back' $false $empty @{ DOTNET_ROOT = $empty }
$staleProbe = Compile-Probe 'StaleRegisteredRootProbe' $missing
Invoke-Probe 'stale-registered-candidate-rejected' $false $missing -Executable $staleProbe

$x86Root = Join-Path $run 'x86-root'
New-Item -ItemType Directory -Path $x86Root -Force | Out-Null
$hostBytes = [IO.File]::ReadAllBytes($dotnet)
$peOffset = [BitConverter]::ToInt32($hostBytes, 0x3c)
$hostBytes[$peOffset + 4] = 0x4c; $hostBytes[$peOffset + 5] = 0x01
[IO.File]::WriteAllBytes((Join-Path $x86Root 'dotnet.exe'), $hostBytes)
Invoke-Probe 'x86-host-rejected-before-execution' $false $x86Root @{ DOTNET_ROOT_X64 = $x86Root }

# Copy actual runtime files to a private fixture. We never rename, unregister,
# repair or uninstall a real runtime; all failure mutations stay under $run.
$clone = Join-Path $run 'copied-runtime'
New-Item -ItemType Directory -Path $clone, (Join-Path $clone 'host'), (Join-Path $clone 'shared/Microsoft.NETCore.App'), (Join-Path $clone 'shared/Microsoft.WindowsDesktop.App') -Force | Out-Null
Copy-Item -LiteralPath $dotnet -Destination (Join-Path $clone 'dotnet.exe')
Copy-Item -LiteralPath (Join-Path $systemRoot 'host/fxr') -Destination (Join-Path $clone 'host/fxr') -Recurse
$core = Join-Path $clone "shared/Microsoft.NETCore.App/$coreVersion"
$desktop = Join-Path $clone "shared/Microsoft.WindowsDesktop.App/$desktopVersion"
Copy-Item -LiteralPath (Join-Path $systemRoot "shared/Microsoft.NETCore.App/$coreVersion") -Destination $core -Recurse
Copy-Item -LiteralPath (Join-Path $systemRoot "shared/Microsoft.WindowsDesktop.App/$desktopVersion") -Destination $desktop -Recurse
$cloneEnvironment = @{ DOTNET_ROOT_X64 = $clone }
Invoke-Probe 'copied-stable-runtime-is-usable' $true $clone $cloneEnvironment
With-RemovedFixture $desktop { Invoke-Probe 'actual-core-only-root-is-insufficient' $false $clone $cloneEnvironment }
With-RemovedFixture (Join-Path $clone 'host/fxr') { Invoke-Probe 'missing-hostfxr-rejected' $false $clone $cloneEnvironment }
With-RemovedFixture (Join-Path $desktop 'PresentationFramework.dll') { Invoke-Probe 'missing-wpf-assembly-rejected' $false $clone $cloneEnvironment }
$wpfNative = Join-Path $desktop 'wpfgfx_cor3.dll'
With-RemovedFixture $wpfNative {
    [IO.File]::WriteAllText($wpfNative, 'Damaged native runtime file.')
    try { Invoke-Probe 'damaged-wpf-native-file-rejected' $false $clone $cloneEnvironment }
    finally { Remove-Item -LiteralPath (Assert-FixturePath $wpfNative) }
}
$runtimeConfig = Join-Path $desktop 'Microsoft.WindowsDesktop.App.runtimeconfig.json'
With-RemovedFixture $runtimeConfig {
    [IO.File]::WriteAllText($runtimeConfig, '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.999"}}}')
    try { Invoke-Probe 'insufficient-core-patch-rejected' $false $clone $cloneEnvironment }
    finally { Remove-Item -LiteralPath (Assert-FixturePath $runtimeConfig) }
}
With-RemovedFixture $runtimeConfig {
    [IO.File]::WriteAllText($runtimeConfig, '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14-preview.1"}}}')
    try { Invoke-Probe 'preview-core-requirement-rejected' $false $clone $cloneEnvironment }
    finally { Remove-Item -LiteralPath (Assert-FixturePath $runtimeConfig) }
}
[ordered]@{
    Passed = $true; Checks = ($results | Measure-Object Checks -Sum).Sum; Cases = $results
    ActualRuntimeRoot = $systemRoot; DesktopVersion = $desktopVersion; CoreVersion = $coreVersion
    Limitation = 'Covers production prerequisite parsing and detection with the installed runtime and isolated copies; does not emulate a clean Windows installation.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding UTF8
Write-Host "PASS runtime prerequisite suite: $(Join-Path $run 'result.json')"
