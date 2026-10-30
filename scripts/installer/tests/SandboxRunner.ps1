#requires -Version 7.2
# This script intentionally performs real installations, runtime installation and
# data-removal tests, exclusively inside a disposable Windows Sandbox guest.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inputRoot = 'C:\FloatCanvasInput'
$results = 'C:\FloatCanvasResults'
$runId = (Get-Content -LiteralPath (Join-Path $results 'run-id.txt') -Raw).Trim()
$root = 'C:\FloatCanvasValidation'
$configuration = $null
$guardPassed = $false
$transcriptStarted = $false
$proxyRelay = $null
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ Passed = $false; RunId = $runId; Error = $null; Checks = $checks; Environment = $null; GuestOnly = $false }
$transcript = Join-Path $results 'transcript.txt'
function Check([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAILED: $Name" }
    $checks.Add($Name); Write-Host "PASS: $Name"
}
function Invoke-Exe([string]$Executable, [string[]]$Arguments, [hashtable]$ChildEnvironment = @{}, [int[]]$ExitCodes = @(0), [int]$Timeout = 180) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $ChildEnvironment.Keys) { $info.Environment[$key] = $ChildEnvironment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit($Timeout * 1000)) { $process.Kill($true); throw "Guest process timed out: $Executable" }
        if ($process.ExitCode -notin $ExitCodes) { throw "Guest process exited $($process.ExitCode): $Executable" }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
function Stop-InstalledProcesses([string]$Directory) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    if (-not $prefix.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Guest process cleanup escaped fixture.' }
    foreach ($process in @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })) {
        Stop-Process -Id $process.ProcessId -ErrorAction Stop
    }
    Start-Sleep -Milliseconds 400
}
function Registration([string]$Scope) {
    if ($Scope -eq 'machine') { return 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1' }
    return 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1'
}
function Setup-Arguments([string]$Scope, [string]$Directory, [string]$Log, [string]$Language = 'en') {
    $mode = if ($Scope -eq 'machine') { '/ALLUSERS' } else { '/CURRENTUSER' }
    return @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', $mode, "/LANG=$Language", '/NOICONS', '/TASKS=', "/DIR=$Directory", "/LOG=$Log")
}
function Install([string]$Scope, [string]$Directory, [string]$Language = 'en') {
    Invoke-Exe $setup (Setup-Arguments $Scope $Directory (Join-Path $results "$Scope-$Language-install.log") $Language) | Out-Null
    $key = Registration $Scope
    Check "$Scope installation registered in correct hive" ((Get-ItemProperty -LiteralPath $key).InstallLocation.TrimEnd('\') -ieq $Directory)
    Check "$Scope uninstall executable exists" (Test-Path -LiteralPath (Join-Path $Directory 'unins000.exe'))
}
function Install-WithUpdateHandoff([string]$Directory) {
    $eventName = 'Local\FloatCanvas.Update.' + [Guid]::NewGuid().ToString('N')
    $event = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)
    $handoffProfile = Join-Path $root 'handoff-profile'
    New-Item -ItemType Directory -Path $handoffProfile | Out-Null
    $info = [Diagnostics.ProcessStartInfo]::new($setup)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $arguments = (Setup-Arguments 'machine' $Directory (Join-Path $results 'machine-handoff-install.log')) +
        @("/FLOATCANVASUPDATEEVENT=$eventName", "/FLOATCANVASUPDATEPROFILE=$handoffProfile", '/FLOATCANVASUPDATEMONITOR=primary')
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($info)
        $signalled = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $signalled = $event.WaitOne(100)
            if ($signalled -or $process.HasExited) { break }
        } while ([DateTime]::UtcNow -lt $deadline)
        Check 'production Setup signalled machine update readiness after validating profile arguments' $signalled
        if (-not $process.WaitForExit(180000)) { throw 'Machine handoff Setup did not finish.' }
        Check 'machine update handoff Setup completed successfully' ($process.ExitCode -eq 0)
        Check 'handoff machine installation registered in HKLM' ((Get-ItemProperty -LiteralPath (Registration 'machine')).InstallLocation.TrimEnd('\') -ieq $Directory)
        Check 'handoff installation contains native uninstaller' (Test-Path -LiteralPath (Join-Path $Directory 'unins000.exe'))
        Check 'silent handoff does not launch the finish-page program' (@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -ieq (Join-Path $Directory 'FloatCanvas.exe') }).Count -eq 0)
    }
    finally {
        if ($process) { if (-not $process.HasExited) { $process.Kill($true) }; $process.Dispose() }
        $event.Dispose()
    }
}
function Uninstall([string]$Directory, [string]$Label, [switch]$DeleteData, [switch]$BrokenRuntime) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $results "$Label-uninstall.log")")
    if ($DeleteData) { $arguments += '/DELETEUSERDATA' }
    $environment = @{}
    if ($BrokenRuntime) { $environment['DOTNET_ROOT_X64'] = $emptyRuntime }
    $desktopRuntime = Join-Path (Split-Path -Parent $runtimeHost) 'shared\Microsoft.WindowsDesktop.App'
    $hiddenRuntime = $desktopRuntime + '.sandbox-hidden-' + $configuration.RunId
    if ($BrokenRuntime) {
        # The top-level guest identity/model guard has already run. Remove the
        # actual desktop framework from discovery, then restore it even if the
        # native uninstaller fails. The host runtime is never mapped writable.
        $expectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'dotnet\shared')) + '\'
        foreach ($path in @($desktopRuntime, $hiddenRuntime)) {
            if (-not [IO.Path]::GetFullPath($path).StartsWith($expectedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Guest runtime fixture escaped the expected runtime directory.'
            }
        }
        if (Test-Path -LiteralPath $hiddenRuntime) { throw 'Guest runtime fixture destination already exists.' }
        Move-Item -LiteralPath $desktopRuntime -Destination $hiddenRuntime
    }
    try {
        if ($BrokenRuntime) {
            $availableRuntimes = & $runtimeHost --list-runtimes | Out-String
            Check "$Label has no installed desktop framework during native uninstall" ($availableRuntimes -notmatch 'Microsoft.WindowsDesktop.App')
        }
        Invoke-Exe (Join-Path $Directory 'unins000.exe') $arguments $environment | Out-Null
    }
    finally {
        if ($BrokenRuntime) { Move-Item -LiteralPath $hiddenRuntime -Destination $desktopRuntime }
    }
}
function Check-UninstallBlocked([string]$Directory, [string]$Scope, [string]$Label, [switch]$DeleteData) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $results "$Label-uninstall.log")")
    if ($DeleteData) { $arguments += '/DELETEUSERDATA' }
    $code = Invoke-Exe (Join-Path $Directory 'unins000.exe') $arguments @{} (1..255)
    Check "$Label returns a nonzero uninstall result" ($code -ne 0)
    Check "$Label preserves Windows uninstall registration" (Test-Path -LiteralPath (Registration $Scope))
    Check "$Label preserves native uninstaller" (Test-Path -LiteralPath (Join-Path $Directory 'unins000.exe'))
    Check "$Label preserves installed program payload" (Test-Path -LiteralPath (Join-Path $Directory 'FloatCanvas.exe'))
}
function Seed-Integration([string]$Executable) {
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $run -Force | Out-Null
    Set-ItemProperty -LiteralPath $run -Name FloatCanvas -Value ('"' + $Executable + '" --startup')
    Set-ItemProperty -LiteralPath $run -Name FloatCanvasForeignTest -Value 'preserve foreign startup'
    $command = 'HKCU:\Software\Classes\FloatCanvas.Project\shell\open\command'
    New-Item -Path $command -Force | Out-Null
    Set-Item -LiteralPath $command -Value ('"' + $Executable + '" --open "%1"')
    Set-ItemProperty -LiteralPath $command -Name ForeignValue -Value 'preserve foreign registry value'
}
function Check-IntegrationRemoved([string]$Label) {
    $run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Check "$Label owned startup removed without .NET" ($null -eq $run.PSObject.Properties['FloatCanvas'])
    Check "$Label foreign startup preserved" ($run.FloatCanvasForeignTest -ceq 'preserve foreign startup')
    $command = 'HKCU:\Software\Classes\FloatCanvas.Project\shell\open\command'
    Check "$Label owned open command removed without .NET" ([string](Get-Item -LiteralPath $command).GetValue('') -eq '')
    Check "$Label foreign registry value preserved" ((Get-ItemProperty -LiteralPath $command).ForeignValue -ceq 'preserve foreign registry value')
}
function New-OfflineProfileFixture([string]$Executable) {
    # A saved private hive models an unloaded Windows profile without creating a
    # login, password, or session. Every path/key is confined to this guest.
    $sid = 'S-1-5-21-424242421-424242422-424242423-1001'
    $profileKey = "HKLM:\Software\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"
    Check 'offline profile fixture SID is unused' (-not (Test-Path -LiteralPath $profileKey))
    $directory = Join-Path $root 'offline-profile'
    New-Item -ItemType Directory -Path $directory | Out-Null
    $hive = Join-Path $directory 'NTUSER.DAT'
    $seed = 'Software\FloatCanvas-SandboxHive-' + $configuration.RunId
    $key = 'HKCU:\' + $seed + '\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -LiteralPath $key -Name FloatCanvas -Value ('"' + $Executable + '" --startup')
    Set-ItemProperty -LiteralPath $key -Name ForeignStartup -Value 'preserve offline startup'
    try { Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('save', ('HKCU\' + $seed), $hive, '/y') | Out-Null }
    finally { Remove-Item -LiteralPath ('HKCU:\' + $seed) -Recurse }
    $classesHive = Join-Path $directory 'AppData\Local\Microsoft\Windows\UsrClass.dat'
    New-Item -ItemType Directory -Path (Split-Path -Parent $classesHive) -Force | Out-Null
    $classesSeed = $seed + '-Classes'
    $classesCommand = 'HKCU:\' + $classesSeed + '\FloatCanvas.Project\shell\open\command'
    New-Item -Path $classesCommand -Force | Out-Null
    Set-Item -LiteralPath $classesCommand -Value ('"' + $Executable + '" --open "%1"')
    Set-ItemProperty -LiteralPath $classesCommand -Name ForeignValue -Value 'preserve offline class value'
    try { Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('save', ('HKCU\' + $classesSeed), $classesHive, '/y') | Out-Null }
    finally { Remove-Item -LiteralPath ('HKCU:\' + $classesSeed) -Recurse }
    New-Item -Path $profileKey -Force | Out-Null
    New-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath -Value $directory -PropertyType ExpandString | Out-Null
    $personal = Join-Path $directory 'AppData\Local\FloatCanvas\other-user-data.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $personal) -Force | Out-Null
    'Other Windows user data.' | Set-Content -LiteralPath $personal -Encoding UTF8
    return @{ Sid = $sid; Hive = $hive; ClassesHive = $classesHive; ProfileKey = $profileKey; PersonalFile = $personal }
}
function Check-OfflineProfileCleaned([hashtable]$Fixture) {
    $loaded = 'FloatCanvas-SandboxCheck-' + $configuration.RunId
    Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('load', ('HKU\' + $loaded), $Fixture.Hive) | Out-Null
    try {
        $path = 'Registry::HKEY_USERS\' + $loaded + '\Software\Microsoft\Windows\CurrentVersion\Run'
        $values = Get-ItemProperty -LiteralPath $path
        Check 'machine uninstall removed owned startup from unloaded profile' ($null -eq $values.PSObject.Properties['FloatCanvas'])
        Check 'machine uninstall preserved foreign unloaded-profile startup' ($values.ForeignStartup -ceq 'preserve offline startup')
    }
    finally { [GC]::Collect(); [GC]::WaitForPendingFinalizers(); Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('unload', ('HKU\' + $loaded)) | Out-Null }
    Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('load', ('HKU\' + $loaded), $Fixture.ClassesHive) | Out-Null
    try {
        $path = 'Registry::HKEY_USERS\' + $loaded + '\FloatCanvas.Project\shell\open\command'
        Check 'machine uninstall removed owned open command from unloaded classes hive' ([string](Get-Item -LiteralPath $path).GetValue('') -eq '')
        Check 'machine uninstall preserved foreign unloaded classes value' ((Get-ItemProperty -LiteralPath $path).ForeignValue -ceq 'preserve offline class value')
    }
    finally { [GC]::Collect(); [GC]::WaitForPendingFinalizers(); Invoke-Exe "$env:SystemRoot\System32\reg.exe" @('unload', ('HKU\' + $loaded)) | Out-Null }
}
function Invoke-ZipUpdate([string]$InstallDirectory) {
    $profile = Join-Path $root 'update-profile'
    $job = Join-Path $profile ('updates\jobs\' + [Guid]::NewGuid().ToString('N'))
    foreach ($part in @('worker', 'package', 'backup')) { New-Item -ItemType Directory -Path (Join-Path $job $part) -Force | Out-Null }
    $old = Get-Content -LiteralPath (Join-Path $InstallDirectory 'PACKAGE-INFO.json') -Raw | ConvertFrom-Json
    $next = $old | ConvertTo-Json -Depth 15 | ConvertFrom-Json
    $parts = $old.Version.Split('-', 2)[0].Split('.')
    $next.Version = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
    $added = 'updated-components/新增文件.txt'
    $next.Files = @($old.Files | Where-Object { $_ -cne 'LICENSE' }) + $added
    foreach ($relative in $old.Files) {
        $target = Join-Path (Join-Path $job 'worker') $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $InstallDirectory $relative) -Destination $target
    }
    foreach ($relative in $next.Files | Where-Object { $_ -cne $added }) {
        $target = Join-Path (Join-Path $job 'package') $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $InstallDirectory $relative) -Destination $target
    }
    $addedSource = Join-Path (Join-Path $job 'package') $added
    New-Item -ItemType Directory -Path (Split-Path -Parent $addedSource) -Force | Out-Null
    'Owned by the synthetic newer ZIP fixture.' | Set-Content -LiteralPath $addedSource -Encoding UTF8
    $next | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path (Join-Path $job 'package') 'PACKAGE-INFO.json') -Encoding UTF8
    $oldFiles = @($old.Files | ForEach-Object { @{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $InstallDirectory $_) -Algorithm SHA256).Hash } })
    $newFiles = @($next.Files | ForEach-Object { @{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path (Join-Path $job 'package') $_) -Algorithm SHA256).Hash } })
    # Use the production apply-update worker and real startup acknowledgement.
    # The fixture changes manifest version/file membership, not application code.
    $plan = @{ Format = 1; InstallDirectory = $InstallDirectory; ProfileDirectory = $profile
        ParentProcessId = [int]::MaxValue; ParentStartUtcTicks = 1; Version = $next.Version
        RestartArguments = @('--profile', $profile, '--recover', '--primary-screen'); OldFiles = $oldFiles; NewFiles = $newFiles }
    $planPath = Join-Path $job 'plan.json'
    $plan | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $planPath -Encoding UTF8
    try {
        Invoke-Exe (Join-Path (Join-Path $job 'worker') 'FloatCanvas.Host.exe') @('--apply-update', $planPath) | Out-Null
        $journal = Get-Content -LiteralPath (Join-Path $job 'journal.json') -Raw | ConvertFrom-Json
        Check 'production ZIP worker committed after real application startup' ($journal.Phase -ceq 'Committed')
        $list = [IO.File]::ReadAllLines((Join-Path $InstallDirectory 'FloatCanvas.install-files.txt'))
        Check 'ZIP update refreshed uninstall list with added file' ($list -ccontains $added)
        Check 'ZIP update removed obsolete owned file from uninstall list' ($list -cnotcontains 'LICENSE')
        Check 'ZIP update preserved original uninstaller' (Test-Path -LiteralPath (Join-Path $InstallDirectory 'unins000.exe'))
        Check 'ZIP update refreshed Windows registered version' ((Get-ItemProperty -LiteralPath (Registration 'user')).DisplayVersion -ceq $next.Version)
        'User file placed at a formerly owned path.' | Set-Content -LiteralPath (Join-Path $InstallDirectory 'LICENSE') -Encoding UTF8
    }
    finally { Stop-InstalledProcesses $InstallDirectory }
    return $added
}
try {
    $configuration = Get-Content -LiteralPath (Join-Path $inputRoot 'configuration.json') -Raw | ConvertFrom-Json
    $model = (Get-CimInstance Win32_ComputerSystem).Model
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $report.Environment = $identity.Name
    if ($env:USERNAME -cne 'WDAGUtilityAccount' -or $model -notmatch 'Virtual Machine' -or
        -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
        -not $configuration.SandboxOnly -or $configuration.RunId -cnotmatch '^[a-f0-9]{32}$' -or
        $runId -cne $configuration.RunId) {
        throw 'This destructive lifecycle suite runs only in the explicitly mapped Windows Sandbox guest.'
    }
    $guardPassed = $true
    $report.GuestOnly = $true
    if (Test-Path -LiteralPath $root) { throw 'The guest fixture directory must not already exist.' }
    New-Item -ItemType Directory -Path $root | Out-Null
    Start-Transcript -LiteralPath $transcript -Force | Out-Null
    $transcriptStarted = $true
    if ($configuration.GuestProxy) {
        # Only the disposable guest is configured. The host proxy must already
        # accept this endpoint; the launcher never changes host network policy.
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public static class SandboxInternetSettings {
    [StructLayout(LayoutKind.Sequential)]
    private struct IeProxy { public int AutoDetect; public IntPtr AutoConfigUrl; public IntPtr Proxy; public IntPtr Bypass; }
    [DllImport("winhttp.dll", SetLastError=true)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IeProxy proxy);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr pointer);
    public static string ReadProxy() {
        IeProxy proxy;
        if (!WinHttpGetIEProxyConfigForCurrentUser(out proxy)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(proxy.Proxy) ?? ""; }
        finally { GlobalFree(proxy.AutoConfigUrl); GlobalFree(proxy.Proxy); GlobalFree(proxy.Bypass); }
    }
}
public sealed class SandboxProxyRelay : IDisposable {
    private readonly TcpListener listener;
    private readonly string host;
    private readonly int port;
    private readonly CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    private readonly Task loop;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> connections = new System.Collections.Concurrent.ConcurrentBag<Task>();
    public SandboxProxyRelay(int localPort, string host, int port) {
        this.host=host; this.port=port;
        listener=new TcpListener(IPAddress.Loopback, localPort);
        listener.Start(8);
        loop=Task.Run(AcceptAsync);
    }
    private async Task AcceptAsync() {
        try {
            while (!stop.IsCancellationRequested) {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task ForwardAsync(TcpClient incoming) {
        using (incoming)
        using (var outgoing=new TcpClient())
        using (var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            var token=timeout.Token;
            try {
                var input=incoming.GetStream();
                using var header=new MemoryStream();
                var one=new byte[1];
                string text="";
                while (header.Length < 16384) {
                    if (await input.ReadAsync(one.AsMemory(), token) != 1) return;
                    header.WriteByte(one[0]);
                    if (header.Length >= 4 && one[0] == 10) {
                        text=Encoding.ASCII.GetString(header.ToArray());
                        if (text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                }
                // Forward only TLS tunnels to the pinned runtime origin. TLS
                // and Microsoft's certificate validation remain end to end.
                if (!text.StartsWith("CONNECT builds.dotnet.microsoft.com:443 HTTP/1.", StringComparison.OrdinalIgnoreCase) ||
                    !text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
                await outgoing.ConnectAsync(host, port, token);
                var output=outgoing.GetStream();
                await output.WriteAsync(header.ToArray().AsMemory(), token);
                var upload=input.CopyToAsync(output, token);
                var download=output.CopyToAsync(input, token);
                await Task.WhenAny(upload, download);
                timeout.Cancel();
                try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { }
            }
            catch (Exception error) when (error is OperationCanceledException || error is IOException || error is SocketException) { }
        }
    }
    public void Dispose() {
        stop.Cancel(); listener.Stop();
        try { loop.Wait(TimeSpan.FromSeconds(2)); Task.WhenAll(connections.ToArray()).Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        stop.Dispose();
    }
}
'@
        $report['GuestProxy'] = $configuration.GuestProxy
        $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
        $network = [ordered]@{
            Proxy = $configuration.GuestProxy
            WinHttpIeProxyBefore = [SandboxInternetSettings]::ReadProxy()
            WinHttpBefore = (& $netsh winhttp show proxy | Out-String)
            Addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress, PrefixLength)
            DefaultRoutes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Select-Object InterfaceAlias, NextHop)
        }
        try {
            $endpoint = $configuration.GuestProxy.Split(':')
            $effectiveProxy = $network.WinHttpIeProxyBefore
            if ($effectiveProxy -match '^(127\.0\.0\.1|localhost):([0-9]{1,5})$') {
                # Windows Sandbox can mirror the host's loopback proxy even
                # after guest settings change. Supply that loopback endpoint
                # inside the guest, forwarding solely the runtime HTTPS origin.
                $proxyRelay = [SandboxProxyRelay]::new([int]$Matches[2], $endpoint[0], [int]$endpoint[1])
                $network['GuestLoopbackRelay'] = $effectiveProxy
            }
            elseif ($effectiveProxy -cne $configuration.GuestProxy) {
                throw 'GuestProxy requires an inherited loopback proxy or the same existing guest endpoint.'
            }
            $connection = [Net.Sockets.TcpClient]::new()
            try {
                $connect = $connection.ConnectAsync($endpoint[0], [int]$endpoint[1])
                if (-not $connect.Wait(10000)) { throw 'Guest cannot reach the existing host proxy within 10 seconds.' }
                $network['ProxyTcpConnected'] = $connection.Connected
                Check 'guest can connect to the existing host proxy' $connection.Connected
            }
            finally { $connection.Dispose() }
            $response = Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -Proxy ('http://' + $effectiveProxy) -TimeoutSec 30
            $network['ExplicitProxyStatus'] = [int]$response.StatusCode
            $network['ExplicitProxyBytes'] = $response.RawContentLength
            Check 'guest explicit proxy retrieves official runtime metadata over HTTPS' ($response.StatusCode -eq 200)
        }
        catch { $network['Error'] = $_.ToString(); throw }
        finally { $network | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results 'guest-network.json') -Encoding UTF8 }
    }
    $zip = Join-Path $inputRoot $configuration.ZipName
    $setup = Join-Path $inputRoot $configuration.SetupName
    Check 'ZIP input hash matches frozen host artifact' ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ceq $configuration.ZipSha256)
    Check 'Setup input hash matches frozen host artifact' ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ceq $configuration.SetupSha256)
    Check 'clean guest has no existing per-user registration' (-not (Test-Path -LiteralPath (Registration 'user')))
    Check 'clean guest has no existing machine registration' (-not (Test-Path -LiteralPath (Registration 'machine')))
    $emptyRuntime = Join-Path $root 'empty-runtime'
    New-Item -ItemType Directory -Path $emptyRuntime | Out-Null
    $runtimeHost = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    $initialRuntimes = if (Test-Path -LiteralPath $runtimeHost) { & $runtimeHost --list-runtimes | Out-String } else { '' }
    Check 'clean guest initially has no .NET 10 desktop runtime' ($initialRuntimes -notmatch 'Microsoft.WindowsDesktop.App 10\.0\.')
    $bootstrapInstall = Join-Path $root 'runtime-bootstrap'
    Invoke-Exe $setup ((Setup-Arguments 'user' $bootstrapInstall (Join-Path $results 'runtime-bootstrap.log')) + '/INSTALLRUNTIME') @{} @(0) 600 | Out-Null
    $installedRuntimes = & $runtimeHost --list-runtimes | Out-String
    Check 'Setup downloaded and installed desktop runtime in guest' ($installedRuntimes -match 'Microsoft.WindowsDesktop.App 10\.0\.\d+\s')
    Check 'runtime installation includes the Core runtime' ($installedRuntimes -match 'Microsoft.NETCore.App 10\.0\.\d+\s')
    Check 'Setup continued after installing runtime' (Test-Path -LiteralPath (Join-Path $bootstrapInstall 'FloatCanvas.exe'))
    Uninstall $bootstrapInstall 'runtime-bootstrap'
    foreach ($language in @('en', 'zhcn')) {
        & (Join-Path $inputRoot 'Smoke-Installer.ps1') -ZipPath $zip -SetupPath $setup -WorkingDirectory (Join-Path $root "smoke-$language") -Language $language
        Check "$language production installer smoke suite" ($LASTEXITCODE -eq 0)
    }
    $profile = Join-Path $env:LOCALAPPDATA 'FloatCanvas'
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    $profileSentinel = Join-Path $profile 'sandbox-personal-data.txt'
    'Personal data fixture.' | Set-Content -LiteralPath $profileSentinel -Encoding UTF8
    $userInstall = Join-Path $root 'user-installed'
    Install 'user' $userInstall
    Seed-Integration (Join-Path $userInstall 'FloatCanvas.exe')
    $added = Invoke-ZipUpdate $userInstall
    Uninstall $userInstall 'zip-updated-no-runtime' -BrokenRuntime
    Check 'updated program file removed by original uninstaller' (-not (Test-Path -LiteralPath (Join-Path $userInstall $added)))
    Check 'formerly owned user file preserved after ZIP update and uninstall' ((Get-Content -LiteralPath (Join-Path $userInstall 'LICENSE') -Raw).Trim() -ceq 'User file placed at a formerly owned path.')
    Check 'personal data retained by default' (Test-Path -LiteralPath $profileSentinel)
    Check 'per-user uninstall registration removed' (-not (Test-Path -LiteralPath (Registration 'user')))
    Check-IntegrationRemoved 'user'
    $deleteInstall = Join-Path $root 'user-delete-data'
    Install 'user' $deleteInstall 'zhcn'
    $outsideData = Join-Path $root 'outside-personal-data'
    New-Item -ItemType Directory -Path $outsideData | Out-Null
    $outsideSentinel = Join-Path $outsideData 'preserve.txt'
    'Must survive a junction in the selected profile.' | Set-Content -LiteralPath $outsideSentinel
    $junction = Join-Path $profile 'sandbox-junction'
    New-Item -ItemType Junction -Path $junction -Target $outsideData | Out-Null
    try {
        Check-UninstallBlocked $deleteInstall 'user' 'personal-data-junction' -DeleteData
        Check 'unsafe profile junction target was not deleted' (Test-Path -LiteralPath $outsideSentinel)
    }
    finally { [IO.Directory]::Delete($junction) }
    $lockedFile = [IO.File]::Open($profileSentinel, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { Check-UninstallBlocked $deleteInstall 'user' 'locked-personal-data' -DeleteData }
    finally { $lockedFile.Dispose() }
    Uninstall $deleteInstall 'user-delete-data' -DeleteData -BrokenRuntime
    Check 'explicit per-user data removal deletes default profile' (-not (Test-Path -LiteralPath $profileSentinel))
    $machineInstall = Join-Path $root 'machine-installed'
    Install-WithUpdateHandoff $machineInstall
    Check 'machine install did not create per-user uninstall registration' (-not (Test-Path -LiteralPath (Registration 'user')))
    $machineAssociation = 'HKLM:\Software\Classes\FloatCanvas.Project\shell\open\command'
    Check 'machine install registers system file association' ([string](Get-Item -LiteralPath $machineAssociation).GetValue('') -like ('*' + $machineInstall + '*'))
    Set-ItemProperty -LiteralPath $machineAssociation -Name ForeignValue -Value 'preserve foreign system association value'
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    'Personal data fixture.' | Set-Content -LiteralPath $profileSentinel -Encoding UTF8
    Seed-Integration (Join-Path $machineInstall 'FloatCanvas.exe')
    $offline = New-OfflineProfileFixture (Join-Path $machineInstall 'FloatCanvas.exe')
    $offlineBytes = [IO.File]::ReadAllBytes($offline.Hive)
    [IO.File]::WriteAllText($offline.Hive, 'Invalid offline hive fixture.')
    try {
        Check-UninstallBlocked $machineInstall 'machine' 'offline-registry-cleanup-failure'
        Check 'registry cleanup failure preserves personal data' (Test-Path -LiteralPath $profileSentinel)
    }
    finally { [IO.File]::WriteAllBytes($offline.Hive, $offlineBytes) }
    Uninstall $machineInstall 'machine-no-runtime' -BrokenRuntime
    Check 'machine uninstall registration removed' (-not (Test-Path -LiteralPath (Registration 'machine')))
    Check 'machine uninstall preserves personal data by default' (Test-Path -LiteralPath $profileSentinel)
    Check 'machine uninstall removes owned system association command' ([string](Get-Item -LiteralPath $machineAssociation).GetValue('') -eq '')
    Check 'machine uninstall preserves foreign system association values' ((Get-ItemProperty -LiteralPath $machineAssociation).ForeignValue -ceq 'preserve foreign system association value')
    Check-IntegrationRemoved 'machine'
    Check-OfflineProfileCleaned $offline
    $machineDeleteInstall = Join-Path $root 'machine-delete-data'
    Install 'machine' $machineDeleteInstall 'zhcn'
    Uninstall $machineDeleteInstall 'machine-delete-data' -DeleteData -BrokenRuntime
    Check 'explicit machine uninstall data removal deletes selected current account profile' (-not (Test-Path -LiteralPath $profileSentinel))
    Check 'machine current-account data deletion preserves another account profile' (Test-Path -LiteralPath $offline.PersonalFile)
    Remove-Item -LiteralPath $offline.ProfileKey -Recurse
    $report.Passed = $true
    $report['ZipUpdateFixture'] = 'Production updater and real startup; synthetic newer manifest and added/removed files over the same frozen program payload.'
    $report['Limitations'] = 'Windows Sandbox guest admin account; does not exercise over-the-shoulder UAC or a physical Windows 10 device.'
}
catch { $report.Error = $_.ToString(); Write-Host $report.Error }
finally {
    if ($proxyRelay) { $proxyRelay.Dispose() }
    $report['Timestamp'] = [DateTime]::UtcNow.ToString('o')
    $report['CheckCount'] = $checks.Count
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    $temporary = Join-Path $results 'result.pending.json'
    $report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $results 'result.json') -Force
}
if ($guardPassed -and -not $configuration.KeepOpen) { & "$env:SystemRoot\System32\shutdown.exe" /s /t 0 }
if (-not $report.Passed) { exit 1 }
