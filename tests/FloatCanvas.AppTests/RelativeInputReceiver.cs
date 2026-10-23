using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// An independent foreground input client. It never inspects the overlay or
// changes behavior for editing: Alt releases its cursor lock, like a game UI.
internal static class RelativeInputReceiver
{
    internal static void Run()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        var text=new TextBlock{FontSize=21,Margin=new(24),TextWrapping=TextWrapping.Wrap};
        var window=new Window{Title="FloatCanvas Relative Input Receiver",Width=1120,Height=760,
            Content=text,Background=Brushes.DarkSlateGray,Foreground=Brushes.White};
        string folder=Environment.GetEnvironmentVariable("FLOATCANVAS_SYSTEM_OVERLAY_EVIDENCE")??Path.GetTempPath();
        Directory.CreateDirectory(folder);
        string log=Path.Combine(folder,$"relative-input-{Environment.ProcessId}.jsonl");
        bool legacyAlt=false,rawAlt=false,locked=false,clipOwned=false;
        int legacyAltDown=0,legacyAltUp=0,rawAltDown=0,rawAltUp=0,rawMoves=0;
        int clipCalls=0,clipFailures=0,warpCalls=0,warpFailures=0,lastClipError=0,lastWarpError=0;
        int legacyKeys=0,legacyClicks=0,rawKeys=0,rawButtons=0;
        long cameraX=0,cameraY=0;
        nint hwnd=0;
        long lastWrite=0;
        void Write(string kind)
        {
            GetClipCursor(out var clip);GetCursorPos(out var cursor);
            File.AppendAllText(log,JsonSerializer.Serialize(new{Utc=DateTimeOffset.UtcNow,Event=kind,
                Foreground=GetForegroundWindow()==hwnd,legacyAlt,rawAlt,legacyAltDown,legacyAltUp,rawAltDown,rawAltUp,
                AsyncAlt=GetAsyncKeyState(0x12)<0,QueueAlt=GetKeyState(0x12)<0,rawMoves,cameraX,cameraY,locked,
                clipCalls,clipFailures,warpCalls,warpFailures,lastClipError,lastWarpError,legacyKeys,legacyClicks,rawKeys,rawButtons,
                Clip=new[]{clip.Left,clip.Top,clip.Right,clip.Bottom},Cursor=new[]{cursor.X,cursor.Y}})+Environment.NewLine);
        }
        void Unlock(){if(clipOwned){ClipCursor(0);clipOwned=false;}}
        nint Hook(nint handle,int message,nint wp,nint lp,ref bool handled)
        {
            if(message==0x1C && wp==0)
            {legacyAlt=rawAlt=false;Unlock();Write("deactivated-reset");}
            if(message is 0x100 or 0x104 && wp==0x1B){window.Close();handled=true;return 0;}
            if(message is 0x100 or 0x104)legacyKeys++;
            if(message is 0x201 or 0x203 or 0x204 or 0x206)legacyClicks++;
            if(message is 0x100 or 0x104 or 0x101 or 0x105 && wp==0x12)
            {
                legacyAlt=message is 0x100 or 0x104;
                if(legacyAlt)legacyAltDown++;else legacyAltUp++;
                Write("legacy-alt");
            }
            if(message==0xFF)
            {
                uint size=48;
                if(GetRawInputData(lp,0x10000003,out var raw,ref size,24)!=uint.MaxValue)
                {
                    if(raw.Type==1)rawKeys++;
                    if(raw.Type==0 && raw.MouseButtonFlags!=0)rawButtons++;
                    if(raw.Type==0 && (raw.X!=0||raw.Y!=0))
                    {rawMoves++;if(!legacyAlt&&!rawAlt){cameraX+=raw.X;cameraY+=raw.Y;}}
                    if(raw.Type==1 && raw.VirtualKey==0x12)
                    {rawAlt=(raw.KeyboardFlags&1)==0;if(rawAlt)rawAltDown++;else rawAltUp++;Write("raw-alt");}
                }
            }
            return 0;
        }
        window.Show();hwnd=new WindowInteropHelper(window).Handle;
        var source=HwndSource.FromHwnd(hwnd);source.AddHook(Hook);
        RawDevice[] devices=[new(){Page=1,Usage=2,Target=hwnd},new(){Page=1,Usage=6,Target=hwnd}];
        if(!RegisterRawInputDevices(devices,2,(uint)Marshal.SizeOf<RawDevice>()))throw new System.ComponentModel.Win32Exception();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};
        timer.Tick+=(_,_)=>
        {
            locked=GetForegroundWindow()==hwnd&&!legacyAlt&&!rawAlt;
            if(locked)
            {
                var center=window.PointToScreen(new(window.ActualWidth/2,window.ActualHeight/2));
                var rect=new RectI{Left=(int)center.X,Top=(int)center.Y,Right=(int)center.X+1,Bottom=(int)center.Y+1};
                clipCalls++;
                if(ClipCursor(ref rect)){clipOwned=true;lastClipError=0;}
                else{clipFailures++;lastClipError=Marshal.GetLastPInvokeError();}
                warpCalls++;
                if(SetCursorPos(rect.Left,rect.Top))lastWarpError=0;
                else{warpFailures++;lastWarpError=Marshal.GetLastPInvokeError();}
            }
            else Unlock();
            if(Environment.TickCount64-lastWrite<250)return;
            lastWrite=Environment.TickCount64;Write("sample");
            text.Text=$"FloatCanvas Relative Input Receiver\n\n独立模拟游戏：默认锁定光标并用 Raw Mouse 改变视角；按住 Alt 解锁。\n"+
                $"前台：{GetForegroundWindow()==hwnd}；锁定：{locked}\n"+
                $"WM Alt 按下/松开：{legacyAltDown}/{legacyAltUp}，当前 {legacyAlt}\n"+
                $"Raw Alt 按下/松开：{rawAltDown}/{rawAltUp}，当前 {rawAlt}\n"+
                $"物理 Alt：{GetAsyncKeyState(0x12)<0}；线程 Alt：{GetKeyState(0x12)<0}\n"+
                $"Raw Mouse 移动包：{rawMoves}；视角：{cameraX}, {cameraY}\n\n"+
                $"ClipCursor 失败/调用：{clipFailures}/{clipCalls}；SetCursorPos：{warpFailures}/{warpCalls}\n"+
                $"键盘 WM/Raw：{legacyKeys}/{rawKeys}；鼠标按键 WM/Raw：{legacyClicks}/{rawButtons}\n\n"+
                "用当前浮绘快捷键进入编辑，尝试绘画，再用工具条鼠标退出。\n退出后先不要按 Alt，保留计数。Esc 关闭本测试；切换其他应用会解除锁定。";
        };
        timer.Start();Write("ready");Console.WriteLine($"RELATIVE_INPUT_READY pid={Environment.ProcessId} log={log}");
        try{app.Run();}finally{timer.Stop();Unlock();Write("closed");}
    }
    [StructLayout(LayoutKind.Sequential)]private struct RawDevice{public ushort Page,Usage;public uint Flags;public nint Target;}
    [StructLayout(LayoutKind.Explicit,Size=48)]private struct RawInput
    {
        [FieldOffset(0)]public uint Type;
        [FieldOffset(26)]public ushort KeyboardFlags;
        [FieldOffset(30)]public ushort VirtualKey;
        [FieldOffset(28)]public ushort MouseButtonFlags;
        [FieldOffset(36)]public int X;
        [FieldOffset(40)]public int Y;
    }
    [StructLayout(LayoutKind.Sequential)]private struct RectI{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct PointI{public int X,Y;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern short GetKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetClipCursor(out RectI rect);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out PointI point);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool ClipCursor(ref RectI rect);
    [DllImport("user32.dll")]private static extern bool ClipCursor(nint rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterRawInputDevices(RawDevice[] devices,uint count,uint size);
    [DllImport("user32.dll")]private static extern uint GetRawInputData(nint input,uint command,out RawInput data,ref uint size,uint header);
}
