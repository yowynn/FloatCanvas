using System.Windows.Threading;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using FloatCanvas.Windows;
using SkiaSharp;

namespace FloatCanvas.App;

internal readonly record struct WindowRuntimeDemand(SourceNodeAddress Source, Guid BindingIntent);
internal sealed record WindowRuntimeState(bool Connected, bool UserPaused, bool Demanded, bool Allowed,
    WindowCaptureStatus Status, bool HasFrame, bool BudgetLimited, long Revision);

/// <summary>UI-owned explicit bindings. Document loading never manufactures a native capture source.</summary>
internal sealed class WindowSourceRuntime : IDisposable
{
    private const int MaximumRetainedBindings = 16;
    private sealed class Entry(object owner, WindowSourceKey source, Guid intent, IWindowCaptureSource capture)
    {
        internal object Owner = owner;
        internal WindowSourceKey Source = source;
        internal readonly Guid Intent = intent, ConnectionId = Guid.NewGuid();
        internal readonly IWindowCaptureSource Capture = capture;
        internal WindowFrameLease? Last;
        internal bool UserPaused, Demanded, Limited;
        internal WindowCaptureStatus Status = capture.Status;
        internal long Touched;
        internal long AcceptedGeneration = -1, AcceptedSequence = -1;
        internal int Queued;
        internal Action? FrameHandler, StateHandler;
    }
    private readonly Dispatcher dispatcher;
    private readonly Action<bool, bool> changed;
    private readonly Dictionary<(object Owner, Guid Intent), Entry> bindings = [];
    private readonly WindowFrameBudget budget;
    private object? activeOwner, planIdentity;
    private Guid resourceId;
    private WindowRuntimeDemand[] demands = [];
    private WindowRuntimeDemand[] candidates = [];
    private bool allowed;
    private volatile bool disposed;
    private long revision, touches;
    private WindowFrameSet? frames;
    internal object RenderEpoch { get; private set; } = new();
    internal WindowFrameBudget FrameBudget => budget;
    internal long Revision => revision;
    internal int BindingCount => bindings.Count;
    internal event Action? MediaFrameAccepted;

    internal WindowSourceRuntime(Dispatcher dispatcher, Action<bool, bool> changed, WindowFrameBudget? budget = null)
    { this.dispatcher = dispatcher; this.changed = changed; this.budget = budget ?? new(); }

    /// <summary>Takes ownership of the service on success. The caller owns it if validation fails.</summary>
    internal void Bind(object owner, Guid ownerResourceId, SourceNodeAddress address, Guid intent, IWindowCaptureSource capture)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(capture);
        if (ownerResourceId == Guid.Empty || address.NodeId == Guid.Empty || address.DefinitionId == Guid.Empty || intent == Guid.Empty)
            throw new ArgumentException("Invalid explicit window binding.");
        if (bindings.ContainsKey((owner, intent))) throw new InvalidOperationException("The window intent is already bound in this owner.");
        while (bindings.Count >= MaximumRetainedBindings)
        {
            var victim = bindings.Values.Where(entry => !entry.Demanded).MinBy(entry => entry.Touched);
            if (victim is null) throw new InvalidOperationException("The retained window binding limit is full.");
            Remove(victim);
        }
        var entry = new Entry(owner, new(ownerResourceId, address), intent, capture) { Touched = ++touches };
        entry.FrameHandler = entry.StateHandler = () => Queue(entry);
        bindings.Add((owner, intent), entry);
        capture.FrameReady += entry.FrameHandler; capture.StateChanged += entry.StateHandler;
        bool selectionChanged = SelectDemands();
        if (selectionChanged) RenderEpoch = new();
        Reconcile(); Drain(entry);
        if (selectionChanged) RebuildFrames();
        ++revision; changed(selectionChanged, true);
    }

    internal void Configure(object owner, Guid ownerResourceId, IEnumerable<WindowRuntimeDemand> visibleSources, bool canCapture, object plan)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(plan);
        var next = visibleSources.Distinct().ToArray();
        bool contentChanged = !ReferenceEquals(activeOwner, owner) || resourceId != ownerResourceId || !ReferenceEquals(planIdentity, plan) || !candidates.SequenceEqual(next);
        bool statusChanged = allowed != canCapture || contentChanged;
        activeOwner = owner; resourceId = ownerResourceId; allowed = canCapture; planIdentity = plan; candidates = next;
        contentChanged |= SelectDemands();
        if (contentChanged) RenderEpoch = new();
        Reconcile();
        if (contentChanged) RebuildFrames();
        if (statusChanged) { ++revision; changed(contentChanged, true); }
    }

    internal WindowFrameSet? CaptureFrames()
    { dispatcher.VerifyAccess(); return frames?.Retain(); }
    internal bool IsSourceDemanded(SourceNodeAddress source, Guid intent)
    { dispatcher.VerifyAccess(); return demands.Contains(new(source, intent)); }

    internal bool TryRetainFrame(object owner, Guid ownerResourceId, SourceNodeAddress address, Guid intent, out WindowFrameLease? frame)
    {
        dispatcher.VerifyAccess(); frame = null;
        if (!bindings.TryGetValue((owner, intent), out var entry) || entry.Source.OwnerResourceId != ownerResourceId || entry.Last is null) return false;
        entry.Touched = ++touches; frame = entry.Last.ForSource(new(ownerResourceId, address), intent); return true;
    }

    internal WindowRuntimeState State(object owner, Guid intent)
    {
        dispatcher.VerifyAccess();
        return bindings.TryGetValue((owner, intent), out var entry)
            ? new(true, entry.UserPaused, entry.Demanded, allowed && ReferenceEquals(activeOwner, owner), entry.Status, entry.Last is not null, entry.Limited, revision)
            : new(false, false, false, false, WindowCaptureStatus.Stopped, false, false, revision);
    }
    internal void SetPaused(object owner, IEnumerable<Guid> intents, bool paused)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        bool any = false;
        foreach (var intent in intents.Distinct())
        {
            if (!bindings.TryGetValue((owner, intent), out var entry)) continue;
            bool retry = !paused && entry.Status == WindowCaptureStatus.Unavailable;
            if (entry.UserPaused == paused && !retry) continue;
            entry.UserPaused = paused; entry.Touched = ++touches; any = true;
            if (retry && entry.Demanded && allowed && ReferenceEquals(activeOwner, owner)) entry.Capture.Resume();
        }
        if (!any) return;
        Reconcile(); ++revision; changed(false, true);
    }

    /// <summary>Explicit successful prefab publication may move selected newly bound intents to its parent owner.</summary>
    internal void Transfer(object fromOwner, object toOwner, Guid targetResourceId, IReadOnlyDictionary<Guid, SourceNodeAddress> intents)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        foreach (var pair in intents)
        {
            if (!bindings.TryGetValue((fromOwner, pair.Key), out var entry)) continue;
            if (bindings.ContainsKey((toOwner, pair.Key))) throw new InvalidOperationException("A published window intent already exists in its parent.");
            if (targetResourceId == Guid.Empty || pair.Value.NodeId == Guid.Empty || pair.Value.DefinitionId == Guid.Empty) throw new ArgumentException("Invalid publication address.");
        }
        foreach (var pair in intents)
        {
            if (!bindings.Remove((fromOwner, pair.Key), out var entry)) continue;
            entry.Capture.SetActive(false); entry.Demanded = false;
            if (entry.Source.OwnerResourceId != targetResourceId) { entry.Last?.Dispose(); entry.Last = null; }
            entry.Owner = toOwner; entry.Source = new(targetResourceId, pair.Value); entry.Touched = ++touches;
            bindings.Add((toOwner, pair.Key), entry);
        }
        if (SelectDemands()) RenderEpoch = new();
        Reconcile(); RebuildFrames(); ++revision; changed(true, true);
    }

    internal void Forget(object owner)
    {
        dispatcher.VerifyAccess();
        foreach (var entry in bindings.Values.Where(entry => ReferenceEquals(entry.Owner, owner)).ToArray()) Remove(entry);
        if (ReferenceEquals(activeOwner, owner)) { activeOwner = null; candidates = demands = []; RenderEpoch = new(); }
        else if (SelectDemands()) RenderEpoch = new();
        RebuildFrames(); ++revision; if (!disposed) changed(true, true);
    }

    private void Reconcile()
    {
        foreach (var entry in bindings.Values)
        {
            entry.Demanded = ReferenceEquals(activeOwner, entry.Owner) && entry.Source.OwnerResourceId == resourceId && demands.Any(demand => demand.BindingIntent == entry.Intent);
            entry.Capture.SetActive(entry.Demanded && allowed && !entry.UserPaused && entry.Status is not (WindowCaptureStatus.Closed or WindowCaptureStatus.Stopped));
        }
    }
    private bool SelectDemands()
    {
        var next = candidates.OrderByDescending(demand => activeOwner is not null && bindings.TryGetValue((activeOwner, demand.BindingIntent), out var entry) &&
            entry.Source.OwnerResourceId == resourceId).Take(WindowFrameBudget.MaximumSources).ToArray();
        if (demands.SequenceEqual(next)) return false;
        demands = next; return true;
    }
    private void Queue(Entry entry)
    {
        if (disposed || Interlocked.Exchange(ref entry.Queued, 1) != 0) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref entry.Queued, 0);
                if (!disposed && IsCurrent(entry)) Drain(entry);
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref entry.Queued, 0); }
    }
    private bool IsCurrent(Entry entry) => bindings.TryGetValue((entry.Owner, entry.Intent), out var current) && ReferenceEquals(current, entry);
    private void Drain(Entry entry)
    {
        dispatcher.VerifyAccess(); if (!IsCurrent(entry)) return;
        bool statusChanged = entry.Status != entry.Capture.Status, pixelsChanged = false;
        entry.Status = entry.Capture.Status;
        if (entry.Capture.TryGetLatest(out var native))
        {
            using (native)
            {
                if (entry.Demanded && allowed && ReferenceEquals(activeOwner, entry.Owner) && !entry.UserPaused &&
                    entry.Status is WindowCaptureStatus.Starting or WindowCaptureStatus.Live or WindowCaptureStatus.Resizing &&
                    native.StreamId == entry.Capture.StreamId && native.ConnectionVersion == entry.Capture.ConnectionVersion &&
                    (native.ConnectionVersion > entry.AcceptedGeneration || native.ConnectionVersion == entry.AcceptedGeneration && native.Sequence > entry.AcceptedSequence))
                {
                    SKImage? image = SKImage.FromBitmap(native.Bitmap);
                    try
                    {
                        WindowFrameLease Adopt() => WindowFrameLease.FromImage(entry.Source, entry.Intent,
                            new(entry.ConnectionId, native.ConnectionVersion, native.Sequence), image,
                            native.ContentSize.Width, native.ContentSize.Height, budget);
                        WindowFrameLease adopted;
                        try { adopted = Adopt(); }
                        catch (InvalidOperationException)
                        {
                            if (!ReleaseDormantFrames((long)native.Bitmap.RowBytes * native.Bitmap.Height)) throw;
                            statusChanged = true; adopted = Adopt();
                        }
                        image = null; entry.Last?.Dispose(); entry.Last = adopted; pixelsChanged = true;
                        entry.AcceptedGeneration = native.ConnectionVersion; entry.AcceptedSequence = native.Sequence;
                        statusChanged |= entry.Limited; entry.Limited = false; entry.Touched = ++touches;
                    }
                    catch (InvalidOperationException) { statusChanged |= !entry.Limited; entry.Limited = true; }
                    finally { image?.Dispose(); }
                }
            }
        }
        if (pixelsChanged && entry.Demanded) { RebuildFrames(); MediaFrameAccepted?.Invoke(); }
        if (statusChanged) ++revision;
        if (pixelsChanged && entry.Demanded || statusChanged) changed(pixelsChanged && entry.Demanded, statusChanged);
    }
    private bool ReleaseDormantFrames(long incomingBytes)
    {
        if (incomingBytes > budget.MaximumBytes) return false;
        bool released = false;
        foreach (var entry in bindings.Values.Where(entry => !entry.Demanded && entry.Last is not null).OrderBy(entry => entry.Touched))
        {
            if (budget.RetainedBytes <= budget.MaximumBytes - incomingBytes) break;
            entry.Last!.Dispose(); entry.Last = null; released = true;
        }
        return released;
    }
    private void RebuildFrames()
    {
        var retained = new List<WindowFrameLease>();
        try
        {
            foreach (var demand in demands)
                if (activeOwner is not null && bindings.TryGetValue((activeOwner, demand.BindingIntent), out var entry) &&
                    entry.Source.OwnerResourceId == resourceId && entry.Last is { } last)
                    retained.Add(last.ForSource(new(resourceId, demand.Source), demand.BindingIntent));
            var next = retained.Count == 0 ? null : WindowFrameSet.Capture(RenderEpoch, retained);
            var old = frames; frames = next; old?.Dispose();
        }
        finally { foreach (var frame in retained) frame.Dispose(); }
    }
    private void Remove(Entry entry)
    {
        bindings.Remove((entry.Owner, entry.Intent));
        entry.Capture.FrameReady -= entry.FrameHandler; entry.Capture.StateChanged -= entry.StateHandler;
        entry.Capture.Dispose(); entry.Last?.Dispose(); entry.Last = null;
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        disposed = true; foreach (var entry in bindings.Values.ToArray()) Remove(entry);
        frames?.Dispose(); frames = null; candidates = demands = [];
    }
}
