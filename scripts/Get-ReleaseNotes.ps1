[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '../CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'
$changelog = Get-Content -LiteralPath $ChangelogPath -Raw
$heading = '^## \[' + [regex]::Escape($Version) + '\] - \d{4}-\d{2}-\d{2}\r?$'
$section = [regex]::Match($changelog, $heading, [Text.RegularExpressions.RegexOptions]::Multiline)
if (-not $section.Success) { throw "No dated changelog section exists for $Version." }
$remaining = $changelog.Substring($section.Index + $section.Length)
$next = [regex]::Match($remaining, '^## ', [Text.RegularExpressions.RegexOptions]::Multiline)
$notes = if ($next.Success) { $remaining.Substring(0, $next.Index).Trim() } else { $remaining.Trim() }
if ([string]::IsNullOrWhiteSpace($notes)) { throw "The changelog section for $Version is empty." }
$notes
