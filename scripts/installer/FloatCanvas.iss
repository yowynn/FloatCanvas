; Built only from a ZIP that passed Smoke-Package.ps1 -RequireRelease.
#ifndef AppVersion
  #error Use scripts/Publish-Installer.ps1 to compile this installer.
#endif

[Setup]
AppId=FloatCanvas
AppName=浮绘 FloatCanvas
AppVersion={#AppVersion}
AppPublisher=FloatCanvas contributors
AppPublisherURL=https://github.com/yowynn/FloatCanvas
AppSupportURL=https://github.com/yowynn/FloatCanvas/issues
AppUpdatesURL=https://github.com/yowynn/FloatCanvas/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\FloatCanvas
DefaultGroupName=FloatCanvas
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
LicenseFile={#PayloadDirectory}\LICENSE
SetupIconFile=..\..\src\FloatCanvas.App\Assets\FloatCanvas.ico
UninstallDisplayIcon={app}\FloatCanvas.exe
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
SetupMutex=Local\FloatCanvas.Setup
; Both Setup and the uninstaller are deliberately unsigned.
SignedUninstaller=no

[Languages]
Name: en; MessagesFile: "compiler:Default.isl"
Name: zhcn; MessagesFile: "Languages\ChineseSimplified.isl"

#include "Messages.iss"
#include "RuntimeInstallMessages.iss"
#include "UninstallMessages.iss"

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; Payload ownership follows the current sidecar after ZIP updates. Do not retain
; stale file ownership in Inno's cumulative uninstall log.
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall
Source: "{#OwnedList}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\FloatCanvas"; Filename: "{app}\FloatCanvas.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FloatCanvas"; Filename: "{app}\FloatCanvas.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FloatCanvas.exe"; Parameters: "{code:LaunchArguments}"; Description: "{cm:LaunchFloatCanvas}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent; Check: HasDesktopRuntime

[Registry]
; Machine installations register a shared file type. Each user's default choice
; and optional login-start preference remain under that user's control.
Root: HKLM; Subkey: "Software\Classes\FloatCanvas.Project"; ValueType: string; ValueData: "FloatCanvas project"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\FloatCanvas.Project\DefaultIcon"; ValueType: string; ValueData: """{app}\FloatCanvas.exe"",0"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\FloatCanvas.Project\shell\open\command"; ValueType: string; ValueData: """{app}\FloatCanvas.exe"" --open ""%1"""; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\.floatcanvas"; ValueType: string; ValueData: "FloatCanvas.Project"; Flags: createvalueifdoesntexist; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\.floatcanvas\OpenWithProgids"; ValueType: string; ValueName: "FloatCanvas.Project"; ValueData: ""; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\Applications\FloatCanvas.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "FloatCanvas"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\Applications\FloatCanvas.exe\shell\open\command"; ValueType: string; ValueData: """{app}\FloatCanvas.exe"" --open ""%1"""; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\Classes\Applications\FloatCanvas.exe\SupportedTypes"; ValueType: string; ValueName: ".floatcanvas"; ValueData: ""; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\FloatCanvas\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "FloatCanvas"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\FloatCanvas\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "FloatCanvas project"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\FloatCanvas\Capabilities"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}\FloatCanvas.exe"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\FloatCanvas\Capabilities\FileAssociations"; ValueType: string; ValueName: ".floatcanvas"; ValueData: "FloatCanvas.Project"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "FloatCanvas"; ValueData: "Software\FloatCanvas\Capabilities"; Check: IsAdminInstallMode

[Code]
const
  OwnedListName = 'FloatCanvas.install-files.txt';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatCanvas_is1';
  InvalidAttributes = $FFFFFFFF;
  ReparseAttribute = $400;

var
  UninstallFiles: TArrayOfString;
  NewFiles: TArrayOfString;
  PreviousFiles: TArrayOfString;

function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFileW(Name: String; Access, Share: LongWord; Security: LongWord;
  Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

#include "Runtime.iss"
#include "RuntimeInstall.iss"

#include "Paths.iss"

#include "UninstallLifecycle.iss"
#include "UpdateHandoff.iss"

function CheckDirectory(Root: String): String;
var Profile, Registered, Child, Version: String; Files: TArrayOfString; I: Integer;
  Existing: Boolean; Found: TFindRec;
begin
  Result := '';
  SetArrayLength(PreviousFiles, 0);
  Profile := NormalPath(ExpandConstant('{localappdata}\FloatCanvas'));
  if (Length(Root) < 4) or (Copy(Root, 2, 2) <> ':\') or not NoLinks(Root) or
    IsWithin(Root, Profile) or IsWithin(Profile, Root) then begin
    Result := CustomMessage('InvalidDirectory'); exit;
  end;
  if RegQueryStringValue(HKA, UninstallKey, 'InstallLocation', Registered) and
    (CompareText(NormalPath(Registered), Root) <> 0) then begin
    Result := CustomMessage('DifferentInstallation'); exit;
  end;
  if FileExists(AddBackslash(Root) + '.floatcanvas-update') or DirExists(AddBackslash(Root) + '.floatcanvas-update') then begin
    Result := CustomMessage('UnfinishedUpdate'); exit;
  end;
  Existing := FileExists(AddBackslash(Root) + 'PACKAGE-INFO.json');
  if Existing then begin
    if not RegQueryStringValue(HKA, UninstallKey, 'InstallLocation', Registered) or
      (CompareText(NormalPath(Registered), Root) <> 0) then begin
      Result := CustomMessage('PortableDirectory'); exit;
    end;
    if not ReadOwnedFiles(Root, Files) or not ReadPackageVersion(Root, Version) then begin
      Result := CustomMessage('InvalidMetadata'); exit;
    end;
    if CompareVersions(Version, '{#AppVersion}') > 0 then begin
      Result := CustomMessage('NewerVersion'); exit;
    end;
    for I := 0 to GetArrayLength(Files) - 1 do begin
      if not SafeChild(Root, Files[I], Child) or FileBusy(Child) then begin
        Result := CustomMessage('FilesBusy'); exit;
      end;
    end;
    PreviousFiles := Files;
  end else begin
    if FileExists(AddBackslash(Root) + OwnedListName) then begin
      Result := CustomMessage('ExistingInstallationFiles'); exit;
    end;
    if FindFirst(AddBackslash(Root) + 'unins*', Found) then begin
      FindClose(Found);
      Result := CustomMessage('ExistingUninstallerFiles'); exit;
    end;
  end;
  for I := 0 to GetArrayLength(NewFiles) - 1 do begin
    if not SafeChild(Root, NewFiles[I], Child) then begin
      Result := CustomMessage('InvalidDestination'); exit;
    end;
    if DirExists(Child) or (FileExists(Child) and (not Existing or not Listed(Files, NewFiles[I]))) then begin
      Result := FmtMessage(CustomMessage('FileConflict'), [NewFiles[I]]); exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var I: Integer; Root, Child: String;
begin
  if CurStep <> ssPostInstall then exit;
  Root := NormalPath(ExpandConstant('{app}'));
  { Remove obsolete files only after the replacement package installed. Unknown
    files and user data never participate in this cleanup. }
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do
    if not Listed(NewFiles, PreviousFiles[I]) and SafeChild(Root, PreviousFiles[I], Child) then
      if not DeleteFile(Child) and FileExists(Child) then Log('Unable to remove obsolete package file: ' + Child);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsureRuntimeForInstall(NeedsRestart);
  if Result <> '' then exit;
  if GetArrayLength(NewFiles) = 0 then begin
    ExtractTemporaryFile(OwnedListName);
    if not ReadOwnedFiles(ExpandConstant('{tmp}'), NewFiles) then begin
      Result := CustomMessage('InvalidFileList'); exit;
    end;
  end;
  Result := CheckDirectory(NormalPath(ExpandConstant('{app}')));
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption := CustomMessage('WelcomeDescription');
  InitializeRuntimeInstallPage;
  NotifyUpdateLauncher;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := RuntimeInstallShouldSkipPage(PageID);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  RuntimeInstallCurPageChanged(CurPageID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := RuntimeInstallNextButtonClick(CurPageID);
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeInstallNeedsRestart;
end;

function InitializeUninstall: Boolean;
begin
  Result := PrepareUninstallLifecycle;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then ExecuteUninstallLifecycle;
end;