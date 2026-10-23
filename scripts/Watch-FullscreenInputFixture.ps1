param(
    [Parameter(Mandatory)][int]$FixturePid,
    [Parameter(Mandatory)][string]$ExpectedFixturePath,
    [Parameter(Mandatory)][int]$OverlayPid,
    [Parameter(Mandatory)][string]$ExpectedOverlayPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$PresentMonPath
)
$ErrorActionPreference='Stop'
$fixture=Get-Process -Id $FixturePid
$overlay=Get-Process -Id $OverlayPid
if($fixture.Path -ine $ExpectedFixturePath -or $overlay.Path -ine $ExpectedOverlayPath){throw 'Unexpected test or overlay process.'}
[void][IO.Directory]::CreateDirectory($EvidenceDirectory)
$observerLog=Join-Path $EvidenceDirectory "observer-$FixturePid.jsonl"
$csv=Join-Path $EvidenceDirectory "present-$FixturePid.csv"
$traceSession='FloatCanvasFixture-'+$FixturePid+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$trace=$null
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FixtureObserver {
 public delegate bool Callback(IntPtr window,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
 [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)] struct GuiInfo {public int Size,Flags;public IntPtr Active,Focus,Capture,Menu,Move,Caret;public Rect CaretRect;}
 public class Snapshot {
  public uint ForegroundPid;public int InputWindows,VisibleInputWindows;
  public long OverlayFocus,OverlayCapture;public bool GuiQuerySucceeded,ObserverAlt,ObserverCtrl,ObserverShift;
 }
 public static Snapshot Read(uint overlayPid) {
  var result=new Snapshot();uint foreground;GetWindowThreadProcessId(GetForegroundWindow(),out foreground);result.ForegroundPid=foreground;
  uint thread=0;
  EnumWindows((window,data)=>{
   uint pid;uint tid=GetWindowThreadProcessId(window,out pid);if(pid!=overlayPid)return true;
   if(thread==0)thread=tid;
   var cls=new StringBuilder(128);GetClassName(window,cls,128);
   if(cls.ToString()!="FloatCanvas.Overlay.v1")return true;
   var title=new StringBuilder(128);GetWindowText(window,title,128);
   if(title.ToString()=="FloatCanvas Input") {result.InputWindows++;if(IsWindowVisible(window))result.VisibleInputWindows++;}
   return true;
  },IntPtr.Zero);
  var info=new GuiInfo{Size=Marshal.SizeOf(typeof(GuiInfo))};
  if(thread!=0&&GetGUIThreadInfo(thread,ref info)){result.GuiQuerySucceeded=true;result.OverlayFocus=info.Focus.ToInt64();result.OverlayCapture=info.Capture.ToInt64();}
  result.ObserverAlt=GetAsyncKeyState(0x12)<0;result.ObserverCtrl=GetAsyncKeyState(0x11)<0;result.ObserverShift=GetAsyncKeyState(0x10)<0;
  return result;
 }
}
'@
try {
    if(-not [string]::IsNullOrWhiteSpace($PresentMonPath)) {
        $trace=Start-Process -FilePath $PresentMonPath -ArgumentList @('--process_id',"$FixturePid",'--output_file',$csv,'--qpc_time','--no_console_stats','--session_name',$traceSession) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $EvidenceDirectory "present-$FixturePid.out.log") -RedirectStandardError (Join-Path $EvidenceDirectory "present-$FixturePid.err.log")
    }
    [pscustomobject]@{FixturePid=$FixturePid;OverlayPid=$OverlayPid;ObserverPid=$PID;TracePid=$(if($null -ne $trace){$trace.Id}else{$null});TraceSession=$traceSession;TraceStartUtc=$(if($null -ne $trace){$trace.StartTime.ToUniversalTime().ToString('o')}else{$null});QpcFrequency=[Diagnostics.Stopwatch]::Frequency;InputWindowVisibilityIsNotNativeDelegationStatus=$true} | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory "capture-$FixturePid.json") -Encoding UTF8
    $previous=$null
    $lastHeartbeat=[Diagnostics.Stopwatch]::StartNew()
    while(-not $fixture.HasExited) {
        $state=[FixtureObserver]::Read([uint32]$OverlayPid)
        $value=$state | ConvertTo-Json -Compress
        if($value -ne $previous -or $lastHeartbeat.ElapsedMilliseconds -ge 250) {
            [pscustomobject]@{Utc=[DateTimeOffset]::UtcNow.ToString('o');Qpc=[Diagnostics.Stopwatch]::GetTimestamp();Event='owned-window-observation';State=$state} | ConvertTo-Json -Depth 3 -Compress | Add-Content -LiteralPath $observerLog -Encoding UTF8
            $previous=$value;$lastHeartbeat.Restart()
        }
        [void]$fixture.WaitForExit(50)
    }
} finally {
    if($null -ne $trace -and -not $trace.HasExited) {
        & $PresentMonPath --session_name $traceSession --terminate_existing_session *> (Join-Path $EvidenceDirectory "present-$FixturePid.stop.log")
        if($LASTEXITCODE -ne 0 -or -not $trace.WaitForExit(10000)){throw 'Could not stop the owned PresentMon session normally.'}
    }
    $rows=@()
    if(Test-Path -LiteralPath $csv){$rows=@(Import-Csv -LiteralPath $csv)}
    [pscustomobject]@{FixturePid=$FixturePid;FrameCaptureEstablished=$rows.Count -gt 0;Frames=$rows.Count;Modes=@($rows | Group-Object PresentMode | Select-Object Name,Count);Interpretation='Presentation classification must be correlated with native case/foreground intervals and window visibility; no automatic input acceptance is assigned. Zero frames does not establish a presentation path.'} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $EvidenceDirectory "present-$FixturePid.summary.json") -Encoding UTF8
}
