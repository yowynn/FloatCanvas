; Runs the real bootstrap decision/hash/re-detection code with compile-time-only
; transport and process fixtures. It cannot install a runtime or application.
#ifndef ProbeOutputDirectory
  #error Compile with Test-InstallerBootstrap.ps1.
#endif
#define RuntimeInstallTestHarness
#define RuntimeInstallerVersion "10.0.0"
#define RuntimeInstallerUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.0/windowsdesktop-runtime-10.0.0-win-x64.exe"
#ifndef ProbeName
  #define ProbeName "RuntimeInstallProbe"
#endif

[Setup]
AppId=FloatCanvas.RuntimeInstallProbe
AppName=FloatCanvas runtime bootstrap verification
AppVersion=1.0
DefaultDirName={tmp}\FloatCanvas.RuntimeInstallProbe
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
DisableStartupPrompt=yes
OutputDir={#ProbeOutputDirectory}
OutputBaseFilename={#ProbeName}

[Languages]
Name: en; MessagesFile: "compiler:Default.isl"
Name: zhcn; MessagesFile: "..\Languages\ChineseSimplified.isl"
#include "..\RuntimeInstallMessages.iss"

[Code]
var
  TestAvailable, TestAfterInstall, TestLaunchSucceeds, PayloadLockedDuringLaunch: Boolean;
  TestDownloadMode, TestExitCode, Downloads, Launches, Checks: Integer;

function RuntimeInstallAvailable: Boolean;
begin
  Result := TestAvailable;
end;

function RuntimeInstallDownloadPayload: String;
begin
  Downloads := Downloads + 1;
  if TestDownloadMode = 1 then RaiseException('Simulated network timeout');
  if TestDownloadMode = 2 then RaiseException('Simulated cancelled download');
  Result := '{#ProbePayloadFile}';
  if TestDownloadMode = 3 then Result := Result + '.corrupt';
  if TestDownloadMode = 4 then Result := Result + '.missing';
end;

function RuntimeInstallLaunchPayload(Path: String; var ExitCode: Integer): Boolean;
begin
  Launches := Launches + 1;
  PayloadLockedDuringLaunch := not SaveStringToFile(Path, 'untrusted replacement', False);
  ExitCode := TestExitCode;
  TestAvailable := TestAfterInstall;
  Result := TestLaunchSucceeds;
end;

#include "..\RuntimeInstall.iss"

procedure Check(Name: String; Condition: Boolean);
begin
  Checks := Checks + 1;
  if not Condition then RaiseException(Name);
  Log('PASS: ' + Name);
end;

procedure Reset;
begin
  TestAvailable := False; TestAfterInstall := True; TestLaunchSucceeds := True;
  TestDownloadMode := 0; TestExitCode := 0; Downloads := 0; Launches := 0;
  RuntimeInstallRestartRequired := False;
  PayloadLockedDuringLaunch := False;
end;

procedure RunChecks;
var Error: String; Restart: Boolean;
begin
  Reset; TestAvailable := True;
  Check('existing runtime is accepted', TryInstallDesktopRuntime(Error));
  Check('existing runtime needs no download or launch', (Downloads = 0) and (Launches = 0));
  Reset;
  Check('download install and automatic re-detection succeed', TryInstallDesktopRuntime(Error));
  Check('one download and one launch', (Downloads = 1) and (Launches = 1));
  Check('verified installer remains locked through launch', PayloadLockedDuringLaunch);
  Check('success has no error or restart', (Error = '') and not RuntimeInstallNeedsRestart);
  Reset; TestDownloadMode := 1;
  Check('network timeout is recoverable', not TryInstallDesktopRuntime(Error));
  Check('network timeout does not execute a partial download', (Launches = 0) and (Error <> ''));
  Reset; TestDownloadMode := 2;
  Check('download cancellation is recoverable', not TryInstallDesktopRuntime(Error));
  Check('cancelled download is not executed', Launches = 0);
  Reset; TestDownloadMode := 3;
  Check('corrupt download is rejected', not TryInstallDesktopRuntime(Error));
  Check('corrupt payload never launches', (Launches = 0) and (Error = CustomMessage('RuntimeInstallHashMismatch')));
  Reset; TestDownloadMode := 4;
  Check('missing download is rejected', not TryInstallDesktopRuntime(Error));
  Check('missing payload never launches', Launches = 0);
  Reset; TestLaunchSucceeds := False; TestExitCode := 1223;
  Check('cancelled elevation is recoverable', not TryInstallDesktopRuntime(Error));
  Check('elevation cancellation is explained', Error = CustomMessage('RuntimeInstallCancelled'));
  Reset; TestLaunchSucceeds := False; TestExitCode := 5;
  Check('launch failure is rejected', not TryInstallDesktopRuntime(Error));
  Check('launch failure has an explanation', Error <> '');
  Reset; TestExitCode := 1602;
  Check('runtime installer cancellation is recoverable', not TryInstallDesktopRuntime(Error));
  Check('runtime cancellation is explained', Error = CustomMessage('RuntimeInstallCancelled'));
  Reset; TestExitCode := 1603;
  Check('installer failure cannot be hidden by detection', not TryInstallDesktopRuntime(Error));
  Check('installer error contains exit code', Pos('1603', Error) > 0);
  Reset; TestAfterInstall := False;
  Check('exit zero alone never allows continuing', not TryInstallDesktopRuntime(Error));
  Check('missing runtime after success is explained', Error = CustomMessage('RuntimeInstallNotDetected'));
  Reset; TestExitCode := 3010;
  Check('usable runtime after restart-required exit permits installing app', TryInstallDesktopRuntime(Error));
  Check('restart request is remembered', RuntimeInstallNeedsRestart);
  Reset; TestExitCode := 3010; TestAfterInstall := False;
  Check('restart-required without usable runtime blocks app install', not TryInstallDesktopRuntime(Error));
  Check('restart instructions are explicit', Error = CustomMessage('RuntimeInstallRestart'));
  Reset; TestExitCode := 1641;
  Check('restart-initiated exit remains explicit', TryInstallDesktopRuntime(Error) and RuntimeInstallNeedsRestart);
  Reset; Restart := False;
  Error := EnsureRuntimeForInstall(Restart);
  if RuntimeInstallExplicitlyRequested then begin
    Check('explicit silent runtime request completes', Error = '');
    Check('explicit silent request downloads and launches', (Downloads = 1) and (Launches = 1));
  end else begin
    Check('implicit silent runtime download is rejected', Error <> '');
    Check('implicit silent request performs no installation', (Downloads = 0) and (Launches = 0));
  end;
  Check('download size limit rejects oversized progress', not RuntimeDownloadProgress('', '', 268435457, 0));
  Check('download size limit rejects oversized server length', not RuntimeDownloadProgress('', '', 0, 268435457));
  Check('unknown download length can proceed', RuntimeDownloadProgress('', '', 1024, 0));
end;

function InitializeSetup: Boolean;
var Report: String;
begin
  Report := ExpandConstant('{param:Report}');
#ifdef ProbeWizardFlow
  Reset;
  Result := True;
#else
  try
    RunChecks;
    SaveStringToFile(Report, 'Passed=True' + #13#10 + 'Checks=' + IntToStr(Checks) + #13#10, False);
  except
    SaveStringToFile(Report, 'Passed=False' + #13#10 + 'Error=' + GetExceptionMessage + #13#10, False);
  end;
  Result := False;
#endif
end;

#ifdef ProbeWizardFlow
procedure InitializeWizard;
begin
  InitializeRuntimeInstallPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := RuntimeInstallShouldSkipPage(PageID);
end;

procedure CurPageChanged(PageID: Integer);
begin
  RuntimeInstallCurPageChanged(PageID);
end;

function NextButtonClick(PageID: Integer): Boolean;
begin
  Result := RuntimeInstallNextButtonClick(PageID);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Report: String;
begin
  Report := ExpandConstant('{param:Report}');
  try
    Check('silent wizard reaches prepare', WizardSilent);
    Result := EnsureRuntimeForInstall(NeedsRestart);
    if RuntimeInstallExplicitlyRequested then begin
      Check('explicit missing runtime installs before app installation', Result = '');
      Check('explicit request downloads once', Downloads = 1);
      Check('explicit request installs once and detects runtime', (Launches = 1) and TestAvailable);
    end else begin
      Check('default silent missing runtime blocks before app installation', Result <> '');
      Check('default silent never downloads', Downloads = 0);
      Check('default silent never installs', Launches = 0);
    end;
    SaveStringToFile(Report, 'Passed=True' + #13#10 + 'Checks=' + IntToStr(Checks) + #13#10, False);
  except
    SaveStringToFile(Report, 'Passed=False' + #13#10 + 'Error=' + GetExceptionMessage + #13#10, False);
    Result := 'Verification failed';
  end;
end;
#endif
