using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Explicit export/capture media leases, without windows, native sources or OS clipboard access.</summary>
internal static class WindowRasterCaptureTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check)
    { ActualShownAndLifetime(check); GuardedSource(check); RasterPixels(check); Task.Run(() => StampModesAsync(check)).GetAwaiter().GetResult(); }

    private static void ActualShownAndLifetime(Action<string, bool> check)
    {
        var fixture = new CaptureFixture(); var source = fixture.Session.Document.Clone(); var assets = fixture.Session.Assets;
        var frozen = fixture.Freeze();
        check("raster capture chooses actual shown media rather than a newer unpresented registry frame", frozen is not null && frozen.Frames.TryGet(fixture.Key, out var chosen) && chosen.Id.Sequence == 1);
        using (var future = fixture.Runtime.CaptureFrames())
            check("the controlled registry actually contains a different later media frame", future is not null && future.TryGet(fixture.Key, out var latest) && latest.Id.Sequence == 2);
        using var retained = frozen!.Retain(); frozen.Dispose();
        check("raster operation retain owns an independent collection handle", retained.Frames.TryGet(fixture.Key, out var stillShown) && stillShown.Id.Sequence == 1);
        using (var selected = retained.RetainFrame(source.ResourceId, new(null, fixture.Image.Id), fixture.Image.WindowSource!.BindingIntent))
            check("individual explicit source lookup retains the same shown identity", selected is not null && selected.Id.Sequence == 1);
        using (var wrong = retained.RetainFrame(source.ResourceId, new(null, fixture.Image.Id), Guid.NewGuid()))
            check("explicit source lookup cannot relabel shown pixels as a new binding", wrong is null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(async () =>
        {
            await release.Task.ConfigureAwait(false);
            using var renderer = new SceneRenderer(); retained.ApplyTo(renderer);
            return renderer.ExportPng(source, assets, true);
        });
        fixture.Dispose(); release.SetResult();
        using var afterAwait = SKBitmap.Decode(work.GetAwaiter().GetResult());
        check("frozen shown media survives producer and presented-state disposal across an asynchronous render", afterAwait.GetPixel(40, 40) == SKColors.Red);
        check("disposed raster handles cannot be applied or resurrected", Throws<ObjectDisposedException>(() => frozen.Retain()));
    }

    private static void GuardedSource(Action<string, bool> check)
    {
        using (var fixture = new CaptureFixture())
        {
            ((WindowFrameSet)typeof(DesktopSession).GetField("_presentedWindowFrames", Hidden)!.GetValue(fixture.Desktop)!).Dispose();
            Set(fixture.Desktop, "_presentedWindowFrames", null);
            check("missing shown frame returns static fallback rather than adopting unpresented latest", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            Set(fixture.Desktop, "_windowPlanVersion", fixture.Session.ContentVersion + 1);
            check("content-version mismatch rejects the shown bundle", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            fixture.Editor.ScreenOrigin += new PointD(1, 0);
            check("changed physical origin rejects stale media presentation", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            Set(fixture.Desktop, "_windowPlanMonitors", new object());
            check("changed monitor membership rejects stale media presentation", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            var group = new SceneGraph(fixture.Session.Document).InsertGroup(new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] });
            fixture.Session.EnterScope(group.Id); fixture.Session.LeaveScope();
            check("scope out and back cannot resurrect a prior raster capture context", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            fixture.Runtime.Configure(new object(), fixture.Session.Document.ResourceId, [], false, new object());
            check("changed runtime render lifetime rejects an older shown collection", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            fixture.Session.Document.ResourceId = Guid.NewGuid();
            check("reused mutable document reference cannot relabel old frames to a new resource identity", fixture.Freeze() is null);
        }
        using (var fixture = new CaptureFixture())
        {
            fixture.Session.BeginTransaction("foreign");
            check("a foreign uncommitted transaction cannot borrow a committed shown capture", fixture.Freeze() is null && fixture.Session.IsTransactionActive);
            fixture.Session.Cancel();
        }
        using (var fixture = new CaptureFixture())
        {
            var other = new EditorSession(fixture.Session.Document.Clone(), fixture.Session.Assets);
            check("same resource in another editor owner cannot borrow captured pixels", fixture.Freeze(other) is null);
        }
    }

    private static void RasterPixels(Action<string, bool> check)
    {
        using var fixture = new CaptureFixture(); using var frozen = fixture.Freeze();
        var scene = fixture.Session.Document; var assets = fixture.Session.Assets;
        string before = JsonSerializer.Serialize(scene); int resources = assets.Count;
        using var renderer = new SceneRenderer(); frozen!.ApplyTo(renderer);
        using var export = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("explicit user raster export renders shown window media", export.GetPixel(40, 40) == SKColors.Red);
        check("window media export does not implicitly include recent keyboard input", renderer.PresentationFrame is null && export.GetPixel(170, 90).Alpha == 0);
        using var jpeg = SKBitmap.Decode(renderer.ExportJpeg(scene, assets));
        check("explicit JPEG uses the same frozen media bundle", jpeg.GetPixel(40, 40).Red > 220 && jpeg.GetPixel(40, 40).Blue < 25);
        using var defaults = new SceneRenderer(); using var fallback = SKBitmap.Decode(defaults.ExportPng(scene, assets, true));
        check("headless or thumbnail renderer without explicit media context remains static fallback", fallback.GetPixel(40, 40) == SKColors.Blue);
        var selection = new CaptureSelection(CaptureShape.Rectangle, CaptureSource.CanvasOnly, scene.Viewport, [], 0, 0);
        using var capture = SKBitmap.Decode(SnapshotImageCommand.Render(scene, assets.Resources, selection, null, [scene.Viewport], windows: frozen).PngBytes);
        check("explicit region screenshot receives the exact same frozen window frame", capture.GetPixel(40, 40) == SKColors.Red);
        using var blue = new SKBitmap(240, 120); blue.Erase(SKColors.Blue); using var blueImage = SKImage.FromBitmap(blue);
        using var encoded = blueImage.Encode(SKEncodedImageFormat.Png, 100); var desktop = new CapturedDesktop(new(0, 0, 240, 120), encoded.ToArray());
        using var background = SKBitmap.Decode(SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.Background }, desktop, [scene.Viewport], windows: frozen).PngBytes);
        check("desktop-only capture never leaks the frozen author media layer", background.Pixels.All(pixel => pixel == SKColors.Blue));
        using var composite = SKBitmap.Decode(SnapshotImageCommand.Render(scene, assets.Resources, selection with { Source = CaptureSource.BackgroundAndCanvas }, desktop, [scene.Viewport], windows: frozen).PngBytes);
        check("composite screenshot combines fresh background and the fixed shown media frame", composite.GetPixel(40, 40) == SKColors.Red && composite.GetPixel(220, 20) == SKColors.Blue);
        check("explicit raster operations never mutate project source or inject media assets", JsonSerializer.Serialize(scene) == before && assets.Count == resources && fixture.Session.UndoCount == 0);
    }

    private sealed class CaptureFixture : IDisposable
    {
        internal EditorSession Session { get; }
        internal CanvasElement Image { get; }
        internal EditorController Editor { get; }
        internal DesktopSession Desktop { get; }
        internal WindowSourceRuntime Runtime { get; }
        internal WindowSourceKey Key { get; }
        private readonly WindowFrameLease red, blue;
        private readonly WindowFrameSet shown, future;
        internal CaptureFixture()
        {
            var assets = new AssetLibrary(); using var fallback = new SKBitmap(4, 4); fallback.Erase(SKColors.Blue);
            using var fallbackImage = SKImage.FromBitmap(fallback); using var png = fallbackImage.Encode(SKEncodedImageFormat.Png, 100);
            Image = new() { Kind = ElementKind.Image, Width = 100, Height = 100, AssetId = assets.Add(png.ToArray()), WindowSource = new() { BindingIntent = Guid.NewGuid(), Fit = WindowSourceFit.Stretch }, Style = new() { StrokeWidth = 0 } };
            var source = new SceneDocument { Viewport = new(0, 0, 240, 120), Elements = [Image, new() { Kind = ElementKind.Presentation,
                Width = 100, Height = 100, Transform = Affine2D.Translation(130, 0), Presentation = new(), Style = new() { Foreground = 0xFFFFFFFF, Background = 0xFF222222 } }] };
            source.Canvas.Grid.Visible = false; source.Canvas.BackgroundColor = 0;
            Session = new(source, assets); Editor = new(Session);
            var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
            IReadOnlyList<MonitorInfo> monitors = [new("synthetic", new(0, 0, 240, 120), new(0, 0, 240, 120), 96, 96, true)]; Set(host, "_monitors", monitors);
            Desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession)); Set(Desktop, "_editor", Editor); Set(Desktop, "_host", host);
            Runtime = new(Dispatcher.CurrentDispatcher, (_, _) => { }); var epoch = Runtime.RenderEpoch;
            Key = new(source.ResourceId, new(null, Image.Id));
            using var redPixels = new SKBitmap(4, 4); redPixels.Erase(SKColors.Red);
            red = WindowFrameLease.FromBitmapCopy(Key, Image.WindowSource.BindingIntent, new(Guid.NewGuid(), 1, 1), redPixels);
            blue = WindowFrameLease.FromBitmapCopy(Key, Image.WindowSource.BindingIntent, new(Guid.NewGuid(), 1, 2), fallback);
            shown = WindowFrameSet.Capture(epoch, [red]); future = WindowFrameSet.Capture(epoch, [blue]);
            Set(Runtime, "frames", future.Retain());
            Set(Desktop, "_windowRuntime", Runtime); Set(Desktop, "_windowPlanSession", Session); Set(Desktop, "_windowPlanDocument", source);
            Set(Desktop, "_windowPlanVersion", Session.ContentVersion); Set(Desktop, "_windowPlanScope", Session.EditScopeVersion);
            Set(Desktop, "_windowPlanOrigin", Editor.ScreenOrigin); Set(Desktop, "_windowPlanMonitors", monitors);
            Set(Desktop, "_windowSourceLeaves", SceneEvaluation.Build(source).Leaves);
            Set(Desktop, "_windowDemands", new[] { new WindowRuntimeDemand(new(null, Image.Id), Image.WindowSource.BindingIntent) });
            Set(Desktop, "_presentedWindowFrames", shown.Retain()); Set(Desktop, "_presentedWindowOwnerContext", epoch);
            Set(Desktop, "_presentedCanvasContext", new CanvasPreviewContext(Session, monitors, default, false, false, false, DocumentEpoch: source, WindowEpoch: epoch));
        }
        internal WindowRasterCapture? Freeze(EditorSession? target = null) => (WindowRasterCapture?)typeof(DesktopSession).GetMethod("FreezeWindowSourceCapture", Hidden)!.Invoke(Desktop, [target ?? Session]);
        public void Dispose()
        {
            Set(Desktop, "_disposed", true); Runtime.Dispose();
            ((WindowFrameSet?)typeof(DesktopSession).GetField("_presentedWindowFrames", Hidden)!.GetValue(Desktop))?.Dispose();
            shown.Dispose(); future.Dispose(); red.Dispose(); blue.Dispose();
        }
    }
    private static async Task StampModesAsync(Action<string, bool> check)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "window-raster-stamps", Guid.NewGuid().ToString("N"));
        var assets = new AssetLibrary(); using var blue = new SKBitmap(4, 4); blue.Erase(SKColors.Blue);
        using var blueImage = SKImage.FromBitmap(blue); using var oldPng = blueImage.Encode(SKEncodedImageFormat.Png, 100);
        var sourceImage = new CanvasElement { Kind = ElementKind.Image, Width = 100, Height = 100,
            AssetId = assets.Add(oldPng.ToArray()), WindowSource = new() { BindingIntent = Guid.NewGuid(), Fit = WindowSourceFit.Contain },
            ImageCrop = new(.25, 0, .5, 1), Style = new() { StrokeWidth = 0 } };
        var definition = new PrefabDefinition { Graph = new() { Elements = [sourceImage] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        var document = new SceneDocument { Viewport = new(0, 0, 100, 100), Definitions = [definition], Instances = [instance], RootOrder = [instance.Id] };
        document.Canvas.Grid.Visible = false;
        var session = new EditorSession(document, assets); session.SetSelection([instance.Id]);
        string untouched = JsonSerializer.Serialize(document); int originalResources = assets.Count;
        using var red = new SKBitmap(4, 4); red.Erase(SKColors.Red); object epoch = new();
        using var lease = WindowFrameLease.FromBitmapCopy(new(document.ResourceId, new(definition.Id, sourceImage.Id)), sourceImage.WindowSource.BindingIntent,
            new(Guid.NewGuid(), 1, 1), red);
        using var frames = WindowFrameSet.Capture(epoch, [lease]); using var shown = new WindowRasterCapture(frames, epoch);
        using var package = new WindowDocumentPackageCapture(document, assets.Resources,
            (address, intent) => shown.RetainFrame(document.ResourceId, address, intent));
        var prepared = package.Prepare(); var available = new AssetLibrary(); foreach (var resource in prepared.Resources) available.Add(resource);
        var context = new StampRenderContext(prepared.Document, available, WindowFallbacksPrepared: true);
        var library = new StampLibrary(directory); await library.LoadAsync().ConfigureAwait(false);
        foreach (var mode in new[] { StampSaveMode.RasterMerge, StampSaveMode.EditableGroup, StampSaveMode.SharedPrefab })
        {
            var entry = await library.SaveSelectionAsync(session, "shown window " + mode, mode, renderContext: context).ConfigureAwait(false);
            var loaded = await library.LoadStampAsync(entry.Id).ConfigureAwait(false);
            using var renderer = new SceneRenderer(); using var pixels = SKBitmap.Decode(renderer.ExportPng(loaded.Document, loaded.Assets, true));
            check($"{mode} stamp remains offline-renderable with shown pixels after definition member remapping", pixels.GetPixel(pixels.Width / 2, pixels.Height / 2) == SKColors.Red);
            check($"{mode} stamp applies normalized full-window crop once and retains transparent contain padding", pixels.GetPixel(2, pixels.Height / 2).Alpha == 0);
            var source = await library.LoadSourceAsync(entry.Id).ConfigureAwait(false);
            var sourceLeaves = SceneEvaluation.Build(source.Document).Leaves;
            check($"{mode} source preserves editable descriptor and original normalized crop", sourceLeaves.Count == 1 && sourceLeaves[0].RenderElement.WindowSource is not null && sourceLeaves[0].RenderElement.ImageCrop == sourceImage.ImageCrop);
            if (mode == StampSaveMode.RasterMerge)
                check("raster stamp publishes an ordinary PNG image while its editable source remains separately available", loaded.Document.Elements is [{ Kind: ElementKind.Image, WindowSource: null }] && loaded.Document.Instances.Count == 0);
            var thumb = await library.GetThumbnailAsync(entry.Id).ConfigureAwait(false); using var thumbnail = SKBitmap.Decode(thumb);
            check($"{mode} thumbnail reads a static prepared PNG without a live frame bundle", thumbnail.GetPixel(thumbnail.Width / 2, thumbnail.Height / 2).Red > 240);
        }
        foreach (bool changeAddress in new[] { false, true })
        {
            var unrelated = prepared.Document.Clone(); var member = unrelated.Definitions.Single().Graph.Elements.Single();
            if (changeAddress) member.Id = Guid.NewGuid(); else member.WindowSource!.BindingIntent = Guid.NewGuid();
            var rejectedContext = context with { Document = unrelated };
            var entry = await library.SaveSelectionAsync(session, "unrelated prepared media " + changeAddress,
                StampSaveMode.RasterMerge, renderContext: rejectedContext).ConfigureAwait(false);
            var loaded = await library.LoadStampAsync(entry.Id).ConfigureAwait(false);
            using var renderer = new SceneRenderer(); using var pixels = SKBitmap.Decode(renderer.ExportPng(loaded.Document, loaded.Assets, true));
            check(changeAddress ? "stamp source address mismatch preserves original fallback instead of borrowing same-intent pixels" :
                "stamp binding-intent mismatch preserves original fallback instead of borrowing same-address pixels",
                pixels.GetPixel(pixels.Width / 2, pixels.Height / 2) == SKColors.Blue);
        }
        var foreign = prepared.Document.Clone(); foreign.ResourceId = Guid.NewGuid(); bool rejected = false;
        try { await library.SaveSelectionAsync(session, "foreign owner", renderContext: context with { Document = foreign }).ConfigureAwait(false); }
        catch (InvalidDataException) { rejected = true; }
        check("stamp preparation rejects a different resource owner before publication", rejected && library.Items.Count == 5);
        check("stamp media projection never changes active source bytes history assets or shared definition fields", JsonSerializer.Serialize(document) == untouched && assets.Count == originalResources && session.UndoCount == 0);
        Console.WriteLine("WINDOW_RASTER_STAMPS " + directory);
    }
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
}
