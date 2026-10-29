{ .NET 10 apphost lookup order and Windows registry view:
  https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/fxr_resolver.cpp
  https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/hostmisc/pal.windows.cpp
  This is a prerequisite check, not a repair or full runtime integrity scan. }

var
  DetectedDesktopRuntimeVersion: String;
  DetectedDesktopRuntimeRoot: String;

function RuntimeStablePatch(Version: String; var Patch: Integer): Boolean;
var Part: String; I: Integer;
begin
  Result := False;
  if Copy(Version, 1, 5) <> '10.0.' then exit;
  Part := Copy(Version, 6, Length(Version));
  if (Part = '') or ((Length(Part) > 1) and (Part[1] = '0')) then exit;
  for I := 1 to Length(Part) do
    if Pos(Part[I], '0123456789') = 0 then exit;
  Patch := StrToIntDef(Part, -1);
  Result := Patch >= 0;
end;

function RuntimeRootPath: String;
begin
#ifdef RuntimeProbeRoot
  { Used only by the separately compiled prerequisite test harness. }
  Result := '{#RuntimeProbeRoot}';
#else
  Result := GetEnv('DOTNET_ROOT_X64');
  if (Result <> '') and DirExists(Result) then exit;
  Result := GetEnv('DOTNET_ROOT');
  if (Result <> '') and DirExists(Result) then exit;
  { Like the apphost, do not hide a broken registered installation by falling
    through to another root. Windows uses the 32-bit registry for every arch. }
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Result) and
    (Result <> '') then exit;
  Result := ExpandConstant('{commonpf64}\dotnet');
  if ProcessorArchitecture = paArm64 then Result := AddBackslash(Result) + 'x64';
#endif
end;

function RuntimeIsX64Executable(Path: String): Boolean;
var Data: AnsiString; Offset: LongWord; Size: Int64;
begin
  Result := False;
  if not FileSize64(Path, Size) or (Size < 64) or (Size > 16777216) then exit;
  if not LoadStringFromFile(Path, Data) or (Copy(Data, 1, 2) <> 'MZ') then exit;
  Offset := Ord(Data[61]) + (Ord(Data[62]) shl 8) +
    (Ord(Data[63]) shl 16) + (Ord(Data[64]) shl 24);
  if (Offset > LongWord(Length(Data) - 6)) then exit;
  Result := (Copy(Data, Offset + 1, 4) = 'PE' + #0 + #0) and
    (Ord(Data[Offset + 5]) = $64) and (Ord(Data[Offset + 6]) = $86);
end;

function RuntimeLineVersion(Line, Framework, Root: String; var Version: String): Boolean;
var Remaining, Location: String; P, Patch: Integer;
begin
  Result := False;
  if Copy(Line, 1, Length(Framework) + 1) <> Framework + ' ' then exit;
  Remaining := Copy(Line, Length(Framework) + 2, Length(Line));
  P := Pos(' ', Remaining);
  if P = 0 then exit;
  Version := Copy(Remaining, 1, P - 1);
  if not RuntimeStablePatch(Version, Patch) then exit;
  Location := Trim(Copy(Remaining, P + 1, Length(Remaining)));
  if (Length(Location) < 3) or (Location[1] <> '[') or (Location[Length(Location)] <> ']') then exit;
  Location := Copy(Location, 2, Length(Location) - 2);
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Location)),
    RemoveBackslashUnlessRoot(ExpandFileName(AddBackslash(Root) + 'shared\' + Framework))) = 0;
end;

function RuntimeListVersions(Lines: TArrayOfString; Root: String;
  var DesktopVersion, CoreVersion: String): Boolean;
var I, Patch, DesktopPatch, CorePatch: Integer; Version: String;
begin
  DesktopVersion := ''; CoreVersion := '';
  DesktopPatch := -1; CorePatch := -1;
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    if RuntimeLineVersion(Lines[I], 'Microsoft.WindowsDesktop.App', Root, Version) and
      RuntimeStablePatch(Version, Patch) and (Patch > DesktopPatch) then begin
      DesktopVersion := Version; DesktopPatch := Patch;
    end;
    if RuntimeLineVersion(Lines[I], 'Microsoft.NETCore.App', Root, Version) and
      RuntimeStablePatch(Version, Patch) and (Patch > CorePatch) then begin
      CoreVersion := Version; CorePatch := Patch;
    end;
  end;
  Result := (DesktopPatch >= 0) and (CorePatch >= 0);
end;

function RuntimeRequiredCorePatch(DesktopDirectory: String; var Patch: Integer): Boolean;
var Data: AnsiString; Text, Version: String; P: Integer;
begin
  Result := False;
  if not LoadStringFromFile(AddBackslash(DesktopDirectory) + 'Microsoft.WindowsDesktop.App.runtimeconfig.json', Data) then exit;
  Text := String(Data);
  P := Pos('"Microsoft.NETCore.App"', Text);
  if P = 0 then exit;
  Delete(Text, 1, P + Length('"Microsoft.NETCore.App"') - 1);
  P := Pos('"version"', Text);
  if P = 0 then exit;
  Delete(Text, 1, P + Length('"version"') - 1); Text := Trim(Text);
  if (Text = '') or (Text[1] <> ':') then exit;
  Delete(Text, 1, 1); Text := Trim(Text);
  if (Text = '') or (Text[1] <> '"') then exit;
  Delete(Text, 1, 1); P := Pos('"', Text);
  if P = 0 then exit;
  Version := Copy(Text, 1, P - 1);
  Result := RuntimeStablePatch(Version, Patch);
end;

function HasDesktopRuntime: Boolean;
var Root, Host, DesktopVersion, CoreVersion, DesktopDirectory, CoreDirectory: String;
  Output: TExecOutput; ExitCode, RequiredPatch, CorePatch: Integer;
begin
  Result := False;
  DetectedDesktopRuntimeVersion := ''; DetectedDesktopRuntimeRoot := '';
  try
    Root := RemoveBackslashUnlessRoot(ExpandFileName(RuntimeRootPath));
    Host := AddBackslash(Root) + 'dotnet.exe';
    if not RuntimeIsX64Executable(Host) then exit;
    if not ExecAndCaptureOutput(Host, '--list-runtimes', Root, SW_SHOWNORMAL,
      ewWaitUntilTerminated, ExitCode, Output) or (ExitCode <> 0) or Output.Error then exit;
    if not RuntimeListVersions(Output.StdOut, Root, DesktopVersion, CoreVersion) then exit;
    DesktopDirectory := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\' + DesktopVersion;
    CoreDirectory := AddBackslash(Root) + 'shared\Microsoft.NETCore.App\' + CoreVersion;
    if not RuntimeRequiredCorePatch(DesktopDirectory, RequiredPatch) or
      not RuntimeStablePatch(CoreVersion, CorePatch) or (CorePatch < RequiredPatch) then exit;
    Result := FileExists(AddBackslash(DesktopDirectory) + 'PresentationFramework.dll') and
      FileExists(AddBackslash(DesktopDirectory) + 'PresentationCore.dll') and
      FileExists(AddBackslash(DesktopDirectory) + 'WindowsBase.dll') and
      FileExists(AddBackslash(DesktopDirectory) + 'Microsoft.WindowsDesktop.App.deps.json') and
      RuntimeIsX64Executable(AddBackslash(DesktopDirectory) + 'wpfgfx_cor3.dll') and
      FileExists(AddBackslash(CoreDirectory) + 'System.Private.CoreLib.dll') and
      FileExists(AddBackslash(CoreDirectory) + 'Microsoft.NETCore.App.deps.json') and
      RuntimeIsX64Executable(AddBackslash(CoreDirectory) + 'coreclr.dll') and
      RuntimeIsX64Executable(AddBackslash(CoreDirectory) + 'hostpolicy.dll');
    if Result then begin
      DetectedDesktopRuntimeVersion := DesktopVersion;
      DetectedDesktopRuntimeRoot := Root;
      Log('Compatible x64 Windows Desktop Runtime detected: ' + DesktopVersion + ' at ' + Root);
    end;
  except
    Log('Runtime detection failed: ' + GetExceptionMessage);
    Result := False;
  end;
end;
