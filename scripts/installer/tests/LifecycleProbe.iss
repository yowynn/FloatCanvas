; Native contract tests include production helpers, but never call application
; installation, profile enumeration, hive loading, or uninstall entry points.
#ifndef ProbeOutputDirectory
  #error Compile with Test-InstallerLifecycle.ps1.
#endif

[Setup]
AppId=FloatCanvas.LifecycleProbe
AppName=FloatCanvas installer lifecycle verification
AppVersion=1.0
DefaultDirName={tmp}\FloatCanvas.LifecycleProbe
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableStartupPrompt=yes
OutputDir={#ProbeOutputDirectory}
OutputBaseFilename=LifecycleProbe

[Languages]
Name: en; MessagesFile: "compiler:Default.isl"
Name: zhcn; MessagesFile: "..\Languages\ChineseSimplified.isl"
#include "..\Messages.iss"
#include "..\UninstallMessages.iss"

[Code]
const
  OwnedListName = 'FloatCanvas.install-files.txt';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1';
  InvalidAttributes = $FFFFFFFF;
  ReparseAttribute = $400;
  FixtureRegistry = '{#ProbeRegistryPrefix}';
  FixtureDirectory = '{#ProbeFixtureDirectory}';
var
  UninstallFiles: TArrayOfString;
  Checks: Integer;
  HandoffArguments, HandoffProfile: String;

function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFileW(Name: String; Access, Share: LongWord; Security: LongWord;
  Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetACP: LongWord;
  external 'GetACP@kernel32.dll stdcall';

#include "..\Paths.iss"
#include "..\UninstallLifecycle.iss"
#include "..\UpdateHandoff.iss"

procedure Check(Name: String; Condition: Boolean);
begin
  Checks := Checks + 1;
  if not Condition then RaiseException(Name);
  Log('PASS: ' + Name);
end;

procedure Put(Key, Name, Value: String);
begin
  if Pos(FixtureRegistry, Key) <> 1 then RaiseException('Registry fixture escaped');
  if not RegWriteStringValue(HKCU, Key, Name, Value) then RaiseException('Cannot write registry fixture');
end;

procedure SeedClasses(Classes, Executable: String);
begin
  Put(Classes + 'FloatCanvas.Project\shell\open\command', '', '"' + Executable + '" --open "%1"');
  Put(Classes + 'FloatCanvas.Project\DefaultIcon', '', '"' + Executable + '",0');
  Put(Classes + 'FloatCanvas.Project', '', 'FloatCanvas project');
  Put(Classes + 'FloatCanvas.Project', 'ForeignValue', 'keep');
  Put(Classes + '.floatcanvas', '', 'FloatCanvas.Project');
  Put(Classes + '.floatcanvas\OpenWithProgids', 'FloatCanvas.Project', '');
  Put(Classes + '.floatcanvas\OpenWithProgids', 'Other.Project', 'keep');
  Put(Classes + 'Applications\FloatCanvas.exe\shell\open\command', '', '"' + Executable + '" --open "%1"');
  Put(Classes + 'Applications\FloatCanvas.exe\SupportedTypes', '.floatcanvas', '');
  Put(Classes + 'Applications\FloatCanvas.exe\SupportedTypes', '.other', 'keep');
  Put(Classes + 'Applications\FloatCanvas.exe', 'FriendlyAppName', 'FloatCanvas');
end;

procedure SeedIntegration(Prefix, Executable: String);
begin
  SeedClasses(Prefix + 'Software\Classes\', Executable);
  Put(Prefix + 'Software\FloatCanvas\Capabilities', 'InstallPath', Executable);
  Put(Prefix + 'Software\FloatCanvas\Capabilities', 'ApplicationName', 'FloatCanvas');
  Put(Prefix + 'Software\FloatCanvas\Capabilities\FileAssociations', '.floatcanvas', 'FloatCanvas.Project');
  Put(Prefix + 'Software\RegisteredApplications', 'FloatCanvas', 'Software\FloatCanvas\Capabilities');
  Put(Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'FloatCanvas', '"' + Executable + '" --startup');
  Put(Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'FloatCanvas', 'owned state');
  Put(Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'OtherApp', 'keep');
  Put(Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice', 'ProgId', 'FloatCanvas.Project');
end;

procedure RegistryChecks;
var Prefix, Classes, Executable, Foreign: String; Owned: Boolean;
begin
  Executable := AddBackslash(FixtureDirectory) + 'FloatCanvas.exe';
  Foreign := AddBackslash(FixtureDirectory) + 'other\FloatCanvas.exe';
  Prefix := FixtureRegistry + 'Owned\'; Classes := Prefix + 'Software\Classes\';
  SeedIntegration(Prefix, Executable);
  CleanupErrors := ''; RemoveOwnedIntegration(HKCU, Prefix, Executable);
  Check('owned registry cleanup succeeds', CleanupErrors = '');
  Check('owned project command removed', not RegValueExists(HKCU, Classes + 'FloatCanvas.Project\shell\open\command', ''));
  Check('owned icon removed', not RegValueExists(HKCU, Classes + 'FloatCanvas.Project\DefaultIcon', ''));
  Check('owned extension default removed', not RegValueExists(HKCU, Classes + '.floatcanvas', ''));
  Check('owned OpenWith entry removed', not RegValueExists(HKCU, Classes + '.floatcanvas\OpenWithProgids', 'FloatCanvas.Project'));
  Check('other OpenWith entry preserved', RegistryOwns(HKCU, Classes + '.floatcanvas\OpenWithProgids', 'Other.Project', 'keep'));
  Check('unknown project values preserved', RegistryOwns(HKCU, Classes + 'FloatCanvas.Project', 'ForeignValue', 'keep'));
  Check('owned application command removed', not RegValueExists(HKCU, Classes + 'Applications\FloatCanvas.exe\shell\open\command', ''));
  Check('other supported extensions preserved', RegistryOwns(HKCU, Classes + 'Applications\FloatCanvas.exe\SupportedTypes', '.other', 'keep'));
  Check('owned capabilities removed', not RegValueExists(HKCU, Prefix + 'Software\FloatCanvas\Capabilities', 'InstallPath'));
  Check('registered application removed', not RegValueExists(HKCU, Prefix + 'Software\RegisteredApplications', 'FloatCanvas'));
  Check('owned Run removed', not RegValueExists(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'FloatCanvas'));
  Check('owned StartupApproved removed', not RegValueExists(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'FloatCanvas'));
  Check('other Run values preserved', RegistryOwns(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'OtherApp', 'keep'));
  Check('UserChoice preserved', RegistryOwns(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.floatcanvas\UserChoice', 'ProgId', 'FloatCanvas.Project'));

  Prefix := FixtureRegistry + 'Foreign\'; Classes := Prefix + 'Software\Classes\';
  SeedIntegration(Prefix, Foreign); RemoveOwnedIntegration(HKCU, Prefix, Executable);
  Check('foreign project command preserved', RegistryOwns(HKCU, Classes + 'FloatCanvas.Project\shell\open\command', '', '"' + Foreign + '" --open "%1"'));
  Check('foreign application command preserved', RegistryOwns(HKCU, Classes + 'Applications\FloatCanvas.exe\shell\open\command', '', '"' + Foreign + '" --open "%1"'));
  Check('foreign capabilities preserved', RegistryOwns(HKCU, Prefix + 'Software\FloatCanvas\Capabilities', 'InstallPath', Foreign));
  Check('foreign registration preserved', RegValueExists(HKCU, Prefix + 'Software\RegisteredApplications', 'FloatCanvas'));
  Check('foreign Run preserved', RegistryOwns(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run', 'FloatCanvas', '"' + Foreign + '" --startup'));
  Check('foreign StartupApproved preserved', RegValueExists(HKCU, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'FloatCanvas'));

  Prefix := FixtureRegistry + 'Legacy\'; SeedIntegration(Prefix, Executable);
  RegDeleteValue(HKCU, Prefix + 'Software\FloatCanvas\Capabilities', 'InstallPath');
  RemoveOwnedIntegration(HKCU, Prefix, Executable);
  Check('legacy owned capabilities removed', not RegValueExists(HKCU, Prefix + 'Software\FloatCanvas\Capabilities', 'ApplicationName'));

  Classes := FixtureRegistry + 'SeparateClassesHive\'; SeedClasses(Classes, Executable);
  Owned := RemoveOwnedClasses(HKCU, Classes, Executable);
  Check('separate classes root recognized', Owned);
  Check('separate classes root cleaned', not RegValueExists(HKCU, Classes + 'FloatCanvas.Project\shell\open\command', ''));
  Check('separate classes root preserves unknowns', RegistryOwns(HKCU, Classes + 'FloatCanvas.Project', 'ForeignValue', 'keep'));
end;

procedure CurrentUserPathChecks;
begin
  { Execute the actual per-user resolver: unsupported Inno constants must fail
    here before reaching a real uninstall. This reads paths without opening any
    profile files or changing the actual user's registration or data. }
  UninstallMachine := False;
  FindUserProfiles;
  Check('per-user resolver returns exactly one profile', GetArrayLength(UninstallProfiles) = 1);
  Check('per-user home resolves from the environment', UninstallProfiles[0].Home = NormalPath(GetEnv('USERPROFILE')));
  Check('per-user data follows the actual localappdata location', UninstallProfiles[0].Data = NormalPath(ExpandConstant('{localappdata}\FloatCanvas')));
end;

procedure PathChecks;
var Root, Child, Expected, Forbidden, Character: String; Files: TArrayOfString; I, J: Integer;
begin
  Log('Lifecycle path validation ACP=' + IntToStr(GetACP));
  Root := AddBackslash(FixtureDirectory) + 'safe';
  Check('regular data tree accepted', DataTreeSafe(Root));
  Check('locked data rejected', not DataTreeSafe(AddBackslash(FixtureDirectory) + 'busy'));
  Check('junction data rejected', not DataTreeSafe(AddBackslash(FixtureDirectory) + 'linked'));
  Check('junction path rejected', not NoLinks(AddBackslash(FixtureDirectory) + 'linked\outside\keep.txt'));
  Check('normal payload path accepted', SafeChild(Root, 'sub/payload.dll', Child));
  Check('normal payload remains within root', IsWithin(Child, Root));
  Check('sibling prefix rejected', not IsWithin(Root + '-other\payload.dll', Root));
  Check('parent traversal rejected', not SafeChild(Root, '../outside.txt', Child));
  Check('absolute path rejected', not SafeChild(Root, 'C:/outside.txt', Child));
  Check('backslash path rejected', not SafeChild(Root, 'sub\payload.dll', Child));
  Check('device path rejected', not SafeChild(Root, 'CON.txt', Child));
  Check('alternate stream rejected', not SafeChild(Root, 'payload.dll:stream', Child));
  Check('trailing separator rejected', not SafeChild(Root, 'sub/', Child));
  Check('ASCII trailing dot rejected', not SafeChild(Root, 'bad.', Child));
  Check('ASCII trailing space rejected', not SafeChild(Root, 'bad ', Child));
  Check('valid ownership list accepted', ReadOwnedFiles(Root, Files));
  Check('traversing ownership list rejected', not ReadOwnedFiles(AddBackslash(FixtureDirectory) + 'invalid-list', Files));
  for I := 0 to 31 do
    Check('ASCII control rejected ' + IntToStr(I), not SafeChild(Root, 'bad' + Chr(I) + 'name.txt', Child));
  Forbidden := ':<>"|?*';
  for I := 1 to Length(Forbidden) do
    Check('forbidden filename character rejected ' + IntToStr(I),
      not SafeChild(Root, 'bad' + Copy(Forbidden, I, 1) + 'name.txt', Child));
  Root := AddBackslash(FixtureDirectory) + 'unicode-list';
  Check('Unicode fixture list loads', LoadStringsFromFile(AddBackslash(Root) + OwnedListName, Files));
  Check('Unicode fixture has all path families', GetArrayLength(Files) = 7);
  for I := 0 to GetArrayLength(Files) - 1 do begin
    { Compare the former scalar-to-string conversion with a string slice only
      in this fixture. This logs the active-code-page defect on CI without
      adding diagnostics to the production path validator. }
    for J := 1 to Length(Files[I]) do begin
      Character := Copy(Files[I], J, 1);
      if (Pos(Files[I][J], Forbidden) <> 0) and (Pos(Character, Forbidden) = 0) then
        Log('Unicode scalar conversion changed forbidden-character lookup: entry=' + IntToStr(I) +
          '; unit=' + IntToStr(J) + '; scalar=' + IntToStr(Ord(Files[I][J])));
    end;
    Check('Unicode payload path accepted ' + IntToStr(I), SafeChild(Root, Files[I], Child));
    Check('Unicode payload remains within root ' + IntToStr(I), IsWithin(Child, Root));
    Expected := Files[I]; StringChangeEx(Expected, '/', '\', True);
    Check('Unicode payload path preserved exactly ' + IntToStr(I), Child = AddBackslash(Root) + Expected);
    Check('Unicode payload identity preserved ' + IntToStr(I), FileExists(Child));
  end;
  Check('Unicode ownership list accepted', ReadOwnedFiles(Root, Files));
end;

procedure HandoffChecks;
var Failed: Boolean; Arguments, Expected, Mode: String;
begin
  Failed := False;
  Log('Parsed handoff profile: ' + ExpandConstant('{param:FLOATCANVASUPDATEPROFILE|}'));
  try
    Arguments := LaunchArguments('');
    HandoffArguments := Arguments;
    if ExpandConstant('{param:NOTIFY|0}') = '1' then NotifyUpdateLauncher;
  except
    Failed := True;
  end;
  if ExpandConstant('{param:EXPECTERROR|0}') = '1' then
    Check('invalid handoff rejected', Failed)
  else begin
    Check('valid handoff accepted', not Failed);
    Expected := '';
    if ExpandConstant('{param:FLOATCANVASUPDATEEVENT|}') <> '' then begin
      HandoffProfile := NormalPath(ExpandConstant('{param:FLOATCANVASUPDATEPROFILE|}'));
      Expected := HandoffProfile;
      if Expected[Length(Expected)] = '\' then Expected := Expected + '\';
      Expected := '--profile "' + Expected + '" --recover';
      Mode := ExpandConstant('{param:FLOATCANVASUPDATEMONITOR|auto}');
      if Mode = 'all' then Expected := Expected + ' --all-screens';
      if Mode = 'primary' then Expected := Expected + ' --primary-screen';
    end;
    Check('launch arguments preserve profile and monitor', Arguments = Expected);
  end;
end;

function InitializeSetup: Boolean;
var Error, Report, Mode: String;
begin
  Result := False; Error := ''; Checks := 0;
  if Pos('Software\FloatCanvas.Tests\Lifecycle-', FixtureRegistry) <> 1 then exit;
  Mode := ExpandConstant('{param:CASE|contracts}');
  try
    if Mode = 'contracts' then begin CurrentUserPathChecks; RegistryChecks; PathChecks; end
    else HandoffChecks;
  except
    Error := GetExceptionMessage;
  end;
  if RegKeyExists(HKCU, RemoveBackslash(FixtureRegistry)) and
    not RegDeleteKeyIncludingSubkeys(HKCU, RemoveBackslash(FixtureRegistry)) then
    Error := Error + ' Registry fixture cleanup failed.';
  Report := ExpandConstant('{param:REPORT|}');
  SaveStringToFile(Report, 'Passed=' + IntToStr(Ord(Error = '')) + #13#10 +
    'Checks=' + IntToStr(Checks) + #13#10 + 'Error=' + Error + #13#10 +
    'Arguments=' + HandoffArguments + #13#10 + 'Profile=' + HandoffProfile + #13#10, False);
end;
