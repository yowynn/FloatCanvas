using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatCanvas.App;
using FloatCanvas.Core;
using L = FloatCanvas.Localization.AppText;

internal static class InspectorValidationLifecycleTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check,bool windowLifecycle=false)
    {
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
        var report=check;var failures=new List<string>();
        check=(name,passed)=>{if(passed)report(name,true);else{failures.Add(name);Console.WriteLine("INSPECTOR VALIDATION FAIL: "+name);}};
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            var (session, window, fields) = Fixture(); var status = Field<TextBlock>(window, "status");
            fields["W"].Text = "-";
            check(theme + " invalid inspector value has meaningful text in the theme error color", IsError(status) && status.Text == "⚠ " + L.Format("Inspector.NumberRange", L.Get("Inspector.Width"), .01, 10000000) &&
                status.Foreground is SolidColorBrush actual && status.TryFindResource("CanvasErrorText") is SolidColorBrush expected && actual.Color == expected.Color &&
                !session.IsTransactionActive && session.Document.Elements[0].Width == 100);
            window.Refresh();
            check(theme + " refreshing invalid inspector clears its warning without retaining error styling", !IsError(status) &&
                status.Foreground is SolidColorBrush normal && status.TryFindResource(SystemColors.GrayTextBrushKey) is SolidColorBrush muted && normal.Color == muted.Color && fields["W"].Text == "100");
            window.Close();
        }
        AppTheme.Apply("light");
        foreach(string boundary in new[]{"original value","focus loss","refresh","selection"})
        {
            var (session,window,fields)=Fixture();var status=Field<TextBlock>(window,"status");int undo=session.UndoCount;
            fields["W"].Text="-";
            check(boundary+" begins with an invalid draft and no edit transaction",IsError(status)&&!session.IsTransactionActive&&Field<EditorSession?>(window,"editSession") is null);
            if(boundary=="original value") {fields["W"].Text="100";window.CompletePendingEdit();}
            else if(boundary=="focus loss")
            {
                var panel=Field<StackPanel>(window,"propertyPanel");
                panel.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,fields["W"],null) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
            }
            else if(boundary=="refresh")window.Refresh();
            else session.SetSelection([session.Document.Elements[1].Id]);
            check(boundary+" clears obsolete property errors and restores the current value",!IsError(status)&&fields["W"].Text==(boundary=="selection"?"80":"100"));
            check(boundary+" never creates history or changes either layer",session.UndoCount==undo&&!session.IsTransactionActive&&session.Document.Elements[0].Width==100&&session.Document.Elements[1].Width==80);
            check(boundary+" regression remains detached",new WindowInteropHelper(window).Handle==0&&!window.IsVisible);
            window.Close();
        }
        {
            var (session,window,fields)=Fixture();bool allowed=false;
            typeof(InspectorWindow).GetField("canMutate",Private)!.SetValue(window,(Func<bool>)(()=>allowed));
            fields["W"].Text="125";window.CompletePendingEdit();window.Refresh();
            check("a busy document still retains its draft and meaningful error",fields["W"].Text=="125"&&IsError(Field<TextBlock>(window,"status"))&&Field<TextBlock>(window,"status").Text=="⚠ "+L.Get("Inspector.BusyDraftRetained")&&session.Document.Elements[0].Width==100);
            allowed=true;window.Refresh();
            check("unblocking a busy document does not silently discard its retained draft",fields["W"].Text=="125"&&session.Document.Elements[0].Width==100);
            typeof(InspectorWindow).GetMethod("Apply",Private)!.Invoke(window,null);
            check("a retained busy draft applies once mutation resumes",session.Document.Elements[0].Width==125&&!IsError(Field<TextBlock>(window,"status")));
            window.Close();
        }
        if(windowLifecycle)
        {
            var (session,window,fields)=Fixture();var foreground=GetForegroundWindow();nint handle=0;
            try
            {
                window.ShowActivated=false;window.ShowInTaskbar=false;window.Left=window.Top=-20000;window.Width=400;window.Height=300;
                window.Show();handle=new WindowInteropHelper(window).Handle;
                fields["W"].Text="-";check("window lifecycle begins with an actual visible invalid inspector",IsError(Field<TextBlock>(window,"status"))&&window.IsVisible&&handle!=0);
                window.Hide();window.Refresh();window.Show();
                check("hiding and reopening the same inspector drops the old validation error",!IsError(Field<TextBlock>(window,"status"))&&fields["W"].Text=="100"&&!session.IsTransactionActive);
                check("offscreen inspector lifecycle never takes the foreground",GetForegroundWindow()==foreground);
            }
            finally{window.Hide();HwndSource.FromHwnd(handle)?.Dispose();}
            check("offscreen inspector lifecycle destroys its own native source",handle!=0&&!IsWindow(handle));
        }
        var canvas=new CanvasSettingsWindow(CanvasPresetLibrary.OfficialOnly());
        canvas.Refresh(new() {Projection=new() {Enabled=true}},false);
        int canvasEdits=0;canvas.GridCountsChanged+=(_,_)=>canvasEdits++;canvas.GridWidthChanged+=_=>canvasEdits++;
        var columns=Field<NumericValueField>(canvas,"columns");string originalColumns=columns.Input.Text;columns.Input.Text="invalid";
        typeof(CanvasSettingsWindow).GetMethod("CommitCounts",Private)!.Invoke(canvas,null);
        check("Canvas settings invalid counts already restore their current value without stale errors",columns.Input.Text==originalColumns&&canvasEdits==0);
        var width=Field<NumericValueField>(canvas,"width");string originalWidth=width.Input.Text;width.Input.Text="65";
        width.Input.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,width.Input,null) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        check("Canvas settings invalid width already restores on field exit without edits",width.Input.Text==originalWidth&&canvasEdits==0&&new WindowInteropHelper(canvas).Handle==0);
        canvas.CloseForDispose();
        report("Inspector validation lifecycle has no failures ("+failures.Count+")",failures.Count==0);
    }
    private static (EditorSession Session,InspectorWindow Window,Dictionary<string,TextBox> Fields) Fixture()
    {
        var first=new CanvasElement {Kind=ElementKind.Rectangle,Width=100,Height=60};
        var second=new CanvasElement {Kind=ElementKind.Rectangle,Width=80,Height=40};
        var session=new EditorSession(new() {Elements=[first,second]});session.SetSelection([first.Id]);
        var window=new InspectorWindow(()=>session);return(session,window,Field<Dictionary<string,TextBox>>(window,"fields"));
    }
    private static bool IsError(TextBlock status)=>status.Text.StartsWith("⚠ ",StringComparison.Ordinal)&&!string.IsNullOrWhiteSpace(status.Text[2..]);
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindow(nint window);
}
