using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

/// <summary>Detached property edit seams plus nonactivating native visibility and notification checks.</summary>
internal static class InspectorTests
{
    public static void Run(Action<string, bool> assert)
    {
        _ = Wpf.Application.Current ?? new Wpf.Application { ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown };
        AppTheme.Apply("light");
        InspectorVisibilityLifecycleTests.Run(assert);
        bool allowed = true;
        var session = new EditorSession(new() { Viewport = new(0, 0, 300, 400) });
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "浮绘", Width = 100, Height = 27,
            Transform = Affine2D.Translation(20, 20), Style = new() { FontSize = 20 } };
        session.Add(text);
        var inspector = new InspectorWindow(() => session, () => allowed); inspector.PrepareForShow();
        var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
        var layerWindow = new LayersWindow(() => session, () => allowed);
        layerWindow.PrepareForShow();
        var layers = (Controls.ListBox)Field(layerWindow, "layers");
        const string draft = "浮绘\n编辑\n多行\n修改";
        int undoBefore = session.UndoCount;
        using (var layerSurface = new LayersTestSurface(layerWindow))
        {
            assert("busy layer guards exercise a visible offscreen native window", layerWindow.IsVisible && layerSurface.Handle != 0);
            allowed = false;
            fields["Text"].Text = draft;
            Invoke(inspector, "Apply");
            assert("busy inspector refuses property mutation and retains draft", session.Document.Elements[0].Text == "浮绘" && fields["Text"].Text == draft && session.UndoCount == undoBefore);
            Invoke(layerWindow, "SetLayerFlag", text.Id, true, false);
            Invoke(layerWindow, "SetLayerFlag", text.Id, false, true);
            assert("busy inspector refuses visibility and lock mutation", session.Document.Elements[0].Visible && !session.Document.Elements[0].Locked && session.UndoCount == undoBefore);
            layers.SelectedItems.Clear();
            assert("busy inspector rejects and restores layer selection", session.Selection.SetEquals([text.Id]) && layers.SelectedItems.Count == 1 && fields["Text"].Text == draft);
        }
        inspector.PrepareForShow();
        assert("busy refresh preserves unsaved input", fields["Text"].Text == draft);
        allowed = true;
        Invoke(inspector, "Apply");
        var applied = session.Document.Elements[0];
        using var renderer = new SceneRenderer();
        var measured = renderer.MeasureText(draft, applied.Style, applied.Width);
        assert("text property edit refreshes local height at existing width", applied.Text == draft && applied.Width == 100 && applied.Height == measured.Height && applied.Height > text.Height * 3);
        assert("text and measured height commit in one undo step", session.UndoCount == undoBefore + 1);
        var lowerRegion = new RectD(20, 20 + applied.Height - 30, 100, 30);
        assert("new lower text lines are hittable and retained by capture intersection", ElementGeometry.HitTest(applied, new(50, 20 + applied.Height - 5)) && session.Document.CreateCapture(lowerRegion, "background").Elements.Count == 1);
        using (var png = SKBitmap.Decode(renderer.ExportPng(session.Document, session.Assets, true)))
        {
            int count = 0;
            for (int y = (int)lowerRegion.Top; y < (int)lowerRegion.Bottom; y++) for (int x = 20; x < 120; x++) if (png.GetPixel(x, y).Alpha > 0) count++;
            assert("new lower text lines rasterize inside updated geometry", count > 40);
        }
        session.Undo();
        assert("single undo restores text and local height together", session.Document.Elements[0].Text == "浮绘" && session.Document.Elements[0].Height == 27);
        session.Redo(); inspector.PrepareForShow();
        fields["FontSize"].Text = "40"; int beforeFont = session.UndoCount;
        Invoke(inspector, "Apply"); applied = session.Document.Elements[0];
        assert("font size edit remeasures wrapped text in same transaction", applied.Style.FontSize == 40 && applied.Height == renderer.MeasureText(applied.Text, applied.Style, applied.Width).Height && session.UndoCount == beforeFont + 1);
        fields["FontFamily"].Text = "Arial";
        Invoke(inspector, "Apply"); applied = session.Document.Elements[0];
        assert("font family edit remeasures fallback glyph layout", applied.Style.FontFamily == "Arial" && applied.Height == renderer.MeasureText(applied.Text, applied.Style, applied.Width).Height);
        assert("detached inspector tests create no native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && !inspector.IsVisible);
        LayersWindowTests.Run(assert);
        ContextualFields(assert);
        LocalSizeEditing(assert);
        CommandRouting(assert);
        ArrowProperties(assert);
        InspectorLayerPanelTests.Run(assert);
        InspectorScrubTests.Run(assert);
        InspectorLiveTests.Run(assert);
        InspectorValidationLifecycleTests.Run(assert);
        InspectorColorTests.Run(assert);
        SymbolNames(assert);
        ContinuousPanelLayout(assert);
        BillboardInspectorTests.Run(assert);
    }

    private sealed class LayersTestSurface : IDisposable
    {
        private readonly LayersWindow window;
        internal nint Handle { get; }
        internal LayersTestSurface(LayersWindow window)
        {
            this.window = window;
            window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0; window.Topmost = false;
            window.Left = window.Top = -20000; window.Show();
            Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Pump(); window.Left = window.Top = -20000;
        }
        public void Dispose()
        {
            window.Hide(); System.Windows.Interop.HwndSource.FromHwnd(Handle)?.Dispose(); Pump();
        }
        private static void Pump()
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static void LocalSizeEditing(Action<string, bool> assert)
    {
        foreach (double angle in new[] { 30d, 45d })
        {
            var session = new EditorSession();
            var original = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(80, 90).Compose(Affine2D.Rotation(angle)) };
            session.Add(original); var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
            var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
            assert($"{angle} degree inspector displays local-axis size without stroke", double.Parse(fields["W"].Text) == 100 && double.Parse(fields["H"].Text) == 60);
            var center = original.Transform.TransformPoint(new(50, 30)); int before = session.UndoCount;
            fields["W"].Text = "200"; Invoke(inspector, "Apply");
            var t = session.Document.Elements[0].Transform;
            assert($"{angle} degree width-only property edit retains orthogonal rotation", Math.Abs(t.M11 * t.M12 + t.M21 * t.M22) < 1e-8 && Math.Abs(Math.Atan2(t.M21, t.M11) * 180 / Math.PI - angle) < 1e-8);
            var resized = session.Document.Elements[0];
            assert($"{angle} degree width-only property edit preserves center and untouched axis", resized.Width == 200 && resized.Height == 60 && Math.Abs(Math.Sqrt(t.M11 * t.M11 + t.M21 * t.M21) - 1) < 1e-8 && Math.Abs(Math.Sqrt(t.M12 * t.M12 + t.M22 * t.M22) - 1) < 1e-8 && t.TransformPoint(new(100, 30)).DistanceTo(center) < 1e-8);
            assert($"{angle} degree local resize uses one undo step", session.UndoCount == before + 1 && session.Undo() && session.Document.Elements[0].Transform == original.Transform);
        }
        var pathSession = new EditorSession();
        var path = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(-50, -20), new(0, 40), new(50, 10)], Transform = Affine2D.Translation(100, 80).Compose(Affine2D.Rotation(45)) };
        pathSession.Add(path); var pathInspector = new InspectorWindow(() => pathSession); pathInspector.PrepareForShow();
        var pathFields = (Dictionary<string, Controls.TextBox>)Field(pathInspector, "fields");
        pathFields["W"].Text = "180"; Invoke(pathInspector, "Apply"); var edited = pathSession.Document.Elements[0];
        assert("negative local path bounds resize preserves points and local center", edited.Points.SequenceEqual(path.Points) && edited.Transform.TransformPoint(new(0, 10)).DistanceTo(path.Transform.TransformPoint(new(0, 10))) < 1e-8 && Math.Abs(Math.Sqrt(edited.Transform.M11 * edited.Transform.M11 + edited.Transform.M21 * edited.Transform.M21) - 1.8) < 1e-8);
        var lineSession = new EditorSession(); lineSession.Add(new() { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 0)], Style = new() { StrokeWidth = 12 } });
        var lineInspector = new InspectorWindow(() => lineSession); lineInspector.PrepareForShow(); var lineFields = (Dictionary<string, Controls.TextBox>)Field(lineInspector, "fields");
        assert("horizontal line geometric height excludes visible stroke", lineFields["H"].Text == "0");
        int undo = lineSession.UndoCount; lineFields["H"].Text = "50"; Invoke(lineInspector, "Apply");
        assert("zero local size rejects resize and preserves property draft", lineSession.UndoCount == undo && lineFields["H"].Text == "50" && ((Controls.TextBlock)Field(lineInspector, "status")).Text.Contains("端点"));
    }

    private static void CommandRouting(Action<string, bool> assert)
    {
        bool allowed = true; var inspector = new InspectorWindow(() => new EditorSession(), () => allowed); inspector.PrepareForShow();
        var keys = new List<int>(); inspector.CommandKey += keys.Add;
        bool Route(int key, bool ctrl, bool text) => (bool)inspector.GetType().GetMethod("RouteCommandKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(inspector, [key, ctrl, text])!;
        foreach (int key in new[] { 0x5A, 0x59, 0x41, 0x43, 0x56, 0x44 }) assert("layer context forwards Ctrl command " + key, Route(key, true, false) && keys[^1] == key);
        assert("layer context forwards Delete", Route(0x2E, false, false) && keys[^1] == 0x2E);
        int count = keys.Count;
        assert("text input retains its editing chords and Delete", new[] { 0x5A, 0x59, 0x41, 0x43, 0x56, 0x44 }.All(key => !Route(key, true, true)) && !Route(0x2E, false, true) && keys.Count == count);
        assert("text input forwards project Save and Open", Route(0x53, true, true) && Route(0x4F, true, true) && keys.TakeLast(2).SequenceEqual([0x53, 0x4F]));
        assert("nonediting property controls forward object movement arrows", Route(0x25, false, false) && Route(0x28, false, false));
        foreach (var source in new Wpf.DependencyObject[] { new Controls.TextBox(), new Controls.ComboBox(), new Controls.Slider(), new Controls.Primitives.Thumb(), new Controls.MenuItem() })
            assert(source.GetType().Name + " retains local Enter and deletion instead of canvas commands", InspectorWindow.OwnsLocalEditingKeys(source) && !Route(0x0D, false, true) && !Route(0x08, true, true) && !Route(0x2E, true, true));
        allowed = false; count = keys.Count;
        assert("busy inspector consumes project and object commands without forwarding", Route(0x53, true, true) && Route(0x2E, false, false) && keys.Count == count);
    }

    private static void ArrowProperties(Action<string, bool> assert)
    {
        foreach (var style in Enum.GetValues<ArrowHeadStyle>())
        {
            var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Arrow, Points = [new(0, 0), new(120, 40)] });
            var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
            var containers = (Dictionary<string, Wpf.FrameworkElement>)Field(inspector, "fieldContainers");
            var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
            var picker = (Controls.ComboBox)Field(inspector, "arrowStyle");
            assert(style + " arrow properties are visible", containers["ArrowHeadStyle"].Visibility == Wpf.Visibility.Visible && containers["ArrowHeadSize"].Visibility == Wpf.Visibility.Visible);
            int undo = session.UndoCount; picker.SelectedIndex = (int)style; fields["ArrowHeadSize"].Text = "24"; Invoke(inspector, "Apply");
            assert(style + " arrow style and size apply atomically", session.Document.Elements[0].Style.ArrowHeadStyle == style && session.Document.Elements[0].Style.ArrowHeadSize == 24 && session.UndoCount == undo + 1);
            fields["ArrowHeadSize"].Text = "0"; Invoke(inspector, "Apply");
            assert(style + " arrow size can return to automatic", session.Document.Elements[0].Style.ArrowHeadSize == 0);
            fields["ArrowHeadSize"].Text = "513"; undo = session.UndoCount; Invoke(inspector, "Apply");
            assert(style + " invalid arrow size keeps draft and document", session.Document.Elements[0].Style.ArrowHeadSize == 0 && session.UndoCount == undo && fields["ArrowHeadSize"].Text == "513");
        }
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.Image, ElementKind.Text, ElementKind.Line })
        {
            var session = new EditorSession(); session.Add(new() { Kind = kind, Width = 100, Height = 60 }); var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
            var containers = (Dictionary<string, Wpf.FrameworkElement>)Field(inspector, "fieldContainers");
            assert(kind + " hides legacy arrow-style control", containers["ArrowHeadStyle"].Visibility == Wpf.Visibility.Collapsed);
            assert(kind + " shows arrow size only for line families", containers["ArrowHeadSize"].Visibility == (kind == ElementKind.Line ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed));
        }
    }

    private static void SymbolNames(Action<string, bool> assert)
    {
        string preference = FloatCanvas.Localization.AppText.Preference;
        try
        {
            foreach (string language in new[] { "zh-CN", "en" })
            {
                FloatCanvas.Localization.AppText.Initialize(language);
                assert(language + " symbol layers and properties have a recognized name", Enum.GetValues<SymbolKind>().All(symbol =>
                    InspectorWindow.ElementName(new() { Kind = ElementKind.Symbol, Symbol = symbol }) == FloatCanvas.Localization.AppText.Get("Editor.ElementKind.Symbol")));
            }
        }
        finally { FloatCanvas.Localization.AppText.Initialize(preference); }
    }

    private static void ContextualFields(Action<string, bool> assert)
    {
        var session = new EditorSession();
        var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
        var containers = (Dictionary<string, Wpf.FrameworkElement>)Field(inspector, "fieldContainers");
        foreach (var kind in new[] { ElementKind.Image, ElementKind.Rectangle, ElementKind.Text, ElementKind.Number })
        {
            session.Add(new() { Kind = kind, Width = 100, Height = 50, Text = "浮绘" }); inspector.PrepareForShow();
            bool Visible(string key) => containers[key].Visibility == Wpf.Visibility.Visible;
            assert(kind + " keeps common geometry and opacity fields", new[] { "X", "Y", "W", "H", "Angle", "Opacity" }.All(Visible));
            if (kind == ElementKind.Image)
                assert("image inspector collapses irrelevant style and text rows", new[] { "Stroke", "Fill", "StrokeWidth", "FontFamily", "FontSize", "TextAlignment", "Text", "Number" }.All(key => !Visible(key)));
            if (kind == ElementKind.Rectangle)
                assert("shape inspector keeps paint and collapses text properties", Visible("Stroke") && Visible("Fill") && Visible("StrokeWidth") && new[] { "FontFamily", "FontSize", "TextAlignment", "Text", "Number" }.All(key => !Visible(key)));
            if (kind == ElementKind.Text)
                assert("text inspector shows fonts alignment and content without number", Visible("FontFamily") && Visible("FontSize") && Visible("TextAlignment") && Visible("Text") && !Visible("Number"));
            if (kind == ElementKind.Number)
                assert("number inspector shows font family and number while badge geometry controls size", Visible("FontFamily") && !Visible("FontSize") && Visible("Number") && !Visible("Text") && !Visible("TextAlignment"));
        }
        assert("contextual property layout remains detached", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
    }

    private static void ContinuousPanelLayout(Action<string, bool> assert)
    {
        var session = new EditorSession();
        session.Add(new() { Kind = ElementKind.Arrow, Points = [new(0, 0), new(120, 40)] });
        var inspector = new InspectorWindow(() => session); inspector.PrepareForShow();
        var layout = (Controls.Grid)inspector.Content;
        var scroll = LogicalDescendants(layout).OfType<Controls.ScrollViewer>().Single();
        assert("live inspector has no apply or reload actions", !LogicalDescendants(layout).OfType<Controls.Button>().Any(button => new[] { FloatCanvas.Localization.AppText.Get("Inspector.Apply"), FloatCanvas.Localization.AppText.Get("Inspector.ReloadSelection") }.Contains(System.Windows.Automation.AutomationProperties.GetName(button))));
        var status = (Controls.TextBlock)Field(inspector, "status");
        assert("property sections are directly available without category tabs or expanders", !LogicalDescendants(layout).Any(control => control is Controls.Expander or Controls.TabControl));
        var scrollingControls = LogicalDescendants(scroll).ToHashSet();
        assert("property status is outside the scrolling content", !scrollingControls.Contains(status));

        var size = new Wpf.Size(inspector.MinWidth, inspector.MinHeight - System.Windows.Shell.WindowChrome.GetWindowChrome(inspector).CaptionHeight);
        layout.Measure(size); layout.Arrange(new Wpf.Rect(new Wpf.Point(), size)); layout.UpdateLayout();
        var before = status.TranslatePoint(new Wpf.Point(), layout);
        assert("small property panel keeps its status inside its bounds", new Wpf.FrameworkElement[] { status }.All(InBounds));
        assert("small property panel scrolls long applicable fields", scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0);
        scroll.ScrollToEnd(); layout.UpdateLayout();
        assert("scrolling long properties leaves the status stationary", status.TranslatePoint(new Wpf.Point(), layout) == before && scroll.VerticalOffset > 0);
        assert("property layout checks create no native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);

        bool InBounds(Wpf.FrameworkElement control)
        {
            var origin = control.TranslatePoint(new Wpf.Point(), layout);
            return control.ActualWidth > 0 && control.ActualHeight > 0 && origin.X >= 0 && origin.Y >= 0 &&
                origin.X + control.ActualWidth <= size.Width + .5 && origin.Y + control.ActualHeight <= size.Height + .5;
        }
    }


    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Invoke(object instance, string method, params object[] arguments) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
    private static IEnumerable<Wpf.DependencyObject> LogicalDescendants(Wpf.DependencyObject parent)
    {
        foreach (var child in Wpf.LogicalTreeHelper.GetChildren(parent).OfType<Wpf.DependencyObject>())
        {
            yield return child;
            foreach (var nested in LogicalDescendants(child)) yield return nested;
        }
    }
}
