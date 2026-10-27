using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class ToolPanelVisibilityTests
{
    internal static void Run(Action<string, bool> check)
    {
        _=Application.Current??new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};AppTheme.Apply("light");
        string folder=Path.Combine(Path.GetTempPath(),"FloatCanvas-HiddenTool-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var editor=new EditorController(new EditorSession());
        var panel=new ToolOptionsPanel(editor,new ToolPresetLibrary(Path.Combine(folder,"tools.json")),new ColorLibrary(Path.Combine(folder,"colors.json")));
        var presets=Field<ToolPresetGallery>(panel,"presets");var heading=Field<TextBlock>(panel,"heading");
        var findings=new List<(string Name,bool Pass)>();
        findings.Add(("never-shown tool presets create no preview controls",presets.OfficialTiles.Children.Count==0&&presets.UserTiles.Children.Count==0));
        var root=new StackPanel {Opacity=0};root.Children.Add(panel);
        using var source=new HwndSource(new HwndSourceParameters("FloatCanvas.HiddenTools")
        {Width=1100,Height=700,PositionX=-10000,PositionY=-10000,WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x080000A0}) {RootVisual=root};
        ShowWindow(source.Handle,4);Pump();
        findings.Add(("visible tool options synchronize their current tool and presets",presets.OfficialTiles.Children.Count>0&&!string.IsNullOrEmpty(heading.Text)));
        string oldHeading=heading.Text;var oldTiles=presets.OfficialTiles.Children.Cast<UIElement>().ToArray();
        root.Visibility=Visibility.Collapsed;Pump();
        editor.Tool=EditorTool.Text;editor.Style.FontFamily="Arial";editor.Style.FontSize=41;
        panel.Refresh();presets.Refresh();Pump();
        findings.Add(("hidden tool options refresh only records pending state",heading.Text==oldHeading));
        findings.Add(("hidden tool presets retain their exact existing controls",presets.OfficialTiles.Children.Cast<UIElement>().SequenceEqual(oldTiles)));
        root.Visibility=Visibility.Visible;Pump();
        findings.Add(("reshown tool options consume the latest model",heading.Text==FloatCanvas.Localization.AppText.Get("ToolDescription.Text")&&
            Field<Dictionary<string,NumericValueField>>(panel,"numericFields")["FontSize"].Value==41));
        findings.Add(("reshown tool presets switch to the current family",presets.OfficialTiles.Children.OfType<Button>().Any(button=>Equals(button.Tag,"official.text"))));
        findings.Add(("tool visibility synchronization never edits document history",editor.Session.UndoCount==0&&editor.Session.Document.Elements.Count==0));
        GetWindowThreadProcessId(GetForegroundWindow(),out uint foreground);findings.Add(("hidden tool visibility fixture never activates",foreground!=Environment.ProcessId));
        root.Children.Clear();Pump();
        foreach(var result in findings)Console.WriteLine($"HIDDEN_TOOL_FINDING {result.Pass}: {result.Name}");
        foreach(var result in findings)check(result.Name,result.Pass);
    }
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
}
