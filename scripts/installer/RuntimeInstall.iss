{ Runtime download identity is resolved and verified when Setup is built.
  The resulting Setup pins its URL and SHA-256; no runtime-supplied URL, hash,
  executable or command line can override them. }
#ifndef RuntimeInstallerVersion
  #error Build with Get-DotNetRuntime.ps1 metadata.
#endif
#ifndef RuntimeInstallerUrl
  #error RuntimeInstallerUrl is required.
#endif
#ifndef RuntimeInstallerSha256
  #error RuntimeInstallerSha256 is required.
#endif

var
  RuntimeInstallPage: TWizardPage;
  RuntimeInstallStatus: TNewStaticText;
  RuntimeInstallButton, RuntimeInstallRetryButton, RuntimeInstallManualButton: TNewButton;
  RuntimeDownloadPage: TDownloadWizardPage;
  RuntimeInstallBusy, RuntimeInstallRestartRequired: Boolean;

procedure RuntimeInstallSetStatus(Text: String);
begin
  Log(Text);
  if RuntimeInstallStatus <> nil then begin
    RuntimeInstallStatus.Caption := Text;
    RuntimeInstallStatus.Update;
  end;
end;

function RuntimeInstallSucceeded(ExitCode: Integer): Boolean;
begin
  Result := (ExitCode = 0) or (ExitCode = 3010) or (ExitCode = 1641);
end;

function RuntimeInstallNeedsRestart: Boolean;
begin
  Result := RuntimeInstallRestartRequired;
end;

function RuntimeDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  { Bound unexpected server responses; the embedded hash is checked separately. }
  Result := (Progress <= 268435456) and (ProgressMax <= 268435456);
end;

#ifndef RuntimeInstallTestHarness
function RuntimeInstallAvailable: Boolean;
begin
  Result := HasDesktopRuntime;
end;

function RuntimeInstallDownloadPayload: String;
begin
  if WizardSilent then
    DownloadTemporaryFile('{#RuntimeInstallerUrl}',
      'windowsdesktop-runtime-{#RuntimeInstallerVersion}-win-x64.exe', '{#RuntimeInstallerSha256}', @RuntimeDownloadProgress)
  else begin
    RuntimeDownloadPage.Clear;
    RuntimeDownloadPage.Add('{#RuntimeInstallerUrl}',
      'windowsdesktop-runtime-{#RuntimeInstallerVersion}-win-x64.exe', '{#RuntimeInstallerSha256}');
    RuntimeDownloadPage.Show;
    try
      RuntimeDownloadPage.Download;
    finally
      RuntimeDownloadPage.Hide;
    end;
  end;
  Result := ExpandConstant('{tmp}\windowsdesktop-runtime-{#RuntimeInstallerVersion}-win-x64.exe');
end;

function RuntimeInstallLaunchPayload(Path: String; var ExitCode: Integer): Boolean;
begin
  { ShellExec permits Windows to request administrator consent for per-user
    FloatCanvas installs too. Waiting returns the launched process's exit code. }
  Result := ShellExec('runas', Path, '/install /quiet /norestart',
    ExtractFileDir(Path), SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode);
end;
#endif

function RuntimeInstallLaunchVerifiedPayload(Path: String; var ExitCode: Integer; var Error: String): Boolean;
var Payload: TFileStream;
begin
  Result := False; Payload := nil;
  try
    try
      { Keep the verified file locked against replacement until the installer
        exits, including while Windows displays its elevation prompt. }
      Payload := TFileStream.Create(Path, fmOpenRead or fmShareDenyWrite);
      if CompareText(GetSHA256OfStream(Payload), '{#RuntimeInstallerSha256}') <> 0 then begin
        Error := CustomMessage('RuntimeInstallHashMismatch'); exit;
      end;
    except
      Error := CustomMessage('RuntimeInstallHashMismatch'); exit;
    end;
    RuntimeInstallSetStatus(CustomMessage('RuntimeInstallRunning'));
    Result := RuntimeInstallLaunchPayload(Path, ExitCode);
    if not Result then begin
      if ExitCode = 1223 then Error := CustomMessage('RuntimeInstallCancelled')
      else Error := FmtMessage(CustomMessage('RuntimeInstallLaunchFailed'), [SysErrorMessage(ExitCode)]);
    end;
  finally
    if Payload <> nil then Payload.Free;
  end;
end;

function TryInstallDesktopRuntime(var Error: String): Boolean;
var Path: String; ExitCode: Integer;
begin
  Result := False; Error := '';
  if RuntimeInstallAvailable then begin Result := True; exit; end;
  try
    Path := RuntimeInstallDownloadPayload;
  except
    Error := FmtMessage(CustomMessage('RuntimeInstallDownloadFailed'), [GetExceptionMessage]);
    exit;
  end;
  if not RuntimeInstallLaunchVerifiedPayload(Path, ExitCode, Error) then exit;
  Log('Runtime installer exit code: ' + IntToStr(ExitCode));
  if (ExitCode = 1602) or (ExitCode = 1223) then begin
    Error := CustomMessage('RuntimeInstallCancelled'); exit;
  end;
  if not RuntimeInstallSucceeded(ExitCode) then begin
    Error := FmtMessage(CustomMessage('RuntimeInstallFailed'), [IntToStr(ExitCode)]); exit;
  end;
  if (ExitCode = 3010) or (ExitCode = 1641) then RuntimeInstallRestartRequired := True;
  RuntimeInstallSetStatus(CustomMessage('RuntimeInstallChecking'));
  Result := RuntimeInstallAvailable;
  if not Result then begin
    if RuntimeInstallRestartRequired then Error := CustomMessage('RuntimeInstallRestart')
    else Error := CustomMessage('RuntimeInstallNotDetected');
  end;
end;

function RuntimeInstallExplicitlyRequested: Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/INSTALLRUNTIME') = 0 then begin Result := True; exit; end;
end;

function EnsureRuntimeForInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if RuntimeInstallAvailable then exit;
  { Quiet deployment must opt in before this installer changes a shared runtime. }
  if WizardSilent and RuntimeInstallExplicitlyRequested then begin
    if TryInstallDesktopRuntime(Result) then Result := '';
    NeedsRestart := RuntimeInstallNeedsRestart;
  end else
    Result := CustomMessage('RuntimeInstallMissing');
end;

procedure RuntimeInstallRefresh;
var Available: Boolean;
begin
  Available := RuntimeInstallAvailable;
  RuntimeInstallButton.Enabled := not Available and not RuntimeInstallBusy;
  RuntimeInstallRetryButton.Enabled := not RuntimeInstallBusy;
  RuntimeInstallManualButton.Enabled := not RuntimeInstallBusy;
  if Available then RuntimeInstallSetStatus(CustomMessage('RuntimeInstallReady'));
  if WizardForm.CurPageID = RuntimeInstallPage.ID then
    WizardForm.NextButton.Enabled := Available and not RuntimeInstallBusy;
end;

procedure RuntimeInstallClick(Sender: TObject);
var Error: String; Installed: Boolean;
begin
  if RuntimeInstallBusy then exit;
  RuntimeInstallBusy := True;
  RuntimeInstallRefresh;
  WizardForm.BackButton.Enabled := False;
  WizardForm.CancelButton.Enabled := False;
  try
    Installed := TryInstallDesktopRuntime(Error);
  finally
    RuntimeInstallBusy := False;
    WizardForm.BackButton.Enabled := True;
    WizardForm.CancelButton.Enabled := True;
    RuntimeInstallRefresh;
  end;
  if Installed then WizardForm.NextButton.OnClick(WizardForm.NextButton)
  else RuntimeInstallSetStatus(Error);
end;

procedure RuntimeInstallRetryClick(Sender: TObject);
begin
  RuntimeInstallRefresh;
  if WizardForm.NextButton.Enabled then WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure RuntimeInstallManualClick(Sender: TObject);
var ErrorCode: Integer;
begin
  if not ShellExecAsOriginalUser('open', 'https://dotnet.microsoft.com/download/dotnet/10.0',
    '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    RuntimeInstallSetStatus(CustomMessage('RuntimeInstallBrowserFailed'));
end;

procedure InitializeRuntimeInstallPage;
begin
  RuntimeInstallPage := CreateCustomPage(wpWelcome, CustomMessage('RuntimeInstallTitle'), CustomMessage('RuntimeInstallSubtitle'));
  RuntimeInstallStatus := TNewStaticText.Create(RuntimeInstallPage);
  RuntimeInstallStatus.Parent := RuntimeInstallPage.Surface;
  RuntimeInstallStatus.SetBounds(0, ScaleY(8), RuntimeInstallPage.SurfaceWidth, ScaleY(142));
  RuntimeInstallStatus.AutoSize := False;
  RuntimeInstallStatus.WordWrap := True;
  RuntimeInstallStatus.Caption := CustomMessage('RuntimeInstallMissing');
  RuntimeInstallButton := TNewButton.Create(RuntimeInstallPage);
  RuntimeInstallButton.Parent := RuntimeInstallPage.Surface;
  RuntimeInstallButton.SetBounds(0, ScaleY(158), ScaleX(235), ScaleY(30));
  RuntimeInstallButton.Caption := CustomMessage('RuntimeInstallAction');
  RuntimeInstallButton.OnClick := @RuntimeInstallClick;
  RuntimeInstallRetryButton := TNewButton.Create(RuntimeInstallPage);
  RuntimeInstallRetryButton.Parent := RuntimeInstallPage.Surface;
  RuntimeInstallRetryButton.SetBounds(ScaleX(250), ScaleY(158), ScaleX(130), ScaleY(30));
  RuntimeInstallRetryButton.Caption := CustomMessage('RuntimeInstallRetry');
  RuntimeInstallRetryButton.OnClick := @RuntimeInstallRetryClick;
  RuntimeInstallManualButton := TNewButton.Create(RuntimeInstallPage);
  RuntimeInstallManualButton.Parent := RuntimeInstallPage.Surface;
  RuntimeInstallManualButton.SetBounds(0, ScaleY(200), ScaleX(235), ScaleY(28));
  RuntimeInstallManualButton.Caption := CustomMessage('RuntimeInstallManual');
  RuntimeInstallManualButton.OnClick := @RuntimeInstallManualClick;
  RuntimeDownloadPage := CreateDownloadPage(CustomMessage('RuntimeInstallDownloading'),
    CustomMessage('RuntimeInstallDownloadDetail'), @RuntimeDownloadProgress);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function RuntimeInstallShouldSkipPage(PageID: Integer): Boolean;
begin
  { Silent Setup must reach PrepareToInstall, which either enforces the
    prerequisite or performs an explicitly requested /INSTALLRUNTIME install. }
  Result := (PageID = RuntimeInstallPage.ID) and (WizardSilent or RuntimeInstallAvailable);
end;

procedure RuntimeInstallCurPageChanged(PageID: Integer);
begin
  if PageID = RuntimeInstallPage.ID then RuntimeInstallRefresh;
end;

function RuntimeInstallNextButtonClick(PageID: Integer): Boolean;
begin
  Result := True;
  if (PageID = RuntimeInstallPage.ID) and not WizardSilent then begin
    RuntimeInstallRefresh;
    Result := WizardForm.NextButton.Enabled;
  end;
end;
