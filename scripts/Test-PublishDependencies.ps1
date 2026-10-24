[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
foreach ($name in @('App','Core','Localization','Rendering','Storage','Windows')) {
    $directory = Join-Path $ProjectRoot "src/FloatCanvas.$name"
    $baseline = Get-Content -LiteralPath (Join-Path $directory 'packages.lock.json') -Raw | ConvertFrom-Json
    $publish = Get-Content -LiteralPath (Join-Path $directory 'packages.win-x64.lock.json') -Raw | ConvertFrom-Json
    foreach ($target in $baseline.dependencies.PSObject.Properties) {
        $ridTarget = $publish.dependencies.PSObject.Properties[$target.Name]
        if ($null -eq $ridTarget) { throw "Publish lock is missing tested target $name / $($target.Name)." }
        foreach ($package in $target.Value.PSObject.Properties) {
            if ($package.Value.type -eq 'Project') { continue }
            $candidate = $ridTarget.Value.PSObject.Properties[$package.Name]
            if ($null -eq $candidate -or $candidate.Value.resolved -cne $package.Value.resolved -or $candidate.Value.contentHash -cne $package.Value.contentHash) {
                throw "Publish dependency differs from the tested lock: $name / $($package.Name). Update both locks and retest."
            }
        }
    }
}
Write-Host 'PASS: Windows publish locks match tested package versions and content hashes.'
