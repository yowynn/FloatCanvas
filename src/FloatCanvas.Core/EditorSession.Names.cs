using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    public const int MaximumLayerNameLength = 1024;

    private static string? NormalizeInsertionName(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > MaximumLayerNameLength) throw new ArgumentException(L.Get("Layers.NameTooLong"), nameof(name));
        return name;
    }

    private string NextGroupName()
    {
        var names = Document.Groups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index <= names.Count + 1; index++)
        {
            string name = L.Format("Layers.NumberedGroup", index);
            if (!names.Contains(name)) return name;
        }
        throw new InvalidOperationException("Cannot allocate a group name.");
    }

    /// <summary>Renames one layer, including a locked layer, without changing its content or selection.</summary>
    public void RenameElement(Guid id, string? name)
        => RenameLayer(id, name);

    public void RenameLayer(Guid id, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > MaximumLayerNameLength) throw new ArgumentException(L.Get("Layers.NameTooLong"), nameof(name));
        var graph = new SceneGraph(Document);
        if (graph.FindGroup(id) is { } group)
        {
            if (group.Name == (name ?? "")) return;
            Execute(L.Get("Layers.Rename"), _ => group.Name = name ?? ""); return;
        }
        if (graph.FindInstance(id) is { } instance)
        {
            if (instance.Name == (name ?? "")) return;
            Execute(L.Get("Layers.Rename"), _ => instance.Name = name ?? ""); return;
        }
        var element = graph.FindElement(id) ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        if (element.Name == name) return;
        Execute(L.Get("Layers.Rename"), _ => element.Name = name);
    }
}
