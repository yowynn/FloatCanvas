using System.Text.Json;
using System.IO;
using System.Reflection;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class Feedback4GeometryTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var tool in new[] { EditorTool.Freehand, EditorTool.Highlighter })
        {
            var session = new EditorSession(); var editor = new EditorController(session) { Tool = tool };
            Gesture(editor, new(40, 50), new(140, 90));
            check(tool + " commits an unselected stroke", session.Document.Elements.Count == 1 && session.Selection.Count == 0 && session.UndoCount == 1);
            Gesture(editor, new(80, 65), new(160, 100));
            check(tool + " can draw across a preceding stroke without moving it", session.Document.Elements.Count == 2 && session.Selection.Count == 0 && session.Document.Elements[0].Transform == Affine2D.Translation(40, 50));
        }
        foreach (var tool in new[] { EditorTool.Line, EditorTool.Arrow, EditorTool.DoubleArrow })
        {
            var session = new EditorSession(); var editor = new EditorController(session) { Tool = tool };
            Gesture(editor, new(50, 50), new(50, 50));
            check(tool + " requires a drag and does not insert a default line on click", session.Document.Elements.Count == 0 && session.UndoCount == 0);
        }
        foreach (var symbol in Enum.GetValues<SymbolKind>())
        {
            var square = SymbolGeometry.Outline(symbol, 100, 100);
            var wide = SymbolGeometry.Outline(symbol, 240, 50);
            check(symbol + " scales each normalized coordinate by the requested width and height", square.Zip(wide).All(p =>
                Math.Abs(p.Second.X - p.First.X * 2.4) < 1e-8 && Math.Abs(p.Second.Y - p.First.Y * .5) < 1e-8));
        }
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Symbol })
        {
            var element = new CanvasElement { Kind = kind, Width = 100, Height = 60, CornerRadius = 12, Symbol = SymbolKind.Heart,
                Transform = Affine2D.Translation(100, 100), Style = new() { StrokeWidth = 4 } };
            var session = new EditorSession(); session.Add(element);
            var editor = new EditorController(session) { Tool = EditorTool.Select };
            string before = JsonSerializer.Serialize(session.Document); var bounds = session.SelectionBounds()!.Value;
            var from = new PointD(bounds.Right, bounds.Bottom);
            Gesture(editor, from, from + new PointD(50, 0));
            var resized = session.Document.Elements.Single();
            check(kind + " changes width independently without distorting its stroke matrix", resized.Width > 190 && Math.Abs(resized.Height - 60) < 1e-8 &&
                Math.Abs(resized.Transform.M11 - 1) < 1e-8 && Math.Abs(resized.Transform.M22 - 1) < 1e-8 && resized.Style.StrokeWidth == 4);
            check(kind + " preserves explicit corner radius and has an exact undo", resized.CornerRadius == 12 && session.Undo() && JsonSerializer.Serialize(session.Document) == before);
        }
        var numbers = new EditorSession(); var numberEditor = new EditorController(numbers) { Tool = EditorTool.Number };
        numberEditor.Style.FontSize = 64;
        Gesture(numberEditor, new(200, 200), new(200, 200));
        var number = numbers.Document.Elements.Single();
        check("number font size determines its insertion bounds", Math.Abs(number.Width - 64 / .58) < 1e-8 && number.Height == number.Width && number.Style.FontSize == 64);
        NumberRules(check);
        ShapeGeometry(check);
        InspectorGeometry(check);
    }

    private static void NumberRules(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-feedback4-geometry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var parameters = new ToolParameters { Tool = EditorTool.Number, NumberStart = 7, NumberStep = 3 };
            var presets = new ToolPresetLibrary(Path.Combine(folder, "presets.json")); presets.Save("Counting", parameters);
            var loaded = new ToolPresetLibrary(Path.Combine(folder, "presets.json")).User.Single().Parameters;
            check("number start and step persist in independent tool presets", loaded.NumberStart == 7 && loaded.NumberStep == 3 && !JsonSerializer.Serialize(loaded).Contains("NextNumber", StringComparison.Ordinal));
            var legacy = JsonSerializer.Deserialize<ToolParameters>("{\"Tool\":11}")!;
            check("older tool snapshots gain default sequence rules", legacy.IsValid() && legacy.NumberStart == 1 && legacy.NumberStep == 1);
            check("sequence rules reject invalid starts and steps", !new ToolParameters { NumberStart = 0 }.IsValid() && !new ToolParameters { NumberStep = 0 }.IsValid());
            var session = new EditorSession(new() { NextNumber = loaded.NumberStart }); var editor = new EditorController(session); editor.ApplyParameters(loaded);
            Gesture(editor, new(50, 50), new(50, 50)); Gesture(editor, new(300, 50), new(300, 50));
            check("number insertion advances the document counter by the configured step", session.Document.Elements.Select(e => e.Number).SequenceEqual([7, 10]) && session.Document.NextNumber == 13);
            check("number insertion undoes its object and counter together", session.Undo() && session.Document.Elements.Count == 1 && session.Document.NextNumber == 10);
            check("number insertion redo restores its counter without consulting new tool settings", session.Redo() && session.Document.NextNumber == 13);
            session.Execute("Change next number", document => document.NextNumber = 42);
            var store = new ProjectStore(); string path = Path.Combine(folder, "sequence.floatcanvas");
            store.SaveAsync(path, session.Document, session.Assets).GetAwaiter().GetResult();
            var project = store.LoadAsync(path).GetAwaiter().GetResult();
            check("project roundtrip retains the editable current document number", project.Document.NextNumber == 42 && project.Document.Elements.Select(e => e.Number).SequenceEqual([7, 10]));
            check("editing the current document number is independently undoable", session.Undo() && session.Document.NextNumber == 13 && session.Undo() && session.Document.NextNumber == 10);
            check("tool settings remain independent of document undo and current counter", editor.NumberStart == 7 && editor.NumberStep == 3 && editor.Parameters.NumberStart == 7);
            editor.ApplyParameters(new() { Tool = EditorTool.Text });
            check("other tool presets cannot reset the numbering rules", editor.NumberStart == 7 && editor.NumberStep == 3 && editor.Session.Document.NextNumber == 10);
            editor.ApplyParameters(loaded);
            session.Document.NextNumber = int.MaxValue - 1; session.SetSelection([]);
            Gesture(editor, new(600, 50), new(600, 50));
            check("number increments cannot overflow into invalid negative counters", session.Document.NextNumber == int.MaxValue && session.Document.Elements[^1].Number == int.MaxValue - 1);
        }
        finally
        {
            string absolute = Path.GetFullPath(folder), temporary = Path.GetFullPath(Path.GetTempPath());
            if (!absolute.StartsWith(Path.Combine(temporary, "FloatCanvas-feedback4-geometry-"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture folder");
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static void ShapeGeometry(Action<string, bool> check)
    {
        var shape = new CanvasElement { Kind = ElementKind.RoundedRectangle, Width = 100, Height = 60, CornerRadius = 12,
            Transform = Affine2D.Translation(200, 100).Compose(Affine2D.Rotation(30)).Compose(Affine2D.Scale(2, .5)) };
        var center = shape.Transform.TransformPoint(new(50, 30));
        ElementGeometry.ResizeShapeGeometry(shape, 1.5, 2);
        check("old scaled shapes normalize geometry while preserving the world center and rotation", Math.Abs(shape.Width - 300) < 1e-8 && Math.Abs(shape.Height - 60) < 1e-8 &&
            shape.Transform.TransformPoint(new(shape.Width / 2, shape.Height / 2)).DistanceTo(center) < 1e-8 && Math.Abs(shape.Transform.MaximumScale - 1) < 1e-8);
        check("normalized rounded shapes retain one circular corner radius", Math.Abs(ElementGeometry.CornerRadius(shape) - 6) < 1e-8);
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 60, Transform = Affine2D.Translation(100, 100),
            Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 4 } };
        ElementGeometry.ResizeShapeGeometry(rectangle, 2, 1);
        using var renderer = new SceneRenderer();
        using var bitmap = SKBitmap.Decode(renderer.ExportPng(new() { Viewport = new(0, 0, 400, 260), Elements = [rectangle] }, new(), true));
        int verticalWidth = Enumerable.Range(40, 25).Count(x => bitmap.GetPixel(x, 130).Alpha > 200);
        int horizontalWidth = Enumerable.Range(90, 25).Count(y => bitmap.GetPixel(150, y).Alpha > 200);
        check("nonuniform shape dimensions render equal horizontal and vertical stroke thickness", verticalWidth == 4 && horizontalWidth == 4 && ElementGeometry.HitTest(rectangle, new(50, 130), 0));
    }

    private static void InspectorGeometry(Action<string, bool> check)
    {
        _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var session = new EditorSession(); session.Add(new() { Kind = ElementKind.Rectangle, Width = 100, Height = 60, CornerRadius = 12 });
        var inspector = new InspectorWindow(() => session);
        var fields = (Dictionary<string, System.Windows.Controls.TextBox>)typeof(InspectorWindow).GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!;
        fields["W"].Text = "240";
        typeof(InspectorWindow).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(inspector, null);
        var shape = session.Document.Elements.Single();
        check("numeric shape width editing rebuilds geometry while preserving line and corner sizes", shape.Width == 240 && shape.Height == 60 && shape.Transform.M11 == 1 && shape.Style.StrokeWidth == 4 && shape.CornerRadius == 12);
        session.ReplaceDocument(new() { Elements = [new() { Kind = ElementKind.Number, Width = 40, Height = 40, Style = new() { FontSize = 24 } }] });
        session.SelectAll(); inspector.Refresh(); fields["FontSize"].Text = "64";
        typeof(InspectorWindow).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(inspector, null);
        var number = session.Document.Elements.Single();
        check("existing number font editing enlarges its circle in the same undoable operation", Math.Abs(number.Width - 64 / .58) < 1e-8 && number.Height == number.Width && number.Style.FontSize == 64 && session.UndoCount == 1);
    }

    private static void Gesture(EditorController editor, PointD from, PointD to)
    {
        foreach (var (kind, point) in new[] { (OverlayPointerKind.Down, from), (OverlayPointerKind.Move, to), (OverlayPointerKind.Up, to) })
            editor.Pointer(new(kind, new(point.X, point.Y), OverlayMouseButton.Left, OverlayModifiers.None));
    }
}
