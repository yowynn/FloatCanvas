using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

internal static class CaptureViewportPlacementTests
{
    internal static void Run(Action<string, bool> check)
    {
        var primary = Monitor("primary", new(0, 0, 1920, 1080), true, 144);
        var secondary = Monitor("secondary", new(-2560, -200, 2560, 1440), false, 192);
        void Case(string name, RectD viewport, PointD origin, MonitorInfo[] active, PointD expected)
        {
            var actual = CaptureViewportPlacement.ResolveOrigin(viewport, origin, active);
            check(name + " resolves the expected physical origin", actual == expected);
            var mapped = Affine2D.Translation(actual.X, actual.Y).TransformBounds(viewport);
            check(name + " leaves positive-area visibility on an active screen", active.Any(m =>
                !mapped.Intersect(new(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height)).IsEmpty));
            check(name + " is stable when repeated", CaptureViewportPlacement.ResolveOrigin(viewport, actual, active) == actual);
        }
        Case("4K crop after primary shrinks", new(3000, 1500, 500, 300), new(0, 0), [primary], new(-2290, -1110));
        Case("Detached negative-coordinate secondary in primary-only scope", new(50, 50, 500, 300), new(-2560, 0), [primary], new(660, 340));
        var negativePrimary = Monitor("negative-primary", new(-1920, -300, 1920, 1080), true, 192);
        Case("Negative physical primary origin", new(3000, 1500, 500, 300), new(0, 0), [negativePrimary], new(-4210, -1410));
        Case("Existing crop on active secondary remains in place", new(50, 50, 500, 300), new(-2560, 0), [primary, secondary], new(-2560, 0));
        Case("Existing cross-screen crop remains in place", new(-1500, 100, 3000, 800), new(0, 0), [primary, secondary], new(0, 0));
        Case("Partly visible crop keeps its original mapping", new(1900, 900, 500, 300), new(0, 0), [primary], new(0, 0));
        Case("Edge contact without visible area is remapped", new(1920, 1080, 500, 300), new(0, 0), [primary], new(-1210, -690));
        Case("Oversized crop gains an accessible central portion without scaling", new(3000, 0, 4000, 2500), new(0, 0), [primary], new(-4040, -710));
        Case("Primary identity wins over enumeration order", new(5000, 3000, 500, 300), new(0, 0), [secondary, primary], new(-4290, -2610));
        Case("The sole active screen is a valid fallback", new(5000, 3000, 500, 300), new(0, 0), [secondary], new(-6530, -2630));

        var document = new SceneDocument { Kind = DocumentKind.FrozenCapture, Viewport = new(3000, 1500, 500, 300),
            Background = new() { Kind = ElementKind.Image, AssetId = "original-image", Width = 3840, Height = 2160,
                ImageCrop = new(.1, .2, .7, .6), Locked = true },
            Elements = [new() { Kind = ElementKind.Text, Width = 120, Height = 60, Text = "Keep geometry", Transform = Affine2D.Translation(3100, 1600).Compose(Affine2D.Rotation(30)) }] };
        var session = new EditorSession(document);
        session.SetSelection([document.Elements[0].Id]);
        session.TransformSelection(Affine2D.Translation(1, 2));
        session.Undo();
        string before = JsonSerializer.Serialize(session.Document);
        int undo = session.UndoCount, redo = session.RedoCount;
        var origin = CaptureViewportPlacement.ResolveOrigin(session.Document.Viewport, new(0, 0), [primary]);
        check("Viewport remapping leaves crop, original asset and all document geometry unchanged", before == JsonSerializer.Serialize(session.Document));
        check("Viewport remapping leaves undo, redo and selection intact", session.UndoCount == undo && session.RedoCount == redo && session.Selection.SetEquals([document.Elements[0].Id]));
        check("Centered mapping uses physical pixels independently of DPI", origin == CaptureViewportPlacement.CenterOrigin(session.Document.Viewport, primary.Bounds) &&
            CaptureViewportPlacement.ResolveOrigin(session.Document.Viewport, new(0, 0), [primary with { DpiX = 96, DpiY = 96 }]) == origin);
        bool rejected = false;
        try { CaptureViewportPlacement.ResolveOrigin(document.Viewport, new(0, 0), []); } catch (ArgumentException) { rejected = true; }
        check("No active screen is rejected without inventing a display", rejected);
        Console.WriteLine("CAPTURE_VIEWPORT pure physical-layout checks passed; no native windows, cursor calls or input ran.");
    }

    private static MonitorInfo Monitor(string name, PixelBounds bounds, bool primary, uint dpi) =>
        new(name, bounds, bounds, dpi, dpi, primary);
}
