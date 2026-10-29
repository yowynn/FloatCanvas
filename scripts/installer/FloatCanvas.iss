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
DefaultDirName={localappdata}\Programs\FloatCanvas
DefaultGroupName=FloatCanvas
PrivilegesRequired=lowest
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
Filename: "{app}\FloatCanvas.exe"; Description: "{cm:LaunchFloatCanvas}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent; Check: HasDesktopRuntime

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
  RuntimePage: TWizardPage;
  RuntimeStatus: TNewStaticText;
  RuntimeDownloadButton, RuntimeRetryButton: TNewButton;

function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFileW(Name: String; Access, Share: LongWord; Security: LongWord;
  Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

#include "Runtime.iss"

function NormalPath(Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

function IsWithin(Path, Root: String): Boolean;
begin
  Result := (CompareText(Path, Root) = 0) or
    (CompareText(Copy(Path, 1, Length(AddBackslash(Root))), AddBackslash(Root)) = 0);
end;

function NoLinks(Path: String): Boolean;
var Attributes: LongWord; Parent: String;
begin
  Result := False;
  while Path <> '' do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> InvalidAttributes) and ((Attributes and ReparseAttribute) <> 0) then exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
  Result := True;
end;

function SafeChild(Root, Relative: String; var Path: String): Boolean;
var Remaining, Part, Stem: String; Separator, I: Integer;
begin
  Result := False;
  if (Relative = '') or (Length(Relative) > 240) or (Pos('\', Relative) <> 0) then exit;
  Remaining := Relative;
  while Remaining <> '' do begin
    Separator := Pos('/', Remaining);
    if Separator = 0 then begin Part := Remaining; Remaining := ''; end
    else begin
      Part := Copy(Remaining, 1, Separator - 1);
      Delete(Remaining, 1, Separator);
      if Remaining = '' then exit;
    end;
    if (Part = '') or (Part = '.') or (Part = '..') then exit;
    if (Part[Length(Part)] = '.') or (Part[Length(Part)] = ' ') then exit;
    for I := 1 to Length(Part) do
      if (Ord(Part[I]) < 32) or (Pos(Part[I], ':<>"|?*') <> 0) then exit;
    Stem := Uppercase(Part); Separator := Pos('.', Stem);
    if Separator > 0 then Stem := Copy(Stem, 1, Separator - 1);
    if (Stem = 'CON') or (Stem = 'PRN') or (Stem = 'AUX') or (Stem = 'NUL') or
      ((Length(Stem) = 4) and ((Copy(Stem, 1, 3) = 'COM') or (Copy(Stem, 1, 3) = 'LPT')) and
       (Pos(Stem[4], '123456789') > 0)) then exit;
  end;
  StringChangeEx(Relative, '/', '\', True);
  Path := NormalPath(AddBackslash(Root) + Relative);
  Result := IsWithin(Path, Root) and (CompareText(Path, Root) <> 0) and NoLinks(Path);
end;

function ReadOwnedFiles(Root: String; var Files: TArrayOfString): Boolean;
var I: Integer; Child: String;
begin
  Result := False;
  if not NoLinks(AddBackslash(Root) + OwnedListName) then exit;
  if not LoadStringsFromFile(AddBackslash(Root) + OwnedListName, Files) then exit;
  if (GetArrayLength(Files) < 1) or (GetArrayLength(Files) > 4096) then exit;
  for I := 0 to GetArrayLength(Files) - 1 do
    if not SafeChild(Root, Files[I], Child) then exit;
  Result := True;
end;

function TreeHasLinks(Root: String): Boolean;
var Found: TFindRec; Child: String;
begin
  Result := not NoLinks(Root);
  if Result then exit;
  if FindFirst(AddBackslash(Root) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          if (Found.Attributes and ReparseAttribute) <> 0 then begin Result := True; break; end;
          if (Found.Attributes and $10) <> 0 then begin
            Child := AddBackslash(Root) + Found.Name;
            if TreeHasLinks(Child) then begin Result := True; break; end;
          end;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function Listed(Files: TArrayOfString; Relative: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Files) - 1 do
    if CompareText(Files[I], Relative) = 0 then begin Result := True; exit; end;
end;

function FileBusy(Path: String): Boolean;
var Handle: THandle;
begin
  Result := False;
  if not FileExists(Path) then exit;
  { Open for write without writing: mapped/running binaries and locked files must
    block installation/uninstallation instead of being scheduled for deletion. }
  Handle := CreateFileW(Path, $C0000000, 0, 0, 3, 0, 0);
  Result := Handle = THandle(-1);
  if not Result then CloseHandle(Handle);
end;

function ReadPackageVersion(Root: String; var Version: String): Boolean;
var Text: AnsiString; Value: String; P: Integer;
begin
  Result := False;
  if not LoadStringFromFile(AddBackslash(Root) + 'PACKAGE-INFO.json', Text) then exit;
  { The application generates this manifest with a single top-level Version.
    Reject unreadable metadata rather than trusting the uninstall display text. }
  Value := String(Text); P := Pos('"Version"', Value);
  if P = 0 then exit;
  Delete(Value, 1, P + Length('"Version"') - 1); Value := Trim(Value);
  if (Value = '') or (Value[1] <> ':') then exit;
  Delete(Value, 1, 1); Value := Trim(Value);
  if (Value = '') or (Value[1] <> '"') then exit;
  Delete(Value, 1, 1); P := Pos('"', Value);
  if P = 0 then exit;
  Version := Copy(Value, 1, P - 1);
  Result := (Version <> '') and (Length(Version) < 128);
end;

function NextPart(var Value: String; Delimiter: String): String;
var P: Integer;
begin
  P := Pos(Delimiter, Value);
  if P = 0 then begin Result := Value; Value := ''; end
  else begin Result := Copy(Value, 1, P - 1); Delete(Value, 1, P); end;
end;

function Digits(Value: String): Boolean;
var I: Integer;
begin
  Result := Value <> '';
  for I := 1 to Length(Value) do
    if Pos(Value[I], '0123456789') = 0 then Result := False;
end;

function CompareNumber(A, B: String): Integer;
begin
  Result := Length(A) - Length(B);
  if Result = 0 then Result := CompareStr(A, B);
end;

function CompareVersions(A, B: String): Integer;
var CoreA, CoreB, PreA, PreB, PartA, PartB: String; I: Integer;
begin
  CoreA := NextPart(A, '-'); PreA := A;
  CoreB := NextPart(B, '-'); PreB := B;
  for I := 1 to 3 do begin
    PartA := NextPart(CoreA, '.'); PartB := NextPart(CoreB, '.');
    if not Digits(PartA) or not Digits(PartB) then RaiseException(CustomMessage('InvalidVersion'));
    Result := CompareNumber(PartA, PartB);
    if Result <> 0 then exit;
  end;
  if (CoreA <> '') or (CoreB <> '') then RaiseException(CustomMessage('InvalidVersion'));
  Result := 0;
  if (PreA = '') and (PreB = '') then exit;
  if PreA = '' then begin Result := 1; exit; end;
  if PreB = '' then begin Result := -1; exit; end;
  while (PreA <> '') and (PreB <> '') do begin
    PartA := NextPart(PreA, '.'); PartB := NextPart(PreB, '.');
    if Digits(PartA) and Digits(PartB) then Result := CompareNumber(PartA, PartB)
    else if Digits(PartA) then Result := -1
    else if Digits(PartB) then Result := 1
    else Result := CompareStr(PartA, PartB);
    if Result <> 0 then exit;
  end;
  if PreA <> '' then Result := 1 else if PreB <> '' then Result := -1;
end;

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
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Registered) and
    (CompareText(NormalPath(Registered), Root) <> 0) then begin
    Result := CustomMessage('DifferentInstallation'); exit;
  end;
  if FileExists(AddBackslash(Root) + '.floatcanvas-update') or DirExists(AddBackslash(Root) + '.floatcanvas-update') then begin
    Result := CustomMessage('UnfinishedUpdate'); exit;
  end;
  Existing := FileExists(AddBackslash(Root) + 'PACKAGE-INFO.json');
  if Existing then begin
    if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Registered) or
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
  if not HasDesktopRuntime then begin
    Result := CustomMessage('RuntimeMissing'); exit;
  end;
  if GetArrayLength(NewFiles) = 0 then begin
    ExtractTemporaryFile(OwnedListName);
    if not ReadOwnedFiles(ExpandConstant('{tmp}'), NewFiles) then begin
      Result := CustomMessage('InvalidFileList'); exit;
    end;
  end;
  Result := CheckDirectory(NormalPath(ExpandConstant('{app}')));
end;

procedure UpdateRuntimeStatus;
var Available: Boolean;
begin
  Available := HasDesktopRuntime;
  if Available then
    RuntimeStatus.Caption := FmtMessage(CustomMessage('RuntimeAvailable'), [DetectedDesktopRuntimeVersion])
  else
    RuntimeStatus.Caption := CustomMessage('RuntimeMissing');
  RuntimeDownloadButton.Enabled := not Available;
  if WizardForm.CurPageID = RuntimePage.ID then WizardForm.NextButton.Enabled := Available;
end;

procedure RetryRuntime(Sender: TObject);
begin
  UpdateRuntimeStatus;
end;

procedure DownloadRuntime(Sender: TObject);
var ErrorCode: Integer;
begin
  if not ShellExecAsOriginalUser('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    SuppressibleMsgBox(CustomMessage('RuntimeDownloadFailed'), mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel2.Caption := CustomMessage('WelcomeDescription');
  RuntimePage := CreateCustomPage(wpWelcome, CustomMessage('RuntimeTitle'), CustomMessage('RuntimeSubtitle'));
  RuntimeStatus := TNewStaticText.Create(RuntimePage);
  RuntimeStatus.Parent := RuntimePage.Surface;
  RuntimeStatus.SetBounds(0, ScaleY(8), RuntimePage.SurfaceWidth, ScaleY(148));
  RuntimeStatus.AutoSize := False;
  RuntimeStatus.WordWrap := True;
  RuntimeDownloadButton := TNewButton.Create(RuntimePage);
  RuntimeDownloadButton.Parent := RuntimePage.Surface;
  RuntimeDownloadButton.SetBounds(0, ScaleY(168), ScaleX(215), ScaleY(28));
  RuntimeDownloadButton.Caption := CustomMessage('RuntimeDownload');
  RuntimeDownloadButton.OnClick := @DownloadRuntime;
  RuntimeRetryButton := TNewButton.Create(RuntimePage);
  RuntimeRetryButton.Parent := RuntimePage.Surface;
  RuntimeRetryButton.SetBounds(ScaleX(230), ScaleY(168), ScaleX(150), ScaleY(28));
  RuntimeRetryButton.Caption := CustomMessage('RuntimeRetry');
  RuntimeRetryButton.OnClick := @RetryRuntime;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = RuntimePage.ID then UpdateRuntimeStatus;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = RuntimePage.ID then begin
    UpdateRuntimeStatus;
    Result := WizardForm.NextButton.Enabled;
  end;
end;

function InitializeUninstall: Boolean;
var Root, Child: String; I: Integer;
begin
  Root := NormalPath(ExpandConstant('{app}'));
  { Reject redirection before deleting payload files or allowing Inno to remove
    its own metadata and shortcuts. Never follow user-created junctions. }
  Result := not TreeHasLinks(Root) and not FileExists(AddBackslash(Root) + '.floatcanvas-update') and
    not DirExists(AddBackslash(Root) + '.floatcanvas-update') and ReadOwnedFiles(Root, UninstallFiles);
  if Result then
    for I := 0 to GetArrayLength(UninstallFiles) - 1 do begin
      if not SafeChild(Root, UninstallFiles[I], Child) or FileBusy(Child) then begin Result := False; break; end;
    end;
  if not Result then SuppressibleMsgBox(CustomMessage('UninstallBlocked'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Root, Child, Parent: String; I, ExitCode: Integer;
begin
  Root := NormalPath(ExpandConstant('{app}'));
  if CurUninstallStep = usUninstall then begin
    { The host command removes only integration owned by this executable path.
      Missing .NET must not prevent removal of the application itself. }
    if HasDesktopRuntime and FileExists(AddBackslash(Root) + 'FloatCanvas.Host.exe') then
      Exec(AddBackslash(Root) + 'FloatCanvas.Host.exe', '--unregister-integration', Root, SW_HIDE, ewWaitUntilTerminated, ExitCode);
    { In-app ZIP updates can add files absent from Inno's original uninstall log. }
    for I := 0 to GetArrayLength(UninstallFiles) - 1 do
      if SafeChild(Root, UninstallFiles[I], Child) then begin
        if not DeleteFile(Child) and FileExists(Child) then Log('Unable to remove owned file: ' + Child);
        Parent := ExtractFileDir(Child);
        while (CompareText(Parent, Root) <> 0) and IsWithin(Parent, Root) and NoLinks(Parent) do begin
          if not RemoveDir(Parent) then break;
          Parent := ExtractFileDir(Parent);
        end;
      end;
  end;
end;
