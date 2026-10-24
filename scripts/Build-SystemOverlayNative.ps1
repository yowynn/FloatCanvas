param([Parameter(Mandatory)][string] $OutputDirectory,[switch]$RunTests)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if(-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'Missing Visual Studio Installer (vswhere.exe). Install Visual Studio Build Tools with Desktop development with C++, MSVC x64/x86 tools and a Windows SDK. See README: Build from source.'
}
$vsPath=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vsPath){throw 'Missing MSVC x64/x86 tools. Modify Visual Studio Build Tools: select Desktop development with C++ (Microsoft.VisualStudio.Workload.VCTools), including a Windows SDK.'}
$vcvars=Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if(-not (Test-Path -LiteralPath $vcvars -PathType Leaf)){throw "Missing compiler environment: $vcvars. Repair the Visual Studio C++ build tools installation."}
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/FloatCanvas.SystemOverlay.Native/SystemOverlay.cpp'))
$command=Join-Path $output 'build-system-overlay.cmd'
$text="@echo off`r`ncall `"$vcvars`" >nul`r`nif errorlevel 1 exit /b %errorlevel%`r`ncl /nologo /std:c++20 /EHsc /MT /O2 /W4 /LD /DUNICODE /D_UNICODE `"$source`" /Fo`"$output\SystemOverlay.obj`" /link /OUT:`"$output\FloatCanvas.SystemOverlay.dll`" /IMPLIB:`"$output\SystemOverlay.lib`" user32.lib runtimeobject.lib ole32.lib`r`nexit /b %errorlevel%`r`n"
if($RunTests){
    $text="@echo off`r`ncall `"$vcvars`" >nul`r`nif errorlevel 1 exit /b %errorlevel%`r`n"
    foreach($name in @('SystemOverlayTests','EntryKeyStateTests','InputSafetyWatchdogTests')) {
        $source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../tests/FloatCanvas.SystemOverlay.NativeTests/$name.cpp"))
        $text+="cl /nologo /std:c++20 /EHsc /MT /O2 /W4 /DUNICODE /D_UNICODE `"$source`" /Fo`"$output\$name.obj`" /Fe`"$output\$name.exe`" /link user32.lib runtimeobject.lib ole32.lib`r`nif errorlevel 1 exit /b %errorlevel%`r`n"
    }
    $text+="exit /b %errorlevel%`r`n"
}
[IO.File]::WriteAllText($command,$text,[Text.Encoding]::ASCII)
& $env:ComSpec /d /c $command
if($LASTEXITCODE -ne 0){throw "System overlay native build failed ($LASTEXITCODE)."}
if($RunTests){foreach($name in @('SystemOverlayTests','EntryKeyStateTests','InputSafetyWatchdogTests')){& (Join-Path $output "$name.exe");if($LASTEXITCODE -ne 0){throw "System overlay native tests failed: $name ($LASTEXITCODE)."}}}
