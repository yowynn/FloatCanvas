[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Interactive
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$appProject = Join-Path $projectRoot 'tests/FloatCanvas.AppTests/FloatCanvas.AppTests.csproj'
$appExecutable = Join-Path $projectRoot "tests/FloatCanvas.AppTests/bin/$Configuration/net10.0-windows/FloatCanvas.AppTests.exe"
$artifacts = Join-Path $projectRoot ('.artifacts/app-overlay-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$previousBackend = $env:FLOATCANVAS_FOCUS_BACKEND
$previousArtifacts = $env:FLOATCANVAS_FOCUS_ARTIFACTS

Push-Location $projectRoot
try {
    $running = Get-Process -Name FloatCanvas -ErrorAction SilentlyContinue
    if ($running) {
        throw 'Close the running FloatCanvas instance before starting overlay verification.'
    }

    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    & dotnet build $appProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Application test build failed with exit code $LASTEXITCODE" }

    $cases = [System.Collections.Generic.List[object]]::new()
    $cases.Add([pscustomobject]@{ Name = 'window-reveal'; Arguments = @('--window-reveal-only') })
    $cases.Add([pscustomobject]@{ Name = 'launcher-position'; Arguments = @('--launcher-position-only') })
    $cases.Add([pscustomobject]@{ Name = 'overlay-window-contract'; Arguments = @('--system-overlay-window-contract') })
    $cases.Add([pscustomobject]@{ Name = 'overlay-popup-contract'; Arguments = @('--system-overlay-popup-only') })
    if ($Interactive) {
        $cases.Add([pscustomobject]@{ Name = 'preview4-settings'; Arguments = @('--preview4-settings-runtime-only') })
        $cases.Add([pscustomobject]@{ Name = 'history-toolbar'; Arguments = @('--focus-session-only', '--focus-case', 'history-pass-through') })
        $cases.Add([pscustomobject]@{ Name = 'history-editing-toolbar'; Arguments = @('--focus-session-only', '--focus-case', 'history-editing') })
        $cases.Add([pscustomobject]@{ Name = 'input-entry-recovery'; Arguments = @('--system-input-entry-recovery-only') })
    }

    if ($Interactive) {
        $env:FLOATCANVAS_FOCUS_BACKEND = 'enhanced'
        $env:FLOATCANVAS_FOCUS_ARTIFACTS = Join-Path $artifacts 'focus'
        Write-Host 'Interactive overlay checks will briefly create owned windows and may change foreground focus.' -ForegroundColor Yellow
    }
    foreach ($case in $cases) {
        $log = Join-Path $artifacts ($case.Name + '.log')
        Write-Host "Running $($case.Name)..."
        $arguments = @($case.Arguments)
        & $appExecutable @arguments *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $log -Tail 80
            throw "$($case.Name) failed with exit code $LASTEXITCODE. Full log: $log"
        }
    }
    Write-Host "Overlay checks passed. Logs: $artifacts"
}
finally {
    $env:FLOATCANVAS_FOCUS_BACKEND = $previousBackend
    $env:FLOATCANVAS_FOCUS_ARTIFACTS = $previousArtifacts
    Pop-Location
}
