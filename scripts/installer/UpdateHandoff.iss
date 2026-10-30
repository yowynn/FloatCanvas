function OpenEventW(Access: LongWord; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';

function UpdateEventName: String;
var I: Integer; Suffix: String;
begin
  Result := ExpandConstant('{param:FLOATCANVASUPDATEEVENT|}');
  if Result = '' then exit;
  if Copy(Result, 1, 25) <> 'Local\FloatCanvas.Update.' then
    RaiseException(CustomMessage('InvalidUpdateHandoff'));
  Suffix := Copy(Result, 26, MaxInt);
  if Length(Suffix) <> 32 then RaiseException(CustomMessage('InvalidUpdateHandoff'));
  for I := 1 to Length(Suffix) do
    if Pos(Lowercase(Suffix[I]), '0123456789abcdef') = 0 then
      RaiseException(CustomMessage('InvalidUpdateHandoff'));
end;

function LaunchArguments(Param: String): String;
var Profile, QuotedProfile, Monitor: String;
begin
  Result := '';
  if UpdateEventName = '' then exit;
  Profile := ExpandConstant('{param:FLOATCANVASUPDATEPROFILE|}');
  Monitor := ExpandConstant('{param:FLOATCANVASUPDATEMONITOR|auto}');
  if (Length(Profile) < 3) or (Copy(Profile, 2, 2) <> ':\') or
    (Pos('"', Profile) <> 0) or (Pos(#13, Profile) <> 0) or (Pos(#10, Profile) <> 0) or
    not DirExists(Profile) or not NoLinks(Profile) then
    RaiseException(CustomMessage('InvalidUpdateHandoff'));
  Profile := NormalPath(Profile);
  QuotedProfile := Profile;
  { Windows argument parsing treats a backslash before the closing quote as an
    escape. Normal directories are trimmed; drive roots retain a separator. }
  if QuotedProfile[Length(QuotedProfile)] = '\' then QuotedProfile := QuotedProfile + '\';
  Result := '--profile "' + QuotedProfile + '" --recover';
  if Monitor = 'primary' then Result := Result + ' --primary-screen'
  else if Monitor = 'all' then Result := Result + ' --all-screens'
  else if Monitor <> 'auto' then RaiseException(CustomMessage('InvalidUpdateHandoff'));
end;

procedure NotifyUpdateLauncher;
var Name, Arguments: String; Handle: THandle;
begin
  Name := UpdateEventName;
  if Name = '' then exit;
  Arguments := LaunchArguments('');
  Handle := OpenEventW($0002, False, Name);
  if Handle = 0 then RaiseException(CustomMessage('InvalidUpdateHandoff'));
  try
    if not SetEvent(Handle) then RaiseException(CustomMessage('InvalidUpdateHandoff'));
  finally
    CloseHandle(Handle);
  end;
end;
