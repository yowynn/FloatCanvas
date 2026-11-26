using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class PresentationPreviewTests
{
    internal static void Run(Action<string, bool> check)
    {
        DirtyAndCache(check); OverflowDamage(check); OverflowDirections(check); VerticalOverflowDirections(check);
        KeyboardCache(check); BudgetClears(check); LargeFrozenScene(check);
    }

    private static void DirtyAndCache(Action<string, bool> check)
    {
        var region = Region(); var back = Box(0xFF0000FF, 0, 0); var front = Box(0xFF00FF00, 300, 250);
        var source = new SceneDocument { Elements = [back, region, front], Viewport = new(0, 0, 400, 300) };
        string original = JsonSerializer.Serialize(source);
        var origin = new PointD(-1920, 120); var context = new CanvasPreviewContext(new object(), new object(), origin, false, false, false);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        var firstInput = Input(1, wide: true); using var firstSnapshot = Snapshot(firstInput); using var first = renderer.Render(firstSnapshot);
        using var firstPixels = Pixels(first, origin);
        check("keyboard preview uses the output physical origin once while keeping its panel layer-local", HasKeyboardPixels(firstPixels) && firstPixels.GetPixel(60, 60).Alpha == 0);
        using var secondSnapshot = Snapshot(Input(2, wide: false)); using var second = renderer.Render(secondSnapshot); using var secondPixels = Pixels(second, origin);
        check("new semantic keyboard cards clear old wide-card pixels and draw the shorter card", HasKeyboardPixels(secondPixels) && Enumerable.Range(0, 200).Any(x => Enumerable.Range(20, 140).Any(y => firstPixels.GetPixel(x, y).Alpha > 0 && secondPixels.GetPixel(x, y).Alpha == 0)));
        check("dynamic presentation frames reuse static prefix and suffix tiles", first.Cache!.BuildCount == 2 && second.Cache!.BuildCount == first.Cache.BuildCount && second.Cache.HitCount > first.Cache.HitCount);
        check("runtime presentation damage is bounded to old and new primitives rather than full output", second.Pixels.CopiedBytes < 400L * 300 * 4);
        check("runtime ticks share one immutable evaluated base picking view", ReferenceEquals(first.PickingDocument, second.PickingDocument) &&
            first.PickingDocument!.Elements.Count == 3 && freezer.EvaluationBuildCount == 1 && freezer.ClonedElementCount == 3);
        check("accepted preview retains the exact immutable semantic input for an explicit keyboard screenshot", ReferenceEquals(first.PresentationFrameSnapshot, firstInput));
        check("presentation runtime data never enters source JSON or authored identity", JsonSerializer.Serialize(source) == original && !source.IsRuntimeEvaluated &&
            source.Elements.All(element => element.Points.Count == 0) && source.Definitions.Count == 0);
        using var pausedSnapshot = Snapshot(null); using var paused = renderer.Render(pausedSnapshot); using var pausedPixels = Pixels(paused, origin);
        check("an empty keyboard frame erases cached key cards while retaining ordinary layers", !HasKeyboardPixels(pausedPixels) && pausedPixels.GetPixel(5, 5) == SKColors.Blue && pausedPixels.GetPixel(305, 255) == SKColors.Lime);
        check("paused frame retains the stable dynamic span instead of caching former input pixels", paused.Cache!.BuildCount == second.Cache!.BuildCount && paused.PresentationPrimitiveCount == 0 && !paused.PresentationBudgetExceeded);
        check("paused presentation frame retains no prior input sample", paused.PresentationFrameSnapshot is null);
        check("old immutable preview pixels remain valid after a runtime update and clear", HasKeyboardPixels(firstPixels) && first.PickingDocument!.Elements.Count == 3);

        PresentationFrame Input(long sequence, bool wide)
        {
            var time = TimeSpan.FromMilliseconds(1000 + sequence);
            PresentationKeyCap[] caps = wide ? [new(0x11, "Ctrl", true), new(0x10, "Shift", true), new(0x7B, "F12")] : [new(0x70, "F1")];
            return new(9, sequence, time, pressedKeys: caps, recentChords: [new(caps, time, true)], cursorScreen: origin + new PointD(60, 60));
        }
        CanvasPreviewSnapshot Snapshot(PresentationFrame? frame) => new()
        {
            Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
            Resources = [], Outputs = [new((int)origin.X, (int)origin.Y, 400, 300)], Revision = 1, Transaction = 0, ContentRevision = 1,
            PresentationFrame = frame, PresentationOrigin = origin
        };
    }

    private static void OverflowDamage(Action<string, bool> check)
    {
        var source = new SceneDocument { Elements = [Region()], Viewport = new(0, 0, 400, 300) };
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        CanvasPreviewSnapshot Snapshot(PresentationFrame? frame) => new() { Context = context, SceneIdentity = source,
            Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen), Resources = [],
            Outputs = [new(0, 0, 400, 300)], Revision = 1, Transaction = 0, ContentRevision = 1, PresentationFrame = frame };
        var frame = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), recentChords:
            [new([new(0x41, new string('W', 64))], TimeSpan.FromSeconds(1), true)]);
        using var firstSnapshot = Snapshot(frame); using var first = renderer.Render(firstSnapshot); using var before = Pixels(first, default);
        using var emptySnapshot = Snapshot(null); using var empty = renderer.Render(emptySnapshot); using var after = Pixels(empty, default);
        bool hadOverflow = Enumerable.Range(185, 215).Any(x => Enumerable.Range(20, 140).Any(y => before.GetPixel(x, y).Alpha > 0));
        bool remains = Enumerable.Range(185, 215).Any(x => Enumerable.Range(20, 140).Any(y => after.GetPixel(x, y).Alpha > 0));
        check("overflowing keyboard pixels are included in partial refresh and cleared", hadOverflow && !remains);
    }

    private static void BudgetClears(Action<string, bool> check)
    {
        var source = new SceneDocument { Elements = Enumerable.Range(0, 9).Select(_ => Region(PresentationModules.Keys)).ToList(), Viewport = new(0, 0, 400, 300) };
        foreach (var element in source.Elements) element.Presentation!.KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys;
        var context = new CanvasPreviewContext(new object(), new object(), default, false, false, false);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer();
        using var visibleSnapshot = Snapshot(new(10, 1, TimeSpan.FromSeconds(1), recentChords: [new([new(0x70, "F1")], TimeSpan.FromSeconds(1), true)]));
        using var visible = renderer.Render(visibleSnapshot); using var visiblePixels = Pixels(visible, default);
        check("bounded keyboard frame produces real preview pixels", visiblePixels.Pixels.Any(pixel => pixel.Alpha > 0));
        using var excessiveSnapshot = Snapshot(new(10, 2, TimeSpan.FromSeconds(2), recentChords:
            [new(Enumerable.Range(0, 16).Select(index => new PresentationKeyCap(0x41 + index, new string('W', 64))), TimeSpan.FromSeconds(2), true)]));
        using var excessive = renderer.Render(excessiveSnapshot); using var cleared = Pixels(excessive, default);
        check("worker publishes budget overflow metadata and clears every previous key primitive", excessive.PresentationBudgetExceeded &&
            excessive.PresentationPrimitiveCount > PresentationLimits.MaximumPrimitives && cleared.Pixels.All(pixel => pixel.Alpha == 0) && excessive.PresentationFrameSnapshot is null);
        check("overflow frame does not refreeze or evaluate source input as new authored nodes", freezer.EvaluationBuildCount == 1 &&
            freezer.ClonedElementCount == 9 && ReferenceEquals(visible.PickingDocument, excessive.PickingDocument));
        using var emptySnapshot = Snapshot(null); using var empty = renderer.Render(emptySnapshot);
        check("disarmed empty frame clears budget state without changing source configuration", !empty.PresentationBudgetExceeded && empty.PresentationPrimitiveCount == 0 &&
            source.Elements.All(element => element.Presentation is { Enabled: true }));
        CanvasPreviewSnapshot Snapshot(PresentationFrame? frame) => new()
        {
            Context = context, SceneIdentity = source, Document = frozen, SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen),
            Resources = [], Outputs = [new(0, 0, 400, 300)], Revision = 1, Transaction = 0, ContentRevision = 1,
            PresentationFrame = frame, PresentationOrigin = default
        };
    }

    private static void OverflowDirections(Action<string, bool> check)
    {
        foreach (var style in new[] { PresentationDisplayStyle.Log, PresentationDisplayStyle.Keycaps })
        foreach (var alignment in Enum.GetValues<PresentationPanelAlignment>())
        {
            var element = new CanvasElement
            {
                Kind = ElementKind.Presentation, Width = 120, Height = 90, Transform = Affine2D.Translation(180, 70),
                Style = new() { Foreground = 0xFFFFFFFF, Background = 0xFF203040, FontSize = 22 },
                Presentation = new() { DisplayStyle = style, KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys,
                    KeyPanelAlignment = alignment, RecentChordLimit = 1 }
            };
            var frame = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), recentChords:
                [new([new(0x11, "Ctrl", true), new(0x41, new string('W', 64))], TimeSpan.FromSeconds(1), true)]);
            using var renderer = new SceneRenderer { PresentationFrame = frame };
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(new SceneDocument { Viewport = new(0, 0, 500, 240), Elements = [element] },
                new AssetLibrary(), true));
            bool left = Ink(bitmap, 0, 165, 0, bitmap.Height);
            bool right = Ink(bitmap, 315, 500, 0, bitmap.Height);
            bool horizontal = alignment switch
            {
                PresentationPanelAlignment.BottomLeft or PresentationPanelAlignment.TopLeft => right,
                PresentationPanelAlignment.BottomRight or PresentationPanelAlignment.TopRight => left,
                _ => left && right
            };
            check($"{style} {alignment} content overflows its selection frame in the aligned direction", horizontal);
        }
        static bool Ink(SKBitmap bitmap, int startX, int endX, int startY, int endY) =>
            Enumerable.Range(Math.Max(0, startX), Math.Max(0, Math.Min(bitmap.Width, endX) - Math.Max(0, startX)))
                .Any(x => Enumerable.Range(Math.Max(0, startY), Math.Max(0, Math.Min(bitmap.Height, endY) - Math.Max(0, startY)))
                    .Any(y => bitmap.GetPixel(x, y).Alpha > 0));
    }

    private static void VerticalOverflowDirections(Action<string, bool> check)
    {
        foreach (var style in new[] { PresentationDisplayStyle.Log, PresentationDisplayStyle.Keycaps })
        foreach (var alignment in Enum.GetValues<PresentationPanelAlignment>())
        {
            var element = new CanvasElement
            {
                Kind = ElementKind.Presentation, Width = 120, Height = 44, Transform = Affine2D.Translation(180, 150),
                Style = new() { Foreground = 0xFFFFFFFF, Background = 0xFF203040, FontSize = 22 },
                Presentation = new() { DisplayStyle = style, KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys,
                    KeyPanelAlignment = alignment, RecentChordLimit = 5 }
            };
            var frame = new PresentationFrame(1, 1, TimeSpan.FromSeconds(1), recentChords:
            [
                new([new(0x41, "A")], TimeSpan.FromSeconds(1), true),
                new([new(0x42, "B")], TimeSpan.FromSeconds(1), true),
                new([new(0x43, "C")], TimeSpan.FromSeconds(1), true),
                new([new(0x44, "D")], TimeSpan.FromSeconds(1), true),
                new([new(0x45, "E")], TimeSpan.FromSeconds(1), true)
            ]);
            using var renderer = new SceneRenderer { PresentationFrame = frame };
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(new SceneDocument { Viewport = new(0, 0, 500, 360), Elements = [element] },
                new AssetLibrary(), true));
            bool above = Ink(bitmap, 160, 340, 70, 148);
            bool below = Ink(bitmap, 160, 340, 202, 280);
            bool vertical = alignment switch
            {
                PresentationPanelAlignment.TopLeft or PresentationPanelAlignment.TopCenter or PresentationPanelAlignment.TopRight => below,
                _ => above
            };
            check($"{style} {alignment} content overflows its selection frame in the aligned direction", vertical);
        }
        static bool Ink(SKBitmap bitmap, int startX, int endX, int startY, int endY) =>
            Enumerable.Range(Math.Max(0, startX), Math.Max(0, Math.Min(bitmap.Width, endX) - Math.Max(0, startX)))
                .Any(x => Enumerable.Range(Math.Max(0, startY), Math.Max(0, Math.Min(bitmap.Height, endY) - Math.Max(0, startY)))
                    .Any(y => bitmap.GetPixel(x, y).Alpha > 0));
    }

    private static void KeyboardCache(Action<string, bool> check)
    {
        var keyboard = new CanvasElement
        {
            Kind = ElementKind.Presentation, Width = 760, Height = 260, Transform = Affine2D.Translation(40, 40),
            Style = new() { Foreground = 0xFFFFFFFF, Background = 0xFF203040, FontSize = 18 },
            Presentation = new() { DisplayStyle = PresentationDisplayStyle.Keyboard, KeyDisplay = PresentationKeyDisplayMode.AllPhysicalKeys }
        };
        var document = new SceneDocument { Viewport = new(0, 0, 900, 360), Elements = [keyboard] };
        var runtimeDocument = SceneEvaluation.Build(document).RenderDocument;
        using var renderer = new SceneRenderer();
        renderer.PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1),
            pressedKeys: [new(0x70, "F1")], recentChords: [new([new(0x70, "F1")], TimeSpan.FromSeconds(1), true)]);
        _ = renderer.PreparePresentation(runtimeDocument);
        renderer.PresentationFrame = new(1, 2, TimeSpan.FromSeconds(2),
            pressedKeys: [new(0x71, "F2")], recentChords: [new([new(0x71, "F2")], TimeSpan.FromSeconds(2), true)]);
        _ = renderer.PreparePresentation(runtimeDocument);
        check($"a live keyboard reuses static key geometry and fitted text metrics across input frames (layout={renderer.PresentationKeyboardLayoutBuildCount}, plan={renderer.PresentationPlanBuildCount})",
            renderer.PresentationKeyboardLayoutBuildCount == 1 && renderer.PresentationPlanBuildCount == 1);
    }

    private static void LargeFrozenScene(Action<string, bool> check)
    {
        const int count = 40_000;
        var ordinary = Enumerable.Range(0, count).Select(index => Box(0xFF112233, 10000 + index % 200 * 4, 10000 + index / 200 * 4)).ToList();
        ordinary.Insert(count / 2, Region());
        var source = new SceneDocument { Elements = ordinary, Viewport = new(0, 0, 400, 300) };
        var session = new EditorSession(source); long content = session.ContentVersion;
        var context = new CanvasPreviewContext(session, new object(), default, false, false, false);
        var freezer = new CanvasPreviewScene(); var frozen = freezer.Capture(source, 1, 1, 0, -1, -1, false);
        using var renderer = new CanvasPreviewRenderer(); SceneDocument? firstWorld = null; long firstBuilds = -1, finalHits = 0;
        bool stable = true; byte[]? firstPixels = null; bool changingPixels = false;
        for (int tick = 0; tick < 100; tick++)
        {
            using var snapshot = new CanvasPreviewSnapshot
            {
                Context = context, SceneIdentity = source, Document = freezer.Capture(source, 1, 1, 0, -1, -1, false),
                SourceDocument = frozen, FrozenEvaluation = freezer.EvaluationFor(frozen), Resources = [], Outputs = [new(0, 0, 400, 300)],
                Revision = 1, Transaction = 0, ContentRevision = 1,
                PresentationFrame = new(11, tick + 1, TimeSpan.FromMilliseconds(1000 + tick * 16), recentChords:
                    [new([new(0x70 + tick % 2, tick % 2 == 0 ? "F1" : "F2")], TimeSpan.FromMilliseconds(1000 + tick * 16), true)]), PresentationOrigin = default
            };
            using var frame = renderer.Render(snapshot);
            firstWorld ??= frame.PickingDocument; if (firstBuilds < 0) firstBuilds = frame.Cache!.BuildCount;
            stable &= ReferenceEquals(firstWorld, frame.PickingDocument) && frame.Cache!.BuildCount == firstBuilds && frame.Cache.PresentationPlanBuildCount == 1 && frame.PresentationPrimitiveCount > 0;
            finalHits = frame.Cache!.HitCount;
            if (tick is 0 or 99)
            {
                using var pixels = Pixels(frame, default);
                if (tick == 0) firstPixels = pixels.Bytes;
                else changingPixels = HasKeyboardPixels(pixels) && firstPixels is not null && !firstPixels.SequenceEqual(pixels.Bytes);
            }
        }
        check("40k ordinary layers plus presentation reuse one frozen evaluation and static span over 100 input frames", stable &&
            freezer.ClonedElementCount == count + 1 && freezer.EvaluationBuildCount == 1 && finalHits >= 99);
        check("runtime ticks leave authored source versions, history, assets and node tables unchanged", session.ContentVersion == content &&
            session.UndoCount == 0 && session.Assets.Count == 0 && source.Elements.Count == count + 1 && !source.IsRuntimeEvaluated);
        check("40k-layer repeated input still paints changing semantic keyboard glyphs", changingPixels);
    }

    private static bool HasKeyboardPixels(SKBitmap image) => Enumerable.Range(15, 185).Any(x => Enumerable.Range(20, 140).Any(y => image.GetPixel(x, y).Alpha > 0));

    private static CanvasElement Region(PresentationModules modules = PresentationModules.Keys) => new()
    {
        Kind = ElementKind.Presentation, Width = 160, Height = 120, Transform = Affine2D.Translation(20, 20),
        Presentation = new() { Modules = modules, CursorRadiusPixels = 8 }, Style = new() { Foreground = 0xFFFFFFFF, Background = 0xE0203040, FontSize = 20 }
    };
    private static CanvasElement Box(uint color, double x, double y) => new() { Kind = ElementKind.Rectangle, Width = 12, Height = 12,
        Transform = Affine2D.Translation(x, y), Style = new() { Fill = color, StrokeWidth = 0 } };
    private static SKBitmap Pixels(CanvasPreviewFrame frame, PointD origin)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300)); surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(-(float)origin.X, -(float)origin.Y); frame.Pixels.Draw(surface.Canvas);
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }
}
