using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using L=FloatCanvas.Localization.AppText;

internal static class ShortcutDesktopTests
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static void Run(Action<string,bool> check)
    {
        var app=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Func<IReadOnlyList<MonitorInfo>> screens=()=>[new("shortcuts",new(0,0,640,480),new(0,0,640,440),96,96,true)];
        using var host=(OverlayHost)Activator.CreateInstance(typeof(OverlayHost),Hidden,null,[OverlayMonitorScope.AllScreens,screens,false],null)!;
        using var desktop=new DesktopSession(app,host);
        T Field<T>(string name)=>(T)typeof(DesktopSession).GetField(name,Hidden)!.GetValue(desktop)!;
        object? Call(string name,params object?[] args)=>typeof(DesktopSession).GetMethod(name,Hidden)!.Invoke(desktop,args);
        Field<System.Windows.Forms.NotifyIcon>("_tray").Visible=false;
        var editor=Field<EditorController>("_editor");
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host,true);
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(host,true);
        editor.BackgroundPickingEnabled=false;
        editor.Tool=EditorTool.Rectangle;
        Call("CurrentGroupCanvasView"); editor.ResumePresentedPicking();
        editor.Pointer(new(OverlayPointerKind.Down,new(20,20),OverlayMouseButton.Left,0));
        check("idempotent entry fixture owns a creation",editor.Session.IsTransactionActive);
        Call("ExecuteGlobalCommand","Global.Enter");
        check("entering an already editing canvas preserves the gesture",editor.Session.IsTransactionActive);
        editor.Cancel();
        BorrowedToolTests.Run(check,desktop,editor);
        var parametersBeforeOptions=editor.Parameters.Clone();
        var toolbar=Field<Window>("_toolbar"); toolbar.Show(); toolbar.UpdateLayout();
        foreach(var tool in new[]{EditorTool.Select,EditorTool.Stamp,EditorTool.Freehand,EditorTool.Canvas})
        {
            Call("CloseToolOptions"); editor.Tool=tool;
            Call("OnCommandInput",new OverlayKeyEvent(0x73,OverlayModifiers.None,false));
            var options=Field<Window>("_toolOptionsWindow");
            check("F4 opens current tool properties for "+tool,options.IsVisible && editor.Tool==tool);
            Call("OnCommandInput",new OverlayKeyEvent(0x73,OverlayModifiers.None,false));
            check("repeated F4 keeps tool properties open for "+tool,options.IsVisible && editor.Tool==tool);
            var menu=(MenuItem)Call("ToolMenuItem",tool)!;
            check("tool menu advertises the common properties shortcut for "+tool,menu.ToolTip?.ToString()?.Contains(EditorCommandCatalog.Hint("Panel.Tools"))==true);
        }
        Call("CloseToolOptions"); toolbar.Hide(); editor.ApplyParameters(parametersBeforeOptions);
        var bindings=EditorCommandCatalog.Overrides;
        try
        {
            EditorCommandCatalog.Overrides=new()
            {
                ["Tools.More"]=[new(0x51)], ["Tool.Blur"]=[new(0x4B)],
                ["Tool.Freehand"]=[new(0x42,OverlayModifiers.Alt)], ["Stamp.Library"]=[new(0x76)]
            };
            editor.Tool=EditorTool.Select;
            Call("RefreshToolShortcutHints");Call("RefreshToolbarState");
            var tools=Field<Dictionary<EditorTool,Button>>("_toolbarToolButtons");
            var extra=Field<Dictionary<EditorTool,Button>>("_extraToolButtons");
            check("selection tooltip advertises tool properties",tools[EditorTool.Select].ToolTip?.ToString()?.Contains(EditorCommandCatalog.Hint("Panel.Tools"))==true);
            check("More tooltip follows its remapped binding after state refresh",tools[EditorTool.Blur].ToolTip?.ToString()?.EndsWith(" · Q")==true);
            check("extra tool tooltip exposes its newly assigned binding",extra[EditorTool.Blur].ToolTip?.ToString()?.Contains(" · K")==true);
            check("multi-key tool binding remains readable in tooltip",tools[EditorTool.Freehand].ToolTip?.ToString()?.Contains("Alt+B")==true);
            check("stamp library tooltip uses remapped binding",Field<Button>("_stampLibraryButton").ToolTip?.ToString()?.EndsWith(" · F7")==true);
        }
        finally {EditorCommandCatalog.Overrides=bindings;Call("RefreshToolShortcutHints");}
        check("command search explains an unavailable crop",Call("EditorCommandDisabledReason",EditorCommandCatalog.All.Single(c=>c.Tool==EditorTool.Crop)) as string==L.Get("Audit.ImageRequirement"));
        var shape=editor.Parameters;shape.Tool=EditorTool.Symbol;shape.Symbol=SymbolKind.Star;editor.ApplyParameters(shape);
        Call("ExecuteEditorCommand",EditorCommandCatalog.All.Single(c=>c.Id=="Tool.Triangle"));
        check("triangle command replaces a remembered star with the triangle preset",editor.Parameters.Symbol==SymbolKind.Triangle);
        var pen=editor.Parameters;pen.Tool=EditorTool.Freehand;pen.Style.StrokeWidth=7;editor.ApplyParameters(pen);
        Call("ExecuteGlobalCommand","Global.Capture");
        check("global capture owns a pending selection request",editor.Tool==EditorTool.CaptureRegion && Field<object?>("_globalCapture") is not null);
        Call("ExecuteGlobalCommand","Global.Capture");
        Call("HandleCanvasContextMenu",new OverlayPointerEvent(OverlayPointerKind.Down,new(30,30),OverlayMouseButton.Right,0));
        check("right-cancel before capture restores original tool parameters and mode",editor.Tool==EditorTool.Freehand && editor.Style.StrokeWidth==7 && host.IsEditing && host.IsVisible && Field<object?>("_globalCapture") is null);
        Call("ExecuteGlobalCommand","Global.Capture");
        Call("OnKey",0x1B);
        check("Escape cancels the global capture request before a selection exists",editor.Tool==EditorTool.Freehand && Field<object?>("_globalCapture") is null);
        Call("ExecuteGlobalCommand","Global.Capture");
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host,false);
        Call("CompleteGlobalCapture",false,false);
        check("external capture cancellation restores the tool without reacquiring editing",!host.IsEditing && editor.Tool==EditorTool.Freehand);
        typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(host,true);
        Call("ExecuteGlobalCommand","Global.Capture");Call("CompleteGlobalCapture",true,true);
        check("successful global capture ends in selection for image editing",editor.Tool==EditorTool.Select && Field<object?>("_globalCapture") is null);
        var frozen=new SceneDocument {Kind=DocumentKind.FrozenCapture,Viewport=new(0,0,640,480)};
        editor.Session.ReplaceDocument(frozen);
        Call("ExecuteGlobalCommand","Global.HideAll");
        check("hide all preserves the frozen capture document",ReferenceEquals(editor.Session.Document,frozen) && !host.IsEditing && !host.IsVisible);
        Call("ExecuteGlobalCommand","Global.Capture");
        Call("CompleteGlobalCapture",false,true);
        check("cancelling capture restores the hidden-all snapshot and frozen document",!host.IsEditing && !host.IsVisible &&
            Field<object?>("_allOverlaysHidden") is not null && ReferenceEquals(editor.Session.Document,frozen));
        // Native registration on the isolated desktop, without injecting input.
        var original=new Dictionary<string,HotkeyBinding> { ["Global.Enter"]=new(3,0x79) };
        host.RegisterCommandHotkeys(original);
        using var peer=new HwndSource(new HwndSourceParameters("shortcut conflict fixture") {Width=1,Height=1,WindowStyle=0});
        check("reserve conflicting chord on the isolated desktop",RegisterHotKey(peer.Handle,91,3|0x4000,0x7A));
        try
        {
            bool rejected=false;
            try { host.RegisterCommandHotkeys(new Dictionary<string,HotkeyBinding> { ["Global.Enter"]=new(3,0x7A) }); }
            catch(System.ComponentModel.Win32Exception) { rejected=true; }
            var actual=(Dictionary<string,HotkeyBinding>)typeof(OverlayHost).GetField("_commandHotkeys",Hidden)!.GetValue(host)!;
            check("registration failure restores the complete prior collection",rejected && actual.Count==1 && actual["Global.Enter"]==original["Global.Enter"]);
        }
        finally {UnregisterHotKey(peer.Handle,91);}
        Call("ShowCommandSearch",true);
        var window=Field<Window>("_commandWindow");
        var panel=(DockPanel)window.Content;
        var footer=panel.Children.OfType<StackPanel>().Single();
        var actions=footer.Children.OfType<WrapPanel>().Single();
        var list=panel.Children.OfType<ListBox>().Single();
        check("command picker list never forwards canvas shortcuts",!EditorShortcutPolicy.ShouldRoute(0x56,ModifierKeys.None,list));
        list.SelectedItem=list.Items.Cast<ListBoxItem>().Single(item=>((EditorCommand)item.Tag).Id=="Edit.Copy");
        window.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("B:/FloatCanvas-shortcuts-20261116");
        using(var output=System.IO.File.Create("B:/FloatCanvas-shortcuts-20261116/shortcut-settings.png")) encoder.Save(output);
        void Click(string key)=>actions.Children.OfType<Button>().Single(b=>Equals(b.Content,L.Get(key))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click("Shortcuts.RecordPrimary"); Click("Shortcuts.RecordAlternate");
        check("reentering shortcut recording retains one suspended scope",host.ApplicationHotkeysSuspended);
        var source=PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Escape) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
        check("cancelling repeated recording restores global bindings",!host.ApplicationHotkeysSuspended);
        Click("Shortcuts.RecordPrimary");window.Hide();
        check("hiding recorder releases hotkeys and input ownership",!host.ApplicationHotkeysSuspended && !EditorShortcutPolicy.RecordingWindows.Contains(window));
        window.Close();host.RegisterCommandHotkeys(new Dictionary<string,HotkeyBinding>());
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window,int id);
}
