#requires -Version 7.2
[CmdletBinding()]
param([string]$CacheDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'FloatCanvas-build-tools'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Pinned upstream compiler. /PORTABLE=1 disables its uninstall registration,
# shortcuts and file associations; it does not install a global build tool.
$version = '6.7.4'
$expectedHash = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
$root = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$download = Join-Path $root "innosetup-$version.exe"
$directory = Join-Path $root "inno-$version"
if (-not (Test-Path -LiteralPath $download)) {
    Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-$version.exe" -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHash) {
    throw 'The downloaded Inno Setup compiler does not match the pinned upstream SHA256.'
}
$compiler = Join-Path $directory 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', "/DIR=`"$directory`"")
    $process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Inno Setup portable extraction failed: $($process.ExitCode)" }
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The portable Inno Setup compiler is missing.' }
return $compiler
