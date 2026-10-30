#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$IsccPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-lifecycle-tests')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsccPath) { $IsccPath = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).Path
$id = [Guid]::NewGuid().ToString('N')
$run = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) $id
$fixture = Join-Path $run 'fixtures'
$registryPrefix = "Software\FloatCanvas.Tests\Lifecycle-$id\"
$eventName = "Local\FloatCanvas.Update.$id"
$junction = Join-Path $fixture 'linked\outside'
$results = [Collections.Generic.List[object]]::new()
$busyStream = $null
$handoffEvent = $null
if (-not ('FloatCanvasLifecycleProbe.CommandLine' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace FloatCanvasLifecycleProbe {
    public static class CommandLine {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        public static string[] Parse(string commandLine) {
            IntPtr values = CommandLineToArgvW(commandLine, out int count);
            if (values == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try {
                var result = new string[count];
                for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(values, i * IntPtr.Size));
                return result;
            } finally { LocalFree(values); }
        }
    }
}
'@
}

function Invoke-Probe([string]$Name, [string[]]$Arguments) {
    $report = Join-Path $run "$Name.txt"
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $run 'LifecycleProbe.exe'))
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', "/REPORT=$report", "/LOG=$(Join-Path $run "$Name.log")") + $Arguments) {
        $info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw "Lifecycle probe timed out: $Name" }
        if ($process.ExitCode -ne 1 -or -not (Test-Path -LiteralPath $report)) { throw "Lifecycle probe did not finish: $Name (exit $($process.ExitCode))." }
    } finally { $process.Dispose() }
    $record = @{}
    foreach ($line in [IO.File]::ReadAllLines($report)) {
        $parts = $line.Split('=', 2)
        if ($parts.Count -eq 2) { $record[$parts[0]] = $parts[1] }
    }
    if ($record.Passed -ne '1') { throw "Lifecycle probe failed: $Name ($($record.Error)). See $report." }
    if ($record.Profile) {
        $parsed = [FloatCanvasLifecycleProbe.CommandLine]::Parse('FloatCanvas.exe ' + $record.Arguments)
        if ($parsed.Count -lt 4 -or $parsed[1] -ne '--profile' -or $parsed[2] -cne $record.Profile -or $parsed[3] -ne '--recover') {
            throw "Windows command-line parsing changed the handoff profile or recovery option: $Name."
        }
    }
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Checks = [int]$record.Checks; Report = $report })
}

try {
    foreach ($relative in @('safe\sub', 'busy', 'linked', 'outside', 'invalid-list')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $relative) -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'safe\sub\payload.dll'), 'isolated payload')
    [IO.File]::WriteAllText((Join-Path $fixture 'safe\FloatCanvas.install-files.txt'), "sub/payload.dll`r`n")
    [IO.File]::WriteAllText((Join-Path $fixture 'invalid-list\FloatCanvas.install-files.txt'), "../outside/keep.txt`r`n")
    [IO.File]::WriteAllText((Join-Path $fixture 'outside\keep.txt'), 'outside fixture must survive')
    [IO.File]::WriteAllText((Join-Path $fixture 'busy\locked.txt'), 'locked fixture must survive')
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $fixture 'outside') | Out-Null
    $busyStream = [IO.File]::Open((Join-Path $fixture 'busy\locked.txt'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $handoffEvent = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)

    & $IsccPath /Q "/DProbeOutputDirectory=$run" "/DProbeFixtureDirectory=$fixture" "/DProbeRegistryPrefix=$registryPrefix" (Join-Path $PSScriptRoot 'installer/tests/LifecycleProbe.iss') *> (Join-Path $run 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle harness compilation failed. See $run." }
    foreach ($language in @('en', 'zhcn')) {
        Invoke-Probe "contracts-$language" @("/LANG=$language")
        Invoke-Probe "normal-launch-$language" @("/LANG=$language", '/CASE=handoff')
        foreach ($monitor in @('auto', 'primary', 'all')) {
            Invoke-Probe "handoff-$monitor-$language" @("/LANG=$language", '/CASE=handoff', "/FLOATCANVASUPDATEEVENT=$eventName", "/FLOATCANVASUPDATEPROFILE=$(Join-Path $fixture 'safe')", "/FLOATCANVASUPDATEMONITOR=$monitor")
        }
        foreach ($case in @(
            @{ Name = 'wrong-event-prefix'; Event = "Global\FloatCanvas.Update.$id"; Profile = (Join-Path $fixture 'safe'); Monitor = 'auto' },
            @{ Name = 'short-event'; Event = 'Local\FloatCanvas.Update.abcd'; Profile = (Join-Path $fixture 'safe'); Monitor = 'auto' },
            @{ Name = 'nonhex-event'; Event = "Local\FloatCanvas.Update.$('g' * 32)"; Profile = (Join-Path $fixture 'safe'); Monitor = 'auto' },
            @{ Name = 'missing-profile'; Event = $eventName; Profile = (Join-Path $fixture 'does-not-exist'); Monitor = 'auto' },
            @{ Name = 'relative-profile'; Event = $eventName; Profile = '..\safe'; Monitor = 'auto' },
            @{ Name = 'junction-profile'; Event = $eventName; Profile = $junction; Monitor = 'auto' },
            @{ Name = 'invalid-monitor'; Event = $eventName; Profile = (Join-Path $fixture 'safe'); Monitor = 'secondary' }
        )) {
            Invoke-Probe "$($case.Name)-$language" @("/LANG=$language", '/CASE=handoff', '/EXPECTERROR=1', "/FLOATCANVASUPDATEEVENT=$($case.Event)", "/FLOATCANVASUPDATEPROFILE=$($case.Profile)", "/FLOATCANVASUPDATEMONITOR=$($case.Monitor)")
        }
        Invoke-Probe "trailing-profile-$language" @("/LANG=$language", '/CASE=handoff', "/FLOATCANVASUPDATEEVENT=$eventName", "/FLOATCANVASUPDATEPROFILE=$(Join-Path $fixture 'safe')\")
        Invoke-Probe "root-profile-$language" @("/LANG=$language", '/CASE=handoff', "/FLOATCANVASUPDATEEVENT=$eventName", "/FLOATCANVASUPDATEPROFILE=$([IO.Path]::GetPathRoot($fixture))")
        $handoffEvent.Reset() | Out-Null
        Invoke-Probe "signal-event-$language" @("/LANG=$language", '/CASE=handoff', '/NOTIFY=1', "/FLOATCANVASUPDATEEVENT=$eventName", "/FLOATCANVASUPDATEPROFILE=$(Join-Path $fixture 'safe')")
        if (-not $handoffEvent.WaitOne(0)) { throw 'The production handoff did not signal the private test event.' }
        Invoke-Probe "absent-event-$language" @("/LANG=$language", '/CASE=handoff', '/NOTIFY=1', '/EXPECTERROR=1', "/FLOATCANVASUPDATEEVENT=Local\FloatCanvas.Update.$([Guid]::NewGuid().ToString('N'))", "/FLOATCANVASUPDATEPROFILE=$(Join-Path $fixture 'safe')")
    }
    if ([IO.File]::ReadAllText((Join-Path $fixture 'outside\keep.txt')) -ne 'outside fixture must survive') { throw 'Outside fixture changed.' }
    $remaining = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPrefix.TrimEnd('\'))
    if ($remaining) { $remaining.Dispose(); throw 'Private registry fixture was not cleaned.' }
    $result = [ordered]@{
        Passed = $true
        Checks = ($results | Measure-Object -Property Checks -Sum).Sum
        Cases = $results.ToArray()
        RegistryScope = "HKCU\$registryPrefix"
        Limitation = 'Tests exercise production native helpers using only private HKCU, filesystem and event fixtures. Actual install/uninstall, machine registry, offline hives and UAC require the separate disposable Windows Sandbox suite.'
    }
    $reportPath = Join-Path $run 'result.json'
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "PASS installer lifecycle: $reportPath"
} finally {
    if ($busyStream) { $busyStream.Dispose() }
    if ($handoffEvent) { $handoffEvent.Dispose() }
    # Delete only this run's junction, without following it or removing its target.
    if ([IO.Path]::GetFullPath($junction).StartsWith([IO.Path]::GetFullPath($fixture) + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Directory]::Exists($junction)) {
        if (([IO.File]::GetAttributes($junction) -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'Fixture link changed unexpectedly.' }
        [IO.Directory]::Delete($junction)
    }
    if ($registryPrefix -eq "Software\FloatCanvas.Tests\Lifecycle-$id\" -and $id -match '^[a-f0-9]{32}$') {
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryPrefix.TrimEnd('\'), $false)
    }
}
