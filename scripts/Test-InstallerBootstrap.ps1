#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$IsccPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-bootstrap-tests'),
    [switch]$VerifyMicrosoftDownload
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsccPath) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
$run = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$fixture = Join-Path $run 'runtime-fixture.bin'
[IO.File]::WriteAllText($fixture, "Isolated runtime bootstrap payload.`r`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText("$fixture.corrupt", 'A different payload.', [Text.UTF8Encoding]::new($false))
$fixtureHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
& $IsccPath /Q "/DProbeOutputDirectory=$run" "/DProbePayloadFile=$fixture" "/DRuntimeInstallerSha256=$fixtureHash" (Join-Path $PSScriptRoot 'installer/tests/RuntimeInstallProbe.iss') *> (Join-Path $run 'bootstrap-build.log')
if ($LASTEXITCODE -ne 0) { throw "Runtime bootstrap harness compilation failed. See $run." }
$results = [Collections.Generic.List[object]]::new()

function Invoke-Probe([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$ExpectedExitCode = 1) {
    $report = Join-Path $run "$Name.txt"
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', "/Report=$report", "/LOG=$(Join-Path $run "$Name.log")") + $Arguments) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "Bootstrap probe timed out: $Name" }
        if ($process.ExitCode -ne $ExpectedExitCode -or -not (Test-Path -LiteralPath $report)) { throw "Bootstrap probe did not finish: $Name (exit $($process.ExitCode), expected $ExpectedExitCode)" }
    } finally { $process.Dispose() }
    $record = @{}
    foreach ($line in [IO.File]::ReadAllLines($report)) {
        $parts = $line.Split('=', 2)
        if ($parts.Count -eq 2) { $record[$parts[0]] = $parts[1] }
    }
    if ($record.Passed -ne 'True') { throw "Bootstrap probe failed: $Name. See $report." }
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Checks = $(if ($record.ContainsKey('Checks')) { [int]$record.Checks } else { 1 }); Report = $report })
}

foreach ($language in @('en', 'zhcn')) {
    Invoke-Probe "bootstrap-$language" (Join-Path $run 'RuntimeInstallProbe.exe') @("/LANG=$language")
    Invoke-Probe "bootstrap-explicit-$language" (Join-Path $run 'RuntimeInstallProbe.exe') @("/LANG=$language", '/INSTALLRUNTIME')
}
& $IsccPath /Q "/DProbeOutputDirectory=$run" /DProbeName=RuntimeInstallWizardProbe /DProbeWizardFlow=1 "/DProbePayloadFile=$fixture" "/DRuntimeInstallerSha256=$fixtureHash" (Join-Path $PSScriptRoot 'installer/tests/RuntimeInstallProbe.iss') *> (Join-Path $run 'bootstrap-wizard-build.log')
if ($LASTEXITCODE -ne 0) { throw "Runtime wizard harness compilation failed. See $run." }
foreach ($language in @('en', 'zhcn')) {
    Invoke-Probe "wizard-blocked-$language" (Join-Path $run 'RuntimeInstallWizardProbe.exe') @("/LANG=$language") 7
    Invoke-Probe "wizard-explicit-$language" (Join-Path $run 'RuntimeInstallWizardProbe.exe') @("/LANG=$language", '/INSTALLRUNTIME') 0
}
if ($VerifyMicrosoftDownload) {
    $runtime = & (Join-Path $PSScriptRoot 'Get-DotNetRuntime.ps1') -CacheDirectory (Join-Path $run 'runtime-cache')
    foreach ($valid in @($true, $false)) {
        $name = $(if ($valid) { 'official-download' } else { 'incorrect-download-hash' })
        $hash = $(if ($valid) { $runtime.Sha256 } else { '0' * 64 })
        & $IsccPath /Q "/DProbeOutputDirectory=$run" "/DProbeName=$name" "/DRuntimeInstallerVersion=$($runtime.Version)" "/DRuntimeInstallerUrl=$($runtime.Url)" "/DRuntimeInstallerSha256=$hash" (Join-Path $PSScriptRoot 'installer/tests/RuntimeDownloadProbe.iss') *> (Join-Path $run "$name-build.log")
        if ($LASTEXITCODE -ne 0) { throw "Runtime download probe compilation failed. See $run." }
        Invoke-Probe $name (Join-Path $run "$name.exe") @("/ExpectFailure=$([int](-not $valid))")
    }
}
$result = [ordered]@{
    Passed = $true
    Checks = ($results | Measure-Object -Property Checks -Sum).Sum
    Cases = $results.ToArray()
    MicrosoftDownloadVerified = [bool]$VerifyMicrosoftDownload
    Limitation = 'Bootstrap decisions use isolated download and process fixtures. Optional native Microsoft downloads are verified but never executed. Actual runtime installation and UAC require a disposable Windows environment.'
}
$reportPath = Join-Path $run 'result.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "PASS runtime bootstrap: $reportPath"
