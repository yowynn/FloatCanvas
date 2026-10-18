using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;

/// <summary>Explicit production pointer/keyboard boundary for click versus drag-sized objects.
/// Every input checks its own foreground process, bounded native HWND and capture.</summary>
internal static class DragSizedObjectsFixture
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, DesktopSession session, OverlayHost host, EditorController editor,
        Window toolbar, string run, string scenario, Action<string> snapshot)
    {
        var native = host.Diagnostics.Single();
        var samples = new List<object>(); var imagePaths = new List<(string Label, string Path)>();
        using var driver = new OwnInput(native.InputHandle, native.Bounds);
        foreach (var tool in new[] { EditorTool.Number, EditorTool.Emoji })
        {
            Console.WriteLine("DRAG_SIZED_TOOL " + tool);
            editor.Cancel(); editor.Session.Execute("isolated drag-sized fixture setup", document => { document.Elements.Clear(); document.NextNumber = 7; }); editor.Session.SetSelection([]);
            Field<Dictionary<EditorTool, Button>>(session, "_toolbarToolButtons")[tool].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var options = Field<Window>(session, "_toolOptionsWindow");
            if (options.IsVisible) SystemCommands.CloseWindowCommand.Execute(null, options);
            Pump(60);
            var parameters = editor.Parameters; parameters.Style.FontSize = 32; parameters.NumberStart = 7; parameters.NumberStep = 3; parameters.Emoji = "🔥";
            editor.ApplyParameters(parameters); Pump(50);
            check("native " + tool + " uses the production toolbar selection and actual isolation", editor.Tool == tool && host.IsEditing && host.IsKeyboardIsolationActive);

            int before = editor.Session.UndoCount, next = editor.Session.Document.NextNumber;
            driver.Move(70, 65); driver.Down();
            check("native " + tool + " creates an uncommitted configured preview on press", editor.Session.IsTransactionActive && editor.Session.UndoCount == before && editor.Session.Document.Elements.Count == 1);
            if (tool == EditorTool.Number) check("native number preview does not consume the next value", editor.Session.Document.NextNumber == next);
            driver.Up();
            var clicked = editor.Session.Document.Elements.Single().Clone(); var defaultBounds = ElementGeometry.Bounds(clicked, false);
            check("native " + tool + " click commits one selected configured object", editor.Session.UndoCount == before + 1 && !editor.Session.IsTransactionActive && editor.Session.Selection.SetEquals([clicked.Id]));
            if (tool == EditorTool.Number) check("native number click consumes exactly the configured step", clicked.Number == next && editor.Session.Document.NextNumber == next + 3);
            driver.Move(260, 65); driver.Down(); driver.Up();
            check("native " + tool + " consecutive blank clicks insert without a deselection-only click", editor.Session.Document.Elements.Count == 2 && editor.Session.Selection.Count == 1 && !editor.Session.Selection.Contains(clicked.Id));

            before = editor.Session.UndoCount;
            driver.Move(70, 155); driver.Down(); driver.Move(230, 285); driver.Up();
            var large = editor.Session.SelectedElements().Single().Clone(); var largeBounds = ElementGeometry.Bounds(large, false);
            check("native " + tool + " drag enlarges the complete instance without distorting its aspect", largeBounds.Width > defaultBounds.Width * 2 &&
                Math.Abs(largeBounds.Width / largeBounds.Height - defaultBounds.Width / defaultBounds.Height) < .0001 && editor.Session.UndoCount == before + 1);
            before = editor.Session.UndoCount;
            driver.Move(400, 155); driver.Down(); driver.Move(412, 163); driver.Up();
            var small = editor.Session.SelectedElements().Single().Clone(); var smallBounds = ElementGeometry.Bounds(small, false);
            check("native " + tool + " drag can create a smaller proportional instance", smallBounds.Width < defaultBounds.Width * .8 && smallBounds.Width > 0 &&
                Math.Abs(smallBounds.Width / smallBounds.Height - defaultBounds.Width / defaultBounds.Height) < .0001 && editor.Session.UndoCount == before + 1);
            check("native " + tool + " drag leaves the configured font size unchanged", editor.Style.FontSize == 32);

            editor.Session.SetSelection([large.Id]);
            var start = largeBounds.Center;
            int count = editor.Session.Document.Elements.Count; before = editor.Session.UndoCount;
            driver.Key(0xA2, false);
            check("native " + tool + " Ctrl hides the existing selection before overlapping creation", !editor.ShouldShowSelection(host.InteractionModifiers));
            driver.MoveWorld(start.X, start.Y); driver.Down(); driver.MoveWorld(start.X + 85, start.Y + 55);
            driver.Key(0xA2, true); driver.MoveWorld(start.X + 95, start.Y + 65); driver.Up();
            var preserved = editor.Session.Document.Elements.Single(element => element.Id == large.Id);
            check("native " + tool + " releasing Ctrl mid-drag retains new-object ownership", editor.Session.Document.Elements.Count == count + 1 && editor.Session.UndoCount == before + 1 &&
                preserved.Transform == large.Transform && preserved.Width == large.Width && preserved.Height == large.Height);
            var overlapping = editor.Session.SelectedElements().Single();
            int afterNext = editor.Session.Document.NextNumber;
            check("native " + tool + " completed drag is exactly one reversible undo operation", editor.Session.Undo() && editor.Session.Document.Elements.Count == count &&
                (tool != EditorTool.Number || editor.Session.Document.NextNumber == afterNext - 3) && editor.Session.Redo() && editor.Session.Document.Elements.Count == count + 1);

            count = editor.Session.Document.Elements.Count; before = editor.Session.UndoCount; next = editor.Session.Document.NextNumber;
            driver.Move(470, 265); driver.Down(); driver.Move(520, 315);
            check("native " + tool + " cancellation starts from a real pending drag", editor.Session.IsTransactionActive);
            driver.Key(0x1B, false); driver.Key(0x1B, true); driver.Up();
            check("native " + tool + " Escape rolls back geometry and numbering without leaving edit mode", !editor.Session.IsTransactionActive && editor.Session.Document.Elements.Count == count &&
                editor.Session.UndoCount == before && editor.Session.Document.NextNumber == next && host.IsEditing && host.IsKeyboardIsolationActive && toolbar.IsVisible);

            editor.Session.SetSelection([large.Id]); before = editor.Session.UndoCount;
            driver.MoveWorld(start.X, start.Y); driver.Down(); driver.MoveWorld(start.X + 12, start.Y + 8); driver.Up();
            preserved = editor.Session.Document.Elements.Single(element => element.Id == large.Id);
            check("native " + tool + " an ordinary drag inside a selection still edits that object", editor.Session.Document.Elements.Count == count && editor.Session.UndoCount == before + 1 &&
                Math.Abs(preserved.Transform.OffsetX - large.Transform.OffsetX - 12) < 1 && Math.Abs(preserved.Transform.OffsetY - large.Transform.OffsetY - 8) < 1);

            before = editor.Session.UndoCount; count = editor.Session.Document.Elements.Count;
            driver.Move(540, 70); driver.Down(); driver.Up();
            var nextClick = editor.Session.SelectedElements().Single(); var nextBounds = ElementGeometry.Bounds(nextClick, false);
            check("native " + tool + " the next click returns to configured dimensions after drags", editor.Session.Document.Elements.Count == count + 1 && editor.Session.UndoCount == before + 1 &&
                Math.Abs(nextBounds.Width - defaultBounds.Width) < .001 && Math.Abs(nextBounds.Height - defaultBounds.Height) < .001);
            check("native " + tool + " leaves native capture released and the inserted object selected", host.CaptureHandle == 0 && editor.Session.Selection.SetEquals([nextClick.Id]));
            snapshot("drag-sized-" + tool + "-complete");
            var document = editor.Session.Document.Clone();
            samples.Add(new { Tool = tool.ToString(), DefaultBounds = defaultBounds, LargeBounds = largeBounds, SmallBounds = smallBounds,
                ConfiguredFont = editor.Style.FontSize, NextNumber = editor.Session.Document.NextNumber, Elements = document.Elements });
            string imagePath = Path.Combine(run, tool.ToString().ToLowerInvariant() + "-native-samples.png");
            using var renderer = new SceneRenderer(); File.WriteAllBytes(imagePath, renderer.ExportPng(document, new AssetLibrary(), transparent: true));
            imagePaths.Add((tool.ToString(), imagePath));
        }
        File.WriteAllText(Path.Combine(run, "drag-sized-metrics.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        if (scenario == "drag-sized-ui")
        {
            var stack = new StackPanel { Margin = new Thickness(16) };
            foreach (var (label, path) in imagePaths)
            {
                stack.Children.Add(new TextBlock { Text = label + " — click / larger drag / smaller drag / Ctrl overlap", FontSize = 15, Margin = new Thickness(0, 4, 0, 8) });
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
                stack.Children.Add(new Border { Background = Brushes.White, Child = new Image { Source = image, Width = 640, Height = 360, Stretch = Stretch.Uniform } });
            }
            var window = new Window { Title = "FloatCanvas · Number and emoji drag samples", Owner = toolbar, Content = new ScrollViewer { Content = stack }, Width = 750, Height = 850, Topmost = true, ShowInTaskbar = true };
            AppWindowChrome.Apply(window); window.Show(); window.Activate();
            Console.WriteLine($"DRAG_SIZED_UI_READY pid={Environment.ProcessId} hwnd={new WindowInteropHelper(window).Handle:X} run={run} timeoutSeconds=150"); Console.Out.Flush();
            DateTime deadline = DateTime.UtcNow.AddSeconds(150);
            while (window.IsVisible && DateTime.UtcNow < deadline) Pump(50);
            if (window.IsVisible) window.Close();
        }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => frame.Continue = false;
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private sealed class OwnInput : IDisposable
    {
        private readonly nint target, previousDpi;
        private readonly PixelBounds bounds;
        private readonly PixelPoint original;
        private bool held, control;
        internal OwnInput(nint target, PixelBounds bounds) { this.target = target; this.bounds = bounds; previousDpi = SetThreadDpiAwarenessContext(-4); GetCursorPos(out original); }
        internal void Move(int x, int y) => MoveWorld(bounds.Left + x, bounds.Top + y);
        internal void MoveWorld(double x, double y)
        {
            Check(); var point = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            if (WindowFromPhysicalPoint(point) != target) throw new InvalidOperationException("Drag-sized fixture point is not its own bounded canvas; input stopped.");
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Send(new() { Type = 0, Data = new() { Mouse = new() { X = (int)Math.Round((x - left) * 65535 / (width - 1)), Y = (int)Math.Round((y - top) * 65535 / (height - 1)), Flags = 0xC001 } } });
        }
        internal void Down() { Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }); held = true; }
        internal void Up() { Send(new() { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } }); held = false; }
        internal void Key(ushort key, bool up)
        {
            Send(new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } });
            if (key == 0xA2) control = !up;
        }
        private void Send(NativeInput input)
        {
            Check();
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Pump(40); Check();
        }
        private void Check()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
            if (process != Environment.ProcessId || GetCapture() is var capture && capture != 0 && capture != target)
                throw new InvalidOperationException("Drag-sized fixture lost its own foreground/capture; input stopped.");
        }
        public void Dispose()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint process);
            if (process == Environment.ProcessId) { if (held) Up(); if (control) Key(0xA2, true); SetCursorPos(original.X, original.Y); }
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelPoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(PixelPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
}
