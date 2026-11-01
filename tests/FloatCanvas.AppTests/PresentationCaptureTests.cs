using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Explicit screenshot freezing, with synthetic input and no native windows or input registration.</summary>
internal static class PresentationCaptureTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        PauseAndDisplayedFrame(check);
        StateGuards(check);
        ScreenshotPixels(check);
    }

    private static void PauseAndDisplayedFrame(Action<string, bool> check)
    {
        using var fixture = new CaptureFixture();
        var keys = new PresentationFrame(1, 1, TimeSpan.FromMilliseconds(1000),
            pressedKeys: [new(0x11, "Ctrl", true), new(0x43, "C")],
            recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromMilliseconds(1000), true)]);
        var unshown = new PresentationFrame(1, 2, TimeSpan.FromMilliseconds(1001), cursorScreen: new(999, 999));
        fixture.Show(keys);
        typeof(PresentationRuntimeBroker).GetProperty("Frame", Hidden)!.SetValue(fixture.Runtime, unshown);
        var frozen = fixture.Freeze();
        check("explicit capture freezes accepted shown input rather than newer unpublished broker input", ReferenceEquals(frozen?.Frame, keys) && !ReferenceEquals(frozen?.Frame, unshown));
        check("a semantic keyboard chord can be captured without any trail samples", frozen is { Frame.PressedKeys.Count: 2, Frame.RecentChords.Count: 1, Frame.Trail.Count: 0 });
        fixture.Pause(true);
        fixture.ClearShown(); // An explicitly paused worker can finish an empty frame before the screenshot.
        var paused = fixture.Freeze();
        check("an explicit pause retains the shown keyboard frame after the worker publishes an empty frame", ReferenceEquals(paused?.Frame, keys));
        check("capture retention does not retain or restart the input service", !fixture.Runtime.Observing && fixture.Sources.Single().StopCount > 0 && fixture.Sources.Count == 1);
        fixture.Pause(true);
        check("repeating an explicit pause preserves the guarded shown frame", ReferenceEquals(fixture.Freeze()?.Frame, keys));
        fixture.Runtime.Configure(Demand, true, new object(), "paused");
        check("resuming observation invalidates the earlier paused screenshot frame", fixture.Freeze() is null);
        var single = new PresentationFrame(2, 1, TimeSpan.FromMilliseconds(1200),
            recentChords: [new([new(0x70, "F1")], TimeSpan.FromMilliseconds(1200), true)]);
        fixture.Show(single); fixture.Pause(true); fixture.ClearShown();
        check("a single displayed semantic key without a trail survives an explicit pause", fixture.Freeze() is { Frame.RecentChords.Count: 1, Frame.Trail.Count: 0 });
        fixture.Pause(false);
        check("a nonretaining pause clears the screenshot snapshot", fixture.Freeze() is null);
        check("previously frozen screenshots remain immutable after producer resume and pause", ReferenceEquals(frozen?.Frame, keys) && frozen is { Frame.PressedKeys.Count: 2, Frame.CursorScreen: null });
        fixture.Show(keys); fixture.Session.NotifyDisplayLayoutChanged();
        check("a newer preview revision rejects old shown input even when source content version is unchanged", fixture.Freeze() is null);
        fixture.Show(keys);
        check("a newly presented input at the current preview revision is eligible again", ReferenceEquals(fixture.Freeze()?.Frame, keys));
        fixture.Session.Document.ResourceId = Guid.NewGuid();
        check("the actual capture entry rejects a shown frame from a previous source resource", fixture.Freeze() is null);
    }

    private static void StateGuards(Action<string, bool> check)
    {
        var owner = new EditorSession(Document()); var origin = new PointD(-1920, 100); object monitors = new(), lifetime = new();
        var frame = Input(origin); var state = new PresentationCaptureState(owner, owner.Document, owner.Document.ResourceId,
            owner.ContentVersion, owner.EditScopeVersion, origin, monitors, lifetime, frame);
        check("capture state accepts only its exact owner document source version and physical context", state.IsCurrent(owner, origin, monitors, lifetime));
        check("another session cannot read the prior session's captured keys", !state.IsCurrent(new(owner.Document.Clone()), origin, monitors, lifetime));
        check("changed monitor identity or physical origin rejects retained input", !state.IsCurrent(owner, origin, new object(), lifetime) && !state.IsCurrent(owner, origin + new PointD(1, 0), monitors, lifetime));
        check("new runtime lifetime prevents capture resurrection", !state.IsCurrent(owner, origin, monitors, new object()));
        var group = new SceneGraph(owner.Document).InsertGroup(new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
        owner.EnterScope(group.Id); owner.LeaveScope();
        check("scope out and back cannot revive a retained screenshot frame", owner.EditScopeId is null && !state.IsCurrent(owner, origin, monitors, lifetime));
        state = state with { ScopeVersion = owner.EditScopeVersion };
        owner.NotifyChanged();
        check("source content changes reject a retained screenshot frame", !state.IsCurrent(owner, origin, monitors, lifetime));
        state = state with { ContentVersion = owner.ContentVersion };
        owner.Document.ResourceId = Guid.NewGuid();
        check("a different document resource identity rejects input even before a content notification", !state.IsCurrent(owner, origin, monitors, lifetime));
        var snapshot = state.Freeze();
        check("the frozen render input carries only immutable frame and origin without a service or source graph", ReferenceEquals(snapshot.Frame, frame) && snapshot.Origin == origin && typeof(PresentationCaptureSnapshot).GetProperties().All(property => property.PropertyType == typeof(PresentationFrame) || property.PropertyType == typeof(PointD)));
    }

    private static void ScreenshotPixels(Action<string, bool> check)
    {
        var source = Document(); var assets = new AssetLibrary(); var origin = new PointD(-1920, 120);
        var input = Input(origin); var frozen = new PresentationCaptureSnapshot(input, origin);
        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly, source.Viewport, [], 0, 0);
        string original = JsonSerializer.Serialize(source);
        var raster = SnapshotImageCommand.Render(source, assets.Resources, selection, null, [source.Viewport], screenOrigin: origin, presentation: frozen);
        using var pixels = SKBitmap.Decode(raster.PngBytes);
        check("explicit canvas screenshot excludes cursor click and trail metadata from legacy region modules", pixels.GetPixel(80, 60).Alpha == 0 && pixels.GetPixel(140, 60).Alpha == 0 && pixels.GetPixel(50, 100).Alpha == 0);
        check("explicit screenshot freezes the actually displayed semantic keyboard card", pixels.GetPixel(30, 190).Alpha > 0 && pixels.Pixels.Skip(320 * 150).Any(pixel => pixel.Alpha > 0));
        using var expectedRenderer = new SceneRenderer(); frozen.ApplyTo(expectedRenderer);
        using var expected = SKBitmap.Decode(expectedRenderer.ExportPng(source, assets, transparent: true));
        check("screenshot pixels match one renderer evaluation of the frozen frame and origin", pixels.Pixels.SequenceEqual(expected.Pixels));
        using var anotherOrigin = SKBitmap.Decode(SnapshotImageCommand.Render(source, assets.Resources, selection, null, [source.Viewport],
            screenOrigin: default, presentation: frozen with { Origin = default }).PngBytes);
        check("global pointer coordinates and capture origins cannot move layer-local keyboard pixels", anotherOrigin.Pixels.SequenceEqual(pixels.Pixels));
        using var defaultRenderer = new SceneRenderer();
        using var emptyExport = SKBitmap.Decode(defaultRenderer.ExportPng(source, assets, transparent: true));
        check("ordinary transparent PNG export remains static and never includes recent input", emptyExport.Pixels.All(pixel => pixel.Alpha == 0));
        using var emptySnapshot = SKBitmap.Decode(SnapshotImageCommand.Render(source, assets.Resources, selection, null, [source.Viewport]).PngBytes);
        check("snapshot rendering without an explicit frozen input also remains empty", emptySnapshot.Pixels.All(pixel => pixel.Alpha == 0));
        using var blue = new SKBitmap(320, 220); blue.Erase(SKColors.Blue);
        using var blueImage = SKImage.FromBitmap(blue); using var encoded = blueImage.Encode(SKEncodedImageFormat.Png, 100);
        var desktop = new CapturedDesktop(new(-1920, 120, 320, 220), encoded.ToArray());
        using var backgroundOnly = SKBitmap.Decode(SnapshotImageCommand.Render(source, assets.Resources,
            selection with { Source = CaptureSource.Background }, desktop, [source.Viewport], screenOrigin: origin, presentation: frozen).PngBytes);
        check("desktop-only screenshot excludes the frozen presentation along with other canvas layers", backgroundOnly.Pixels.All(pixel => pixel == SKColors.Blue));
        using var composite = SKBitmap.Decode(SnapshotImageCommand.Render(source, assets.Resources,
            selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, [source.Viewport], screenOrigin: origin, presentation: frozen).PngBytes);
        check("desktop and canvas screenshot composites only the frozen keyboard over fresh desktop pixels", composite.GetPixel(30, 190) != SKColors.Blue && composite.GetPixel(80, 60) == SKColors.Blue && composite.GetPixel(240, 60) == SKColors.Blue);
        check("freezing screenshots leaves authored settings assets and JSON untouched", JsonSerializer.Serialize(source) == original && assets.Count == 0 && !original.Contains("PressedKeys", StringComparison.Ordinal) && !original.Contains("Ctrl", StringComparison.Ordinal));
        var session = new EditorSession(source.Clone()); SnapshotImageCommand.Insert(session, raster);
        check("inserting an explicit screenshot stores ordinary PNG pixels rather than a runtime input frame", session.Document.Elements.Last().Kind == ElementKind.Image && session.Assets.Count == 1 && !JsonSerializer.Serialize(session.Document).Contains("PressedKeys", StringComparison.Ordinal));
    }

    private static readonly PresentationRuntimeDemand Demand = new(true, true, true, true, false, 900, 450, 800);
    private static PresentationFrame Input(PointD origin) => new(1, 1, TimeSpan.FromMilliseconds(1000),
        pressedKeys: [new(0x11, "Ctrl", true), new(0x43, "C")],
        recentChords: [new([new(0x11, "Ctrl", true), new(0x43, "C")], TimeSpan.FromMilliseconds(1000), true)], cursorScreen: origin + new PointD(80, 60),
        clicks: [new(origin + new PointD(140, 60), PresentationMouseButton.Left, TimeSpan.FromMilliseconds(900))],
        trail: [new(origin + new PointD(20, 100), TimeSpan.FromMilliseconds(900)), new(origin + new PointD(100, 100), TimeSpan.FromMilliseconds(1000))]);
    private static SceneDocument Document()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 320, 220), Elements = [new()
        {
            Kind = ElementKind.Presentation, Width = 320, Height = 220,
            Presentation = new() { Modules = PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail },
            Style = new() { Foreground = 0xFFFF00FF, Background = 0xEE101010, FontSize = 20 }
        }] };
        document.Canvas.Grid.Visible = false; document.Canvas.BackgroundColor = 0; return document;
    }

    private sealed class CaptureFixture : IDisposable
    {
        internal EditorSession Session { get; } = new(Document());
        internal readonly List<FakeSource> Sources = [];
        internal PresentationRuntimeBroker Runtime { get; }
        private readonly DesktopSession desktop;
        internal CaptureFixture()
        {
            // A field-only host supplies the immutable monitor identity. It never constructs
            // an HWND, registers a hotkey, acquires input, shows UI or calls native capture.
            var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
            IReadOnlyList<MonitorInfo> monitors = [new("synthetic", new(0, 0, 320, 220), new(0, 0, 320, 220), 96, 96, true)];
            Set(host, "_monitors", monitors);
            var editor = new EditorController(Session);
            desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
            Set(desktop, "_host", host); Set(desktop, "_editor", editor);
            Runtime = new(Dispatcher.CurrentDispatcher, () => { }, (id, _, _, _) =>
            { var source = new FakeSource(id); Sources.Add(source); return Task.FromResult<IPresentationInputSource>(source); }, () => TimeSpan.FromMilliseconds(1000));
            Set(desktop, "_presentationRuntime", Runtime);
            Set(desktop, "_presentationOwner", Session); Set(desktop, "_presentationDocument", Session.Document);
            Set(desktop, "_presentationResource", Session.Document.ResourceId);
            Set(desktop, "_presentationVersion", Session.ContentVersion); Set(desktop, "_presentationPlanScope", Session.EditScopeVersion);
            Set(desktop, "_presentationPlanOrigin", editor.ScreenOrigin); Set(desktop, "_presentationPlanMonitors", monitors);
            Runtime.Configure(Demand, true, new object(), "paused"); Runtime.Toggle();
        }
        internal void Show(PresentationFrame frame) { Set(desktop, "_presentedPresentationFrame", frame); Set(desktop, "_presentedPresentationLifetime", Runtime.Lifetime); Set(desktop, "_presentedPresentationRevision", Session.PreviewRevision); }
        internal void ClearShown() => Set(desktop, "_presentedPresentationFrame", null);
        internal PresentationCaptureSnapshot? Freeze() => (PresentationCaptureSnapshot?)typeof(DesktopSession).GetMethod("FreezePresentationCapture", Hidden)!.Invoke(desktop, [Session]);
        internal void Pause(bool retain) => typeof(DesktopSession).GetMethod("PausePresentationRuntime", Hidden)!.Invoke(desktop, [retain]);
        public void Dispose() => Runtime.Dispose();
    }
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);
    private sealed class FakeSource(long epoch) : IPresentationInputSource
    {
        public long Epoch => epoch;
        public bool IsRunning { get; private set; } = true;
        public Task Completion => Task.CompletedTask;
        public event Action? Available { add { } remove { } }
        internal int StopCount;
        public PresentationObservedBatch Drain() => new(Epoch, [], null, false);
        public bool Stop() { StopCount++; IsRunning = false; return true; }
        public void Dispose() => IsRunning = false;
    }
}
