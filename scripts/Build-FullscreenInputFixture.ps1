param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vsPath){throw 'Visual Studio C++ x64 tools are required.'}
$vcvars=Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tests/FloatCanvas.FullscreenInputFixture/FullscreenInputFixture.cpp'))
$manifest=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../tests/FloatCanvas.FullscreenInputFixture/Fixture.manifest'))
$command=Join-Path $output 'build-fullscreen-input.cmd'
$text="@echo off`r`ncall `"$vcvars`" >nul`r`nif errorlevel 1 exit /b %errorlevel%`r`ncl /nologo /std:c++17 /utf-8 /EHsc /MT /O2 /W4 /DUNICODE /D_UNICODE `"$source`" /Fo`"$output\FullscreenInputFixture.obj`" /Fe`"$output\FullscreenInputFixture.exe`" /link /SUBSYSTEM:WINDOWS /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTINPUT:`"$manifest`" user32.lib gdi32.lib shell32.lib advapi32.lib d3d11.lib dxgi.lib`r`nexit /b %errorlevel%`r`n"
[IO.File]::WriteAllText($command,$text,[Text.Encoding]::ASCII)
& $env:ComSpec /d /c $command
if($LASTEXITCODE -ne 0){throw "Fullscreen input fixture build failed ($LASTEXITCODE)."}
Write-Output (Join-Path $output 'FullscreenInputFixture.exe')
