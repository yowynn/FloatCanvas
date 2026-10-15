param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build FloatCanvas.slnx --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
}
finally { Pop-Location }
