using System.Reflection;
using System.Runtime.CompilerServices;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Windows;

/// <summary>Only the production async guards: no native windows, capture or foreground input.</summary>
internal static class EffectScopeRequestTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static void Run(Action<string, bool> check)
    {
        var document = new SceneDocument(); var graph = new SceneGraph(document);
        var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock });
        var target = new EditorSession(document); var editor = new EditorController(target);
        using var lifetime = new CancellationTokenSource();
        // These guard-only surfaces deliberately have no HWNDs or native input owners.
        // Initialize every field consumed by the production predicates explicitly.
        var host = (OverlayHost)RuntimeHelpers.GetUninitializedObject(typeof(OverlayHost));
        var focus = typeof(OverlayHost).GetField("_focusTransitions", Private)!;
        focus.SetValue(host, Activator.CreateInstance(focus.FieldType, nonPublic: true));
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        Set(desktop, "_host", host); Set(desktop, "_editor", editor); Set(desktop, "_documentLifetime", lifetime);
        object Request() => typeof(DesktopSession).GetMethod("BeginEffectRender", Private)!.Invoke(desktop, [target])!;
        bool Current(object request, bool inserting = false) => (bool)typeof(DesktopSession).GetMethod(inserting ? "CanFinishEffectInsertion" : "CanFinishEffect", Private)!.Invoke(desktop, [request])!;

        var request = Request(); long revision = target.SceneRevision;
        check("effect request accepts its exact current document and writable scope", Current(request) && Current(request, true));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> FinishLater()
        {
            await completion.Task.ConfigureAwait(false);
            return Current(request, true);
        }
        var pending = FinishLater(); target.EnterScope(group.Id);
        check("production effect guard rejects entering another scope without changing document content", target.SceneRevision == revision && !Current(request));
        target.LeaveScope(); completion.SetResult(true);
        check("delayed effect insertion cannot revive after leaving and returning to the original scope", !pending.GetAwaiter().GetResult() && !Current(request, true));
        target.EnterScope(group.Id); request = Request();
        target.SetEditScope(group.Id);
        check("effect guard keeps a no-op scope reaffirmation valid", Current(request, true));
        group.Locked = true;
        check("effect insertion guard rejects a newly locked active scope before asset publication", Current(request) && !Current(request, true) && target.Assets.Count == 0 && target.Document.Elements.Count == 0);
        group.Locked = false; target.LeaveScope(); target.EnterScope(group.Id);
        check("an effect request cannot revive after leaving and reentering the same nested scope", !Current(request));
        request = Request(); lifetime.Cancel();
        check("scope-aware effect guards continue to reject a cancelled document lifetime", !Current(request, true));
    }
    private static void Set(object target, string field, object value) => typeof(DesktopSession).GetField(field, Private)!.SetValue(target, value);
}
