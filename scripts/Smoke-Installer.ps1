#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$SetupPath,
    [ValidateSet('en', 'zhcn')][string]$Language = 'en',
    [string]$WorkingDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-installer-smoke')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1'
if (Test-Path -LiteralPath $uninstallKey) {
    throw 'Installer smoke requires a Windows user without an existing FloatCanvas installation. Its registration will not be overwritten.'
}
$run = Join-Path ([IO.Path]::GetFullPath($WorkingDirectory)) ([Guid]::NewGuid().ToString('N'))
$install = Join-Path $run 'installed'
New-Item -ItemType Directory -Path $run -Force | Out-Null
& (Join-Path $PSScriptRoot 'Smoke-Package.ps1') -ZipPath $ZipPath -ExtractionRoot (Join-Path $run 'source') -StaticOnly -RequireRelease
$manifestPath = @(Get-ChildItem -LiteralPath (Join-Path $run 'source') -Filter PACKAGE-INFO.json -Recurse -File)
if ($manifestPath.Count -ne 1) { throw 'Expected exactly one package manifest.' }
$source = $manifestPath[0].DirectoryName
$manifest = Get-Content -LiteralPath $manifestPath[0].FullName -Raw | ConvertFrom-Json
$expectedName = "FloatCanvas-$($manifest.Version)-win-x64-Setup.exe"
if ([IO.Path]::GetFileName($SetupPath) -cne $expectedName) { throw 'Installer filename does not match the verified ZIP version.' }
$hash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath "$SetupPath.sha256" -Raw).Trim() -cne "$hash  $expectedName") { throw 'Installer checksum mismatch.' }

function Invoke-Installer([string]$Executable, [string[]]$Arguments, [switch]$ExpectFailure, [hashtable]$ChildEnvironment = @{}) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @("/LANG=$Language") + $Arguments) {
        # Existing callers quote paths for the Windows command line. ArgumentList
        # handles quoting itself, so retain the value but remove those wrappers.
        if ($argument -match '^([^=]+=)"(.*)"$') { $argument = $Matches[1] + $Matches[2] }
        $start.ArgumentList.Add($argument)
    }
    foreach ($key in $ChildEnvironment.Keys) { $start.Environment[$key] = [string]$ChildEnvironment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(120000)) {
            $process.Kill($true)
            throw "Installer test timed out: $Executable"
        }
        if ($ExpectFailure) {
            if ($process.ExitCode -eq 0) { throw "Installer accepted an unsafe operation: $Executable" }
        }
        elseif ($process.ExitCode -ne 0) { throw "Installer test exited with code $($process.ExitCode): $Executable" }
    }
    finally { $process.Dispose() }
}
function Assert-Payload {
    foreach ($relative in $manifest.Files) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $source $relative) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $install $relative) -Algorithm SHA256).Hash
        if ($actual -cne $expected) { throw "Installer payload differs from the ZIP: $relative" }
    }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    if ($registration.InstallLocation.TrimEnd('\') -ine $install -or $registration.DisplayVersion -cne $manifest.Version) {
        throw 'Installed location or Windows uninstall version is incorrect.'
    }
    $owned = [IO.File]::ReadAllLines((Join-Path $install 'FloatCanvas.install-files.txt'))
    if (@(Compare-Object @($manifest.Files | Sort-Object) @($owned | Sort-Object)).Count -ne 0) {
        throw 'The installer-owned file list must exactly match the verified ZIP manifest.'
    }
}
$uninstaller = Join-Path $install 'unins000.exe'
try {
    $arguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=""', "/DIR=`"$install`"")
    $missingRuntime = Join-Path $run 'missing-runtime'
    New-Item -ItemType Directory -Path $missingRuntime -Force | Out-Null
    Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-missing-runtime.log')`"") -ExpectFailure -ChildEnvironment @{ DOTNET_ROOT_X64 = $missingRuntime }
    if ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $uninstallKey)) { throw 'Missing-runtime setup wrote application files or registration.' }
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    $conflict = Join-Path $install 'LICENSE'
    [IO.File]::WriteAllText($conflict, 'Existing user file.')
    Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-existing-file.log')`"") -ExpectFailure
    if ([IO.File]::ReadAllText($conflict) -cne 'Existing user file.') { throw 'Setup overwrote an existing user file.' }
    Remove-Item -LiteralPath $conflict
    Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'install.log')`"")
    Assert-Payload
    $listPath = Join-Path $install 'FloatCanvas.install-files.txt'
    $originalList = [IO.File]::ReadAllText($listPath)
    $lock = [IO.File]::Open((Join-Path $install 'FloatCanvas.dll'), 'Open', 'Read', 'Read')
    try {
        Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-locked-file.log')`"") -ExpectFailure
        Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $run 'reject-locked-uninstall.log')`"") -ExpectFailure
    }
    finally { $lock.Dispose() }
    Assert-Payload
    # Moving an existing installation must not orphan its original uninstall log.
    $otherArguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=""', "/DIR=`"$(Join-Path $run 'other-install')`"")
    Invoke-Installer $SetupPath ($otherArguments + "/LOG=`"$(Join-Path $run 'reject-moved-install.log')`"") -ExpectFailure
    Assert-Payload
    # A future package must not claim a user file that it did not previously own.
    try {
        [IO.File]::WriteAllLines($listPath, [string[]]@($manifest.Files | Where-Object { $_ -cne 'LICENSE' }), [Text.UTF8Encoding]::new($false))
        Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-new-owned-conflict.log')`"") -ExpectFailure
    }
    finally { [IO.File]::WriteAllText($listPath, $originalList, [Text.UTF8Encoding]::new($false)) }
    $pointer = Join-Path $install '.floatcanvas-update'
    try {
        [IO.File]::WriteAllText($pointer, 'Unfinished update fixture.')
        Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-pending-update.log')`"") -ExpectFailure
        Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -ExpectFailure
    }
    finally { Remove-Item -LiteralPath $pointer }
    $installedManifest = Join-Path $install 'PACKAGE-INFO.json'
    try {
        $futureManifest = Get-Content -LiteralPath $installedManifest -Raw | ConvertFrom-Json
        $futureManifest.Version = '999.0.0'
        $futureManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $installedManifest -Encoding UTF8
        Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reject-downgrade.log')`"") -ExpectFailure
    }
    finally { Copy-Item -LiteralPath (Join-Path $source 'PACKAGE-INFO.json') -Destination $installedManifest -Force }
    $outside = Join-Path $run 'outside-protected.txt'
    [IO.File]::WriteAllText($outside, 'Outside installation.')
    try {
        [IO.File]::WriteAllText($listPath, $originalList + "../outside-protected.txt`r`n", [Text.UTF8Encoding]::new($false))
        Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $run 'reject-path-escape.log')`"") -ExpectFailure
        if ([IO.File]::ReadAllText($outside) -cne 'Outside installation.') { throw 'Uninstall changed an external file.' }
    }
    finally { [IO.File]::WriteAllText($listPath, $originalList, [Text.UTF8Encoding]::new($false)) }
    Assert-Payload
    # Inno's own historic uninstall log may still contain files removed by a ZIP
    # update. Even a link outside the current owned list must block that log.
    $linkedDirectory = Join-Path $install 'old-package-directory'
    $outsideDirectory = Join-Path $run 'outside-directory'
    New-Item -ItemType Directory -Path $outsideDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $outsideDirectory 'keep.txt'), 'External data.')
    try {
        New-Item -ItemType Junction -Path $linkedDirectory -Target $outsideDirectory | Out-Null
        Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $run 'reject-junction.log')`"") -ExpectFailure
        if ([IO.File]::ReadAllText((Join-Path $outsideDirectory 'keep.txt')) -cne 'External data.') { throw 'Uninstall followed an external junction.' }
    }
    finally {
        # Remove only this fixture's junction itself, never the external target.
        if ((Get-Item -LiteralPath $linkedDirectory -ErrorAction SilentlyContinue).LinkType -eq 'Junction') {
            [IO.Directory]::Delete($linkedDirectory)
        }
    }
    # A user file must survive both an in-place reinstall and uninstall.
    $sentinel = Join-Path $install 'my-work.floatcanvas'
    [IO.File]::WriteAllText($sentinel, 'User-owned work must survive.')
    Invoke-Installer $SetupPath ($arguments + "/LOG=`"$(Join-Path $run 'reinstall.log')`"")
    Assert-Payload
    if ([IO.File]::ReadAllText($sentinel) -cne 'User-owned work must survive.') { throw 'Reinstall changed user-owned work.' }

    # Model an owned file added by the ZIP updater after the original installation.
    $added = 'updated-components/更新后新增文件.txt'
    $addedPath = Join-Path $install $added
    New-Item -ItemType Directory -Path (Split-Path -Parent $addedPath) -Force | Out-Null
    [IO.File]::WriteAllText($addedPath, 'Owned by a subsequent update.')
    # A path removed by an update can later belong to the user. Inno's historic
    # install log must not override the current package's ownership list.
    $currentFiles = @($manifest.Files | Where-Object { $_ -cne 'LICENSE' })
    [IO.File]::WriteAllText((Join-Path $install 'LICENSE'), 'User file at a formerly owned path.')
    [IO.File]::WriteAllLines((Join-Path $install 'FloatCanvas.install-files.txt'), [string[]]($currentFiles + $added), [Text.UTF8Encoding]::new($false))
    Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $run 'uninstall.log')`"")
    if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall registration remains.' }
    foreach ($relative in $currentFiles + $added + 'FloatCanvas.install-files.txt') {
        if (Test-Path -LiteralPath (Join-Path $install $relative)) { throw "An owned file remains after uninstall: $relative" }
    }
    if ([IO.File]::ReadAllText($sentinel) -cne 'User-owned work must survive.') { throw 'Uninstall changed user-owned work.' }
    if ([IO.File]::ReadAllText((Join-Path $install 'LICENSE')) -cne 'User file at a formerly owned path.') { throw 'Historic installer ownership removed a current user file.' }
    [ordered]@{
        Passed = $true; Zip = $ZipPath; Setup = $SetupPath; SetupSha256 = $hash
        Version = $manifest.Version; PayloadFiles = @($manifest.Files).Count
        Language = $Language; MissingRuntimeBlockedBeforeInstall = $true
        ExactZipPayload = $true; Reinstall = $true; UpdatedOwnedFileRemoved = $true; UserWorkPreserved = $true
        FormerlyOwnedUserFilePreserved = $true
        RejectedUnsafeOperations = @('existing user file', 'locked file', 'locked uninstall', 'moved install', 'new owned file conflict', 'pending update', 'downgrade', 'path escape', 'junction')
        Limitation = 'Tests this Windows user with its existing runtime; missing-runtime and Windows 10 UI behavior require separate acceptance.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding UTF8
    Write-Host "PASS installer smoke: $(Join-Path $run 'result.json')"
}
finally {
    if (Test-Path -LiteralPath $uninstallKey) {
        $location = (Get-ItemProperty -LiteralPath $uninstallKey).InstallLocation
        if ($location.TrimEnd('\') -ieq $install -and (Test-Path -LiteralPath $uninstaller)) {
            Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $run 'cleanup.log')`"")
        }
    }
}
