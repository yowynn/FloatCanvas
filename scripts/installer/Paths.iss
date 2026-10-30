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
    if (Attributes <> InvalidAttributes) and ((Attributes and ReparseAttribute) <> 0) then begin
      Log('Refusing linked path: ' + Path); exit;
    end;
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
  Result := True;
end;

function SafeChild(Root, Relative: String; var Path: String): Boolean;
var Remaining, Part, Stem, Backslash, Slash, Dot, Character: String; Separator, I: Integer;
begin
  Result := False;
  { Pos with a Char needle narrows to ANSI; keep Unicode path searches and
    their returned indices in String form, independently of the system ACP. }
  Backslash := '\'; Slash := '/'; Dot := '.';
  if (Relative = '') or (Length(Relative) > 240) or (Pos(Backslash, Relative) <> 0) then exit;
  Remaining := Relative;
  while Remaining <> '' do begin
    Separator := Pos(Slash, Remaining);
    if Separator = 0 then begin Part := Remaining; Remaining := ''; end
    else begin
      Part := Copy(Remaining, 1, Separator - 1);
      Delete(Remaining, 1, Separator);
      if Remaining = '' then exit;
    end;
    if (Part = '') or (Part = '.') or (Part = '..') then exit;
    if (Part[Length(Part)] = '.') or (Part[Length(Part)] = ' ') then exit;
    for I := 1 to Length(Part) do begin
      { Nested Copy arguments can use ANSI temporaries; store the Unicode slice. }
      Character := Copy(Part, I, 1);
      if (Ord(Part[I]) < 32) or (Pos(Character, ':<>"|?*') <> 0) then exit;
    end;
    Stem := Uppercase(Part); Separator := Pos(Dot, Stem);
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
  if not LoadStringsFromFile(AddBackslash(Root) + OwnedListName, Files) then begin
    Log('Unable to read ownership list: ' + AddBackslash(Root) + OwnedListName); exit;
  end;
  if (GetArrayLength(Files) < 1) or (GetArrayLength(Files) > 4096) then begin
    Log('Invalid ownership list count: ' + IntToStr(GetArrayLength(Files))); exit;
  end;
  Log('Ownership list count=' + IntToStr(GetArrayLength(Files)) + '; last=' + Files[GetArrayLength(Files) - 1]);
  for I := 0 to GetArrayLength(Files) - 1 do
    if not SafeChild(Root, Files[I], Child) then begin
      Log('Unsafe ownership list entry [' + IntToStr(I) + ']: ' + Files[I]); exit;
    end;
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
          if (Found.Attributes and ReparseAttribute) <> 0 then begin
            Log('Directory contains a link: ' + AddBackslash(Root) + Found.Name); Result := True; break;
          end;
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
var Handle: THandle; ErrorCode: Integer;
begin
  Result := False;
  if not FileExists(Path) then exit;
  { Open for write without writing: mapped/running binaries and locked files must
    block installation/uninstallation instead of being scheduled for deletion. }
  Handle := CreateFileW(Path, $C0000000, 0, 0, 3, 0, 0);
  ErrorCode := DLLGetLastError;
  Result := Handle = THandle(-1);
  if Result then Log('File preflight rejected: ' + Path + '; error=' + IntToStr(ErrorCode) + ' (' + SysErrorMessage(ErrorCode) + ')')
  else CloseHandle(Handle);
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
