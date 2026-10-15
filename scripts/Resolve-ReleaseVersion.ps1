[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../src/FloatCanvas.App/FloatCanvas.App.csproj'),
    [string]$RepositoryPath = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This packaging contract deliberately excludes SemVer build metadata (+...).
if ($Tag.Contains('+')) { throw 'Release versions must use vMAJOR.MINOR.PATCH[-PRERELEASE]; +build metadata is not supported.' }
$numeric = '(?:0|[1-9][0-9]*)'
$prereleaseIdentifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$tagPattern = "\Av$numeric\.$numeric\.$numeric(?:-$prereleaseIdentifier(?:\.$prereleaseIdentifier)*)?\z"
if (-not [regex]::IsMatch($Tag, $tagPattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
    throw 'The release tag must be vMAJOR.MINOR.PATCH[-PRERELEASE], with no leading zeroes in numeric identifiers.'
}

$version = $Tag.Substring(1)
[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
$versions = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($versions.Count -ne 1 -or $versions[0].InnerText -cne $version) {
    throw "Release tag $Tag must exactly match the single Version value in the application project."
}

$repository = (Resolve-Path -LiteralPath $RepositoryPath).Path
$tagCommit = & git -C $repository rev-parse --verify "refs/tags/$Tag`^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) { throw "Release tag $Tag does not exist as a local commit tag. Fetch the existing remote tag first." }
$headCommit = & git -C $repository rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $headCommit) {
    throw "The checked-out commit must be the existing $Tag tag."
}

[pscustomobject]@{
    Tag = $Tag
    Version = $version
    Prerelease = $version.Contains('-')
    Commit = $headCommit
}
