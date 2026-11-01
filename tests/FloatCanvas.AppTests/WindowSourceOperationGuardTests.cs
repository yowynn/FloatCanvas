using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class WindowSourceOperationGuardTests
{
    private sealed class Context
    {
        internal readonly EditorSession Owner;
        internal readonly EditorController Editor;
        internal readonly Guid First, Second, Group;
        internal bool Editing = true;
        internal long EditingVersion = 17;
        internal object? Prefab = new object();
        internal object Monitors = new object(), Lifetime = new object();
        internal Context()
        {
            var first = new CanvasElement { Kind = ElementKind.Rectangle, Width = 30, Height = 20 };
            var second = new CanvasElement { Kind = ElementKind.Rectangle, Width = 40, Height = 30 };
            var group = new CanvasGroup { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] };
            var document = new SceneDocument { Viewport = new(0, 0, 800, 700), Elements = [first, second], Groups = [group], RootOrder = [first.Id, second.Id, group.Id] };
            document.Canvas.Projection.Enabled = true; document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
            document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)];
            Owner = new(document); Owner.SetSelection([first.Id]); Editor = new(Owner); First = first.Id; Second = second.Id; Group = group.Id;
        }
        internal WindowSourceOperationGuard Guard(bool selection = true) => new(Owner, Editor, Editing, EditingVersion, Prefab, Monitors, Lifetime, selection);
        internal bool Current(WindowSourceOperationGuard guard) => guard.IsCurrent(Owner, Editor, Editing, EditingVersion, Prefab, Monitors, Lifetime);
        internal string Fingerprint() => JsonSerializer.Serialize(new { Owner.Document, Selection = Owner.Selection.Order(), Owner.ContentVersion, Owner.SceneRevision,
            Owner.EditScopeVersion, Owner.UndoCount, Owner.RedoCount, Owner.TransactionId, Owner.IsTransactionActive, Assets = Owner.Assets.Count,
            Editor.ScreenOrigin, Editor.PlacementContextVersion, Editor.Tool, Editor.BillboardMode, Editor.BillboardWorkflow });
    }
    internal static void Run(Action<string, bool> check)
    {
        var context = new Context(); string before = context.Fingerprint(); var guard = context.Guard();
        check("unchanged window-source operation context remains current", context.Current(guard) && context.Current(guard));
        check("capturing and checking a guard does not mutate owner editor assets or history", context.Fingerprint() == before);
        Reject(check, "another owning EditorSession is rejected even with the same ResourceId", value =>
        {
            var other = new EditorSession(value.Owner.Document.Clone());
            return guard => guard.IsCurrent(other, value.Editor, value.Editing, value.EditingVersion, value.Prefab, value.Monitors, value.Lifetime);
        });
        Reject(check, "another EditorController is rejected even when it edits the same owner", value =>
        {
            var other = new EditorController(value.Owner);
            return guard => guard.IsCurrent(value.Owner, other, value.Editing, value.EditingVersion, value.Prefab, value.Monitors, value.Lifetime);
        });
        Changed(check, "same-resource document replacement is rejected even at the same content version", value =>
        {
            long version = value.Owner.ContentVersion; Guid resource = value.Owner.Document.ResourceId;
            value.Owner.RestoreDocument("same resource", value.Owner.Document.Clone(), version);
            if (value.Owner.ContentVersion != version || value.Owner.Document.ResourceId != resource) throw new InvalidOperationException("Same-version document fixture drifted.");
        });
        Changed(check, "committed source content changes invalidate an outstanding operation", value => value.Owner.Execute("content", document => document.NextNumber = 42));
        Changed(check, "an active foreign transaction is rejected without cancelling it", value => value.Owner.BeginTransaction("foreign"));
        context = new(); context.Owner.BeginTransaction("already active"); guard = context.Guard();
        check("a guard captured inside a foreign transaction starts invalid", !context.Current(guard) && context.Owner.IsTransactionActive); context.Owner.Cancel();
        check("ending the original foreign transaction cannot revive an invalid guard", !context.Current(guard));
        Changed(check, "scope A to B to A is rejected without an intermediate guard check", value => { value.Owner.EnterScope(value.Group); value.Owner.LeaveScope(); value.Owner.SetSelection([value.First]); });
        Changed(check, "screen origin A to B to A is rejected by placement context version", value => { value.Editor.ScreenOrigin = new(100, -50); value.Editor.ScreenOrigin = default; });
        Changed(check, "billboard mode A to B to A is rejected by placement context version", value => { value.Editor.BillboardMode = true; value.Editor.BillboardMode = false; });
        Changed(check, "billboard workflow A to B to A is rejected by placement context version", value => { value.Editor.BillboardWorkflow = BillboardCreationWorkflow.FixedDepthLine; value.Editor.BillboardWorkflow = BillboardCreationWorkflow.ScreenFirst; });
        Changed(check, "tool A to B to A is rejected without polling the intermediate tool", value => { value.Editor.Tool = EditorTool.Image; value.Editor.Tool = EditorTool.Freehand; });
        Changed(check, "ApplyParameters tool changes cannot revive an earlier operation", value =>
        { var parameters = value.Editor.Parameters; parameters.Tool = EditorTool.Image; value.Editor.ApplyParameters(parameters); parameters.Tool = EditorTool.Freehand; value.Editor.ApplyParameters(parameters); });
        Changed(check, "EditorSession A to B to A is rejected by controller lifetime generation", value =>
        { value.Editor.ReplaceSession(new EditorSession(value.Owner.Document.Clone()), default); value.Editor.ReplaceSession(value.Owner, default); });
        Changed(check, "even replacing the controller session with the same owner invalidates its old operation", value => value.Editor.ReplaceSession(value.Owner, default));
        Changed(check, "editing mode changes invalidate a pending source operation", value => value.Editing = false);
        Changed(check, "editing mode A to B to A is rejected by the host session version", value => { value.Editing = false; value.EditingVersion++; value.Editing = true; value.EditingVersion++; });
        Changed(check, "replacing the physical monitor snapshot invalidates a source operation", value => value.Monitors = new object());
        Changed(check, "changing the runtime binding owner lifetime invalidates a source operation", value => value.Lifetime = new object());
        Changed(check, "changing the prefab editing context invalidates a source operation", value => value.Prefab = new object());
        Changed(check, "leaving the prefab editing context invalidates a source operation", value => value.Prefab = null);
        Changed(check, "a changed plane mapping is rejected even before its producer announces content change", value => value.Owner.Document.Canvas.Projection.Matrix = new(1.1, 0, 0, 0, -.4, 700, 0, -.002, 1));
        Changed(check, "an invalid transient plane mapping fails closed instead of throwing", value => value.Owner.Document.Canvas.Projection.Matrix = default);
        Changed(check, "tracked selection changes invalidate source-edit operations", value => value.Owner.SetSelection([value.Second]));
        context = new(); guard = context.Guard(selection: false); context.Owner.SetSelection([context.Second]);
        check("add-source operations can ignore selection changes without ignoring document or placement state", context.Current(guard));
        context.Editor.ScreenOrigin = new(1, 0);
        check("selection-independent add guards still reject placement changes", !context.Current(guard));
        context = new(); guard = context.Guard(); var parametersOnly = context.Editor.Parameters; parametersOnly.Style.Opacity = .37; context.Editor.ApplyParameters(parametersOnly);
        check("working style refresh does not invalidate a source operation with unchanged placement context", context.Current(guard));
        context = new(); guard = context.Guard(); object monitors = context.Monitors; context.Monitors = new object(); bool rejected = !context.Current(guard); context.Monitors = monitors;
        check("an observed invalidation is sticky and cannot revive after values return", rejected && !context.Current(guard));
        context = new(); context.Prefab = null; guard = context.Guard();
        check("ordinary owner operations support a null prefab context", context.Current(guard));
    }
    private static void Changed(Action<string, bool> check, string name, Action<Context> mutate)
    {
        var context = new Context(); var guard = context.Guard(); mutate(context); string before = context.Fingerprint();
        check(name, !context.Current(guard) && context.Fingerprint() == before);
        if (context.Owner.IsTransactionActive) context.Owner.Cancel();
    }
    private static void Reject(Action<string, bool> check, string name, Func<Context, Func<WindowSourceOperationGuard, bool>> call)
    {
        var context = new Context(); var guard = context.Guard(); var current = call(context); string before = context.Fingerprint();
        check(name, !current(guard) && context.Fingerprint() == before);
    }
}
