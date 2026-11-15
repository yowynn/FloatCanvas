#requires -Version 7.2
[CmdletBinding()]
param([string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('FloatCanvas-notes-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Test-Changelog.ps1')
New-Item -ItemType Directory -Force -Path $WorkingDirectory | Out-Null
$fixture = Join-Path $WorkingDirectory 'CHANGELOG.md'
$source = @'
## [Unreleased]

## [1.0.0] - 2026-11-14
<!-- floatcanvas-notes:1 -->
### 简体中文
<!-- lang:zh-CN -->
#### 新功能
<!-- category:feature -->
- **新功能。** 可见的行为。
<!-- /lang -->
### English
<!-- lang:en -->
#### New features
<!-- category:feature -->
- **A feature.** Observable behavior.
<!-- /lang -->

## [0.3.2] - 2026-10-31
- 保留历史。
- Keep history.
'@
$source | Set-Content -LiteralPath $fixture -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-Changelog.ps1') -ChangelogPath $fixture
$notes = & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version '1.0.0' -ChangelogPath $fixture
if (-not $notes.Contains('<!-- lang:en -->') -or $notes.Contains('Keep history') -or $notes.Contains('[Unreleased]')) { throw 'Wrong release-note extraction boundary.' }
$old = & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version '0.3.2' -ChangelogPath $fixture
if ($old.Trim() -cne "- 保留历史。`n- Keep history.") { throw 'Historical notes changed.' }
$invalid = @(
    $source.Replace('lang:en', 'lang:zh-CN'),
    $source.Replace('category:feature', 'category:unknown'),
    $source.Replace('<!-- floatcanvas-notes:1 -->', '<!-- floatcanvas-notes:2 -->'),
    $source.Replace('- **A feature.** Observable behavior.', ''),
    $source.Replace('- **A feature.** Observable behavior.', "- Extra.`n- **A feature.** Observable behavior."),
    $source.Replace('<!-- /lang -->', ''),
    $source.Replace('## [1.0.0] - 2026-11-14', '## [1.0.0]'),
    ($source + "`n## [1.0.0] - 2026-11-14`nDuplicate")
)
foreach ($bad in $invalid) {
    $bad | Set-Content -LiteralPath $fixture -Encoding utf8
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Test-Changelog.ps1') -ChangelogPath $fixture }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Malformed release notes unexpectedly passed validation.' }
}
$source | Set-Content -LiteralPath $fixture -Encoding utf8
$rejected = $false
try { & (Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1') -Version '2.0.0' -ChangelogPath $fixture }
catch { $rejected = $true }
if (-not $rejected) { throw 'Missing release unexpectedly accepted.' }
Write-Host 'Release-note checks passed: repository contract, extraction, immutable history and 9 rejection cases.'
