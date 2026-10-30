type
  TUserProfile = record
    Sid: String;
    Home: String;
    Data: String;
  end;
  TUserProfiles = array of TUserProfile;

var
  UninstallMachine: Boolean;
  UninstallProfiles: TUserProfiles;
  DeleteDataPaths: TArrayOfString;
  CleanupErrors: String;

procedure SHChangeNotify(EventId, Flags: LongWord; Item1, Item2: LongWord);
  external 'SHChangeNotify@shell32.dll stdcall';
function ExpandEnvironmentStringsW(Source: String; Dest: String; Size: LongWord): LongWord;
  external 'ExpandEnvironmentStringsW@kernel32.dll stdcall';

function ExpandEnvironmentPath(Value: String): String;
var Count: LongWord;
begin
  SetLength(Result, 32768);
  Count := ExpandEnvironmentStringsW(Value, Result, 32768);
  if (Count = 0) or (Count > 32768) then Result := ''
  else SetLength(Result, Count - 1);
end;

procedure CleanupError(Item: String);
begin
  Log('Cleanup failed: ' + Item);
  CleanupErrors := CleanupErrors + Item + #13#10;
end;

function RegistryOwns(Root: Integer; Key, Name, Expected: String): Boolean;
var Actual: String;
begin
  Result := RegQueryStringValue(Root, Key, Name, Actual) and
    (CompareText(Actual, Expected) = 0);
end;

procedure RemoveRegistryValue(Root: Integer; Key, Name: String);
begin
  if RegValueExists(Root, Key, Name) and not RegDeleteValue(Root, Key, Name) then
    CleanupError(Key + ' [' + Name + ']');
  RegDeleteKeyIfEmpty(Root, Key);
end;

function RemoveOwnedClasses(Root: Integer; Classes, Executable: String): Boolean;
var Prog, AppKey, Command: String; ProjectOwned: Boolean;
begin
  Prog := Classes + 'FloatCanvas.Project';
  AppKey := Classes + 'Applications\FloatCanvas.exe';
  Command := '"' + Executable + '" --open "%1"';
  ProjectOwned := RegistryOwns(Root, Prog + '\shell\open\command', '', Command);
  if ProjectOwned then begin
    RemoveRegistryValue(Root, Prog + '\shell\open\command', '');
    RegDeleteKeyIfEmpty(Root, Prog + '\shell\open');
    RegDeleteKeyIfEmpty(Root, Prog + '\shell');
    if RegistryOwns(Root, Prog + '\DefaultIcon', '', '"' + Executable + '",0') then
      RemoveRegistryValue(Root, Prog + '\DefaultIcon', '');
    RemoveRegistryValue(Root, Prog, '');
    RemoveRegistryValue(Root, Classes + '.floatcanvas\OpenWithProgids', 'FloatCanvas.Project');
    if RegistryOwns(Root, Classes + '.floatcanvas', '', 'FloatCanvas.Project') then
      RemoveRegistryValue(Root, Classes + '.floatcanvas', '');
    RegDeleteKeyIfEmpty(Root, Classes + '.floatcanvas');
  end;
  if RegistryOwns(Root, AppKey + '\shell\open\command', '', Command) then begin
    RemoveRegistryValue(Root, AppKey + '\shell\open\command', '');
    RegDeleteKeyIfEmpty(Root, AppKey + '\shell\open');
    RegDeleteKeyIfEmpty(Root, AppKey + '\shell');
    RemoveRegistryValue(Root, AppKey + '\SupportedTypes', '.floatcanvas');
    RemoveRegistryValue(Root, AppKey, 'FriendlyAppName');
  end;
  Result := ProjectOwned;
end;

procedure RemoveOwnedIntegration(Root: Integer; Prefix, Executable: String);
var Capabilities, RunKey: String; ProjectOwned: Boolean;
begin
  ProjectOwned := RemoveOwnedClasses(Root, Prefix + 'Software\Classes\', Executable);
  Capabilities := Prefix + 'Software\FloatCanvas\Capabilities';
  if RegistryOwns(Root, Capabilities, 'InstallPath', Executable) or
    (ProjectOwned and not RegValueExists(Root, Capabilities, 'InstallPath')) then begin
    if RegistryOwns(Root, Prefix + 'Software\RegisteredApplications', 'FloatCanvas', 'Software\FloatCanvas\Capabilities') then
      RemoveRegistryValue(Root, Prefix + 'Software\RegisteredApplications', 'FloatCanvas');
    RemoveRegistryValue(Root, Capabilities + '\FileAssociations', '.floatcanvas');
    RemoveRegistryValue(Root, Capabilities, 'ApplicationName');
    RemoveRegistryValue(Root, Capabilities, 'ApplicationDescription');
    RemoveRegistryValue(Root, Capabilities, 'InstallPath');
    RegDeleteKeyIfEmpty(Root, Prefix + 'Software\FloatCanvas');
  end;
  RunKey := Prefix + 'Software\Microsoft\Windows\CurrentVersion\Run';
  if RegistryOwns(Root, RunKey, 'FloatCanvas', '"' + Executable + '" --startup') then begin
    { StartupApproved belongs to the Run value, so remove it only while that
      value still points to this installation. Preserve another copy's state. }
    RemoveRegistryValue(Root, Prefix + 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'FloatCanvas');
    RemoveRegistryValue(Root, RunKey, 'FloatCanvas');
  end;
end;

function ValidProfileSid(Sid: String): Boolean;
var I: Integer;
begin
  Result := (Copy(Sid, 1, 9) = 'S-1-5-21-') or (Copy(Sid, 1, 9) = 'S-1-12-1-');
  for I := 3 to Length(Sid) do
    if Pos(Sid[I], '0123456789-') = 0 then Result := False;
end;

procedure FindUserProfiles;
var Sids: TArrayOfString; I, N: Integer; Home: String;
begin
  SetArrayLength(UninstallProfiles, 0);
  if not UninstallMachine then begin
    SetArrayLength(UninstallProfiles, 1);
    UninstallProfiles[0].Sid := '';
    UninstallProfiles[0].Home := NormalPath(GetEnv('USERPROFILE'));
    UninstallProfiles[0].Data := NormalPath(ExpandConstant('{localappdata}\FloatCanvas'));
    exit;
  end;
  if not RegGetSubkeyNames(HKLM64, 'Software\Microsoft\Windows NT\CurrentVersion\ProfileList', Sids) then
    RaiseException(CustomMessage('UninstallWrongAccount'));
  for I := 0 to GetArrayLength(Sids) - 1 do
    if ValidProfileSid(Sids[I]) and RegQueryStringValue(HKLM64,
      'Software\Microsoft\Windows NT\CurrentVersion\ProfileList\' + Sids[I], 'ProfileImagePath', Home) then begin
      Home := ExpandEnvironmentPath(Home);
      if (Length(Home) > 3) and (Copy(Home, 2, 2) = ':\') and
        (Pos('"', Home) = 0) and DirExists(Home) then begin
        N := GetArrayLength(UninstallProfiles); SetArrayLength(UninstallProfiles, N + 1);
        UninstallProfiles[N].Sid := Sids[I];
        UninstallProfiles[N].Home := NormalPath(Home);
        UninstallProfiles[N].Data := NormalPath(AddBackslash(Home) + 'AppData\Local\FloatCanvas');
      end;
    end;
end;

procedure CleanupOfflineHive(Hive, Executable: String; ClassesOnly: Boolean);
var Code: Integer; Mount: String; Loaded, Owned: Boolean;
begin
  if not FileExists(Hive) then exit;
  if not NoLinks(Hive) then begin CleanupError(Hive); exit; end;
  Mount := 'FloatCanvasUninstall-' + IntToStr(Random(2147483647));
  if RegKeyExists(HKU, Mount) then begin CleanupError(Mount); exit; end;
  { Windows reg.exe enables the privileges required to load/unload an offline
    hive. No shell, user executable or .NET is involved. }
  Loaded := Exec(ExpandConstant('{sys}\reg.exe'), 'load "HKU\' + Mount + '" "' + Hive + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Loaded then begin CleanupError(Hive); exit; end;
  try
    if ClassesOnly then Owned := RemoveOwnedClasses(HKU, Mount + '\', Executable)
    else RemoveOwnedIntegration(HKU, Mount + '\', Executable);
  finally
    if not Exec(ExpandConstant('{sys}\reg.exe'), 'unload "HKU\' + Mount + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then CleanupError(Hive);
  end;
end;

procedure CleanupAllIntegration(Executable: String);
var I: Integer; Sid: String; Owned: Boolean;
begin
  if not UninstallMachine then begin
    RemoveOwnedIntegration(HKCU, '', Executable);
    SHChangeNotify($08000000, 0, 0, 0); exit;
  end;
  RemoveOwnedIntegration(HKLM64, '', Executable);
  for I := 0 to GetArrayLength(UninstallProfiles) - 1 do begin
    Sid := UninstallProfiles[I].Sid;
    if RegKeyExists(HKU, Sid) then RemoveOwnedIntegration(HKU, Sid + '\', Executable)
    else CleanupOfflineHive(AddBackslash(UninstallProfiles[I].Home) + 'NTUSER.DAT', Executable, False);
    { Per-user Classes have their own hive. Offline NTUSER loading alone does
      not mount UsrClass.dat, so clean that hive explicitly as well. }
    if RegKeyExists(HKU, Sid + '_Classes') then
      Owned := RemoveOwnedClasses(HKU, Sid + '_Classes\', Executable)
    else CleanupOfflineHive(AddBackslash(UninstallProfiles[I].Home) + 'AppData\Local\Microsoft\Windows\UsrClass.dat', Executable, True);
  end;
  SHChangeNotify($08000000, 0, 0, 0);
end;

function DataTreeSafe(Path: String): Boolean;
var Found: TFindRec; Child: String;
begin
  Result := not TreeHasLinks(Path);
  if not Result then exit;
  if FindFirst(AddBackslash(Path) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          Child := AddBackslash(Path) + Found.Name;
          if (Found.Attributes and $10) <> 0 then Result := DataTreeSafe(Child)
          else Result := not FileBusy(Child);
          if not Result then break;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function DeleteUserDataRequested: Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/DELETEUSERDATA') = 0 then Result := True;
end;

function ChoosePersonalData: Boolean;
var Form: TSetupForm; Description, Warning: TNewStaticText;
  Choices: TNewCheckListBox; ContinueButton, CancelButton: TNewButton;
  Paths: TArrayOfString; I, N: Integer; CurrentData, Selected: String;
begin
  Result := True;
  SetArrayLength(DeleteDataPaths, 0);
  if UninstallSilent then begin
    { Silent uninstall keeps data unless explicitly opted in. Never delete
      other accounts' data based on a command-line switch. }
    if DeleteUserDataRequested then begin
      CurrentData := NormalPath(ExpandConstant('{localappdata}\FloatCanvas'));
      for I := 0 to GetArrayLength(UninstallProfiles) - 1 do
        if CompareText(CurrentData, UninstallProfiles[I].Data) = 0 then begin
          SetArrayLength(DeleteDataPaths, 1); DeleteDataPaths[0] := CurrentData;
        end;
    end;
    exit;
  end;
  Form := CreateCustomForm(ScaleX(600), ScaleY(340), False, True);
  try
    Form.Caption := CustomMessage('UninstallDataTitle');
    Form.ClientWidth := ScaleX(600); Form.ClientHeight := ScaleY(340);
    Form.Position := poScreenCenter;
    Description := TNewStaticText.Create(Form); Description.Parent := Form;
    Description.SetBounds(ScaleX(16), ScaleY(16), ScaleX(568), ScaleY(62));
    Description.AutoSize := False; Description.WordWrap := True;
    Description.Caption := CustomMessage('UninstallDataDescription');
    Choices := TNewCheckListBox.Create(Form); Choices.Parent := Form;
    Choices.SetBounds(ScaleX(16), ScaleY(84), ScaleX(568), ScaleY(116));
    for I := 0 to GetArrayLength(UninstallProfiles) - 1 do
      if DirExists(UninstallProfiles[I].Data) then begin
        N := GetArrayLength(Paths); SetArrayLength(Paths, N + 1); Paths[N] := UninstallProfiles[I].Data;
        Choices.AddCheckBox(ExtractFileName(UninstallProfiles[I].Home) + ': ' + Paths[N], '', 0, False, True, False, False, nil);
      end;
    Warning := TNewStaticText.Create(Form); Warning.Parent := Form;
    Warning.SetBounds(ScaleX(16), ScaleY(212), ScaleX(568), ScaleY(76));
    Warning.AutoSize := False; Warning.WordWrap := True;
    if GetArrayLength(Paths) = 0 then Warning.Caption := CustomMessage('UninstallDataEmpty')
    else Warning.Caption := CustomMessage('UninstallDataWarning');
    ContinueButton := TNewButton.Create(Form); ContinueButton.Parent := Form;
    ContinueButton.SetBounds(ScaleX(380), ScaleY(300), ScaleX(98), ScaleY(26));
    ContinueButton.Caption := CustomMessage('UninstallContinue'); ContinueButton.ModalResult := mrOk;
    ContinueButton.Default := True;
    CancelButton := TNewButton.Create(Form); CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(486), ScaleY(300), ScaleX(98), ScaleY(26));
    CancelButton.Caption := CustomMessage('UninstallCancel'); CancelButton.ModalResult := mrCancel; CancelButton.Cancel := True;
    Result := Form.ShowModal = mrOk;
    if not Result then exit;
    Selected := '';
    for I := 0 to GetArrayLength(Paths) - 1 do
      if Choices.Checked[I] then begin
        N := GetArrayLength(DeleteDataPaths); SetArrayLength(DeleteDataPaths, N + 1);
        DeleteDataPaths[N] := Paths[I]; Selected := Selected + Paths[I] + #13#10;
      end;
    if Selected <> '' then Result := MsgBox(FmtMessage(CustomMessage('UninstallDataConfirm'), [Selected]),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  finally
    Form.Free;
  end;
end;

function PrepareUninstallLifecycle: Boolean;
var Root, Child, Installed: String; I: Integer; UserOwned, MachineOwned: Boolean;
begin
  CleanupErrors := '';
  Root := NormalPath(ExpandConstant('{app}'));
  Log('Uninstall preflight root: ' + Root);
  UserOwned := RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Installed) and
    (CompareText(NormalPath(Installed), Root) = 0);
  MachineOwned := RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Installed) and
    (CompareText(NormalPath(Installed), Root) = 0);
  Result := UserOwned or MachineOwned;
  if not Result then begin SuppressibleMsgBox(CustomMessage('UninstallWrongAccount'), mbError, MB_OK, IDOK); exit; end;
  UninstallMachine := MachineOwned;
  Result := not TreeHasLinks(Root) and not FileExists(AddBackslash(Root) + '.floatcanvas-update') and
    not DirExists(AddBackslash(Root) + '.floatcanvas-update') and ReadOwnedFiles(Root, UninstallFiles);
  if Result then
    for I := 0 to GetArrayLength(UninstallFiles) - 1 do
      if not SafeChild(Root, UninstallFiles[I], Child) or FileBusy(Child) then begin
        Log('Uninstall preflight rejected entry [' + IntToStr(I) + ']: ' + UninstallFiles[I]); Result := False; break;
      end;
  if not Result then begin
    if FileExists(AddBackslash(Root) + '.floatcanvas-update') or DirExists(AddBackslash(Root) + '.floatcanvas-update') then
      Log('Uninstall preflight found an unfinished update marker.');
    SuppressibleMsgBox(CustomMessage('UninstallBlocked'), mbError, MB_OK, IDOK); exit;
  end;
  FindUserProfiles;
  Result := ChoosePersonalData;
  if not Result then exit;
  for I := 0 to GetArrayLength(DeleteDataPaths) - 1 do
    if not DataTreeSafe(DeleteDataPaths[I]) then begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('UninstallUnsafeData'), [DeleteDataPaths[I]]), mbError, MB_OK, IDOK);
      Result := False; exit;
    end;
end;

procedure ExecuteUninstallLifecycle;
var Root, Child, Parent: String; I: Integer;
begin
  Root := NormalPath(ExpandConstant('{app}'));
  CleanupAllIntegration(AddBackslash(Root) + 'FloatCanvas.exe');
  if CleanupErrors <> '' then RaiseException(FmtMessage(CustomMessage('UninstallCleanupFailed'), [CleanupErrors]));
  for I := 0 to GetArrayLength(DeleteDataPaths) - 1 do begin
    { Revalidate the exact directory selected from known Windows profiles,
      immediately before deletion. No custom profile paths are accepted. }
    if not DataTreeSafe(DeleteDataPaths[I]) then
      RaiseException(FmtMessage(CustomMessage('UninstallUnsafeData'), [DeleteDataPaths[I]]));
    if DirExists(DeleteDataPaths[I]) and not DelTree(DeleteDataPaths[I], True, True, True) then
      CleanupError(DeleteDataPaths[I]);
  end;
  if CleanupErrors <> '' then RaiseException(FmtMessage(CustomMessage('UninstallCleanupFailed'), [CleanupErrors]));
  for I := 0 to GetArrayLength(UninstallFiles) - 1 do
    if SafeChild(Root, UninstallFiles[I], Child) then begin
      if not DeleteFile(Child) and FileExists(Child) then CleanupError(Child);
      Parent := ExtractFileDir(Child);
      while (CompareText(Parent, Root) <> 0) and IsWithin(Parent, Root) and NoLinks(Parent) do begin
        if not RemoveDir(Parent) then break;
        Parent := ExtractFileDir(Parent);
      end;
    end else CleanupError(UninstallFiles[I]);
  if CleanupErrors <> '' then RaiseException(FmtMessage(CustomMessage('UninstallCleanupFailed'), [CleanupErrors]));
end;
