#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '../CHANGELOG.md'),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ChangelogPath)).Replace("`r`n", "`n")
$sections = [regex]::Matches($text, '(?ms)^## \[(?<version>[^\]]+)\](?<suffix>[^\n]*)\n(?<body>.*?)(?=^## |\z)')
if ($sections.Count -eq 0) { throw 'No changelog version sections found.' }
$seen = @{}
$validated = 0
foreach ($section in $sections) {
    $name = $section.Groups['version'].Value
    if ($seen.ContainsKey($name)) { throw "Duplicate changelog version: $name" }
    $seen[$name] = $true
    if ($Version -and $name -cne $Version) { continue }
    $body = $section.Groups['body'].Value
    $structured = $body.Contains('<!-- floatcanvas-notes:')
    # Historical releases are immutable. Only the new format is enforced from 0.4.
    if (-not $structured -and $name -ne 'Unreleased' -and $name -match '^\d+\.\d+\.\d+' -and
        [version]$Matches[0] -lt [version]'0.4.0') { continue }
    if ($name -eq 'Unreleased' -and [string]::IsNullOrWhiteSpace($body)) { continue }
    if ($body.Length -gt 200000) { throw "Notes exceed the client size limit in $name." }
    if ($name -ne 'Unreleased' -and $section.Groups['suffix'].Value -notmatch '^ - \d{4}-\d{2}-\d{2}$') {
        throw "A dated heading is required for $name."
    }
    if ([regex]::Matches($body, '<!-- floatcanvas-notes:1 -->').Count -ne 1) { throw "Missing or unsupported notes format for $name." }
    $blocks = [regex]::Matches($body, '(?s)<!-- lang:(?<language>[^ ]+) -->(?<content>.*?)<!-- /lang -->')
    if ($blocks.Count -ne 2 -or [regex]::Matches($body, '<!-- lang:').Count -ne 2 -or
        [regex]::Matches($body, '<!-- /lang -->').Count -ne 2) { throw "$name requires exactly two closed language blocks." }
    $languages = @{}
    foreach ($block in $blocks) {
        $language = $block.Groups['language'].Value
        if ($language -cnotin @('zh-CN', 'en') -or $languages.ContainsKey($language)) { throw "Invalid or duplicate language in $name." }
        $category = ''
        $counts = [ordered]@{}
        foreach ($raw in $block.Groups['content'].Value.Split("`n")) {
            $line = $raw.Trim()
            if (-not $line -or $line.StartsWith('#')) { continue }
            if ($line -cmatch '^<!-- category:(feature|improvement|fix|compatibility) -->$') {
                if ($category -and $counts[$category] -eq 0) { throw "Empty category in $name/$language." }
                $category = $Matches[1]
                if ($counts.Contains($category)) { throw "Duplicate category in $name/$language." }
                $counts[$category] = 0
            } elseif ($category -and $line.StartsWith('- ') -and $line.Length -gt 2) {
                if ($line.Length -gt 1200) { throw "Overlong entry in $name/$language. Split or shorten it." }
                $counts[$category]++
            } else { throw "Unsupported notes line in $name/${language}: $line" }
        }
        if ($counts.Count -eq 0 -or $counts[$category] -eq 0) { throw "Empty notes in $name/$language." }
        if (($counts.Values | Measure-Object -Sum).Sum -gt 1000) { throw "Too many entries in $name/$language." }
        $languages[$language] = ($counts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ','
    }
    if ($languages['zh-CN'] -cne $languages['en']) { throw "Language categories, order and entry counts must match in $name." }
    $validated++
}
if ($Version -and -not $seen.ContainsKey($Version)) { throw "Changelog version not found: $Version" }
Write-Verbose "Validated $validated structured changelog sections."
