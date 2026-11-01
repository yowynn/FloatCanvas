using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Windows.Threading;

internal static class WindowSourceRuntimeTests
{
    internal static void Run(Action<string, bool> check)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        ConfigureAndOwnership(dispatcher, check);
        DemandAndAliases(dispatcher, check);
        IndependentSourceCapacity(dispatcher, check);
        FrameOwnershipAndBudget(dispatcher, check);
        RetentionAndTransfer(dispatcher, check);
        DormantFrameBudgetPressure(dispatcher, check);
        MediaProvenance(dispatcher, check);
        StalePacketsAndPause(dispatcher, check);
    }

    private static void ConfigureAndOwnership(Dispatcher dispatcher, Action<string, bool> check)
    {
        var owner = new object(); var otherLifetime = new object(); var ownerId = Guid.NewGuid(); var address = Address(); var intent = Guid.NewGuid(); var plan = new object();
        var changes = new List<(bool Pixels, bool Status)>();
        using var runtime = new WindowSourceRuntime(dispatcher, (pixels, status) => { dispatcher.VerifyAccess(); changes.Add((pixels, status)); });
        runtime.Configure(owner, ownerId, [new(address, intent)], true, plan);
        check("window configure never manufactures a connection from persistent hints or resource identity", runtime.BindingCount == 0 && runtime.CaptureFrames() is null && !runtime.State(owner, intent).Connected);
        object firstEpoch = runtime.RenderEpoch; int beforeStable = changes.Count;
        runtime.Configure(owner, ownerId, [new(address, intent)], true, plan);
        check("stable visible source plan keeps its render epoch and creates no redundant callback", ReferenceEquals(runtime.RenderEpoch, firstEpoch) && changes.Count == beforeStable);
        var source = new FakeSource(); runtime.Bind(owner, ownerId, address, intent, source);
        check("only explicit binding starts the demanded native service", runtime.BindingCount == 1 && source.Active && runtime.State(owner, intent).Demanded);
        var native = source.Publish(SKColors.Red, 1); Pump(dispatcher);
        using (var frame = runtime.CaptureFrames())
            check("UI dispatcher adopts an owned frame after the native bitmap handle is disposed", native.Bitmap.Handle == 0 && frame is { Count: 1 } && Pixel(frame, ownerId, address, intent) == SKColors.Red);
        var epochAfterFrame = runtime.RenderEpoch; source.Publish(SKColors.Blue, 2); Pump(dispatcher);
        using (var frame = runtime.CaptureFrames())
            check("ordinary sequence changes update pixels while keeping the source render epoch", ReferenceEquals(epochAfterFrame, runtime.RenderEpoch) && frame!.TryGet(new(ownerId, address), out var lease) && lease.Id.Sequence == 2);
        runtime.Configure(otherLifetime, ownerId, [new(address, intent)], true, plan);
        check("same ResourceId reopened under another owner lifetime cannot adopt or activate an old binding", !source.Active && runtime.CaptureFrames() is null && !runtime.State(otherLifetime, intent).Connected && !runtime.State(owner, intent).Allowed);
        check("frame retention is explicit-owner scoped even when resource and source IDs match", !runtime.TryRetainFrame(otherLifetime, ownerId, address, intent, out var absent) && absent is null);
        check("frame retention cannot alias a binding into another resource", !runtime.TryRetainFrame(owner, Guid.NewGuid(), address, intent, out absent) && absent is null);
        int afterOwnerSwitch = changes.Count; source.Publish(SKColors.Green, 3); Pump(dispatcher);
        using (var oldOwnerFrame = Retain(runtime, owner, ownerId, address, intent))
            check("late inactive-owner pixels cannot change its retained fallback or notify the new lifetime", changes.Count == afterOwnerSwitch && oldOwnerFrame.Id.Sequence == 2);
        runtime.Configure(owner, ownerId, [new(address, intent)], true, plan);
        check("returning within the explicitly retained owner reuses only its own binding", source.Active && runtime.State(owner, intent).Connected && runtime.State(owner, intent).HasFrame);
        object previous = runtime.RenderEpoch; runtime.Configure(owner, ownerId, [new(address, intent)], true, new());
        check("a new authored source plan gets a new render epoch without replacing the service", !ReferenceEquals(previous, runtime.RenderEpoch) && runtime.BindingCount == 1 && source.DisposeCount == 0);
        var scene = new SceneDocument { ResourceId = ownerId, Elements = [Image(address.NodeId, intent)] }; var session = new EditorSession(scene);
        string wire = JsonSerializer.Serialize(scene), fingerprint = AuthoredSceneContent.Fingerprint(scene); long contentVersion = session.ContentVersion, revision = session.SceneRevision;
        for (int i = 3; i <= 20; i++) { source.Publish(SKColors.Green, i); Pump(dispatcher); }
        check("runtime frames change no source fingerprint, revision, history or assets", JsonSerializer.Serialize(scene) == wire && AuthoredSceneContent.Fingerprint(scene) == fingerprint &&
            session.ContentVersion == contentVersion && session.SceneRevision == revision && session.UndoCount == 0 && session.Assets.Count == 0);
        int beforeDispose = changes.Count; runtime.Dispose(); source.NotifyLate(); Pump(dispatcher);
        check("dispose detaches the service and queued callbacks cannot publish changes", source.DisposeCount == 1 && changes.Count == beforeDispose && runtime.FrameBudget.RetainedBytes == 0);
    }

    private static void DemandAndAliases(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid(), intent = Guid.NewGuid(); var addresses = Enumerable.Range(0, 5).Select(_ => Address()).ToArray();
        var changes = new List<(bool Pixels, bool Status)>();
        using var runtime = new WindowSourceRuntime(dispatcher, (pixels, status) => changes.Add((pixels, status)));
        var source = new FakeSource(); runtime.Bind(owner, resource, addresses[0], intent, source);
        check("a retained binding with no visible source demand stays inactive", !source.Active && !runtime.State(owner, intent).Demanded);
        runtime.Configure(owner, resource, addresses.Select(a => new WindowRuntimeDemand(a, intent)), true, plan); source.Publish(SKColors.Red, 1); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
        {
            check("at most four visible real source addresses receive a window frame", frames is { Count: 4 } && !frames.TryGet(new(resource, addresses[4]), out _));
            check("shared binding intent aliases source addresses without copying pixel backing", frames!.PixelBytes == 64 && runtime.FrameBudget.RetainedBytes == 64 && addresses.Take(4).All(a => frames.TryGet(new(resource, a), out var l) && l.BindingIntent == intent));
        }
        int notifications = changes.Count, drains = source.DrainCount;
        Task.Run(() => { for (int i = 0; i < 10000; i++) source.Notify(); }).GetAwaiter().GetResult();
        Pump(dispatcher);
        check("10000 no-pixel native notifications coalesce into one dispatcher drain", source.DrainCount == drains + 1 && changes.Count == notifications);
        runtime.SetPaused(owner, [intent], true);
        check("explicit pause closes the capture gate while preserving the last displayed frame", !source.Active && runtime.State(owner, intent).UserPaused && runtime.State(owner, intent).HasFrame);
        runtime.SetPaused(owner, [intent], false);
        check("explicit resume can reactivate only an allowed demanded binding", source.Active && !runtime.State(owner, intent).UserPaused);
        runtime.Configure(owner, resource, addresses.Select(a => new WindowRuntimeDemand(a, intent)), false, plan);
        check("editing or hidden-host gate pauses native capture while retaining the frozen display", !source.Active && !runtime.State(owner, intent).Allowed && runtime.State(owner, intent).HasFrame);
        runtime.Configure(owner, resource, [], true, plan);
        check("no visible occurrence stops capture and removes it from the frame bundle", !source.Active && runtime.CaptureFrames() is null && !runtime.State(owner, intent).Demanded);
        source.Publish(SKColors.Blue, 2); Pump(dispatcher);
        using (var retained = Retain(runtime, owner, resource, addresses[0], intent))
            check("late no-demand frames cannot overwrite the retained static fallback", retained.Id.Sequence == 1);
        runtime.SetPaused(owner, [intent], false);
        check("resume cannot override the no-demand gate", !source.Active);
        runtime.Configure(owner, resource, [new(addresses[0], intent)], true, plan); source.SetStatus(WindowCaptureStatus.Unavailable); Pump(dispatcher);
        runtime.SetPaused(owner, [intent], false);
        check("explicit resume retries an unavailable source only in the active owner", source.ResumeCount == 1);
    }

    private static void FrameOwnershipAndBudget(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid(), intent = Guid.NewGuid(); var address = Address(); var budget = new WindowFrameBudget(128);
        using var runtime = new WindowSourceRuntime(dispatcher, (_, _) => { }, budget); var source = new FakeSource();
        runtime.Configure(owner, resource, [new(address, intent)], true, plan); runtime.Bind(owner, resource, address, intent, source);
        source.Publish(SKColors.Red, 1); Pump(dispatcher); var oldDisplayed = runtime.CaptureFrames()!;
        source.Publish(SKColors.Blue, 2); Pump(dispatcher);
        check("old displayed leases keep their own frame backing while a new latest frame is adopted", budget.RetainedBytes == 128 && Pixel(oldDisplayed, resource, address, intent) == SKColors.Red);
        var dropped = source.Publish(SKColors.Green, 3); Pump(dispatcher);
        using (var current = runtime.CaptureFrames())
            check("full retained frame budget drops the new frame without invalidating old or latest leases", dropped.Bitmap.Handle == 0 && runtime.State(owner, intent).BudgetLimited && budget.RetainedBytes == 128 &&
                Pixel(current!, resource, address, intent) == SKColors.Blue && Pixel(oldDisplayed, resource, address, intent) == SKColors.Red);
        oldDisplayed.Dispose(); check("final old display release returns its unique byte charge", budget.RetainedBytes == 64);
        source.Publish(SKColors.Yellow, 4); Pump(dispatcher);
        check("new frames recover automatically after retained consumers release budget", !runtime.State(owner, intent).BudgetLimited && budget.RetainedBytes == 64);
        var consumer = runtime.CaptureFrames()!; runtime.Dispose();
        check("runtime disposal does not revoke a retained renderer consumer", budget.RetainedBytes == 64 && Pixel(consumer, resource, address, intent) == SKColors.Yellow);
        consumer.Dispose(); check("the final consumer releases all native raster budget", budget.RetainedBytes == 0 && source.DisposeCount == 1);
    }

    private static void IndependentSourceCapacity(Dispatcher dispatcher, Action<string, bool> check)
    {
        {
            object lateOwner = new(), latePlan = new(); Guid lateResource = Guid.NewGuid();
            var candidates = Enumerable.Range(0, 5).Select(_ => new WindowRuntimeDemand(Address(), Guid.NewGuid())).ToArray();
            using var lateRuntime = new WindowSourceRuntime(dispatcher, (_, _) => { }); var onlyBound = new FakeSource();
            lateRuntime.Configure(lateOwner, lateResource, candidates, true, latePlan);
            lateRuntime.Bind(lateOwner, lateResource, candidates[4].Source, candidates[4].BindingIntent, onlyBound);
            check("unbound earlier source candidates cannot starve the only explicit fifth binding", onlyBound.Active && lateRuntime.State(lateOwner, candidates[4].BindingIntent).Demanded);
            onlyBound.Publish(SKColors.Blue, 1); Pump(dispatcher);
            using var candidateFrames = lateRuntime.CaptureFrames();
            check("only bound fifth candidate enters its owner-scoped frame bundle", candidateFrames is { Count: 1 } && candidateFrames.TryGet(new(lateResource, candidates[4].Source), out _));
        }
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid();
        var addresses = Enumerable.Range(0, 5).Select(_ => Address()).ToArray(); var intents = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var services = Enumerable.Range(0, 5).Select(_ => new FakeSource()).ToArray();
        using var runtime = new WindowSourceRuntime(dispatcher, (_, _) => { });
        for (int i = 0; i < services.Length; i++) runtime.Bind(owner, resource, addresses[i], intents[i], services[i]);
        runtime.Configure(owner, resource, addresses.Select((a, i) => new WindowRuntimeDemand(a, intents[i])), true, plan);
        check("the visible four-source cap starts only four independently bound native services", services.Take(4).All(s => s.Active) && !services[4].Active);
        for (int i = 0; i < services.Length; i++) services[i].Publish(SKColors.Red, 1);
        Pump(dispatcher);
        using var frames = runtime.CaptureFrames();
        check("fifth source late pixels cannot enter the four-source bundle or retained fallback", frames is { Count: 4, PixelBytes: 256 } && !runtime.State(owner, intents[4]).HasFrame && runtime.FrameBudget.RetainedBytes == 256);
    }

    private static void RetentionAndTransfer(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), parent = new(), plan = new(); Guid ownerResource = Guid.NewGuid(), parentResource = Guid.NewGuid();
        var sources = new List<FakeSource>(); var intents = new List<Guid>(); var addresses = new List<SourceNodeAddress>();
        var changes = new List<(bool, bool)>(); using var runtime = new WindowSourceRuntime(dispatcher, (p, s) => changes.Add((p, s)));
        for (int i = 0; i < 17; i++) { var fake = new FakeSource(); sources.Add(fake); intents.Add(Guid.NewGuid()); addresses.Add(Address()); runtime.Bind(owner, ownerResource, addresses[i], intents[i], fake); }
        check("dormant explicit bindings retain at most16 and evict the least recently touched service", runtime.BindingCount == 16 && sources[0].DisposeCount == 1 && sources.Skip(1).All(s => s.DisposeCount == 0));
        int changesBefore = changes.Count; sources[0].NotifyLate(); Pump(dispatcher);
        check("a queued callback from an evicted binding cannot republish or revive it", changes.Count == changesBefore && !runtime.State(owner, intents[0]).Connected);
        runtime.Configure(owner, ownerResource, [new(addresses[1], intents[1]), new(addresses[2], intents[2])], true, plan);
        sources[1].Publish(SKColors.Red, 1); sources[2].Publish(SKColors.Blue, 1); Pump(dispatcher);
        var extra = new FakeSource(); runtime.Bind(owner, ownerResource, Address(), Guid.NewGuid(), extra);
        check("retention eviction preserves demanded bindings and closes only another dormant source", runtime.BindingCount == 16 && sources[1].DisposeCount == 0 && sources[2].DisposeCount == 0 && sources[3].DisposeCount == 1 && !extra.Active);
        var target = new SourceNodeAddress(Guid.NewGuid(), Guid.NewGuid()); runtime.Transfer(owner, parent, parentResource, new Dictionary<Guid, SourceNodeAddress> { [intents[1]] = target });
        check("explicit publication transfers only the selected binding intent to its parent owner", !runtime.State(owner, intents[1]).Connected && runtime.State(parent, intents[1]).Connected && runtime.State(owner, intents[2]).Connected);
        check("cross-resource publication drops the former owner raster instead of remapping cached pixels across owners", !runtime.TryRetainFrame(parent, parentResource, target, intents[1], out var absent) && absent is null && !sources[1].Active);
        runtime.Configure(parent, parentResource, [new(target, intents[1])], true, new()); sources[1].Publish(SKColors.Red, 1); Pump(dispatcher);
        using (var replayed = runtime.CaptureFrames())
            check("transfer keeps sequence high-water even after cross-resource raster release", replayed is null && !runtime.State(parent, intents[1]).HasFrame);
        sources[1].Publish(SKColors.Green, 2); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("the successfully transferred service publishes future frames only at its explicit parent source address", frames is { Count: 1 } && frames.TryGet(new(parentResource, target), out _) && Pixel(frames, parentResource, target, intents[1]) == SKColors.Green && !sources[2].Active);
        int remaining = runtime.BindingCount; var invalid = new SourceNodeAddress(null, Guid.Empty);
        bool rejected = false; try { runtime.Transfer(owner, parent, parentResource, new Dictionary<Guid, SourceNodeAddress> { [intents[2]] = invalid }); } catch (ArgumentException) { rejected = true; }
        check("failed transfer validates its complete map before moving any binding", rejected && runtime.BindingCount == remaining && runtime.State(owner, intents[2]).Connected);
        runtime.Forget(owner); check("forget closes only that owner while keeping the explicitly published parent binding", runtime.BindingCount == 1 && sources[2].DisposeCount == 1 && sources[1].DisposeCount == 0);
        runtime.Forget(parent); changesBefore = changes.Count; sources[1].NotifyLate(); Pump(dispatcher);
        check("forgetting the active owner clears frame epoch and late service callbacks", runtime.BindingCount == 0 && runtime.CaptureFrames() is null && changes.Count == changesBefore && runtime.FrameBudget.RetainedBytes == 0);
    }

    private static void DormantFrameBudgetPressure(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid();
        var budget = new WindowFrameBudget(16 * 64);
        using var runtime = new WindowSourceRuntime(dispatcher, (_, _) => { }, budget);
        var sources = new List<FakeSource>(); var intents = new List<Guid>(); var addresses = new List<SourceNodeAddress>();
        for (int i = 0; i < 16; i++)
        {
            var address = Address(); var intent = Guid.NewGuid(); var source = new FakeSource();
            addresses.Add(address); intents.Add(intent); sources.Add(source);
            runtime.Configure(owner, resource, [new(address, intent)], true, plan);
            runtime.Bind(owner, resource, address, intent, source);
            source.Publish(SKColors.Red, 1); Pump(dispatcher);
        }
        check("sixteen previously visible sources can fill the retained raster budget", budget.RetainedBytes == budget.MaximumBytes && runtime.BindingCount == 16);
        sources[^1].Publish(SKColors.Blue, 2); Pump(dispatcher);
        using (var current = runtime.CaptureFrames())
            check("dormant last-frame pressure cannot freeze an active source", current is { Count: 1 } &&
                Pixel(current, resource, addresses[^1], intents[^1]) == SKColors.Blue && !runtime.State(owner, intents[^1]).BudgetLimited);
        check("pressure reclaims the oldest dormant raster while retaining all explicit native bindings", !runtime.State(owner, intents[0]).HasFrame &&
            runtime.State(owner, intents[1]).HasFrame && runtime.BindingCount == 16 && sources.All(source => source.DisposeCount == 0));

        // A consumer can keep the evicted dormant backing alive. Such ownership must
        // remain valid, and demanded peers must never be reclaimed to force adoption.
        using var smallRuntime = new WindowSourceRuntime(dispatcher, (_, _) => { }, new WindowFrameBudget(3 * 64));
        var first = new FakeSource(); var second = new FakeSource(); var third = new FakeSource();
        var a = Address(); var b = Address(); var c = Address(); Guid ia = Guid.NewGuid(), ib = Guid.NewGuid(), ic = Guid.NewGuid();
        smallRuntime.Configure(owner, resource, [new(a, ia)], true, plan); smallRuntime.Bind(owner, resource, a, ia, first);
        first.Publish(SKColors.Red, 1); Pump(dispatcher); using var retained = smallRuntime.CaptureFrames()!;
        smallRuntime.Configure(owner, resource, [new(b, ib), new(c, ic)], true, plan);
        smallRuntime.Bind(owner, resource, b, ib, second); smallRuntime.Bind(owner, resource, c, ic, third);
        second.Publish(SKColors.Blue, 1); third.Publish(SKColors.Green, 1); Pump(dispatcher);
        second.Publish(SKColors.Yellow, 2); Pump(dispatcher);
        using (var current = smallRuntime.CaptureFrames())
            check("unreleased dormant consumer keeps its pixels and cannot force reclamation of demanded peers", Pixel(retained, resource, a, ia) == SKColors.Red &&
                current is { Count: 2 } && Pixel(current, resource, b, ib) == SKColors.Blue && Pixel(current, resource, c, ic) == SKColors.Green &&
                smallRuntime.State(owner, ib).BudgetLimited && !smallRuntime.State(owner, ia).HasFrame && smallRuntime.State(owner, ic).HasFrame);
        retained.Dispose(); second.Publish(SKColors.Yellow, 3); Pump(dispatcher);
        using (var current = smallRuntime.CaptureFrames())
            check("budget pressure recovers after an external dormant consumer releases its raster", current is { Count: 2 } &&
                Pixel(current, resource, b, ib) == SKColors.Yellow && !smallRuntime.State(owner, ib).BudgetLimited);
    }

    private static void MediaProvenance(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid(), intent = Guid.NewGuid(); var address = Address();
        int media = 0; using var runtime = new WindowSourceRuntime(dispatcher, (_, _) => { }); runtime.MediaFrameAccepted += () => media++;
        runtime.Configure(owner, resource, [new(address, intent)], true, plan);
        runtime.Configure(owner, resource, [new(address, intent)], false, new());
        runtime.Configure(owner, resource, [new(address, intent)], true, plan);
        var source = new FakeSource(); runtime.Bind(owner, resource, address, intent, source);
        check("plan gates and explicit bind without pixels never announce an accepted media frame", media == 0);
        source.Publish(SKColors.Red, 1); Pump(dispatcher);
        check("one successfully adopted native packet emits exactly one media event", media == 1);
        source.Publish(SKColors.Blue, 1); source.Notify(); Pump(dispatcher);
        check("duplicate or no-pixel notifications cannot manufacture media generations", media == 1);
        runtime.SetPaused(owner, [intent], true); source.Publish(SKColors.Green, 2); Pump(dispatcher);
        check("late paused packets and status changes emit no accepted-media event", media == 1);
        runtime.SetPaused(owner, [intent], false); source.Publish(SKColors.Blue, 2); Pump(dispatcher);
        check("a new packet after resume emits one additional media event", media == 2);
        runtime.Forget(owner); runtime.Dispose(); source.NotifyLate(); Pump(dispatcher);
        check("forget dispose and late callbacks cannot announce media without an adoption", media == 2);
    }
    private static void StalePacketsAndPause(Dispatcher dispatcher, Action<string, bool> check)
    {
        object owner = new(), plan = new(); Guid resource = Guid.NewGuid(), intent = Guid.NewGuid(); var address = Address();
        int changes = 0; using var runtime = new WindowSourceRuntime(dispatcher, (_, _) => changes++); var source = new FakeSource();
        runtime.Configure(owner, resource, [new(address, intent)], true, plan); runtime.Bind(owner, resource, address, intent, source);
        source.Publish(SKColors.Red, 5); Pump(dispatcher); int before = changes;
        source.Publish(SKColors.Blue, 6, stream: source.StreamId + 1); Pump(dispatcher);
        source.Publish(SKColors.Blue, 6, connection: source.ConnectionVersion + 1); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("wrong stream or connection generation packets are disposed without replacing the displayed frame", changes == before && frames!.TryGet(new(resource, address), out var frame) && frame.Id.Sequence == 5 && Pixel(frames, resource, address, intent) == SKColors.Red);
        source.Publish(SKColors.Blue, 4); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("older sequence in the same connection cannot roll displayed pixels backward", changes == before && frames!.TryGet(new(resource, address), out var frame) && frame.Id.Sequence == 5 && Pixel(frames, resource, address, intent) == SKColors.Red);
        source.Publish(SKColors.Blue, 5); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("a duplicate sequence cannot overwrite its original immutable frame or notify a new render", changes == before && frames!.TryGet(new(resource, address), out var frame) && frame.Id.Sequence == 5 && Pixel(frames, resource, address, intent) == SKColors.Red);
        runtime.SetPaused(owner, [intent], true); before = changes; source.Publish(SKColors.Green, 7); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("late pixel arrival after user pause cannot change its frozen displayed frame", changes == before && frames!.TryGet(new(resource, address), out var frame) && frame.Id.Sequence == 5 && Pixel(frames, resource, address, intent) == SKColors.Red);
        runtime.SetPaused(owner, [intent], false); source.ConnectionVersion++; source.Publish(SKColors.Yellow, 1); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("a newer connection generation may restart its sequence without reviving the former generation", frames!.TryGet(new(resource, address), out var frame) && frame.Id.Generation == source.ConnectionVersion && frame.Id.Sequence == 1 && Pixel(frames, resource, address, intent) == SKColors.Yellow);
        runtime.Configure(owner, resource, [new(address, intent)], false, plan); before = changes; source.Publish(SKColors.Blue, 2); Pump(dispatcher);
        using (var frames = runtime.CaptureFrames())
            check("late frame after editing or hidden-host gate cannot replace displayed paused pixels", changes == before && Pixel(frames!, resource, address, intent) == SKColors.Yellow);
        runtime.Configure(owner, resource, [new(address, intent)], true, plan);
        foreach (var status in new[] { WindowCaptureStatus.Hidden, WindowCaptureStatus.Minimized, WindowCaptureStatus.Closed, WindowCaptureStatus.Stopped })
        {
            var native = source.Publish(SKColors.Blue, 2, status: status); Pump(dispatcher);
            using var frames = runtime.CaptureFrames();
            check("unavailable native status preserves frozen last pixels and disposes late frame: " + status,
                native.Bitmap.Handle == 0 && frames!.TryGet(new(resource, address), out var frame) && frame.Id.Sequence == 1 && Pixel(frames, resource, address, intent) == SKColors.Yellow);
        }
    }

    private static SourceNodeAddress Address() => new(null, Guid.NewGuid());
    private static WindowFrameLease Retain(WindowSourceRuntime runtime, object owner, Guid resource, SourceNodeAddress source, Guid intent)
    {
        if (!runtime.TryRetainFrame(owner, resource, source, intent, out var frame) || frame is null) throw new InvalidOperationException("Expected retained fake-source frame.");
        return frame;
    }
    private static CanvasElement Image(Guid id, Guid intent) => new() { Id = id, Kind = ElementKind.Image, Width = 4, Height = 4,
        WindowSource = new() { BindingIntent = intent, Fit = WindowSourceFit.Stretch } };
    private static SKColor Pixel(WindowFrameSet frames, Guid owner, SourceNodeAddress source, Guid intent)
    {
        var element = Image(source.NodeId, intent); var scene = new SceneDocument { ResourceId = owner, Viewport = new(0, 0, 4, 4) };
        if (source.DefinitionId is { } definitionId)
        {
            var definition = new PrefabDefinition { Id = definitionId, Graph = new() { Elements = [element] } }; var instance = new PrefabInstance { DefinitionId = definitionId };
            scene.Definitions = [definition]; scene.Instances = [instance]; scene.RootOrder = [instance.Id];
        }
        else scene.Elements = [element];
        using var renderer = new SceneRenderer { WindowFrames = frames, WindowOwnerContext = frames.OwnerContext };
        using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene, new(), true)); return bitmap.GetPixel(2, 2);
    }
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame(); dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }

    private sealed class FakeSource : IWindowCaptureSource
    {
        private static long streams;
        private LiveWindowFrame? latest;
        private Action? frameReady, stateChanged, lateFrameReady;
        internal bool Active;
        internal int DisposeCount, ResumeCount, DrainCount;
        public WindowCaptureTarget Target { get; } = new(1, 1, 1, 1, "Synthetic window", "test", "Synthetic");
        public long StreamId { get; } = Interlocked.Increment(ref streams);
        public long ConnectionVersion { get; set; } = 1;
        public WindowCaptureStatus Status { get; private set; } = WindowCaptureStatus.Starting;
        public Exception? LastError => null;
        public Task Completion => Task.CompletedTask;
        public Task FirstResult => Task.CompletedTask;
        public event Action? FrameReady { add { frameReady += value; lateFrameReady = frameReady; } remove { frameReady -= value; } }
        public event Action? StateChanged { add => stateChanged += value; remove => stateChanged -= value; }
        public void SetActive(bool enabled) => Active = enabled;
        public void Resume() => ResumeCount++;
        public bool TryGetLatest([NotNullWhen(true)] out LiveWindowFrame? frame) { DrainCount++; frame = Interlocked.Exchange(ref latest, null); return frame is not null; }
        internal LiveWindowFrame Publish(SKColor color, long sequence, long? stream = null, long? connection = null, WindowCaptureStatus status = WindowCaptureStatus.Live)
        {
            var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul)); bitmap.Erase(color); bitmap.SetImmutable();
            var frame = new LiveWindowFrame(stream ?? StreamId, connection ?? ConnectionVersion, sequence, new(4, 4), bitmap, 1);
            Interlocked.Exchange(ref latest, frame)?.Dispose(); Status = status; frameReady?.Invoke(); return frame;
        }
        internal void SetStatus(WindowCaptureStatus status) { Status = status; stateChanged?.Invoke(); }
        internal void Notify() => frameReady?.Invoke();
        internal void NotifyLate() => lateFrameReady?.Invoke();
        public void Dispose() { DisposeCount++; Active = false; Interlocked.Exchange(ref latest, null)?.Dispose(); }
    }
}
