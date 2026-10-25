using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

internal static class UnifiedPreviewCorrectnessTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);

    internal static void Run(Action<string, bool> check)
    {
        var results = new List<(string Name, bool Passed)>();
        void Verify(string name, bool passed)
        {
            results.Add((name, passed));
            Console.WriteLine($"UNIFIED_CORRECTNESS {(passed ? "PASS" : "FAIL")} {name}");
        }
        RunLogic(Verify);
        if (Application.Current is not null) throw new InvalidOperationException("Run unified preview correctness in a fresh process.");
        string evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "artifacts",
            "FloatCanvas-unified-preview-correctness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        ApplicationPaths.Configure(["--profile", Path.Combine(evidence, "profile")]);
        new UserSettings { Language = "en", PreferGpuAcceleration = false }.Save();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RunCase("preference invalidation", () => Preferences(app, Verify));
        RunCase("mixed UI and pixel invalidation", () => MixedRequests(app, Verify));
        RunCase("four-point placement preview", () => Placement(app, Verify));
        RunCase("context epoch rejection", () => ContextEpoch(app, Verify));
        RunCase("coalesced context epoch rejection", () => CoalescedContextEpoch(app, Verify));
        RunCase("synthetic two-monitor retained pixels", () => MultipleOutputs(app, Verify));
        string output = Path.Combine(evidence, "results.json");
        File.WriteAllText(output, JsonSerializer.Serialize(results.Select(result => new { result.Name, result.Passed }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("UNIFIED_CORRECTNESS_EVIDENCE " + output);
        foreach (var result in results) check(result.Name, result.Passed);

        void RunCase(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                Console.WriteLine($"UNIFIED_CORRECTNESS_EXCEPTION {name}: {error}");
                Verify(name + " completes without an exception", false);
            }
        }
    }

    internal static void RunLogic(Action<string, bool> check)
    {
        FrozenNodes(check);
        BackgroundChanges(check);
        GestureHandoff(check);
        DamageAndSkippedFrames(check);
    }

    private static void FrozenNodes(Action<string, bool> check)
    {
        var source = new SceneDocument { Viewport = new(0, 0, 800, 300), Elements = [Box(20), Box(200), Box(500)] };
        var freezer = new CanvasPreviewScene();
        var first = freezer.Capture(source, 1, 1, 1, 1, 1, false);
        source.Elements[1].Transform = Affine2D.Translation(240, 80);
        source.Elements[1].Style.Fill = 0xFF0000FF;
        var moved = freezer.Capture(source, 2, 2, 1, 1, 1, false);
        check("dynamic freezing reuses both static sides and replaces only the changed node",
            ReferenceEquals(first.Elements[0], moved.Elements[0]) && ReferenceEquals(first.Elements[2], moved.Elements[2]) &&
            !ReferenceEquals(first.Elements[1], moved.Elements[1]) && freezer.ClonedElementCount == 4);
        check("old frozen element geometry and style stay detached from a later gesture",
            first.Elements[1].Transform.OffsetX == 200 && first.Elements[1].Style.Fill == 0xFFFF0000 && moved.Elements[1].Transform.OffsetX == 240);
        source.LayerOpacity = .4; source.Canvas.Grid.Color = 0xFF336699; source.Canvas.Grid.Visible = true;
        var settings = freezer.Capture(source, 3, 2, 1, -1, -1, false);
        check("global appearance changes share frozen nodes without mutating an older header",
            ReferenceEquals(moved.Elements, settings.Elements) && moved.LayerOpacity == 1 && settings.LayerOpacity == .4 &&
            !moved.Canvas.Grid.Visible && settings.Canvas.Grid.Visible && settings.Canvas.Grid.Color == 0xFF336699);
        var removed = source.Elements[1].Id;
        source.Elements.RemoveAt(1); source.Elements.Add(Box(650));
        var rebuilt = freezer.Capture(source, 4, 3, 2, -1, -1, true);
        check("unknown structural changes rebuild detached nodes and preserve earlier snapshots",
            rebuilt.Elements.All(element => element.Id != removed) && settings.Elements.Any(element => element.Id == removed) &&
            rebuilt.Elements.All(element => !source.Elements.Any(live => ReferenceEquals(element, live))) && freezer.FullBuildCount == 2);
        var replacement = source.Clone(); replacement.Elements[0].Style.Fill = 0xFF00FF00;
        var replaced = freezer.Capture(replacement, 5, 4, 3, -1, -1, true);
        check("document replacement cannot reuse nodes from a previous live document identity",
            !ReferenceEquals(rebuilt.Elements[0], replaced.Elements[0]) && rebuilt.Elements[0].Style.Fill == 0xFFFF0000 && replaced.Elements[0].Style.Fill == 0xFF00FF00);
    }

    private static void GestureHandoff(Action<string, bool> check)
    {
        var source = new SceneDocument { Elements = [Box(0), Box(100), Box(200)] };
        var freezer = new CanvasPreviewScene();
        _ = freezer.Capture(source, 0, 0, 0, -1, -1, false);
        source.Elements[0].Transform = Affine2D.Translation(10, 80);
        var intermediateA = freezer.Capture(source, 1, 1, 1, 0, 0, false);
        // The worker skips A's released value and next sees a new gesture on another object.
        source.Elements[0].Transform = Affine2D.Translation(20, 80);
        source.Elements[1].Transform = Affine2D.Translation(130, 80);
        var gestureB = freezer.Capture(source, 3, 3, 2, 1, 1, false);
        check("a new gesture preserves a skipped final value from the preceding gesture",
            gestureB.Elements[0].Transform.OffsetX == 20 && gestureB.Elements[1].Transform.OffsetX == 130 &&
            intermediateA.Elements[0].Transform.OffsetX == 10);
    }

    private static void BackgroundChanges(Action<string, bool> check)
    {
        foreach (bool asynchronous in new[] { false, true })
        {
            string path = asynchronous ? "asynchronous" : "synchronous";
            var document = new SceneDocument
            {
                Elements = [Box(20), Box(200)],
                Background = new() { Kind = ElementKind.Image, Width = 500, Height = 300, AssetId = "old-asset" }
            };
            var freezer = new CanvasPreviewScene();
            var original = Freeze(1, null);
            document.Background.AssetId = "new-asset";
            document.Background.Transform = Affine2D.Translation(12, 15);
            document.Background.Effects.Add(new() { Kind = ImageEffectKind.Blur, Amount = 8 });
            var changed = Freeze(2, new HashSet<Guid> { document.Background.Id });
            check(path + " known-ID background changes detach its payload while reusing unchanged elements",
                original.Background!.AssetId == "old-asset" && original.Background.Effects.Count == 0 &&
                changed.Background!.AssetId == "new-asset" && changed.Background.Transform.OffsetX == 12 &&
                changed.Background.Effects.Single().Amount == 8 && !ReferenceEquals(original.Background, changed.Background) &&
                ReferenceEquals(original.Elements[0], changed.Elements[0]) && ReferenceEquals(original.Elements[1], changed.Elements[1]));

            Guid priorId = document.Background.Id;
            document.Background = document.Background.Clone(newId: true); document.Background.AssetId = "replacement-asset";
            var replacement = Freeze(3, new HashSet<Guid> { priorId, document.Background.Id });
            check(path + " background replacement cannot retain the previous background identity",
                replacement.Background!.Id != changed.Background!.Id && replacement.Background.AssetId == "replacement-asset" &&
                changed.Background.AssetId == "new-asset");

            priorId = document.Background.Id; document.Background = null;
            var removed = Freeze(4, new HashSet<Guid> { priorId });
            check(path + " known-ID background removal leaves earlier snapshots intact",
                removed.Background is null && replacement.Background!.AssetId == "replacement-asset");
            document.Background = new() { Kind = ElementKind.Image, Width = 500, Height = 300, AssetId = "added-asset" };
            var added = Freeze(5, new HashSet<Guid> { document.Background.Id });
            check(path + " known-ID background insertion is included in the frozen header",
                added.Background!.AssetId == "added-asset" && removed.Background is null &&
                !ReferenceEquals(added.Background, document.Background));

            SceneDocument Freeze(long revision, IReadOnlySet<Guid>? changedIds)
            {
                if (asynchronous)
                {
                    var preparing = freezer.PrepareAsync(document, revision, revision, revision, -1, -1, false,
                        changedIds, revision - 1, () => true).AsTask();
                    Phase2RenderTests.Until(() => preparing.IsCompleted);
                    if (!preparing.GetAwaiter().GetResult()) throw new InvalidOperationException("Owned background preparation was unexpectedly invalidated.");
                }
                return freezer.Capture(document, revision, revision, revision, -1, -1, false, changedIds, revision - 1);
            }
        }
    }

    private static void DamageAndSkippedFrames(Action<string, bool> check)
    {
        var identity = new object();
        var context = new CanvasPreviewContext(identity, new object(), new(0, 0), false, false, false);
        var first = new SceneDocument { Viewport = new(0, 0, 800, 300), Elements = [Box(30), Box(650)] };
        var latest = new SceneDocument { Viewport = first.Viewport, Elements = [first.Elements[0].Clone(), first.Elements[1]] };
        latest.Elements[0].Transform = Affine2D.Translation(420, 80);
        using var before = Snapshot(first, 1);
        using var after = Snapshot(latest, 3);
        using var sceneRenderer = new SceneRenderer();
        var damage = CanvasPreviewDamage.Between(before, after, sceneRenderer, new());
        check("skipped-worker damage includes the last published and latest object positions",
            Covers(damage, new(50, 100)) && Covers(damage, new(440, 100)) && !Covers(damage, new(700, 100)));
        using var renderer = new CanvasPreviewRenderer();
        using var oldFrame = renderer.Render(before);
        using var newFrame = renderer.Render(after);
        using var retained = SKSurface.Create(new SKImageInfo(800, 300, SKColorType.Bgra8888, SKAlphaType.Premul));
        retained.Canvas.Clear(SKColors.Transparent); oldFrame.Pixels.Draw(retained.Canvas);
        var blocks = newFrame.Pixels.DamageComparedTo(oldFrame.Pixels);
        foreach (var block in blocks)
        {
            retained.Canvas.Save(); retained.Canvas.ClipRect(new(block.Left, block.Top, block.Right, block.Bottom));
            retained.Canvas.Clear(SKColors.Transparent); newFrame.Pixels.Draw(retained.Canvas); retained.Canvas.Restore();
        }
        using var actualImage = retained.Snapshot(); using var actual = SKBitmap.FromImage(actualImage);
        using var expected = PreviewTestPixels.Read(newFrame.Pixels);
        check("applying a skipped frame's changed blocks matches a complete redraw including transparent erasure",
            actual.Bytes.SequenceEqual(expected.Bytes) && actual.GetPixel(50, 100).Alpha == 0 && actual.GetPixel(440, 100) == SKColors.Red);
        using var changedEpoch = Snapshot(latest, 4, context with { Epoch = 1 });
        check("a changed context requires a complete replacement rather than local old-context damage",
            CanvasPreviewDamage.Between(before, changedEpoch, sceneRenderer, new()) is null);
        using var changedOpacity = Snapshot(latest.Clone(), 5); changedOpacity.Document.LayerOpacity = .5;
        check("global opacity changes invalidate the full affected output",
            CanvasPreviewDamage.Between(before, changedOpacity, sceneRenderer, new()) is null);

        CanvasPreviewSnapshot Snapshot(SceneDocument document, long revision, CanvasPreviewContext? selected = null) => new()
        {
            Context = selected ?? context, SceneIdentity = identity, Document = document, Resources = [],
            Outputs = [new(0, 0, 800, 300)], Revision = revision, Transaction = 1, First = 0, Last = 0
        };
    }

    private static void Preferences(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var settings = Field<UserSettings>(fixture.Session, "_settings");
        long revision = fixture.Editor.Session.PreviewRevision;
        long before = fixture.Preview.CompletedFrames;
        settings.PreferGpuAcceleration = true;
        Invoke(fixture.Session, "RequestCanvasPreview", false, null); fixture.Settle();
        check("enabling GPU preference invalidates the worker even when document revision is unchanged",
            fixture.Preview.CompletedFrames > before && fixture.Editor.Session.PreviewRevision == revision);
        before = fixture.Preview.CompletedFrames; settings.PreferGpuAcceleration = false;
        Invoke(fixture.Session, "RequestCanvasPreview", false, null); fixture.Settle();
        check("disabling GPU preference also produces a fresh worker result without a document edit",
            fixture.Preview.CompletedFrames > before && fixture.Editor.Session.PreviewRevision == revision);
        check("preference changes leave the fixture's native windows hidden", Hidden(fixture.Host));
    }

    private static void MixedRequests(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        PixelBounds[] area = [new(20, 20, 40, 40)];
        long before = fixture.Preview.CompletedFrames;
        Invoke(fixture.Session, "RequestRender");
        Invoke(fixture.Session, "RequestPixelRender", (object)area);
        fixture.Settle();
        check("a pixel invalidation arriving behind a normal UI request still reaches the worker", fixture.Preview.CompletedFrames > before);
        before = fixture.Preview.CompletedFrames;
        Invoke(fixture.Session, "RequestPixelRender", (object)area);
        Invoke(fixture.Session, "RequestRender");
        fixture.Settle();
        check("a normal UI request does not erase a previously pending pixel invalidation", fixture.Preview.CompletedFrames > before);
    }

    private static void Placement(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        fixture.Editor.Session.Execute("placement reference", document =>
        {
            document.Canvas.Projection = CanvasCalibration.CreateInitial(document.Viewport);
            document.Canvas.Projection.Enabled = true; document.Canvas.Grid.Visible = true;
        });
        Invoke(fixture.Session, "BeginCanvasFourPoints");
        var calibration = Field<CanvasCalibrationController>(fixture.Session, "_canvasCalibration");
        calibration.Down(new(60, 60)); calibration.Down(new(500, 60)); calibration.Down(new(500, 260));
        calibration.Move(new(60, 260)); fixture.Settle();
        long revision = fixture.Editor.Session.PreviewRevision;
        long before = fixture.Preview.CompletedFrames;
        using var firstSnapshot = (CanvasPreviewSnapshot)Invoke(fixture.Session, "CaptureCanvasPreview")!;
        using var firstPixels = PreviewTestPixels.Read(fixture.Preview.Frame!.Pixels);
        calibration.Move(new(90, 290)); fixture.Settle();
        using var secondSnapshot = (CanvasPreviewSnapshot)Invoke(fixture.Session, "CaptureCanvasPreview")!;
        using var secondPixels = PreviewTestPixels.Read(fixture.Preview.Frame!.Pixels);
        check("three-click fourth-point hover updates the raster without changing Core preview revision",
            fixture.Editor.Session.PreviewRevision == revision && fixture.Preview.CompletedFrames > before &&
            firstSnapshot.PlacementClip is { Count: 4 } && secondSnapshot.PlacementClip is { Count: 4 } &&
            !firstSnapshot.PlacementClip.SequenceEqual(secondSnapshot.PlacementClip) && !firstPixels.Bytes.SequenceEqual(secondPixels.Bytes));
        calibration.End(); fixture.Settle();
    }

    private static void ContextEpoch(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var first = (CanvasPreviewContext)Invoke(fixture.Session, "CurrentPreviewContext")!;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(fixture.Host, false);
        var hidden = (CanvasPreviewContext)Invoke(fixture.Session, "CurrentPreviewContext")!;
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(fixture.Host, true);
        var returned = (CanvasPreviewContext)Invoke(fixture.Session, "CurrentPreviewContext")!;
        check("A to B to A visibility transitions acquire a fresh monotonic context epoch",
            first.Epoch < hidden.Epoch && hidden.Epoch < returned.Epoch && first != returned && first with { Epoch = 0 } == returned with { Epoch = 0 });
        long submitted = fixture.Host.PresentedFrameCount;
        Invoke(fixture.Session, "PresentCanvasPreview");
        check("an old A frame does not schedule presentation after returning through B", fixture.Host.PresentedFrameCount == submitted && !fixture.PresentationPending);
        bool leaked = false;
        void Observe(SKSurface surface)
        {
            using var pixels = surface.PeekPixels(); leaked |= pixels.GetPixelColor(40, 100).Alpha != 0;
        }
        fixture.Host.Draw += Observe;
        try { fixture.Host.Render(); }
        finally { fixture.Host.Draw -= Observe; }
        check("explicit redraw rejects old-epoch content until the new A result is ready", !leaked);
        fixture.Settle();
        check("the replacement frame belongs to the new A context", fixture.Preview.Frame?.Context == returned);
    }

    private static void CoalescedContextEpoch(Application app, Action<string, bool> check)
    {
        using var fixture = new Fixture(app);
        var original = fixture.Preview.Frame!.Context;
        // No draw, message pumping or CurrentPreviewContext call occurs between these requests.
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(fixture.Host, false);
        Invoke(fixture.Session, "RequestRender");
        typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(fixture.Host, true);
        Invoke(fixture.Session, "RequestRender");
        var returned = (CanvasPreviewContext)Invoke(fixture.Session, "CurrentPreviewContext")!;
        check("coalesced A to B to A render requests observe both transitions without an intermediate context read",
            returned.Epoch >= original.Epoch + 2 && original with { Epoch = 0 } == returned with { Epoch = 0 });
        bool leaked = false;
        void Observe(SKSurface surface)
        {
            using var pixels = surface.PeekPixels(); leaked |= pixels.GetPixelColor(40, 100).Alpha != 0;
        }
        fixture.Host.Draw += Observe;
        try { fixture.Host.Render(); }
        finally { fixture.Host.Draw -= Observe; }
        check("coalesced visibility changes cannot render an old A frame under the new A epoch", !leaked);
        fixture.Settle();
        check("coalesced visibility changes eventually publish the latest A context", fixture.Preview.Frame?.Context == returned);
    }

    private static void MultipleOutputs(Application app, Action<string, bool> check)
    {
        MonitorInfo[] monitors =
        [
            new("synthetic-left", new(0, 0, 640, 360), new(0, 0, 640, 360), 96, 96, true),
            new("synthetic-right", new(640, 0, 640, 360), new(640, 0, 640, 360), 144, 144, false)
        ];
        using var fixture = new Fixture(app, monitors);
        var rasters = new Dictionary<string, byte[]>();
        var submitted = new List<string>();
        void Observe(SKSurface surface)
        {
            int left = (int)Math.Round(-surface.Canvas.TotalMatrix.TransX);
            var monitor = monitors.Single(output => output.Bounds.Left == left);
            using var pixels = surface.PeekPixels();
            rasters[monitor.DeviceName] = pixels.GetPixelSpan().ToArray();
        }
        void Submitted(OverlayFrameTiming frame) => submitted.AddRange(frame.Outputs.Select(output => output.DeviceName));
        fixture.Host.Draw += Observe; fixture.Host.FrameSubmitted += Submitted;
        try
        {
            var left = Box(20); var right = Box(760); right.Style.Fill = 0xFF0000FF;
            fixture.Editor.Session.Execute("two-output pixel fixture", document => document.Elements = [left, right]);
            fixture.Editor.Session.SetSelection([]); fixture.Settle();
            check("synthetic 96/144-DPI outputs initially match complete per-output scene rendering", MatchesFullScene());

            submitted.Clear();
            fixture.Editor.Session.BeginTransaction("move left output only");
            left.Transform = Affine2D.Translation(120, 80);
            fixture.Editor.Session.NotifyElementsChanged([left.Id]); fixture.Settle();
            check("local content invalidation skips the unchanged synthetic output",
                submitted.Count > 0 && submitted.All(output => output == "synthetic-left") && MatchesFullScene());
            fixture.Editor.Session.Commit(); fixture.Settle();

            submitted.Clear();
            fixture.Editor.Session.BeginTransaction("cross synthetic output boundary");
            left.Transform = Affine2D.Translation(610, 80);
            fixture.Editor.Session.NotifyElementsChanged([left.Id]); fixture.Settle();
            check("cross-output motion updates both outputs and clears its former position",
                submitted.Contains("synthetic-left") && submitted.Contains("synthetic-right") && MatchesFullScene());
            fixture.Editor.Session.Commit(); fixture.Settle();

            submitted.Clear();
            fixture.Editor.Session.BeginTransaction("delete cross-output content");
            fixture.Editor.Session.Document.Elements.RemoveAll(element => element.Id == left.Id);
            fixture.Editor.Session.NotifyElementsChanged([left.Id]); fixture.Settle();
            check("deleting cross-output content restores transparent pixels on both outputs",
                submitted.Contains("synthetic-left") && submitted.Contains("synthetic-right") && MatchesFullScene());
            fixture.Editor.Session.Commit(); fixture.Settle();
            check("synthetic multi-output verification keeps every owned native window hidden", Hidden(fixture.Host));
        }
        finally { fixture.Host.Draw -= Observe; fixture.Host.FrameSubmitted -= Submitted; }

        bool MatchesFullScene()
        {
            using var renderer = new SceneRenderer();
            foreach (var monitor in monitors)
            {
                var bounds = monitor.Bounds;
                using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Translate(-bounds.Left, -bounds.Top);
                renderer.Draw(surface.Canvas, fixture.Editor.Session.Document, fixture.Editor.Session.Assets);
                using var pixels = surface.PeekPixels();
                if (!rasters.TryGetValue(monitor.DeviceName, out var actual) || !pixels.GetPixelSpan().SequenceEqual(actual)) return false;
            }
            return true;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Application app;
        internal Fixture(Application app, IReadOnlyList<MonitorInfo>? monitors = null)
        {
            this.app = app;
            var bounds = new PixelBounds(0, 0, 640, 360);
            var outputs = monitors ?? [new MonitorInfo("hidden-unified-correctness", bounds, bounds, 96, 96, true)];
            Func<IReadOnlyList<MonitorInfo>> provider = () => outputs;
            Host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Private, null,
                [outputs.Count == 1 ? OverlayMonitorScope.PrimaryScreen : OverlayMonitorScope.AllScreens, provider, false], null)!;
            Session = new DesktopSession(app, Host);
            Field<System.Windows.Forms.NotifyIcon>(Session, "_tray").Visible = false;
            typeof(OverlayHost).GetProperty("IsVisible")!.SetValue(Host, true);
            typeof(OverlayHost).GetProperty("IsEditing")!.SetValue(Host, true);
            Editor = Field<EditorController>(Session, "_editor"); Editor.Tool = EditorTool.Select;
            Preview.Failed += error => Failure = error;
            Editor.Session.Execute("unified correctness rectangle", document => document.Elements = [Box(20)]);
            Editor.Session.SelectAll(); Settle();
        }
        internal OverlayHost Host { get; }
        internal DesktopSession Session { get; }
        internal EditorController Editor { get; }
        internal CanvasPreviewPump Preview => Field<CanvasPreviewPump>(Session, "_canvasPreview");
        private Exception? Failure { get; set; }
        internal bool PresentationPending => Pending("_presentOperation");
        private bool Pending(string name) => typeof(DesktopSession).GetField(name, Private)?.GetValue(Session) is DispatcherOperation { Status: DispatcherOperationStatus.Pending };
        internal void Settle()
        {
            var clock = Stopwatch.StartNew();
            while (Pending("_renderOperation") || PresentationPending || Preview.Busy)
            {
                if (Failure is not null) throw new InvalidOperationException("Correctness preview failed.", Failure);
                if (clock.ElapsedMilliseconds > 10000) throw new TimeoutException("Correctness preview did not settle.");
                var frame = new DispatcherFrame();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
        }
        public void Dispose() => Session.Dispose();
    }

    private static CanvasElement Box(double x) => new()
    {
        Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(x, 80),
        Style = new() { Fill = 0xFFFF0000, StrokeWidth = 0 }
    };
    private static bool Covers(IReadOnlyList<PixelBounds>? damage, PointD point) => damage is not null && damage.Any(region => region.Contains(new OverlayPoint(point.X, point.Y)));
    private static bool Hidden(OverlayHost host) => host.Diagnostics.All(window => !IsWindowVisible(window.InputHandle) && !IsWindowVisible(window.DisplayHandle));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
}
