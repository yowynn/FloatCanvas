using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

/// <summary>Real inspector handlers exercised on detached controls; never calls Show or creates an HWND.</summary>
internal static class InspectorTests
{
    public static void Run(Action<string, bool> assert)
    {
        bool allowed = true;
        var session = new EditorSession(new() { Viewport = new(0, 0, 300, 400) });
        var text = new CanvasElement { Kind = ElementKind.Text, Text = "浮绘", Width = 100, Height = 27,
            Transform = Affine2D.Translation(20, 20), Style = new() { FontSize = 20 } };
        session.Add(text);
        var inspector = new InspectorWindow(() => session, () => allowed);
        var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
        var layers = (Controls.ListBox)Field(inspector, "layers");
        const string draft = "浮绘\n编辑\n多行\n修改";
        fields["Text"].Text = draft;
        int undoBefore = session.UndoCount;
        allowed = false;
        Invoke(inspector, "Apply");
        assert("busy inspector refuses property mutation and retains draft", session.Document.Elements[0].Text == "浮绘" && fields["Text"].Text == draft && session.UndoCount == undoBefore);
        Invoke(inspector, "SetLayerFlag", text.Id, true, false);
        Invoke(inspector, "SetLayerFlag", text.Id, false, true);
        assert("busy inspector refuses visibility and lock mutation", session.Document.Elements[0].Visible && !session.Document.Elements[0].Locked && session.UndoCount == undoBefore);
        layers.SelectedItems.Clear();
        assert("busy inspector rejects and restores layer selection", session.Selection.SetEquals([text.Id]) && layers.SelectedItems.Count == 1 && fields["Text"].Text == draft);
        var reload = LogicalDescendants(inspector).OfType<Controls.Button>().Single(b => Equals(b.Content, "重载当前选择"));
        reload.RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        inspector.Refresh();
        assert("busy reload and refresh preserve unsaved input", fields["Text"].Text == draft);
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
        session.Redo(); inspector.Refresh();
        fields["FontSize"].Text = "40"; int beforeFont = session.UndoCount;
        Invoke(inspector, "Apply"); applied = session.Document.Elements[0];
        assert("font size edit remeasures wrapped text in same transaction", applied.Style.FontSize == 40 && applied.Height == renderer.MeasureText(applied.Text, applied.Style, applied.Width).Height && session.UndoCount == beforeFont + 1);
        fields["FontFamily"].Text = "Arial";
        Invoke(inspector, "Apply"); applied = session.Document.Elements[0];
        assert("font family edit remeasures fallback glyph layout", applied.Style.FontFamily == "Arial" && applied.Height == renderer.MeasureText(applied.Text, applied.Style, applied.Width).Height);
        assert("detached inspector tests create no native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && !inspector.IsVisible);
        LayerCommands(assert);
        ContextualFields(assert);
        LocalSizeEditing(assert);
        CommandRouting(assert);
        ArrowProperties(assert);
    }

    private static void LocalSizeEditing(Action<string, bool> assert)
    {
        foreach (double angle in new[] { 30d, 45d })
        {
            var session = new EditorSession();
            var original = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(80, 90).Compose(Affine2D.Rotation(angle)) };
            session.Add(original); var inspector = new InspectorWindow(() => session);
            var fields = (Dictionary<string, Controls.TextBox>)Field(inspector, "fields");
            assert($"{angle} degree inspector displays local-axis size without stroke", double.Parse(fields["W"].Text) == 100 && double.Parse(fields["H"].Text) == 60);
            var center = original.Transform.TransformPoint(new(50, 30)); int before = session.UndoCount;
            fields["W"].Text = "200"; Invoke(inspector, "Apply");
            var t = session.Document.Elements[0].Transform;
            assert($"{angle} degree width-only property edit retains orthogonal rotation", Math.Abs(t.M11 * t.M12 + t.M21 * t.M22) < 1e-8 && Math.Abs(Math.Atan2(t.M21, t.M11) * 180 / Math.PI - angle) < 1e-8);
            assert($"{angle} degree width-only property edit preserves center and untouched axis", Math.Abs(Math.Sqrt(t.M11 * t.M11 + t.M21 * t.M21) - 2) < 1e-8 && Math.Abs(Math.Sqrt(t.M12 * t.M12 + t.M22 * t.M22) - 1) < 1e-8 && t.TransformPoint(new(50, 30)).DistanceTo(center) < 1e-8);
            assert($"{angle} degree local resize uses one undo step", session.UndoCount == before + 1 && session.Undo() && session.Document.Elements[0].Transform == original.Transform);
        }
        var pathSession = new EditorSession();
        var path = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(-50, -20), new(0, 40), new(50, 10)], Transform = Affine2D.Translation(100, 80).Compose(Affine2D.Rotation(45)) };
        pathSession.Add(path); var pathInspector = new InspectorWindow(() => pathSession);
        var pathFields = (Dictionary<string, Controls.TextBox>)Field(pathInspector, "fields");
        pathFields["W"].Text = "180"; Invoke(pathInspector, "Apply"); var edited = pathSession.Document.Elements[0];
        assert("negative local path bounds resize preserves points and local center", edited.Points.SequenceEqual(path.Points) && edited.Transform.TransformPoint(new(0, 10)).DistanceTo(path.Transform.TransformPoint(new(0, 10))) < 1e-8 && Math.Abs(Math.Sqrt(edited.Transform.M11 * edited.Transform.M11 + edited.Transform.M21 * edited.Transform.M21) - 1.8) < 1e-8);
        var lineSession = new EditorSession(); lineSession.Add(new() { Kind = ElementKind.Line, Points = [new(0, 0), new(100, 0)], Style = new() { StrokeWidth = 12 } });
        var lineInspector = new InspectorWindow(() => lineSession); var lineFields = (Dictionary<string, Controls.TextBox>)Field(lineInspector, "fields");
        assert("horizontal line geometric height excludes visible stroke", lineFields["H"].Text == "0");
        int undo = lineSession.UndoCount; lineFields["H"].Text = "50"; Invoke(lineInspector, "Apply");
        assert("zero local size rejects resize and preserves property draft", lineSession.UndoCount == undo && lineFields["H"].Text == "50" && ((Controls.TextBlock)Field(lineInspector, "status")).Text.Contains("端点"));
    }

    private static void CommandRouting(Action<string, bool> assert)
    {
        bool allowed = true; var inspector = new InspectorWindow(() => new EditorSession(), () => allowed);
        var keys = new List<int>(); inspector.CommandKey += keys.Add;
        bool Route(int key, bool ctrl, bool text) => (bool)inspector.GetType().GetMethod("RouteCommandKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(inspector, [key, ctrl, text])!;
        foreach (int key in new[] { 0x5A, 0x59, 0x41, 0x43, 0x56, 0x44 }) assert("layer context forwards Ctrl command " + key, Route(key, true, false) && keys[^1] == key);
        assert("layer context forwards Delete", Route(0x2E, false, false) && keys[^1] == 0x2E);
        int count = keys.Count;
        assert("text input retains its editing chords and Delete", new[] { 0x5A, 0x59, 0x41, 0x43, 0x56, 0x44 }.All(key => !Route(key, true, true)) && !Route(0x2E, false, true) && keys.Count == count);
        assert("text input forwards project Save and Open", Route(0x53, true, true) && Route(0x4F, true, true) && keys.TakeLast(2).SequenceEqual([0x53, 0x4F]));
        assert("layer arrows retain native navigation", !Route(0x25, false, false) && !Route(0x28, false, false));
        allowed = false; count = keys.Count;
        assert("busy inspector consumes project and object commands without forwarding", Route(0x53, true, true) && Route(0x2E, false, false) && keys.Count == count);
    }

    private static void ArrowProperties(Action<string, bool> assert)
    {
        foreach (var style in Enum.GetValues<ArrowHeadStyle>())
        {
            var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Arrow, Points = [new(0, 0), new(120, 40)] });
            var inspector = new InspectorWindow(() => session);
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
            var session = new EditorSession(); session.Add(new() { Kind = kind, Width = 100, Height = 60 }); var inspector = new InspectorWindow(() => session);
            var containers = (Dictionary<string, Wpf.FrameworkElement>)Field(inspector, "fieldContainers");
            assert(kind + " hides arrow-only controls", containers["ArrowHeadStyle"].Visibility == Wpf.Visibility.Collapsed && containers["ArrowHeadSize"].Visibility == Wpf.Visibility.Collapsed);
        }
    }

    private static void ContextualFields(Action<string, bool> assert)
    {
        var session = new EditorSession();
        var inspector = new InspectorWindow(() => session);
        var containers = (Dictionary<string, Wpf.FrameworkElement>)Field(inspector, "fieldContainers");
        foreach (var kind in new[] { ElementKind.Image, ElementKind.Rectangle, ElementKind.Text, ElementKind.Number })
        {
            session.Add(new() { Kind = kind, Width = 100, Height = 50, Text = "浮绘" }); inspector.Refresh();
            bool Visible(string key) => containers[key].Visibility == Wpf.Visibility.Visible;
            assert(kind + " keeps common geometry and opacity fields", new[] { "X", "Y", "W", "H", "Angle", "Opacity" }.All(Visible));
            if (kind == ElementKind.Image)
                assert("image inspector collapses irrelevant style and text rows", new[] { "Stroke", "Fill", "StrokeWidth", "FontFamily", "FontSize", "TextAlignment", "Text", "Number" }.All(key => !Visible(key)));
            if (kind == ElementKind.Rectangle)
                assert("shape inspector keeps paint and collapses text properties", Visible("Stroke") && Visible("Fill") && Visible("StrokeWidth") && new[] { "FontFamily", "FontSize", "TextAlignment", "Text", "Number" }.All(key => !Visible(key)));
            if (kind == ElementKind.Text)
                assert("text inspector shows fonts alignment and content without number", Visible("FontFamily") && Visible("FontSize") && Visible("TextAlignment") && Visible("Text") && !Visible("Number"));
            if (kind == ElementKind.Number)
                assert("number inspector shows fonts and number without text alignment", Visible("FontFamily") && Visible("FontSize") && Visible("Number") && !Visible("Text") && !Visible("TextAlignment"));
        }
        assert("contextual property layout remains detached", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0);
    }

    private static void LayerCommands(Action<string, bool> assert)
    {
        bool allowed = true;
        var session = new EditorSession();
        CanvasElement a = new() { Kind = ElementKind.Rectangle, Width = 40, Height = 40 },
            b = new() { Kind = ElementKind.Ellipse, Width = 40, Height = 40 },
            c = new() { Kind = ElementKind.Triangle, Width = 40, Height = 40 };
        session.Add(a); session.Add(b); session.Add(c); session.SetSelection([a.Id]);
        var inspector = new InspectorWindow(() => session, () => allowed);
        var buttons = LogicalDescendants(inspector).OfType<Controls.Button>().ToDictionary(button => button.Content!.ToString()!, StringComparer.Ordinal);
        void Click(string label) => buttons[label].RaiseEvent(new Wpf.RoutedEventArgs(Controls.Button.ClickEvent));
        int before = session.UndoCount;
        allowed = false;
        foreach (var label in new[] { "前移", "后移", "置顶", "置底", "复制", "删除" }) Click(label);
        assert("busy guard covers every layer action button", session.UndoCount == before && session.Document.Elements.Select(e => e.Id).SequenceEqual([a.Id, b.Id, c.Id]) && session.Selection.SetEquals([a.Id]));
        allowed = true;
        Click("前移"); assert("layer forward button records one atomic step", session.Document.Elements[1].Id == a.Id && session.UndoCount == ++before);
        Click("后移"); assert("layer backward button records one atomic step", session.Document.Elements[0].Id == a.Id && session.UndoCount == ++before);
        Click("置顶"); assert("layer front button records one atomic step", session.Document.Elements[^1].Id == a.Id && session.UndoCount == ++before);
        Click("置底"); assert("layer back button records one atomic step", session.Document.Elements[0].Id == a.Id && session.UndoCount == ++before);
        Click("复制");
        var clone = session.SelectedElements().Single();
        assert("duplicate layer button creates editable independent selection in one step", clone.Id != a.Id && clone.Kind == a.Kind && clone.Transform.OffsetX == 16 && session.Document.Elements.Count == 4 && session.UndoCount == ++before);
        Click("删除"); assert("delete layer button removes selection in one step", session.Document.Elements.Count == 3 && session.Selection.Count == 0 && session.UndoCount == ++before);
        session.Undo(); assert("delete layer command undo restores duplicated element and selection", session.Document.Elements.Count == 4 && session.Selection.Contains(clone.Id));
        assert("layer action validation never creates a native window", new System.Windows.Interop.WindowInteropHelper(inspector).Handle == 0 && !inspector.IsVisible);
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
