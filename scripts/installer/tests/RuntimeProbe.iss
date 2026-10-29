; This executable only runs prerequisite checks and exits before installing.
; Test-only parameters never enter the production installer.
#ifndef ProbeOutputDirectory
  #error Compile using scripts/Test-InstallerRuntime.ps1.
#endif
#ifndef ProbeName
  #define ProbeName "RuntimeProbe"
#endif

[Setup]
AppId=FloatCanvas.RuntimeProbe
AppName=FloatCanvas runtime verification
AppVersion=1.0
DefaultDirName={tmp}\FloatCanvas.RuntimeProbe
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
DisableStartupPrompt=yes
OutputDir={#ProbeOutputDirectory}
OutputBaseFilename={#ProbeName}

[Code]
#include "..\Runtime.iss"

var
  Checks: Integer;

procedure Check(Name: String; Condition: Boolean);
begin
  Checks := Checks + 1;
  if not Condition then RaiseException(Name);
  Log('PASS: ' + Name);
end;

function RuntimeLine(Framework, Version, Root: String): String;
begin
  Result := Framework + ' ' + Version + ' [' + AddBackslash(Root) + 'shared\' + Framework + ']';
end;

procedure CheckPair(Name, Desktop, Core: String; Expected: Boolean);
var Lines: TArrayOfString; DesktopVersion, CoreVersion: String;
begin
  SetArrayLength(Lines, 2);
  Lines[0] := RuntimeLine('Microsoft.WindowsDesktop.App', Desktop, 'C:\runtime fixture');
  Lines[1] := RuntimeLine('Microsoft.NETCore.App', Core, 'C:\runtime fixture');
  Check(Name, RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion) = Expected);
end;

procedure ParserChecks;
var Patch, I: Integer; Versions, Lines: TArrayOfString; DesktopVersion, CoreVersion: String;
begin
  Check('baseline stable 10.0.0', RuntimeStablePatch('10.0.0', Patch) and (Patch = 0));
  Check('stable patch 10.0.14', RuntimeStablePatch('10.0.14', Patch) and (Patch = 14));
  SetArrayLength(Versions, 11);
  Versions[0] := '9.0.99'; Versions[1] := '11.0.0'; Versions[2] := '10.1.0';
  Versions[3] := '10.0.0-preview.1'; Versions[4] := '10.0.14+local';
  Versions[5] := '10.0.01'; Versions[6] := '10.0.-1'; Versions[7] := '10.0.';
  Versions[8] := '10.0.2147483648'; Versions[9] := ' 10.0.14'; Versions[10] := '10.0.14 ';
  for I := 0 to GetArrayLength(Versions) - 1 do
    Check('reject unsupported version ' + Versions[I], not RuntimeStablePatch(Versions[I], Patch));
  CheckPair('matching stable Desktop and Core', '10.0.14', '10.0.14', True);
  CheckPair('only .NET 9 is unsupported', '9.0.99', '9.0.99', False);
  CheckPair('only .NET 11 is unsupported', '11.0.0', '11.0.0', False);
  CheckPair('preview Desktop is unsupported', '10.0.15-preview.1', '10.0.14', False);
  CheckPair('preview Core is unsupported', '10.0.14', '10.0.15-preview.1', False);
  SetArrayLength(Lines, 1);
  Lines[0] := RuntimeLine('Microsoft.NETCore.App', '10.0.14', 'C:\runtime fixture');
  Check('Core runtime alone does not provide WPF', not RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion));
  Lines[0] := RuntimeLine('Microsoft.AspNetCore.App', '10.0.14', 'C:\runtime fixture');
  Check('ASP.NET runtime alone does not provide WPF', not RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion));
  Lines[0] := RuntimeLine('Microsoft.WindowsDesktop.App', '10.0.14', 'C:\runtime fixture');
  Check('Desktop without Core is incomplete', not RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion));
  SetArrayLength(Lines, 2);
  Lines[0] := RuntimeLine('Microsoft.NETCore.App', '10.0.14', 'C:\runtime fixture');
  Lines[1] := RuntimeLine('Microsoft.WindowsDesktop.App', '10.0.14', 'C:\different-root');
  Check('frameworks from different roots cannot be combined', not RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion));
  Lines[1] := 'Microsoft.WindowsDesktop.App 10.0.14 C:\runtime fixture\shared\Microsoft.WindowsDesktop.App';
  Check('malformed runtime location is rejected', not RuntimeListVersions(Lines, 'C:\runtime fixture', DesktopVersion, CoreVersion));
  SetArrayLength(Lines, 6);
  Lines[0] := RuntimeLine('Microsoft.NETCore.App', '10.0.9', 'C:\runtime fixture');
  Lines[1] := RuntimeLine('Microsoft.WindowsDesktop.App', '10.0.9', 'C:\runtime fixture');
  Lines[2] := RuntimeLine('Microsoft.NETCore.App', '10.0.14', 'C:\runtime fixture');
  Lines[3] := RuntimeLine('Microsoft.WindowsDesktop.App', '10.0.14', 'C:\runtime fixture');
  Lines[4] := RuntimeLine('Microsoft.WindowsDesktop.App', '10.0.99-preview.1', 'C:\runtime fixture');
  Lines[5] := 'unrelated output';
  Check('highest supported stable patch wins numerically', RuntimeListVersions(Lines, 'c:\RUNTIME FIXTURE\', DesktopVersion, CoreVersion) and
    (DesktopVersion = '10.0.14') and (CoreVersion = '10.0.14'));
end;

function InitializeSetup: Boolean;
var Report, ExpectedRoot, ActualRoot, Details: String; Expected, Actual: Boolean;
begin
  Result := False;
  Report := ExpandConstant('{param:Report|}');
  if Report = '' then exit;
  try
    if ExpandConstant('{param:Parser|0}') = '1' then ParserChecks;
    ExpectedRoot := ExpandConstant('{param:ExpectedRoot|}');
    ActualRoot := RuntimeRootPath;
    if ExpectedRoot <> '' then
      Check('selected apphost runtime root', CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ActualRoot)),
        RemoveBackslashUnlessRoot(ExpandFileName(ExpectedRoot))) = 0);
    Expected := ExpandConstant('{param:Expected|1}') = '1';
    Actual := HasDesktopRuntime;
    Check('runtime availability matches the fixture', Actual = Expected);
    if Actual then
      Check('successful detection identifies the runtime', (DetectedDesktopRuntimeRoot <> '') and (DetectedDesktopRuntimeVersion <> ''))
    else
      Check('failed detection clears runtime identity', (DetectedDesktopRuntimeRoot = '') and (DetectedDesktopRuntimeVersion = ''));
    Details := 'Passed=True' + #13#10 + 'Checks=' + IntToStr(Checks) + #13#10 + 'Root=' + ActualRoot + #13#10 +
      'Version=' + DetectedDesktopRuntimeVersion + #13#10;
  except
    Details := 'Passed=False' + #13#10 + 'Checks=' + IntToStr(Checks) + #13#10 + 'Error=' + GetExceptionMessage + #13#10;
  end;
  SaveStringToFile(Report, Details, False);
end;
