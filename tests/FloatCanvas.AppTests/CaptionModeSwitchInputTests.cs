using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class CaptionModeSwitchInputTests
{
    internal static void Run(Action<string, bool> check)
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        var previous = new TextBox { Text = "Previous keyboard focus", Height = 30 };
        var ordinary = new Button { Content = "Native input baseline", Height = 30 };
        var mode = new CanvasModeSwitch(); mode.Synchronize(GroupCanvasMode.RootProject, true);
        var body = new StackPanel { Children = { previous, ordinary, mode } };
        var window = new Window { Width = 300, Height = 200, Content = body, ShowInTaskbar = false };
        int baseline = 0, clicks = 0;
        ordinary.Click += (_, _) => baseline++;
        mode.Click += (_, _) => { clicks++; mode.Synchronize(mode.Mode == GroupCanvasMode.RootProject ? GroupCanvasMode.Temporary : GroupCanvasMode.RootProject, true); };
        try
        {
            window.Show(); window.Activate(); window.UpdateLayout(); Pump();
            previous.Focus(); Click(ordinary);
            check("native mouse sequence activates a regular button", baseline == 1);
            previous.Focus(); Click(mode);
            check("native mouse release switches the layer mode exactly once", clicks == 1 && mode.Mode == GroupCanvasMode.Temporary);
            check("pointer activation ends previous text editing without restoring it", !previous.IsKeyboardFocused && mode.IsKeyboardFocused);
        }
        finally { window.Close(); }
        foreach (string theme in new[] { "light", "dark" })
        {
            AppTheme.Apply(theme);
            var session = new EditorSession();
            var layers = new LayersWindow(() => session) { ShowInTaskbar = false };
            int requests = 0; GroupCanvasMode current = GroupCanvasMode.RootProject;
            void Refresh() => layers.SetGroupContext(new("Group", GroupStorageKind.Ordinary, null, current, true,
                Temporary: () => { requests++; current = GroupCanvasMode.Temporary; Refresh(); },
                Root: () => { requests++; current = GroupCanvasMode.RootProject; Refresh(); }));
            try
            {
                layers.Show(); layers.Activate(); Refresh(); layers.UpdateLayout(); Pump();
                var control = Field<CanvasModeSwitch>(layers, "groupCanvasChoice");
                VerifyCaption(check, theme + " layers", control, () => requests);
            }
            finally { layers.Close(); }
            var canvas = new CanvasOptionsTestWindow(CanvasPresetLibrary.OfficialOnly()) { ShowInTaskbar = false };
            bool depth = false; int depthRequests = 0;
            var settings = new CanvasSettings { Projection = CanvasCalibration.CreateInitial(new(0,0,1000,700)) };
            settings.Projection.Enabled = true; CanvasProjectionGeometry.InitializePlane(settings);
            canvas.DepthPriorityOcclusionChanged += enabled => { depthRequests++; depth = enabled; canvas.Options.RefreshPreviewControls(enabled,1); };
            try
            {
                canvas.Refresh(settings); canvas.PrepareForShow(); canvas.Show(); canvas.Activate(); canvas.UpdateLayout(); Pump();
                var control = Field<CaptionModeSwitch>(canvas.Options,"depthPriorityOcclusion");
                VerifyCaption(check, theme + " occlusion", control, () => depthRequests);
                settings.Projection.Enabled = false; canvas.Refresh(settings); canvas.PrepareForShow();
                int before = depthRequests; Click(control);
                check(theme + " flat mode remains disabled under native input", depthRequests == before && !control.IsEnabled);
            }
            finally { canvas.CloseForDispose(); }
        }
        AppTheme.Apply("light");
    }

    private static void VerifyCaption(Action<string,bool> check, string label, CaptionModeSwitch control, Func<int> requests)
    {
        bool pan = false;
        using var router = new SessionKeyboardRouter(() => true, _ => true, () => false, _ => { }, value => pan = value);
        foreach(double fraction in new[]{.1,.5,.9})
        {
            Keyboard.ClearFocus(); int before = requests(); bool selected = control.SelectedFirst;
            var point = control.PointToScreen(new(control.ActualWidth*fraction,control.ActualHeight/2));
            var source = (HwndSource)PresentationSource.FromVisual(control);
            check(label + " title chrome allows client input " + fraction, SendMessage(source.Handle,0x84,0,Pack(point)) == 1);
            Click(control,fraction);
            check(label + " native click switches exactly once " + fraction, requests()==before+1 && control.SelectedFirst != selected);
            int after = requests();
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            control.RaiseEvent(down);
            check(label + " Space starts pan without activating the retained button " + fraction, down.Handled && pan && requests() == after);
            control.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            check(label + " Space release ends pan " + fraction, !pan);
        }
        Keyboard.ClearFocus(); int count = requests(); Click(control,loseCapture:true);
        check(label + " lost capture cancels instead of clicking",requests()==count && !control.IsMouseCaptured);
    }

    private static void Click(FrameworkElement element, double fraction = .5, bool loseCapture = false)
    {
        var source = (HwndSource)PresentationSource.FromVisual(element);
        var point = source.CompositionTarget.TransformToDevice.Transform(element.TranslatePoint(new(element.ActualWidth * fraction, element.ActualHeight / 2), (UIElement)source.RootVisual));
        nint coordinates = Pack(point);
        var original = new byte[256]; if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read test-thread key state.");
        try
        {
            SetCapture(source.Handle);
            SendMessage(source.Handle, 0x200, 0, coordinates); Pump();
            var keys = (byte[])original.Clone(); keys[1] = 0x80; SetKeyboardState(keys);
            SendMessage(source.Handle, 0x201, 1, coordinates);
            if(loseCapture) Mouse.Capture(null);
            keys[1] = 0; SetKeyboardState(keys);
            SendMessage(source.Handle, 0x202, 0, coordinates); Pump();
        }
        finally { SetKeyboardState(original); if (GetCapture() == source.Handle) ReleaseCapture(); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static nint Pack(Point point)=>(nint)(((int)Math.Round(point.X)&0xffff)|(((int)Math.Round(point.Y)&0xffff)<<16));
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
}
