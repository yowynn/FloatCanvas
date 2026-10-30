; Exercises the native Inno transport and pinned digest without launching the
; downloaded executable. No application or runtime installation occurs.
#ifndef ProbeOutputDirectory
  #error Compile with Test-InstallerBootstrap.ps1.
#endif
[Setup]
AppId=FloatCanvas.RuntimeDownloadProbe
AppName=FloatCanvas runtime download verification
AppVersion=1.0
DefaultDirName={tmp}\FloatCanvas.RuntimeDownloadProbe
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
#include "..\Runtime.iss"
#include "..\RuntimeInstall.iss"

function InitializeSetup: Boolean;
var Report, Downloaded, Error: String; Passed, ExpectFailure: Boolean;
begin
  Report := ExpandConstant('{param:Report}');
  ExpectFailure := ExpandConstant('{param:ExpectFailure|0}') = '1';
  Error := ''; Passed := False;
  try
    Downloaded := RuntimeInstallDownloadPayload;
    Passed := not ExpectFailure and (CompareText(GetSHA256OfFile(Downloaded), '{#RuntimeInstallerSha256}') = 0);
  except
    Error := GetExceptionMessage;
    Passed := ExpectFailure and (Pos(SetupMessage(msgVerificationFileHashIncorrect), Error) > 0);
  end;
  if Passed then SaveStringToFile(Report, 'Passed=True' + #13#10 + 'Error=' + Error + #13#10, False)
  else SaveStringToFile(Report, 'Passed=False' + #13#10 + 'Error=' + Error + #13#10, False);
  Result := False;
end;
