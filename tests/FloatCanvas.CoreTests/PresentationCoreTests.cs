using FloatCanvas.Core;
using System.Text.Json;

internal static class PresentationCoreTests
{
    private static CanvasElement Region() => new() { Kind = ElementKind.Presentation, Width = 300, Height = 200,
        Presentation = new() { Modules = PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail } };
    internal static void Run(Action<bool, string> check)
    {
        PresentationKeyboardLayoutTests.Run(check);
        var region = Region();
        check((int)ElementKind.Presentation == 15 && region.Presentation!.IsValid(), "Presentation appends its kind and has valid owned defaults");
        check(AppearanceRegistry.Resolve(region) is { ForegroundActive: true, BackgroundActive: true }, "presentation uses both canonical color roles");
        region.Style.StrokeWidth = 100;
        check(ElementGeometry.StrokedLocalBounds(region) == new RectD(0, 0, 300, 200) && !ElementGeometry.PreservesAspect(region), "presentation region ignores stroke inflation and resizes freely");
        region.Presentation!.Enabled = false; region.Presentation.Modules = PresentationModules.None; region.Style.Foreground = region.Style.Background = 0;
        check(ElementGeometry.HitTest(region, new(100, 100), 0) && !ElementGeometry.HitTest(region, new(-1, 100), 0), "transparent presentation with legacy inactive flags remains pickable only in its stable region");
        ElementGeometry.ResizeShapeGeometry(region, 2, 3);
        check(region.Width == 600 && region.Height == 600 && region.Kind == ElementKind.Presentation, "rectangle-region geometry resize preserves the presentation kind");
        foreach (var edit in new Action<PresentationSettings>[]
        {
            s => s.Modules = (PresentationModules)16, s => s.KeyDisplay = (PresentationKeyDisplayMode)2, s => s.KeyboardScope = (PresentationKeyboardScope)3, s => s.KeyPanelAlignment = (PresentationPanelAlignment)6,
            s => s.RecentChordLimit = 0, s => s.RecentChordLimit = 9, s => s.KeyHoldMilliseconds = 99, s => s.KeyHoldMilliseconds = 5001,
            s => s.KeyPanelMaximumWidth = double.PositiveInfinity, s => s.CursorRadiusPixels = 1, s => s.CursorRadiusPixels = 129,
            s => s.RippleMilliseconds = 99, s => s.RippleMilliseconds = 2001, s => s.TrailMilliseconds = 99, s => s.TrailMilliseconds = 5001,
            s => s.TrailWidthPixels = double.NaN, s => s.TrailWidthPixels = 65
        })
        {
            var invalid = new PresentationSettings { Enabled = false, Modules = PresentationModules.None }; edit(invalid);
            check(!invalid.IsValid(), "legacy inactive flags do not permit invalid presentation configuration");
        }
        foreach (bool enabled in new[] { false, true })
        foreach (int mask in Enumerable.Range(0, 16))
        {
            var legacy = new PresentationSettings { Enabled = enabled, Modules = (PresentationModules)mask, CursorRadiusPixels = 27, TrailWidthPixels = 5 };
            string json = JsonSerializer.Serialize(legacy);
            var restored = JsonSerializer.Deserialize<PresentationSettings>(json)!;
            var copied = restored.Clone(); copied.Enabled = !enabled; copied.Modules = (PresentationModules)(mask ^ 1);
            check(restored.IsValid() && restored.Enabled == enabled && restored.Modules == (PresentationModules)mask &&
                restored.CursorRadiusPixels == 27 && restored.TrailWidthPixels == 5 && JsonSerializer.Serialize(restored) == json,
                $"obsolete Enabled={enabled} and Modules={mask} remain lossless wire data isolated from clones");
        }
        var clone = region.Clone(); clone.Presentation!.TrailWidthPixels = 19;
        check(region.Presentation.TrailWidthPixels == 3, "source snapshots deep-clone authored presentation settings");
        check(JsonSerializer.Deserialize<PresentationSettings>("{}")!.KeyboardScope == PresentationKeyboardScope.AllApplications, "legacy layers without an authored scope use the explicit all-applications default");
        region.Presentation.KeyboardScope = PresentationKeyboardScope.OutsideFloatCanvas;
        var scopedSettings = JsonSerializer.Deserialize<PresentationSettings>(JsonSerializer.Serialize(region.Presentation))!;
        clone.Presentation.KeyboardScope = PresentationKeyboardScope.FloatCanvasOnly;
        check(scopedSettings.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas && region.Presentation.KeyboardScope == PresentationKeyboardScope.OutsideFloatCanvas, "authored keyboard scope roundtrips and clone edits remain independent");
        region = Region(); var session = new EditorSession(new() { Elements = [region] }); session.SetSelection([region.Id]);
        session.UpdateSelection(element => element.Presentation!.Enabled = true);
        check(session.UndoCount == 0, "no-op presentation configuration creates no history");
        session.UpdateSelection(element => element.Presentation!.Modules = PresentationModules.Trail);
        check(session.UndoCount == 1 && session.Undo() && session.Document.Elements[0].Presentation!.Modules != PresentationModules.Trail && session.Redo(),
            "presentation settings participate in one normal reversible source edit");

        var keys = new[] { new PresentationKeyCap(0x11, "Ctrl", true), new PresentationKeyCap(0x43, "C") };
        var trail = new[] { new PresentationTrailSample(new(20, 30), TimeSpan.FromMilliseconds(50)), new PresentationTrailSample(new(100, 120), TimeSpan.FromMilliseconds(100)) };
        var chord = new PresentationChord(keys, TimeSpan.FromMilliseconds(50), true);
        var frame = new PresentationFrame(7, 8, TimeSpan.FromMilliseconds(100), keys, [chord], new(100, 120),
            [new(new(100, 120), PresentationMouseButton.Left, TimeSpan.FromMilliseconds(100))], trail);
        keys[0] = new(0x41, "A"); trail[0] = new(new(-999, -999), TimeSpan.Zero);
        check(frame.PressedKeys[0].Label == "Ctrl" && chord.Keys[0].Label == "Ctrl" && frame.Trail[0].ScreenPoint == new PointD(20, 30), "runtime frames and chords own their immutable input arrays");
        check(frame.PayloadBytes <= PresentationLimits.MaximumFramePayloadBytes && frame.Epoch == 7 && frame.Sequence == 8 && frame.CursorScreen == new PointD(100, 120), "runtime frame carries bounded monotonic identity and unshifted physical coordinates");
        var outside = new PresentationFrame(7, 8, frame.Time, pressedKeys: [new(0x41, "A")]);
        var inside = new PresentationFrame(7, 8, frame.Time, pressedKeys: [new(0x42, "B")]);
        var scopedFrame = PresentationFrame.WithKeyboardScopes(frame, outside, inside);
        check(scopedFrame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).PressedKeys.Single().Label == "A" &&
            scopedFrame.ForKeyboardScope(PresentationKeyboardScope.FloatCanvasOnly).PressedKeys.Single().Label == "B" && scopedFrame.CursorScreen == frame.CursorScreen &&
            scopedFrame.PayloadBytes <= PresentationLimits.MaximumScopedFramePayloadBytes,
            "one bounded immutable runtime snapshot preserves independently partitioned keyboard sources and the global pointer");
        check(frame.ForKeyboardScope(PresentationKeyboardScope.OutsideFloatCanvas).PressedKeys.Count == 0,
            "unclassified legacy snapshots never leak all-applications input into a restricted layer");
        foreach (var create in new Func<object>[]
        {
            () => new PresentationFrame(-1,0,TimeSpan.Zero),
            () => new PresentationFrame(0,0,TimeSpan.Zero, Enumerable.Repeat(new PresentationKeyCap(1,"x"),PresentationLimits.MaximumHeldKeys+1)),
            () => new PresentationFrame(0,0,TimeSpan.Zero, recentChords:Enumerable.Repeat(new PresentationChord([new(1,"x")],TimeSpan.Zero),9)),
            () => new PresentationFrame(0,0,TimeSpan.Zero, clicks:Enumerable.Repeat(new PresentationClick(new(),PresentationMouseButton.Left,TimeSpan.Zero),33)),
            () => new PresentationFrame(0,0,TimeSpan.Zero, trail:Enumerable.Repeat(new PresentationTrailSample(new(),TimeSpan.Zero),513)),
            () => new PresentationFrame(0,0,TimeSpan.Zero, cursorScreen:new(double.NaN,0)),
            () => new PresentationFrame(0,0,TimeSpan.Zero, trail:[new(new(),TimeSpan.FromSeconds(1))]),
            () => new PresentationFrame(0,0,TimeSpan.Zero, pressedKeys:[new(0,"unknown")]),
            () => new PresentationFrame(0,0,TimeSpan.Zero, pressedKeys:[new(1,new string('x',65))])
            , () => PresentationFrame.WithKeyboardScopes(scopedFrame, outside, inside)
            , () => PresentationFrame.WithKeyboardScopes(frame, new PresentationFrame(7, 9, frame.Time), inside)
            , () => PresentationFrame.WithKeyboardScopes(frame, new PresentationFrame(7, 8, frame.Time, keyboardSuppressed:true), inside)
        })
        {
            bool rejected = false; try { create(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "runtime input payload refuses invalid/unbounded snapshots");
        }
        Bake(check);
    }

    private static void Bake(Action<bool, string> check)
    {
        var source = Region(); source.Transform = Affine2D.Translation(10, 20); source.Style.Opacity = .7;
        var document = new SceneDocument { Viewport = new(0, 0, 800, 700), Elements = [source] };
        var mapping = CanvasMapping.Create(document);
        check(PresentationGeometry.TryScreenRegion(source, mapping, document.Viewport, out var polygon) && polygon.Count == 4,
            "presentation region compiles once in target screen space");
        check(PresentationGeometry.TryClipSegment(new(-50,50),new(400,50),polygon,out var first,out var last) && first.DistanceTo(new(10,50))<1e-6 && last.DistanceTo(new(310,50))<1e-6,
            "centerline clipping adds exact region intersections");
        var frame = new PresentationFrame(1,1,TimeSpan.FromMilliseconds(500),trail:
            [new(new(-150,150),TimeSpan.FromMilliseconds(100)),new(new(300,150),TimeSpan.FromMilliseconds(200)),
             new(new(0,180),TimeSpan.FromMilliseconds(300),true),new(new(100,180),TimeSpan.FromMilliseconds(400))]);
        string before = JsonSerializer.Serialize(source);
        check(PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,frame,new(-100,100),out var candidate,out var failure) && failure == PresentationBakeFailure.None && candidate!.SegmentCount == 2,
            "negative physical origin is subtracted once and explicit disjoint sample paths remain separate");
        var strokes = candidate!.CloneWorldStrokes();
        check(strokes[0].Kind == ElementKind.Freehand && strokes[0].Presentation is null && strokes[0].Style.Variant == ElementAppearance.Default &&
            strokes[0].Style.StrokeWidth == 3 && strokes[0].Style.Opacity == .7 && JsonSerializer.Serialize(source) == before,
            "bake keeps configured width/ink opacity, resets presentation variant, and leaves source untouched");
        strokes[0].Points.Clear(); strokes[0].Style.Foreground=0;
        check(candidate.CloneWorldStrokes()[0].Points.Count > 0 && candidate.CloneWorldStrokes()[0].Style.Foreground != 0, "prepared bake candidates never expose their mutable internal source entities");
        var expired = new PresentationFrame(1,2,TimeSpan.FromSeconds(6),trail:[new(new(100,100),TimeSpan.Zero)]);
        check(!PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,expired,new(),out var none,out failure) && none is null && failure == PresentationBakeFailure.NoVisibleTrail,
            "bake does not resurrect samples outside the configured trail lifetime");
        var tooMany = new PresentationFrame(1,3,TimeSpan.FromMilliseconds(100),trail:Enumerable.Range(0,33).Select(i=>new PresentationTrailSample(new(100,100),TimeSpan.Zero,true)));
        check(!PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,tooMany,new(),out none,out failure) && none is null && failure == PresentationBakeFailure.CapacityExceeded,
            "33 path segments reject the complete bake instead of publishing a partial candidate");
        var full=new PresentationFrame(1,4,TimeSpan.FromMilliseconds(600),trail:Enumerable.Range(0,512).Select(i=>new PresentationTrailSample(new(20+i*.2,100),TimeSpan.FromMilliseconds(i))));
        check(PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,full,new(),out candidate,out _)&&candidate!.PointCount==512,
            "all512 sampled source points can be baked without simplification");
        var over=new PresentationFrame(1,5,TimeSpan.FromMilliseconds(600),trail:Enumerable.Range(0,512).Select(i=>new PresentationTrailSample(new(i==256?500:20+i*.2,100),TimeSpan.FromMilliseconds(i))));
        check(!PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,over,new(),out none,out failure)&&none is null&&failure==PresentationBakeFailure.CapacityExceeded,
            "region clipping that would create513 source points rejects the complete candidate");
        var cutoff=new PresentationFrame(1,6,TimeSpan.FromMilliseconds(1000),trail:[new(new(20,100),TimeSpan.FromMilliseconds(100)),new(new(100,100),TimeSpan.FromMilliseconds(300))]);
        check(PresentationTrailBake.TryPrepare(source,mapping,document.Viewport,cutoff,new(),out candidate,out _)&&
            ElementGeometry.LocalToPlane(candidate!.CloneWorldStrokes()[0]).TransformPoint(candidate.CloneWorldStrokes()[0].Points[0]).DistanceTo(new(60,100))<1e-6,
            "paused trail keeps the visible time-cutoff portion of a sampled segment without baking its fade");

        document.Canvas.Projection.Enabled=true; document.Canvas.Projection.Matrix=new(1,0,0,0,-.4,700,0,-.002,1);
        document.Canvas.Projection.Quad=[new(0,0),new(800,0),new(800,700),new(0,700)]; mapping=CanvasMapping.Create(document);
        var crossing = Region(); crossing.Height=1000;crossing.Transform=Affine2D.Translation(100,-100);
        check(PresentationGeometry.TryScreenRegion(crossing,mapping,document.Viewport,out var safeFragment) && safeFragment.Count>=3 &&
            safeFragment.All(point=>double.IsFinite(point.X)&&double.IsFinite(point.Y)),
            "planar region crossing the horizon retains its finite viewport-clipped safe fragment");
        var content = Region(); content.Width=300;content.Height=150;
        check(BillboardGeometry.TryResolveScreenPlacement(content,mapping,Projective2D.FromAffine(Affine2D.Translation(100,50)),new(250,400),out var placement,out _),
            "sky-only presentation region has a valid lower existing ground");
        content.IsBillboard=true;content.Transform=placement.Linear;content.BillboardAnchor=placement.Anchor;content.BillboardHeight=placement.Height;
        frame=new(2,1,TimeSpan.FromMilliseconds(200),trail:[new(new(120,70),TimeSpan.FromMilliseconds(100)),new(new(350,130),TimeSpan.FromMilliseconds(200))]);
        check(PresentationTrailBake.TryPrepare(content,mapping,document.Viewport,frame,new(),out candidate,out failure) && candidate!.SegmentCount==1,
            "entirely above-horizon trail preserves source equal-depth ground instead of requiring its own bottom to be safe");
        var sky=candidate!.CloneWorldStrokes().Single();var screen=mapping.ElementToScreen(sky);
        check(sky.IsBillboard && screen.TransformPoint(sky.Points[0]).DistanceTo(new(120,70))<1e-6 && screen.TransformPoint(sky.Points[^1]).DistanceTo(new(350,130))<1e-6,
            "baked sky centerline preserves sampled physical screen positions");
        document.Canvas.Projection.Matrix=new(1,0,0,0,1,0,.001,0,1);mapping=CanvasMapping.Create(document);
        check(BillboardGeometry.TryResolveScreenPlacement(content,mapping,Projective2D.FromAffine(Affine2D.Translation(100,50)),new(250,400),out placement,out _),
            "near-vertical-depth source still has a valid finite screen region");
        content.Transform=placement.Linear;content.BillboardAnchor=placement.Anchor;content.BillboardHeight=placement.Height;
        check(PresentationGeometry.TryScreenRegion(content,mapping,document.Viewport,out _) &&
            !PresentationTrailBake.TryPrepare(content,mapping,document.Viewport,frame,new(),out none,out failure) && none is null && failure==PresentationBakeFailure.UnrepresentableDepth,
            "near-vertical equal-depth source refuses bake without fallback or partial writes");
    }
}
