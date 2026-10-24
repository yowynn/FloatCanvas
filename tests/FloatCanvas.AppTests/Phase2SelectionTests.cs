using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class Phase2SelectionTests
{
    internal static void Run(Action<string, bool> check)
    {
        var session = new EditorSession();
        var editor = new EditorController(session) { Tool = EditorTool.Select };
        var hollow = new CanvasElement { Kind = ElementKind.Ellipse, Width = 200, Height = 200,
            Transform = Affine2D.Translation(100, 100), Style = new() { Fill = 0, Stroke = 0xFF222222, StrokeWidth = 4 } };
        session.Add(hollow); session.SelectAll(); editor.Tool = EditorTool.Stamp;
        check("stamp tool retains the existing selection frame", editor.ShouldShowSelection(OverlayModifiers.None));
        editor.Tool = EditorTool.Select; session.SetSelection([]);
        Click(200, 200);
        check("hollow shape center is not point-selectable", session.Selection.Count == 0);
        Drag(170, 170, 230, 230);
        check("a marquee wholly inside a hollow shape does not select it", session.Selection.Count == 0);

        var text = new CanvasElement { Kind = ElementKind.Text, Text = "I", Width = 220, Height = 60,
            Transform = Affine2D.Translation(100, 100), Style = new() { FontSize = 40, Stroke = 0xFF222222 } };
        Reset(text); Click(290, 120);
        check("empty text-box space is not point-selectable", session.Selection.Count == 0);
        Drag(250, 100, 290, 140);
        check("empty text-box space is not marquee-selectable", session.Selection.Count == 0);

        var stroke = new CanvasElement { Kind = ElementKind.Freehand, Points = [new(0, 0), new(100, 0), new(100, 100)],
            Transform = Affine2D.Translation(100, 100), Style = new() { StrokeWidth = 4 } };
        Reset(stroke); session.SelectAll(); Drag(130, 160, 150, 175);
        check("a selected stroke moves from empty space inside its frame", session.Document.Elements.Single().Transform == Affine2D.Translation(120, 115));
        Reset(stroke.Clone()); session.SelectAll(); editor.Tool = EditorTool.Stamp;
        var beforeStampMove = session.Document.Elements.Single().Transform;
        Drag(150, 165, 170, 180);
        check("stamp tool also moves an existing selection from its frame interior", session.Document.Elements.Single().Transform == Affine2D.Translation(20, 15).Compose(beforeStampMove));
        editor.Tool = EditorTool.Select;

        var rotated = new CanvasElement { Kind = ElementKind.Rectangle, Width = 120, Height = 50,
            Transform = new(.8, -.6, .6, .8, 300, 200) };
        Reset(rotated); session.SelectAll();
        var corners = editor.SelectionScreenCorners;
        check("single planar shape frame follows its transform rotation", corners.Count == 4 && Math.Abs(corners[1].Y - corners[0].Y) > 20);

        using var image = new SKBitmap(64, 64); image.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(image)) canvas.DrawRect(32, 0, 32, 64, new SKPaint { Color = SKColors.Blue });
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var asset = session.Assets.Add(encoded.ToArray());
        var picture = new CanvasElement { Kind = ElementKind.Image, AssetId = asset, Width = 64, Height = 64, Transform = Affine2D.Translation(100, 100) };
        Reset(picture); Click(110, 120);
        check("transparent image pixels are not point-selectable", session.Selection.Count == 0);
        Click(148, 120);
        check("opaque image pixels remain point-selectable", session.Selection.Contains(picture.Id));
        session.SetSelection([]); Drag(100, 100, 120, 140);
        check("transparent image region is not marquee-selectable", session.Selection.Count == 0);
        editor.DetachSession();

        void Reset(CanvasElement element) { editor.Cancel(); session.Execute("selection fixture", document => document.Elements = [element]); session.SetSelection([]); }
        void Click(double x, double y) { Send(OverlayPointerKind.Down, x, y); Send(OverlayPointerKind.Up, x, y); }
        void Drag(double x, double y, double endX, double endY) { Send(OverlayPointerKind.Down, x, y); Send(OverlayPointerKind.Move, endX, endY); Send(OverlayPointerKind.Up, endX, endY); }
        void Send(OverlayPointerKind kind, double x, double y) => editor.PointerForCanvas(new(kind, new(x, y), OverlayMouseButton.Left, OverlayModifiers.None), false);
    }
}
