using FloatCanvas.Core;
using System.Text.Json;

internal static class SelectionSnapshotOpacityTests
{
    internal static void Run(Action<bool, string> check)
    {
        var outer = new CanvasGroup { Opacity = .5 };
        var inner = new CanvasGroup { ParentId = outer.Id, Opacity = .4 };
        var leaf = new CanvasElement { Kind = ElementKind.Rectangle, GroupId = inner.Id,
            Width = 100, Height = 80, Style = new() { Opacity = .8 } };
        var definitionLeaf = leaf.Clone(); definitionLeaf.Id = Guid.NewGuid(); definitionLeaf.GroupId = null;
        var definition = new PrefabDefinition { Graph = new() { Elements = [definitionLeaf] } };
        var instance = new PrefabInstance { ParentId = inner.Id, DefinitionId = definition.Id, Opacity = .6 };
        var session = new EditorSession(new SceneDocument
            { Groups = [outer, inner], Elements = [leaf], Instances = [instance], Definitions = [definition], RootOrder = [leaf.Id, instance.Id] });
        string authored = JsonSerializer.Serialize(session.Document);
        foreach (var (id, expected, label) in new[]
        {
            (leaf.Id, .16, "child leaf"), (instance.Id, .096, "shared instance")
        })
        {
            session.SetSelection([id]);
            var snapshot = session.CreateSelectionSnapshot();
            check(snapshot.Groups.Count == 0 && Math.Abs(SceneEvaluation.Build(snapshot).Leaves.Single().RenderElement.Style.Opacity - expected) < 1e-9,
                label + " clipboard/stamp snapshot bakes removed ancestors' opacity exactly once");
        }
        session.SetSelection([inner.Id, leaf.Id]);
        var grouped = session.CreateSelectionSnapshot();
        check(grouped.Groups.Count == 1 && Math.Abs(grouped.Groups.Single().Opacity - .2) < 1e-9 && grouped.Elements.Single().Style.Opacity == .8,
            "selected nested group retains child settings and bakes only its removed ancestor");
        check(SceneEvaluation.Build(grouped).Leaves.Select(item => item.RenderElement.Style.Opacity).Order().Zip(new[] { .096, .16 }).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9),
            "nested group snapshot preserves leaf and instance appearances without double application");
        check(JsonSerializer.Serialize(session.Document) == authored, "creating translucent selection snapshots leaves authored source unchanged");
    }
}
