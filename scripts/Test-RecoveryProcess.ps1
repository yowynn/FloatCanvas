[CmdletBinding()]
param(
    [switch]$PrepareOnly,
    [string]$ApplicationPath,
    [string]$PreparedProfile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/e2e/recovery-process-profiles'))
if (-not $ApplicationPath) { $ApplicationPath = Join-Path $projectRoot 'src/FloatCanvas.App/bin/Release/net10.0-windows/FloatCanvas.exe' }
$ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
$profile = if ($PreparedProfile) { [IO.Path]::GetFullPath($PreparedProfile) } else { Join-Path $profilesRoot ([Guid]::NewGuid().ToString('N')) }
if (-not $profile.StartsWith($profilesRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Recovery process tests must use a unique profile below artifacts/e2e/recovery-process-profiles; user profiles are not allowed.'
}
$evidencePath = Join-Path $projectRoot 'artifacts/e2e/recovery-process.json'
$probeProject = Join-Path $projectRoot 'tests/FloatCanvas.RecoveryProbe/FloatCanvas.RecoveryProbe.csproj'
$probeDll = Join-Path $projectRoot 'tests/FloatCanvas.RecoveryProbe/bin/Release/net10.0/FloatCanvas.RecoveryProbe.dll'
$seedReport = Join-Path $profile 'seed-result.json'
$crashReport = Join-Path $profile 'after-crash-result.json'
$restartReport = Join-Path $profile 'after-restart-result.json'
$receiptPath = Join-Path $profile 'second-process/artifacts/m1/app-smoke.json'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$first = $null; $second = $null; $failure = $null; $passed = $false; $firstStarted = $null; $firstKilled = $null
$firstId = $null; $secondId = $null; $seedHash = $null; $generationHash = $null

function Run-Probe([string[]]$Arguments) {
    & dotnet $probeDll @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Production recovery probe failed with exit code $LASTEXITCODE" }
}
function Read-JsonIfPresent([string]$Path) { if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }; return $null }
function Start-OwnedApp([string]$WorkDirectory, [string[]]$Arguments) {
    New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($ApplicationPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $WorkDirectory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not start the owned application process.' }
    $owned.Add($process)
    return $process
}

Push-Location $projectRoot
try {
    & dotnet build $probeProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Recovery probe build failed with exit code $LASTEXITCODE" }
    if ($PreparedProfile) {
        if (-not (Test-Path -LiteralPath $seedReport)) { throw 'Prepared profile does not contain a seed report.' }
    }
    else { Run-Probe @('--seed', $profile, '--report', $seedReport) }
    $seedHash = (Read-JsonIfPresent $seedReport).CurrentHash
    $preparedCurrent = Join-Path $profile 'recovery/workspace.floatcanvas'
    $preparedHash = (Get-FileHash -LiteralPath $preparedCurrent -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($preparedHash -ne $seedHash) { throw 'This prepared profile has already been consumed or changed. Run without -PreparedProfile to create a fresh independent fixture.' }
    if ($PrepareOnly) { Write-Host "Prepared isolated recovery profile; no application launched: $profile"; return }
    if (-not (Test-Path -LiteralPath $ApplicationPath)) { throw "Application executable is missing: $ApplicationPath" }
    Write-Host 'Recovery process acceptance starts: only two owned app processes use this isolated profile. Welcome windows may be visible; no input will be injected.'
    $firstStarted = [DateTimeOffset]::Now
    # Normal startup intentionally opens a fresh canvas. This fixture explicitly
    # resumes its seeded workspace before testing crash persistence and restart.
    $first = Start-OwnedApp (Join-Path $profile 'first-process') @('--recover', '--profile', $profile)
    $firstId = $first.Id
    if (-not $first.WaitForInputIdle(10000)) { throw 'First owned process did not initialize its input message pump.' }
    $current = Join-Path $profile 'recovery/workspace.floatcanvas'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 20) {
        if ($first.HasExited) { throw "First owned process exited early: $($first.ExitCode)" }
        if (Test-Path -LiteralPath $current) {
            try { $candidateHash = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash.ToLowerInvariant() } catch { $candidateHash = $null }
            if ($candidateHash -and $candidateHash -ne $seedHash) { $generationHash = $candidateHash; break }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $generationHash) { throw 'The real application did not persist a new recovery generation within 20 seconds.' }
    if (Test-Path -LiteralPath (Join-Path $profile 'errors.log')) { throw 'First application process wrote errors.log.' }
    # Terminate only the exact process object started above; never enumerate or kill by application name.
    $first.Kill(); $first.WaitForExit(); $firstKilled = [DateTimeOffset]::Now
    Run-Probe @('--verify', $profile, '--expect-live-layout', '--report', $crashReport)
    $second = Start-OwnedApp (Join-Path $profile 'second-process') @('--smoke', '--recover', '--smoke-seconds', '2', '--profile', $profile)
    $secondId = $second.Id
    if ($firstId -eq $secondId) { throw 'Expected two distinct application process IDs.' }
    if (-not $second.WaitForExit(30000)) { throw 'Second owned application process did not complete recovered smoke startup within 30 seconds.' }
    if ($second.ExitCode -ne 0) { throw "Second owned application exited with code $($second.ExitCode)" }
    if (-not (Test-Path -LiteralPath $receiptPath)) { throw 'Second process did not write an actual application state receipt.' }
    $receipt = Read-JsonIfPresent $receiptPath
    if ($receipt.Process -ne $secondId) { throw 'Smoke receipt does not belong to the second owned process.' }
    if (Test-Path -LiteralPath (Join-Path $profile 'errors.log')) { throw 'Recovered application process wrote errors.log.' }
    Run-Probe @('--verify', $profile, '--expect-live-layout', '--receipt', $receiptPath, '--report', $restartReport)
    $passed = $true
}
catch { $failure = $_.Exception.ToString(); Write-Error -Message $failure -ErrorAction Continue }
finally {
    foreach ($process in $owned) {
        try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } } finally { $process.Dispose() }
    }
    $report = [pscustomobject]@{
        Passed = $passed; PreparedOnly = [bool]$PrepareOnly; Timestamp = [DateTimeOffset]::Now.ToString('o')
        Application = $ApplicationPath; Profile = $profile; FirstProcessId = $firstId; SecondProcessId = $secondId
        FirstStarted = $firstStarted; FirstForceTerminated = $firstKilled; SeedGenerationHash = $seedHash; ApplicationGenerationHash = $generationHash
        Seed = (Read-JsonIfPresent $seedReport); AfterForcedTermination = (Read-JsonIfPresent $crashReport)
        RestoredProcessReceipt = (Read-JsonIfPresent $receiptPath); AfterRestart = (Read-JsonIfPresent $restartReport); Failure = $failure
        InputInjection = $false
        Limitations = @('The initial scene is seeded through the production store, not physically drawn.', 'Only a unique isolated test profile is used. No user profile is read or changed.', 'This proves cross-process restoration of committed recovery data, not an abrupt power loss or interruption during an active atomic write.', 'Only child Process objects started by this invocation are terminated; no process-name kill or unrelated application automation is used.')
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $evidencePath) -Force | Out-Null
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Pop-Location
    if (-not $PrepareOnly) { Write-Host "Recovery process acceptance ended; all owned processes have exited. Evidence: $evidencePath" }
}
if ($failure -or (-not $passed -and -not $PrepareOnly)) { throw "Cross-process recovery acceptance failed; see $evidencePath" }
