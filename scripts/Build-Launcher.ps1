param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required. See README: Build from source.' }
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Install the Visual Studio Desktop development with C++ workload and a Windows SDK.' }
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FloatCanvas.Launcher.Native/Launcher.cpp'))
$icon = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FloatCanvas.App/Assets/FloatCanvas.ico')).Replace('\','/')
$manifest = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FloatCanvas.App/app.manifest'))
$resource = Join-Path $output 'launcher.rc'
[IO.File]::WriteAllText($resource, "1 ICON `"$icon`"", [Text.Encoding]::Unicode)
$command = Join-Path $output 'build-launcher.cmd'
$commands = @('@echo off', "call `"$vcvars`" >nul", 'if errorlevel 1 exit /b %errorlevel%',
    "rc /nologo /fo `"$output\launcher.res`" `"$resource`"", 'if errorlevel 1 exit /b %errorlevel%',
    "cl /nologo /std:c++20 /EHsc /MT /O2 /W4 /DUNICODE /D_UNICODE `"$source`" /Fo`"$output\launcher.obj`" /Fe`"$output\FloatCanvas.exe`" /link `"$output\launcher.res`" user32.lib /SUBSYSTEM:WINDOWS /MANIFEST:EMBED /MANIFESTINPUT:`"$manifest`"", 'exit /b %errorlevel%')
[IO.File]::WriteAllLines($command, $commands, [Text.Encoding]::Default)
& $env:ComSpec /d /c $command
if ($LASTEXITCODE -ne 0) { throw "Recovery launcher build failed ($LASTEXITCODE)." }
