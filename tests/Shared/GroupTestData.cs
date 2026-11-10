using FloatCanvas.Core;

// Fixtures that need a destination container must contain actual artwork.
internal static class GroupTestData
{
    internal static Guid CreatePopulatedGroup(this EditorSession session, string? name = null)
    {
        session.Add(new CanvasElement { Kind = ElementKind.Rectangle, Width = 8, Height = 8,
            Name = "Container fixture artwork", Style = new() { Fill = 0xFF336699 } });
        return session.GroupSelection(name)!.Value;
    }
}
