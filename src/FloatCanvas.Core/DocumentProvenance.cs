namespace FloatCanvas.Core;

/// <summary>Package origin shared by editor snapshots; it is not editable scene content.</summary>
public sealed class DocumentProvenance
{
    private readonly object gate = new();
    public bool IsEstablished { get; private set; }
    public string? CreatedWith { get; private set; }
    public string? LastSavedWith { get; private set; }

    public DocumentProvenance() { }
    public DocumentProvenance(string? createdWith, string? lastSavedWith)
    {
        IsEstablished = true; CreatedWith = createdWith; LastSavedWith = lastSavedWith;
    }

    public string? CreatedWithForSave(string applicationVersion)
    {
        lock (gate) return IsEstablished ? CreatedWith : applicationVersion;
    }

    /// <summary>Called only after the package has been committed successfully.</summary>
    public void RecordSuccessfulSave(string applicationVersion)
    {
        lock (gate)
        {
            if (!IsEstablished) CreatedWith = applicationVersion;
            LastSavedWith = applicationVersion; IsEstablished = true;
        }
    }
}
