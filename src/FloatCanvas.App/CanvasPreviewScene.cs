using FloatCanvas.Core;
using System.Diagnostics;
using System.Windows.Threading;

namespace FloatCanvas.App;

/// <summary>UI-owned freezer. Published nodes are never mutated; a gesture replaces only its
/// declared dynamic span. A new/unknown edit deliberately rebuilds instead of sharing live nodes.</summary>
internal sealed class CanvasPreviewScene
{
    private SceneDocument? source, frozen;
    private long capturedRevision = -1, elementsRevision = -1, transaction = -1;
    internal long CapturedRevision => capturedRevision;
    internal long FullBuildCount { get; private set; }
    internal long ClonedElementCount { get; private set; }

    /// <summary>Builds unknown/initial scenes in bounded UI slices. The caller owns the
    /// revision/context predicate; an invalidated attempt never replaces the published scene.</summary>
    internal async ValueTask<bool> PrepareAsync(SceneDocument document, long revision, long elementRevision,
        long transactionId, int first, int last, bool bypass, IReadOnlySet<Guid>? changedIds,
        long transactionBaseRevision, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;
        bool same = ReferenceEquals(source, document) && frozen is not null;
        bool elementsChanged = !same || elementRevision != elementsRevision;
        bool range = TrustedRange(document, transactionId, first, last, bypass, transactionBaseRevision);
        bool settingsOnly = same && !elementsChanged && frozen!.Elements.Count == document.Elements.Count;
        bool elementOnly = same && (changedIds is not null || range);
        if (same && (revision == capturedRevision || settingsOnly))
        {
            Capture(document, revision, elementRevision, transactionId, first, last, bypass, changedIds, transactionBaseRevision);
            return isCurrent();
        }

        var nodes = new List<CanvasElement>(document.Elements.Count);
        var groups = new List<CanvasGroup>(document.Groups.Count);
        var layouts = new List<DisplayLayoutHint>(document.DisplayLayout.Count);
        var previous = frozen;
        var slice = Stopwatch.StartNew();
        for (int index = 0; index < document.Elements.Count; index++)
        {
            var item = document.Elements[index];
            bool changed = !elementOnly || (changedIds is not null ? changedIds.Contains(item.Id) : index >= first && index <= last);
            if (!changed && index < previous!.Elements.Count && previous.Elements[index].Id == item.Id)
                nodes.Add(previous.Elements[index]);
            else { nodes.Add(item.Clone()); ClonedElementCount++; }
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        for (int index = 0; !elementOnly && index < document.Groups.Count; index++)
        {
            groups.Add(document.Groups[index].Clone());
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        for (int index = 0; !elementOnly && index < document.DisplayLayout.Count; index++)
        {
            layouts.Add(document.DisplayLayout[index].Clone());
            if (slice.Elapsed.TotalMilliseconds < 2) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!isCurrent()) return false;
            slice.Restart();
        }
        if (!isCurrent()) return false;
        var next = Header(document, nodes, elementOnly && BackgroundUnchanged(document, previous!, changedIds) ? previous!.Background : document.Background?.Clone(),
            elementOnly ? previous!.Groups : groups, elementOnly ? previous!.DisplayLayout : layouts);
        if (!isCurrent()) return false;
        Publish(document, next, revision, elementRevision, transactionId);
        if (!elementOnly) FullBuildCount++;
        return true;
    }

    internal SceneDocument Capture(SceneDocument document, long revision, long elementRevision,
        long transactionId, int first, int last, bool bypass, IReadOnlySet<Guid>? changedIds = null, long transactionBaseRevision = -1)
    {
        bool same = ReferenceEquals(source, document) && frozen is not null;
        if (same && revision == capturedRevision) return frozen!;
        bool elementsChanged = !same || elementRevision != elementsRevision;
        bool range = TrustedRange(document, transactionId, first, last, bypass, transactionBaseRevision);
        bool settingsOnly = same && !elementsChanged && frozen!.Elements.Count == document.Elements.Count;
        bool elementOnly = same && (changedIds is not null || range);
        List<CanvasElement> nodes;
        if (settingsOnly)
            nodes = frozen!.Elements;
        else if (elementOnly)
        {
            nodes = new(document.Elements.Count);
            for (int i = 0; i < document.Elements.Count; i++)
            {
                var item = document.Elements[i];
                bool changed = changedIds is not null ? changedIds.Contains(item.Id) : i >= first && i <= last;
                if (!changed && i < frozen!.Elements.Count && frozen.Elements[i].Id == item.Id)
                    nodes.Add(frozen.Elements[i]);
                else { nodes.Add(item.Clone()); ClonedElementCount++; }
            }
        }
        else
        {
            nodes = document.Elements.Select(item => item.Clone()).ToList();
            ClonedElementCount += nodes.Count; FullBuildCount++;
        }
        // Header objects are small and detached. Element lists may be shared only because
        // this freezer never writes to a previously published list or element.
        bool shareMetadata = settingsOnly || elementOnly;
        var next = Header(document, nodes, shareMetadata && BackgroundUnchanged(document, frozen!, changedIds) ? frozen!.Background : document.Background?.Clone(),
            shareMetadata ? frozen!.Groups : document.Groups.Select(group => group.Clone()).ToList(),
            shareMetadata ? frozen!.DisplayLayout : document.DisplayLayout.Select(layout => layout.Clone()).ToList());
        Publish(document, next, revision, elementRevision, transactionId);
        return next;
    }

    private bool TrustedRange(SceneDocument document, long transactionId, int first, int last, bool bypass, long transactionBaseRevision) =>
        first >= 0 && last >= first && last < document.Elements.Count && !bypass &&
        (transaction == transactionId || capturedRevision == transactionBaseRevision);

    private static bool BackgroundUnchanged(SceneDocument document, SceneDocument previous, IReadOnlySet<Guid>? changedIds) =>
        document.Background?.Id == previous.Background?.Id &&
        (document.Background is null || changedIds?.Contains(document.Background.Id) != true);

    private static SceneDocument Header(SceneDocument document, List<CanvasElement> nodes, CanvasElement? background,
        List<CanvasGroup> groups, List<DisplayLayoutHint> layouts) => new()
        {
            Version = document.Version, Kind = document.Kind, Elements = nodes,
            Background = background, Canvas = document.Canvas.Clone(), Groups = groups,
            Viewport = document.Viewport, LayerOpacity = document.LayerOpacity, NextNumber = document.NextNumber,
            DisplayLayout = layouts
        };

    private void Publish(SceneDocument document, SceneDocument next, long revision, long elementRevision, long transactionId)
    {
        frozen = next;
        source = document; capturedRevision = revision; elementsRevision = elementRevision; transaction = transactionId;
    }
}
