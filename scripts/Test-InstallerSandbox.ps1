#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$SetupPath,
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-sandbox'),
    [switch]$Run,
    [switch]$KeepOpen,
    [string]$GuestProxy = '',
    [ValidateRange(60, 3600)][int]$TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($GuestProxy -ne '' -and ($GuestProxy -notmatch '^([0-9]{1,3}\.){3}[0-9]{1,3}:[0-9]{1,5}$' -or
    -not [Net.IPAddress]::TryParse($GuestProxy.Split(':')[0], [ref]([Net.IPAddress]$null)) -or
    [int]$GuestProxy.Split(':')[1] -notin 1..65535)) {
    throw 'GuestProxy must be an already accessible IPv4 HTTP proxy endpoint (address:port), without credentials.'
}
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
foreach ($asset in @($zip, $setup)) {
    $expected = '{0}  {1}' -f (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($asset)
    if ((Get-Content -LiteralPath "$asset.sha256" -Raw).Trim() -cne $expected) { throw "Asset checksum mismatch: $asset" }
}
$runId = [Guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) $runId
$inputRoot = Join-Path $root 'input'
$resultRoot = Join-Path $root 'results'
New-Item -ItemType Directory -Path $inputRoot, $resultRoot -Force | Out-Null
foreach ($path in @($root, $inputRoot, $resultRoot, $PSHOME)) {
    if (((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Sandbox mappings cannot be links: $path" }
}
$powerShell = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $powerShell -PathType Leaf)) { throw 'The Sandbox suite needs a portable PowerShell 7 installation.' }
foreach ($asset in @($zip, "$zip.sha256", $setup, "$setup.sha256")) { Copy-Item -LiteralPath $asset -Destination $inputRoot }
foreach ($script in @('Smoke-Installer.ps1', 'Smoke-Package.ps1', 'installer/tests/SandboxRunner.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $inputRoot
}
$configuration = [ordered]@{
    SandboxOnly = $true; RunId = $runId
    ZipName = [IO.Path]::GetFileName($zip); ZipSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    SetupName = [IO.Path]::GetFileName($setup); SetupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    KeepOpen = [bool]$KeepOpen
    GuestProxy = $GuestProxy
}
$configuration | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputRoot 'configuration.json') -Encoding UTF8
$runId | Set-Content -LiteralPath (Join-Path $resultRoot 'run-id.txt') -Encoding ASCII
function Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
$wsb = Join-Path $root 'FloatCanvas-installer.wsb'
@"
<Configuration>
  <vGPU>Disable</vGPU>
  <Networking>Enable</Networking>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>6144</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$(Xml $inputRoot)</HostFolder><SandboxFolder>C:\FloatCanvasInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(Xml $PSHOME)</HostFolder><SandboxFolder>C:\FloatCanvasTooling</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(Xml $resultRoot)</HostFolder><SandboxFolder>C:\FloatCanvasResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>"C:\FloatCanvasTooling\pwsh.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\FloatCanvasInput\SandboxRunner.ps1"</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsb -Encoding UTF8
$prepared = [pscustomobject]@{ RunId = $runId; Configuration = $wsb; Results = $resultRoot; Started = [bool]$Run }
if (-not $Run) { return $prepared }
$sandbox = Get-Command WindowsSandbox.exe -ErrorAction Stop
if (@(Get-Process -Name WindowsSandboxClient -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'A Windows Sandbox client is already open. Close that guest or its shutdown dialog before starting this suite; the launcher will not close an unknown Sandbox session.'
}
# Only the disposable guest installs a runtime, changes machine registration, or
# exercises deleting personal-data fixtures. The host receives logs/results only.
$startedAt = [DateTime]::UtcNow
$process = Start-Process -FilePath $sandbox.Source -ArgumentList ('"' + $wsb + '"') -WindowStyle Hidden -PassThru
$ownedClient = $null
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$reportPath = Join-Path $resultRoot 'result.json'
try {
    while (-not (Test-Path -LiteralPath $reportPath)) {
        if (-not $ownedClient) {
            $clients = @(Get-Process -Name WindowsSandboxClient -ErrorAction SilentlyContinue | Where-Object { $_.StartTime.ToUniversalTime() -ge $startedAt })
            if ($clients.Count -eq 1) { $ownedClient = $clients[0] }
        }
        if ($process.HasExited -and $process.ExitCode -ne 0) {
            throw "Windows Sandbox exited with code $($process.ExitCode) before producing a guest result. Inspect $resultRoot."
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Windows Sandbox did not return a result within $TimeoutSeconds seconds. Inspect $resultRoot. No host installer was run." }
        Start-Sleep -Seconds 2
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.RunId -cne $runId) { throw 'Sandbox result belongs to a different run.' }
    if (-not $report.Passed) { throw "Sandbox validation failed: $($report.Error). See $reportPath." }
    Write-Host "PASS disposable Windows Sandbox installer lifecycle: $reportPath"
    $report
}
finally {
    if (-not $KeepOpen -and $ownedClient -and (Test-Path -LiteralPath $reportPath)) {
        # Result delivery precedes guest shutdown. Its client can retain a
        # shutdown dialog; dismiss only the client started by this invocation.
        Start-Sleep -Seconds 3
        if (-not $ownedClient.HasExited) { [void]$ownedClient.CloseMainWindow() }
    }
    if ($ownedClient) { $ownedClient.Dispose() }
    $process.Dispose()
}
