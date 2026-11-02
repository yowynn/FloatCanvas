using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

/// <summary>Real desktop commands, with owned metadata and simulated pixels, on an undisplayed desktop only.</summary>
internal static class WindowSourceCommandTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        if (!DesktopName().StartsWith("FloatCanvas.Tests.", StringComparison.Ordinal) || PresentationDesktopAvailability.IsCurrentInputDesktop())
            throw new InvalidOperationException("Use --isolated-ui-desktop --window-source-commands-desktop-only.");
        if (Application.Current is not null) throw new InvalidOperationException("Run in a fresh isolated child process.");
        string root = Path.Combine(output ?? Path.GetTempPath(), "window-commands-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ApplicationPaths.Configure(["--profile", Path.Combine(root, "profile"), "--primary-screen"]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var hotkeys = FreeHotkeys();
        new UserSettings { Theme = "light", Language = "en", Toggle = hotkeys.Item1, Hide = hotkeys.Item2,
            PreferGpuAcceleration = false, ShowFloatingLogo = false }.Save();
        var bounds = new PixelBounds(-23000, -23000, 640, 360);
        Func<IReadOnlyList<MonitorInfo>> monitors = () => [new("window-command-test", bounds, bounds, 96, 96, true)];
        using var host = (OverlayHost)Activator.CreateInstance(typeof(OverlayHost), Hidden, null, [OverlayMonitorScope.PrimaryScreen, monitors, false], null)!;
        using var desktop = new DesktopSession(app, host);
        Field<System.Windows.Forms.NotifyIcon>(desktop, "_tray").Visible = false;
        Field<Window>(desktop, "_toolbar").Opacity = 0;
        ((IDisposable?)typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.GetValue(host))?.Dispose();
        typeof(OverlayHost).GetField("_keyboardIsolation", Hidden)!.SetValue(host, null);
        using var target = new OwnTarget();
        var editor = Field<EditorController>(desktop, "_editor"); var owner = editor.Session;
        var fixture = new Fixture(desktop, editor, owner, target.Target);
        check("command fixture reads only its own undisplayed HWND identity without source enumeration", target.Target.IsCurrent && target.Target.ProcessId == Environment.ProcessId);
        AddAndRebind(fixture, check);
        CenteredInsertion(fixture, check);
        Freeze(fixture, check, root);
        Stale(fixture, check);
        Pending(fixture, check);
        Rejected(fixture, check);
        fixture.Reset();
        check("native capture services owned by commands are released when their owner is forgotten", fixture.Sources.All(source => source.DisposeCount == 1 && source.Completion.IsCompleted));
        check("command acceptance never acquires input on the user's desktop", !PresentationDesktopAvailability.IsCurrentInputDesktop() && !host.IsKeyboardIsolationActive && host.Diagnostics.All(pair => pair.Bounds == bounds));
        PendingInvalidation(fixture, check);
        Console.WriteLine("WINDOW_SOURCE_COMMAND_EVIDENCE " + root);
    }

    private sealed class Fixture(DesktopSession desktop, EditorController editor, EditorSession owner, WindowCaptureTarget target)
    {
        internal readonly DesktopSession Desktop = desktop;
        internal readonly EditorController Editor = editor;
        internal readonly EditorSession Owner = owner;
        internal readonly List<FakeSource> Sources = [];
        internal void Reset(SceneDocument? document = null)
        {
            Editor.Cancel(); Invoke(Desktop, "ForgetWindowSourceBindings", Owner);
            Owner.ReplaceDocument(document ?? new() { Viewport = new(0, 0, 640, 360) });
            Editor.BillboardMode = false; Editor.Tool = EditorTool.Select;
            SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(null));
            Pump();
            // ReplaceDocument is a fixture shortcut; settle the same display/camera setup as the real document-open path.
            Invoke(Desktop, "SynchronizeFlatGridOrigin");
        }
        internal DesktopSession.PreparedWindowSource Prepare(SKColor? color = null, RectD? crop = null, WindowSourceFit fit = WindowSourceFit.Contain,
            WindowCaptureSize? contentSize = null)
        {
            var source = new FakeSource(target); Sources.Add(source);
            return new(source, new() { BindingIntent = Guid.NewGuid(), TitleHint = "Owned synthetic source", ProcessNameHint = "fixture", WindowClassHint = "STATIC", Fit = fit },
                Png(color ?? SKColors.Crimson), contentSize ?? new(4000, 2000), crop);
        }
        internal void SetPrepare(Func<WindowSourceOperationGuard, RectD?, WindowSourceFit, CancellationToken, Task<DesktopSession.PreparedWindowSource?>> prepare)
            => typeof(DesktopSession).GetField("_prepareWindowSource", Hidden)!.SetValue(Desktop, prepare);
        internal Task Add() => (Task)Invoke(Desktop, "AddWindowSourceAsync")!;
        internal Task Rebind(Guid id) => (Task)Invoke(Desktop, "RebindWindowSourceAsync", id)!;
        internal Task Freeze(params Guid[] ids) => (Task)Invoke(Desktop, "FreezeWindowSourcesAsync", (object)ids)!;
        internal Task Crop(Guid id) => (Task)Invoke(Desktop, "CropWindowSourceAsync", id)!;
    }

    private static void CenteredInsertion(Fixture f, Action<string, bool> check)
    {
        foreach (var mode in new[] { "perspective", "flat", "foreground", "background", "perspective-scope" })
        {
            var document = new SceneDocument { Viewport = new(0, 0, 640, 360) };
            var projection = document.Canvas.Projection; projection.Enabled = mode != "flat";
            projection.PlaneBounds = document.Viewport;
            projection.Matrix = new(1, .05, 80, .02, 1, 35, .0002, .0005, 1);
            projection.Quad = EditorController.Corners(document.Viewport).Select(projection.Matrix.TransformPoint).ToList();
            Guid? scope = null;
            if (mode == "perspective-scope")
            {
                var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [],
                    Transform = Affine2D.Translation(20, 30).Compose(Affine2D.Scale(2, .5)) };
                document.Groups.Add(group); document.RootOrder = [group.Id]; scope = group.Id;
            }
            f.Reset(document);
            var root = mode == "foreground" ? RootCanvasKind.Foreground : mode == "background" ? RootCanvasKind.Background : RootCanvasKind.Main;
            f.Owner.SetRootCanvas(root); if (scope is { } id) f.Owner.EnterScope(id);
            var prepared = f.Prepare(contentSize: new(32, 24));
            f.SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared));
            Await(f.Add());
            var source = f.Owner.Document.Elements.Single(); var sourceWorld = new SceneGraph(f.Owner.Document).ToWorld(source);
            var monitor = Field<OverlayHost>(f.Desktop, "_host").PrimaryMonitor;
            var visible = new RectD(monitor.WorkArea.Left - f.Editor.CanvasOrigin.X, monitor.WorkArea.Top - f.Editor.CanvasOrigin.Y,
                monitor.WorkArea.Width, monitor.WorkArea.Height).Intersect(f.Owner.Document.Viewport);
            var mapping = f.Editor.CurrentCanvasMapping;
            check(mode + " actual window-source command centers its image on the visible screen area", mapping.ElementToScreen(sourceWorld)
                .TransformPoint(new(sourceWorld.Width / 2, sourceWorld.Height / 2)).DistanceTo(visible.Center) < 1e-6 && source.GroupId == scope);
            Invoke(f.Desktop, "InsertImages", f.Owner, new NormalizedImage[] { new(prepared.Png, 32, 24) }, false, 1d);
            var ordinary = f.Owner.Document.Elements.Last(); var ordinaryWorld = new SceneGraph(f.Owner.Document).ToWorld(ordinary);
            check(mode + " actual ordinary image and window-source commands share geometry and root without double scope conversion",
                ordinary.GroupId == scope && sourceWorld.Transform == ordinaryWorld.Transform && sourceWorld.Width == ordinaryWorld.Width &&
                sourceWorld.Height == ordinaryWorld.Height && SceneCanvasRoots.Resolve(f.Owner.Document, source.Id) == root &&
                SceneCanvasRoots.Resolve(f.Owner.Document, ordinary.Id) == root);
        }
    }

    private static void AddAndRebind(Fixture f, Action<string, bool> check)
    {
        var group = new CanvasGroup { Name = "Affine scope", OrderMode = GroupOrderMode.AtomicBlock,
            Transform = Affine2D.Translation(20, 30).Compose(Affine2D.Scale(2, .5)), ChildOrder = [] };
        f.Reset(new() { Viewport = new(0, 0, 640, 360), Groups = [group], RootOrder = [group.Id] }); f.Owner.EnterScope(group.Id);
        f.Editor.Tool = EditorTool.Image; f.Editor.Style.Opacity = .37;
        var crop = new RectD(.1, .2, .5, .5); var prepared = f.Prepare(crop: crop); var capture = (FakeSource)prepared.Capture;
        f.SetPrepare((_, requestedCrop, fit, _) => { check("Add requests a full source with default Contain fit", requestedCrop is null && fit == WindowSourceFit.Contain); return Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared); });
        string original = JsonSerializer.Serialize(f.Owner.Document); int assets = f.Owner.Assets.Count; Await(f.Add());
        var added = f.Owner.Document.Elements.Single(); var world = new SceneGraph(f.Owner.Document).ToWorld(added); string asset = added.AssetId!;
        check("real Add commits the complete first PNG and independent persistent source descriptor", f.Owner.Assets.Get(asset).Data.Span.SequenceEqual(prepared.Png) && added.WindowSource?.BindingIntent == prepared.Descriptor.BindingIntent && !ReferenceEquals(added.WindowSource, prepared.Descriptor) && f.Owner.Assets.Count == assets + 1);
        check("real Add fits native cropped content ratio and converts its world placement into scope once", added.GroupId == group.Id && Near(ElementGeometry.Bounds(world, false), new(64, 52, 512, 256)) && added.ImageCrop == crop && added.WindowSource!.Fit == WindowSourceFit.Contain && added.Style.Opacity == .37);
        check("real Add is one history operation and transfers capture ownership without disposing it", f.Owner.UndoCount == 1 && !f.Owner.IsTransactionActive && f.Owner.Selection.SetEquals([added.Id]) && f.Editor.Tool == EditorTool.Select && prepared.Transferred && capture.DisposeCount == 0);
        bool undone = f.Owner.Undo(); string afterUndo = JsonSerializer.Serialize(f.Owner.Document);
        if (!undone || afterUndo != original || capture.DisposeCount != 0)
        { Console.WriteLine("ADD_UNDO_RESULT " + JsonSerializer.Serialize(new { undone, capture.DisposeCount, Original = original, Actual = afterUndo })); }
        check("one Undo removes the complete Add without destroying its retained binding", undone && afterUndo == original && capture.DisposeCount == 0);
        check("one Redo restores the same source intent and complete fallback PNG", f.Owner.Redo() && f.Owner.Document.Elements.Single().WindowSource!.BindingIntent == prepared.Descriptor.BindingIntent && f.Owner.Document.Elements.Single().AssetId == asset);
        f.Owner.SetSelection([added.Id]);
        var replacement = f.Prepare(SKColors.DodgerBlue, crop, WindowSourceFit.Stretch); string before = JsonSerializer.Serialize(f.Owner.Document); int undo = f.Owner.UndoCount;
        f.SetPrepare((_, requestedCrop, fit, _) => { check("Rebind retains the current crop and fit as preparation inputs", requestedCrop == crop && fit == WindowSourceFit.Contain); return Task.FromResult<DesktopSession.PreparedWindowSource?>(replacement); });
        Await(f.Rebind(added.Id)); var rebound = f.Owner.Document.Elements.Single();
        check("same-hint Rebind creates a new intent while preserving existing logical placement and opacity", rebound.WindowSource!.TitleHint == prepared.Descriptor.TitleHint && rebound.WindowSource.BindingIntent != prepared.Descriptor.BindingIntent && rebound.WindowSource.BindingIntent == replacement.Descriptor.BindingIntent && rebound.Transform == added.Transform && rebound.Width == added.Width && rebound.Height == added.Height && rebound.Style.Opacity == added.Style.Opacity);
        check("real Rebind commits one undo and transfers the newly prepared capture", f.Owner.UndoCount == undo + 1 && replacement.Transferred && ((FakeSource)replacement.Capture).DisposeCount == 0 && !f.Owner.IsTransactionActive);
        string reboundAsset = rebound.AssetId!;
        check("Rebind Undo restores the exact previous live descriptor and fallback", f.Owner.Undo() && JsonSerializer.Serialize(f.Owner.Document) == before && f.Owner.Document.Elements.Single().WindowSource!.BindingIntent == prepared.Descriptor.BindingIntent);
        check("Rebind Redo restores the new intent and new complete pixels", f.Owner.Redo() && f.Owner.Document.Elements.Single().WindowSource!.BindingIntent == replacement.Descriptor.BindingIntent && f.Owner.Document.Elements.Single().AssetId == reboundAsset && f.Owner.Assets.Get(reboundAsset).Data.Span.SequenceEqual(replacement.Png));
        string stable = Snapshot(f.Owner); int drains = ((FakeSource)replacement.Capture).DrainCount;
        for (int i = 1; i <= 40; i++) ((FakeSource)replacement.Capture).Publish(i);
        Pump();
        check("incoming native frame notifications do not append assets or document history on the hidden desktop", Snapshot(f.Owner) == stable && ((FakeSource)replacement.Capture).DrainCount > drains);
    }

    private static void Freeze(Fixture f, Action<string, bool> check, string output)
    {
        f.Reset(); var prepared = f.Prepare(crop: new(.1, .1, .7, .6));
        f.SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared)); Await(f.Add());
        Guid id = f.Owner.Document.Elements.Single().Id; f.Owner.SetSelection([id]);
        f.Owner.Execute("freeze fixture", document => { var image = document.Elements.Single(); image.Transform = Affine2D.Translation(50, 40).Compose(Affine2D.Rotation(.13));
            image.Width = 240; image.Height = 170; image.Style.Opacity = .41; image.Style.Foreground = 0xff2879ab; image.ImageBorderWidth = 4.5;
            image.ImageFrame = [new(.05, .05), new(.95, .1), new(.9, .9), new(.1, .95)]; image.Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.2, .2, .4, .4), Amount = 2 }]; });
        var live = f.Owner.Document.Elements.Single().Clone(); string before = JsonSerializer.Serialize(f.Owner.Document), preserved = Logical(live); int undo = f.Owner.UndoCount;
        Await(f.Freeze(id)); var frozen = f.Owner.Document.Elements.Single(); string frozenAsset = frozen.AssetId!;
        check("real asynchronous Freeze produces an ordinary image with all runtime crop contour and effects materialized", frozen.WindowSource is null && frozen.ImageCrop is null && frozen.ImageFrame is null && frozen.Effects.Count == 0 && frozenAsset != live.AssetId);
        check("Freeze preserves logical placement dimensions opacity and authored border", Logical(frozen) == preserved && frozen.Style.Opacity == .41 && frozen.ImageBorderWidth == 4.5 && f.Owner.Selection.SetEquals([id]));
        using (var image = SKImage.FromEncodedData(f.Owner.Assets.Get(frozenAsset).Data.ToArray()))
            check("Freeze runs the actual renderer and writes a decodable owned PNG", image is { Width: > 0, Height: > 0 });
        File.WriteAllBytes(Path.Combine(output, "frozen-window-body.png"), f.Owner.Assets.Get(frozenAsset).Data.ToArray());
        check("Freeze commits exactly one transaction", f.Owner.UndoCount == undo + 1 && !f.Owner.IsTransactionActive);
        check("one Freeze Undo restores descriptor crop contour effects and fallback exactly", f.Owner.Undo() && JsonSerializer.Serialize(f.Owner.Document) == before);
        check("Freeze Redo returns to the same static image asset", f.Owner.Redo() && f.Owner.Document.Elements.Single().WindowSource is null && f.Owner.Document.Elements.Single().AssetId == frozenAsset);
    }

    private static void Stale(Fixture f, Action<string, bool> check)
    {
        foreach (string reason in new[] { "scope ABA", "document replacement", "owner lifetime", "mode ABA", "foreign transaction" })
        {
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
            f.Reset(new() { Viewport = new(0, 0, 640, 360), Groups = [group], RootOrder = [group.Id] });
            var pending = new TaskCompletionSource<DesktopSession.PreparedWindowSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prepared = f.Prepare(SKColors.Chartreuse); var source = (FakeSource)prepared.Capture;
            f.SetPrepare((_, _, _, _) => pending.Task); var task = f.Add();
            if (reason == "scope ABA") { f.Owner.EnterScope(group.Id); f.Owner.LeaveScope(); }
            if (reason == "document replacement") f.Owner.ReplaceDocument(f.Owner.Document.Clone());
            if (reason == "owner lifetime") Invoke(f.Desktop, "ForgetWindowSourceBindings", f.Owner);
            if (reason == "mode ABA") { f.Editor.Tool = EditorTool.Rectangle; f.Editor.Tool = EditorTool.Select; }
            if (reason == "foreign transaction") f.Owner.BeginTransaction("foreign command owner");
            string current = Snapshot(f.Owner); long transaction = f.Owner.TransactionId;
            pending.SetResult(prepared); Await(task); Pump();
            check("delayed Add rejects " + reason + " without late insertion assets or history", Snapshot(f.Owner) == current && f.Owner.Document.Elements.Count == 0 && !prepared.Transferred);
            check("rejected " + reason + " disposes its prepared native service exactly once", source.DisposeCount == 1 && source.Completion.IsCompleted);
            if (reason == "foreign transaction") { check("asynchronous stale Add never cancels a foreign transaction", f.Owner.IsTransactionActive && f.Owner.TransactionId == transaction); f.Owner.Cancel(); }
        }
    }

    private static void Pending(Fixture f, Action<string, bool> check)
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 700) };
        document.Canvas.Projection.Enabled = true; document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
        f.Reset(document); f.Editor.BillboardMode = true;
        var prepared = f.Prepare(SKColors.Cyan); f.SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared));
        string before = Snapshot(f.Owner); Await(f.Add());
        check("billboard Add holds only prepared pixels until explicit canvas placement", f.Editor.HasPendingBillboardInsertion && f.Owner.Document.Elements.Count == 0 && Snapshot(f.Owner) == before && ((FakeSource)prepared.Capture).DisposeCount == 0);
        f.Editor.Cancel(); Pump();
        check("cancelling pending billboard placement releases its service without assets or a late insert", !f.Editor.HasPendingBillboardInsertion && Field<object?>(f.Desktop, "_pendingWindowInsertion") is null && ((FakeSource)prepared.Capture).DisposeCount == 1 && Snapshot(f.Owner) == before);
    }

    private static void PendingInvalidation(Fixture f, Action<string, bool> check)
    {
        var samples = new List<(string Reason, bool Started, bool ControllerPending, bool RootPending, int Disposals, bool Completed, bool Stable, bool ForeignPreserved)>();
        foreach (string reason in new[] { "an external opacity edit", "Undo", "a foreign transaction notification" })
        {
            var document = new SceneDocument { Viewport = new(0, 0, 800, 700) };
            document.Canvas.Projection.Enabled = true; document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
            document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
            f.Reset(document);
            if (reason == "Undo") f.Owner.Execute("prior opacity edit", scene => scene.LayerOpacity = .8);
            f.Editor.BillboardMode = true;
            var prepared = f.Prepare(SKColors.MediumPurple); var source = (FakeSource)prepared.Capture;
            f.SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared)); Await(f.Add());
            bool started = f.Editor.HasPendingBillboardInsertion && Field<object?>(f.Desktop, "_pendingWindowInsertion") is not null && source.DisposeCount == 0;
            int assets = f.Owner.Assets.Count;
            long foreignId = 0;
            if (reason == "an external opacity edit") f.Owner.Execute("external opacity edit", scene => scene.LayerOpacity = .55);
            else if (reason == "Undo") f.Owner.Undo();
            else { f.Owner.BeginTransaction("unrelated external edit"); foreignId = f.Owner.TransactionId; f.Owner.NotifyChanged(); }
            // Sample synchronously: the next pointer event must not be needed to retire an invalid callback.
            bool controllerPending = f.Editor.HasPendingBillboardInsertion;
            bool rootPending = Field<object?>(f.Desktop, "_pendingWindowInsertion") is not null;
            bool foreignPreserved = reason != "a foreign transaction notification" || f.Owner.IsTransactionActive && f.Owner.TransactionId == foreignId;
            string afterNotification = Snapshot(f.Owner); Pump();
            samples.Add((reason, started, controllerPending, rootPending, source.DisposeCount, source.Completion.IsCompleted,
                Snapshot(f.Owner) == afterNotification && f.Owner.Document.Elements.Count == 0 && f.Owner.Assets.Count == assets && !prepared.Transferred, foreignPreserved));
            Console.WriteLine("PENDING_INVALIDATION_SAMPLE " + JsonSerializer.Serialize(new { Reason = reason, Started = started, ControllerPending = controllerPending,
                RootPending = rootPending, source.DisposeCount, Completed = source.Completion.IsCompleted, ForeignPreserved = foreignPreserved }));
            // Explicit fixture cleanup; this is never used as the command's cancellation mechanism.
            if (f.Owner.IsTransactionActive) f.Owner.Cancel();
            f.Editor.Cancel();
        }
        foreach (var sample in samples)
        {
            check("billboard invalidation fixture starts owned pending placement before " + sample.Reason, sample.Started);
            check("pending billboard callback is retired immediately after " + sample.Reason, !sample.ControllerPending && !sample.RootPending && sample.Disposals == 1 && sample.Completed && sample.Stable);
            if (sample.Reason == "a foreign transaction notification") check("retiring pending billboard placement preserves the active foreign transaction", sample.ForeignPreserved);
        }
        f.Reset();
    }

    private static void Rejected(Fixture f, Action<string, bool> check)
    {
        f.Reset(); var prepared = f.Prepare(); f.SetPrepare((_, _, _, _) => Task.FromResult<DesktopSession.PreparedWindowSource?>(prepared)); Await(f.Add());
        Guid id = f.Owner.Document.Elements.Single().Id; f.Owner.SetSelection([id]); f.Owner.SetNodeLocked(id, true);
        int preparations = 0; f.SetPrepare((_, _, _, _) => { preparations++; return Task.FromResult<DesktopSession.PreparedWindowSource?>(null); });
        string before = Snapshot(f.Owner); Await(f.Rebind(id)); Await(f.Crop(id)); Await(f.Freeze(id));
        check("locked real image rejects Rebind Crop and Freeze without preparation or transaction", preparations == 0 && Snapshot(f.Owner) == before);
        f.Owner.SetNodeLocked(id, false); var prefab = f.Owner.CreatePrefabFromSelection("Shared window"); f.Owner.SetSelection([prefab.InstanceId]);
        before = Snapshot(f.Owner); Await(f.Rebind(prefab.InstanceId)); Await(f.Crop(prefab.InstanceId)); Await(f.Freeze(prefab.InstanceId));
        check("instance summary cannot author Rebind Crop or Freeze into its shared definition", preparations == 0 && Snapshot(f.Owner) == before && f.Owner.Document.Definitions.Single().Graph.Elements.Single().WindowSource is not null);
        f.Reset(); f.Owner.BeginTransaction("foreign"); long transaction = f.Owner.TransactionId; before = Snapshot(f.Owner); Await(f.Add());
        check("Add refuses an existing foreign transaction without taking ownership", preparations == 0 && f.Owner.IsTransactionActive && f.Owner.TransactionId == transaction && Snapshot(f.Owner) == before); f.Owner.Cancel();
        var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
        f.Reset(new() { Groups = [group], RootOrder = [group.Id], Viewport = new(0, 0, 640, 360) }); f.Owner.EnterScope(group.Id); f.Owner.SetNodeLocked(group.Id, true); before = Snapshot(f.Owner);
        f.SetPrepare((_, _, _, _) => { preparations++; return Task.FromResult<DesktopSession.PreparedWindowSource?>(null); }); Await(f.Add());
        check("Add refuses a locked editing scope without preparing native ownership", preparations == 0 && Snapshot(f.Owner) == before);
    }

    private static string Logical(CanvasElement image) => JsonSerializer.Serialize(new { image.Id, image.Kind, image.GroupId, image.Width, image.Height, image.Transform,
        image.ProjectivePlacement, image.ConstrainAspect, image.IsBillboard, image.BillboardAnchor, image.BillboardHeight, image.Style, image.ImageBorderWidth, image.Visible, image.Locked });
    private static string Snapshot(EditorSession owner) => JsonSerializer.Serialize(new { owner.Document, Selection = owner.Selection.Order(), owner.ContentVersion,
        owner.UndoCount, owner.RedoCount, owner.TransactionId, owner.IsTransactionActive, Assets = owner.Assets.Resources.Select(resource => resource.Id).Order() });
    private static bool Near(RectD left, RectD right) => Math.Abs(left.X - right.X) < 1e-6 && Math.Abs(left.Y - right.Y) < 1e-6 && Math.Abs(left.Width - right.Width) < 1e-6 && Math.Abs(left.Height - right.Height) < 1e-6;
    private static byte[] Png(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 24)); surface.Canvas.Clear(color);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        try { return target.GetType().GetMethod(name, Hidden)!.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
    private static void Await(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15)) Pump();
        if (!task.IsCompleted) throw new TimeoutException("Isolated window source command did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }

    private sealed class FakeSource(WindowCaptureTarget target) : IWindowCaptureSource
    {
        private static long streams;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LiveWindowFrame? latest;
        internal int DisposeCount, DrainCount;
        internal bool Active;
        public WindowCaptureTarget Target { get; } = target;
        public long StreamId { get; } = Interlocked.Increment(ref streams);
        public long ConnectionVersion => 1;
        public WindowCaptureStatus Status => WindowCaptureStatus.Live;
        public Exception? LastError => null;
        public Task Completion => completed.Task;
        public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady;
        public event Action? StateChanged { add { } remove { } }
        public void SetActive(bool enabled) => Active = enabled;
        public void Resume() { }
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame) { DrainCount++; frame = Interlocked.Exchange(ref latest, null); return frame is not null; }
        internal void Publish(long sequence)
        {
            var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Bgra8888, SKAlphaType.Premul)); bitmap.Erase(SKColors.Gold); bitmap.SetImmutable();
            Interlocked.Exchange(ref latest, new(StreamId, 1, sequence, new(4000, 2000), bitmap, sequence))?.Dispose(); FrameReady?.Invoke();
        }
        public void Dispose() { DisposeCount++; Active = false; Interlocked.Exchange(ref latest, null)?.Dispose(); completed.TrySetResult(); }
    }
    private sealed class OwnTarget : IDisposable
    {
        private readonly nint window;
        internal WindowCaptureTarget Target { get; }
        internal OwnTarget()
        {
            window = CreateWindowExW(0, "STATIC", "FloatCanvas.WindowSourceCommandFixture", 0, 0, 0, 1, 1, new(-3), 0, 0, 0);
            if (window == 0 || !WindowCaptureCatalog.TryRead(window, out var target)) throw new InvalidOperationException("Could not identify the fixture's own message-only window."); Target = target;
        }
        public void Dispose() { if (window != 0) DestroyWindow(window); }
    }
    private static (HotkeyBinding, HotkeyBinding) FreeHotkeys()
    {
        for (uint key = 0x7c; key < 0x87; key += 2)
        {
            if (!RegisterHotKey(0, 0x5d31, 7, key)) continue;
            try { if (!RegisterHotKey(0, 0x5d32, 7, key + 1)) continue; UnregisterHotKey(0, 0x5d32); return (new(7, key), new(7, key + 1)); }
            finally { UnregisterHotKey(0, 0x5d31); }
        }
        throw new InvalidOperationException("No test hotkeys available.");
    }
    private static string DesktopName()
    { var name = new StringBuilder(256); if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) throw new InvalidOperationException("Cannot verify isolated desktop."); return name.ToString(); }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder information, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
