using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Core;

public sealed partial class EditorSession
{
    public const int MaximumLayerNameLength = 1024;

    /// <summary>Renames one layer, including a locked layer, without changing its content or selection.</summary>
    public void RenameElement(Guid id, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > MaximumLayerNameLength) throw new ArgumentException(L.Get("Layers.NameTooLong"), nameof(name));
        var element = Document.Elements.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException(L.Get("Inspector.LayerMissing"));
        if (element.Name == name) return;
        Execute(L.Get("Layers.Rename"), document => document.Elements.Single(e => e.Id == id).Name = name);
    }
}
