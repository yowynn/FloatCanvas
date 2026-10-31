#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('LegacySuccess','LegacyStartupFailure','ModernSuccess','OrdinaryLauncher')][string]$Case,
    [string]$InstalledZipPath,
    [Parameter(Mandatory)][string]$CandidateZipPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-update-compatibility')
)
# Exercises immutable old release workers against a newly built ZIP. Every
# application, profile, desktop and transaction belongs to this fresh fixture.
# This script never switches the input desktop or sends keyboard/mouse input.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$run = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) ($Case + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
if (((Get-Item -LiteralPath $run).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Fixture directory must not be a link.' }
$oldVersion = if ($Case -like 'Legacy*') { '0.2.1' } else { '0.3.0' }
$SuspendUpdatedStartup = $Case -eq 'LegacyStartupFailure'
$newZip = (Resolve-Path -LiteralPath $CandidateZipPath).Path
$candidateHash = (Get-FileHash -LiteralPath $newZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath ($newZip + '.sha256') -Raw).Trim() -cne ($candidateHash + '  ' + [IO.Path]::GetFileName($newZip))) { throw 'Candidate ZIP checksum mismatch.' }
if ($Case -eq 'OrdinaryLauncher') { $oldZip = $newZip }
else {
    $oldZip = (Resolve-Path -LiteralPath $InstalledZipPath).Path
    # Pin historical binaries so this test cannot silently exercise a new worker.
    $expected = if ($oldVersion -eq '0.2.1') { 'dcacd450dd34071521cf6ffc59919a03a19ebc139f03efc4adf3de86c5d4a80b' }
        else { '5f705f99dce108078009a6a12a013bb3cbd2ec3aaae597e1d2aa5f359a7ddc18' }
    if ((Get-FileHash -LiteralPath $oldZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw 'Historical official ZIP checksum mismatch.' }
}
Expand-Archive -LiteralPath $oldZip -DestinationPath (Join-Path $run 'old-extracted')
Expand-Archive -LiteralPath $newZip -DestinationPath (Join-Path $run 'new-extracted')
$oldManifestPath = @(Get-ChildItem -LiteralPath (Join-Path $run 'old-extracted') -Filter PACKAGE-INFO.json -Recurse -File)
$newManifestPath = @(Get-ChildItem -LiteralPath (Join-Path $run 'new-extracted') -Filter PACKAGE-INFO.json -Recurse -File)
if ($oldManifestPath.Count -ne 1 -or $newManifestPath.Count -ne 1) { throw 'Unexpected published ZIP structure.' }
$install = $oldManifestPath[0].DirectoryName
$download = $newManifestPath[0].DirectoryName
$old = Get-Content -LiteralPath $oldManifestPath[0].FullName -Raw | ConvertFrom-Json
$next = Get-Content -LiteralPath $newManifestPath[0].FullName -Raw | ConvertFrom-Json
if ($Case -ne 'OrdinaryLauncher' -and $old.Version -cne $oldVersion) { throw 'Unexpected historical package version.' }
if ([Version]($next.Version -split '-',2)[0] -le [Version]'0.3.0') { throw 'The candidate must upgrade both historical releases.' }
$profile = Join-Path $run 'profile'
$toggleKey = @{ LegacySuccess=124; LegacyStartupFailure=126; ModernSuccess=128; OrdinaryLauncher=130 }[$Case]
$job = Join-Path $profile ('updates\jobs\' + [Guid]::NewGuid().ToString('N'))
foreach ($directory in @($profile, $job, (Join-Path $job 'worker'), (Join-Path $job 'package'), (Join-Path $job 'backup'))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
@{
    OverlayMode = 'desktop'; PreferGpuAcceleration = $false; ShowFloatingLogo = $false
    ShowPassThroughLauncher = $false; ShowWelcomeOnStartup = $false
    Toggle = @{ Modifiers = 7; VirtualKey = $toggleKey }; Hide = @{ Modifiers = 7; VirtualKey = ($toggleKey + 1) }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
[IO.File]::WriteAllText((Join-Path $profile 'private-fixture.notes'), 'Fresh generated test profile. No user profile was copied.')
[IO.File]::WriteAllText((Join-Path $install 'private-fixture-user.notes'), 'Unowned file must survive the real published ZIP transaction.')
function Copy-Owned([string]$Source, [string]$Target, [string[]]$Files) {
    foreach ($relative in $Files) {
        $destination = Join-Path $Target $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $relative) -Destination $destination
    }
}
Copy-Owned $install (Join-Path $job 'worker') $old.Files
Copy-Owned $download (Join-Path $job 'package') $next.Files
$oldFiles = @($old.Files | ForEach-Object { @{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $install $_) -Algorithm SHA256).Hash } })
$newFiles = @($next.Files | ForEach-Object { @{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $download $_) -Algorithm SHA256).Hash } })
if (-not ('FloatCanvasPublishedUpdate.Desktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace FloatCanvasPublishedUpdate {
 public static class Desktop {
  private static readonly Dictionary<int,IntPtr> Handles=new Dictionary<int,IntPtr>();
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  private struct Startup { public int Size; public string Reserved, Desktop, Title; public int X,Y,W,H,CX,CY,Fill; public uint Flags; public short Show,ReservedSize; public IntPtr ReservedPointer,Input,Output,Error; }
  [StructLayout(LayoutKind.Sequential)] private struct Info { public IntPtr Process,Thread; public uint ProcessId,ThreadId; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
  [DllImport("user32.dll", SetLastError=true)] private static extern bool CloseDesktop(IntPtr desktop);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref Startup startup, out Info info);
  [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetExitCodeProcess(IntPtr process,out uint code);
  [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenThread(uint access,bool inherit,uint id);
  [DllImport("kernel32.dll",SetLastError=true)] private static extern uint SuspendThread(IntPtr thread);
  [DllImport("kernel32.dll",SetLastError=true)] private static extern uint GetProcessIdOfThread(IntPtr thread);
  public static IntPtr Create(string name) { var result=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x000F01FF,IntPtr.Zero); if(result==IntPtr.Zero)throw new Win32Exception(); return result; }
  public static void Close(IntPtr desktop) { CloseDesktop(desktop); }
  public static uint ExitCode(int id) { uint code; if(!GetExitCodeProcess(Handles[id],out code))throw new Win32Exception();return code; }
  public static void CloseHandles() { foreach(var handle in Handles.Values)CloseHandle(handle);Handles.Clear(); }
  public static int Suspend(int id, string expectedExecutable) {
   int count=0;
   using(var process=Process.GetProcessById(id)) {
    if(process.Handle==IntPtr.Zero || !string.Equals(process.MainModule.FileName,expectedExecutable,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("Refusing to suspend a process outside the fixture installation.");
    foreach(ProcessThread thread in process.Threads) {
     var handle=OpenThread(0x42,false,(uint)thread.Id); if(handle==IntPtr.Zero)continue;
     try { if(GetProcessIdOfThread(handle)==(uint)id && SuspendThread(handle)!=uint.MaxValue)count++; }
     finally { CloseHandle(handle); }
    }
   }
   return count;
  }
  private static string Quote(string value) {
   var result=new StringBuilder("\""); int slashes=0;
   foreach(char c in value) { if(c=='\\'){slashes++;continue;} result.Append('\\',c=='"'?slashes*2+1:slashes).Append(c); slashes=0; }
   return result.Append('\\',slashes*2).Append('"').ToString();
  }
  public static int Start(string executable, string[] args, string cwd, string name) {
   var command=new StringBuilder(Quote(executable)); foreach(var arg in args)command.Append(' ').Append(Quote(arg));
   var startup=new Startup{Size=Marshal.SizeOf<Startup>(),Desktop="WinSta0\\"+name,Flags=1,Show=0}; Info info;
   if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,cwd,ref startup,out info))throw new Win32Exception();
   CloseHandle(info.Thread);Handles.Add((int)info.ProcessId,info.Process);return (int)info.ProcessId;
  }
 }
}
'@
}
function Owned-Processes {
    @(Get-CimInstance Win32_Process -Filter "Name = 'FloatCanvas.exe' OR Name = 'FloatCanvas.Host.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($run + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine)
}
function Matches-Owned([string]$Directory, [object[]]$Files) {
    foreach ($file in $Files) {
        $path = Join-Path $Directory $file.Path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.Sha256) { return $false }
    }
    return $true
}
$savedEnvironment = @{}
foreach ($name in @('FLOATCANVAS_UPDATE_STARTUP_PLAN','FLOATCANVAS_UPDATE_LAUNCHER_PID','FLOATCANVAS_UPDATE_LAUNCHER_CREATION_FILETIME')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
$desktopName = 'FloatCanvas.PublishedUpgrade.' + $PID + '.' + [Guid]::NewGuid().ToString('N')
$desktop = [FloatCanvasPublishedUpdate.Desktop]::Create($desktopName)
$worker = $null
$workerFinished = $false
$observations = [Collections.Generic.List[object]]::new()
try {
    if ($Case -eq 'OrdinaryLauncher') {
        $launcherId = [FloatCanvasPublishedUpdate.Desktop]::Start((Join-Path $download 'FloatCanvas.exe'),
            @('--smoke','--smoke-seconds','2','--profile',$profile), $run, $desktopName)
        $clock = [Diagnostics.Stopwatch]::StartNew(); $hostId = 0
        while ($clock.Elapsed.TotalSeconds -lt 15 -and -not $hostId) {
            $hosts = @(Owned-Processes | Where-Object { $_.ParentProcessId -eq $launcherId -and $_.ExecutablePath -ieq (Join-Path $download 'FloatCanvas.Host.exe') })
            if ($hosts.Count -eq 1) { $hostId = [int]$hosts[0].ProcessId }
            else { Start-Sleep -Milliseconds 100 }
        }
        if (-not $hostId -or [FloatCanvasPublishedUpdate.Desktop]::ExitCode($launcherId) -ne 259) { throw 'Ordinary launcher did not remain alive while its Host was running.' }
        while ($clock.Elapsed.TotalSeconds -lt 30 -and [FloatCanvasPublishedUpdate.Desktop]::ExitCode($launcherId) -eq 259) { Start-Sleep -Milliseconds 100 }
        $code = [FloatCanvasPublishedUpdate.Desktop]::ExitCode($launcherId)
        if ($code -ne 0 -or @(Owned-Processes | Where-Object { $_.ProcessId -eq $hostId }).Count -ne 0) { throw 'Ordinary launcher did not return the smoke Host exit code after Host exit.' }
        $workerFinished = $true
        [pscustomobject]@{ Case=$Case; Passed=$true; Checks=3; NativePid=$launcherId; HostPid=$hostId; ExitCode=$code; Run=$run } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
        Write-Host "PASS $Case (3 checks): $run"
        return
    }
    $launcherId = [FloatCanvasPublishedUpdate.Desktop]::Start((Join-Path $install 'FloatCanvas.exe'),
        @('--smoke','--smoke-seconds','4','--profile',$profile), $run, $desktopName)
    $parent = $null
    if (Test-Path -LiteralPath (Join-Path $install 'FloatCanvas.Host.exe')) {
        $wait = [Diagnostics.Stopwatch]::StartNew()
        while ($wait.Elapsed.TotalSeconds -lt 6) {
            $children = @(Owned-Processes | Where-Object { $_.ParentProcessId -eq $launcherId -and $_.ExecutablePath -ieq (Join-Path $install 'FloatCanvas.Host.exe') })
            if ($children.Count -eq 1) { $parent = Get-Process -Id $children[0].ProcessId; break }
            Start-Sleep -Milliseconds 100
        }
    }
    else {
        $parent = Get-Process -Id $launcherId
    }
    if (-not $parent) { throw 'The actual old published application parent was not found.' }
    $plan = @{
        Format = 1; InstallDirectory = $install; ProfileDirectory = $profile
        ParentProcessId = $parent.Id; ParentStartUtcTicks = $parent.StartTime.ToUniversalTime().Ticks
        Version = $next.Version; RestartArguments = @('--profile',$profile,'--recover','--primary-screen')
        OldFiles = $oldFiles; NewFiles = $newFiles
    }
    $planPath = Join-Path $job 'plan.json'
    $plan | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $planPath -Encoding utf8
    [pscustomobject]@{ Run=$run; Install=$install; Profile=$profile; Job=$job; Desktop=$desktopName; OldLauncher=$launcherId; OldHost=$parent.Id; Plan=$planPath } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'fixture.json') -Encoding utf8
    $workerExe = Join-Path $job 'worker\FloatCanvas.Host.exe'
    if (-not (Test-Path -LiteralPath $workerExe)) { $workerExe = Join-Path $job 'worker\FloatCanvas.exe' }
    $workerId = [FloatCanvasPublishedUpdate.Desktop]::Start($workerExe, @('--apply-update',$planPath), (Join-Path $job 'worker'), $desktopName)
    $worker = Get-Process -Id $workerId
    Write-Host "Actual published ZIP worker started: $workerId; old launcher=$launcherId; old Host=$($parent.Id); fixture=$run"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $previousPhase = ''
    $previousAcknowledgement = ''
    $suspendedUpdatedHost = $false
    $candidateHost = $null
    $candidateNativeId = 0
    while (-not $worker.HasExited -and $clock.Elapsed.TotalSeconds -lt 90) {
        $startedPath = Join-Path $job 'started.json'
        if (Test-Path -LiteralPath $startedPath) {
            $acknowledgementText = Get-Content -LiteralPath $startedPath -Raw
            if ($acknowledgementText -cne $previousAcknowledgement) {
                $previousAcknowledgement = $acknowledgementText
                $observations.Add([pscustomobject]@{ Elapsed=$clock.Elapsed.TotalSeconds; Phase='StartupAcknowledgement'; Identity=($acknowledgementText | ConvertFrom-Json); Processes=@(Owned-Processes) })
                $observations | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'stages.json') -Encoding utf8
                Write-Host 'Actual managed startup acknowledgement recorded with owned native/Host process identities.'
            }
        }
        if ($SuspendUpdatedStartup -and -not $suspendedUpdatedHost) {
            # The unchanged 0.2.1 worker predates launching.json; observe only
            # the newly created Host inside this private installation instead.
            $hosts = @(Owned-Processes | Where-Object { $_.ExecutablePath -ieq (Join-Path $install 'FloatCanvas.Host.exe') })
            if ($hosts.Count -eq 1) {
                if (Test-Path -LiteralPath $startedPath) { throw 'Startup acknowledgement preceded the controlled failure; refusing to claim rollback coverage.' }
                $candidateHost = $hosts[0]; $candidateNativeId = [int]$candidateHost.ParentProcessId
                $observations.Add([pscustomobject]@{ Elapsed=$clock.Elapsed.TotalSeconds; Phase='SuspendingUnacknowledgedHost'; Processes=@(Owned-Processes) })
                $threadCount = [FloatCanvasPublishedUpdate.Desktop]::Suspend($candidateHost.ProcessId, (Join-Path $install 'FloatCanvas.Host.exe'))
                if ($threadCount -lt 1) { throw 'No fixture Host thread could be suspended.' }
                $suspendedUpdatedHost = $true
                Write-Host "CONTROLLED FAULT: suspended $threadCount threads of new fixture Host $($candidateHost.ProcessId) before acknowledgement."
            }
        }
        $journalPath = Join-Path $job 'journal.json'
        if (Test-Path -LiteralPath $journalPath) {
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
            if ($journal.Phase -cne $previousPhase) {
                $previousPhase = $journal.Phase
                $observations.Add([pscustomobject]@{ Elapsed=$clock.Elapsed.TotalSeconds; Phase=$previousPhase; Processes=@(Owned-Processes) })
                $observations | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'stages.json') -Encoding utf8
                Write-Host "Published ZIP stage: $previousPhase"
            }
        }
        Start-Sleep -Milliseconds 100
        $worker.Refresh()
    }
    if (-not $worker.HasExited) { throw 'Worker did not finish within 90 seconds. It is retained; no backup or transaction state was removed.' }
    $workerFinished = $true
    $journalPath = Join-Path $job 'journal.json'
    $journal = if (Test-Path -LiteralPath $journalPath) { Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json } else { $null }
    $resultPath = Join-Path $profile 'updates\result.json'
    $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } else { $null }
    $missing = @($old.Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $install $_) -PathType Leaf) })
    $empty = @($old.Files | Where-Object { (Test-Path -LiteralPath (Join-Path $install $_) -PathType Leaf) -and (Get-Item -LiteralPath (Join-Path $install $_)).Length -eq 0 })
    $summary = [pscustomobject]@{
        Run=$run; Job=$job; ExitCode=[FloatCanvasPublishedUpdate.Desktop]::ExitCode($workerId); Journal=$journal; Result=$result
        WorkerLog=(Get-Content -LiteralPath (Join-Path $job 'worker.log') -Raw)
        ControlledStartupSuspension=[bool]$SuspendUpdatedStartup
        OldVersion=$old.Version; TargetVersion=$next.Version
        RemainingProcesses=@(Owned-Processes); MissingOldManifestFiles=$missing; EmptyOldManifestFiles=$empty
        PrivateUserFilePreserved=((Get-Content -LiteralPath (Join-Path $install 'private-fixture-user.notes') -Raw) -ceq 'Unowned file must survive the real published ZIP transaction.')
        Preconditions='Immutable historical ZIP worker, generated isolated profile, ordinary renderer, private non-input desktop. Real parent exit, worker, restart, startup acknowledgement and rollback; only the failure case suspends its owned unacknowledged Host.'
    }
    $remaining = @($summary.RemainingProcesses)
    $checks = [ordered]@{ UserFilePreserved = $summary.PrivateUserFilePreserved; BackupComplete = (Matches-Owned (Join-Path $job 'backup') $oldFiles) }
    if ($SuspendUpdatedStartup) {
        $newOnly = @($next.Files | Where-Object { $old.Files -cnotcontains $_ })
        $checks.WorkerReportedFailure = $summary.ExitCode -eq 1
        $checks.StartupTimeoutExercised = $suspendedUpdatedHost -and $summary.WorkerLog.Contains('did not confirm a successful startup')
        $checks.CompleteOldGeneration = Matches-Owned $install $oldFiles
        $checks.NewOnlyFilesRemoved = @($newOnly | Where-Object { Test-Path -LiteralPath (Join-Path $install $_) }).Count -eq 0
        $checks.CandidateFamilyExited = $null -ne $candidateHost -and @($remaining | Where-Object { $_.ProcessId -eq $candidateHost.ProcessId -or $_.ProcessId -eq $candidateNativeId }).Count -eq 0
        $checks.NoRecoveryAccessDenied = -not $summary.WorkerLog.Contains('UnauthorizedAccessException') -and -not $summary.WorkerLog.Contains('Recovery failed')
    }
    else {
        # The new session may consume its one-shot result before observation.
        # The old worker's exit/log and exact installed bytes remain authoritative.
        $checks.WorkerSucceeded = $summary.ExitCode -eq 0 -and ($null -eq $result -or $result.Success)
        $checks.WorkerLoggedCommit = $summary.WorkerLog.Contains('Update installed and restarted.')
        $checks.CompleteNewGeneration = Matches-Owned $install $newFiles
        $ack = Get-Content -LiteralPath (Join-Path $job 'started.json') -Raw | ConvertFrom-Json
        $hosts = @($remaining | Where-Object { $_.ExecutablePath -ieq (Join-Path $install 'FloatCanvas.Host.exe') })
        $expectedPid = if ($hosts.Count -eq 1) { if ($Case -eq 'LegacySuccess') { $hosts[0].ParentProcessId } else { $hosts[0].ProcessId } } else { 0 }
        $checks.AcknowledgedExpectedProcess = $expectedPid -ne 0 -and $ack.ProcessId -eq $expectedPid -and
            (Get-Process -Id $expectedPid).StartTime.ToUniversalTime().Ticks -eq $ack.StartUtcTicks
    }
    $summary | Add-Member -NotePropertyName Checks -NotePropertyValue $checks
    $summary | Add-Member -NotePropertyName Passed -NotePropertyValue (@($checks.Values | Where-Object { -not $_ }).Count -eq 0)
    $observations | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'stages.json') -Encoding utf8
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
    if (-not $summary.Passed) { $checks | ConvertTo-Json; Write-Host $summary.WorkerLog; throw "Portable update compatibility failed: $Case. Retained fixture: $run" }
    Write-Host "PASS $Case ($($checks.Count) checks): $run"
}
finally {
    # Stop only descendants whose executable is verified inside this fresh fixture,
    # after the production worker has finished all writes. Preserve every file.
    if ($workerFinished -or -not $worker) {
        foreach ($owned in @(Owned-Processes)) {
            try {
                $process = Get-Process -Id $owned.ProcessId -ErrorAction Stop
                if ($process.MainModule.FileName.StartsWith($run + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    $process.Kill(); [void]$process.WaitForExit(5000)
                }
            } catch { }
        }
    }
    [FloatCanvasPublishedUpdate.Desktop]::Close($desktop)
    [FloatCanvasPublishedUpdate.Desktop]::CloseHandles()
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    if ($worker) { $worker.Dispose() }
}
