using L = FloatCanvas.Localization.AppText;
using System.Security.Cryptography;

namespace FloatCanvas.Core;

/// <summary>Content-addressed immutable image resources, shared by documents and history.</summary>
public sealed class AssetLibrary
{
    private readonly Dictionary<string, AssetResource> resources = new(StringComparer.Ordinal);
    public int Count => resources.Count;
    public long TotalBytes => resources.Values.Sum(a => (long)a.Data.Length);
    public IReadOnlyCollection<AssetResource> Resources => resources.Values;
    public string Add(byte[] data, string mediaType = "image/png")
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new ArgumentException(L.Get("Assets.EmptyResource"), nameof(data));
        string id = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!resources.ContainsKey(id)) resources.Add(id, new(id, mediaType, (byte[])data.Clone()));
        return id;
    }
    public AssetResource Get(string id) => resources.TryGetValue(id, out var resource) ? resource : throw new KeyNotFoundException(L.Format("Assets.MissingResource", id));
    /// <summary>Shares an already immutable content-addressed resource, without copying or rehashing its pixels.</summary>
    public void Add(AssetResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resources.TryAdd(resource.Id, resource);
    }
    public bool TryGet(string id, out AssetResource resource) => resources.TryGetValue(id, out resource!);
    public void Prune(IEnumerable<string> retainedIds)
    {
        var retained = retainedIds.ToHashSet(StringComparer.Ordinal);
        foreach (string id in resources.Keys.Where(id => !retained.Contains(id)).ToArray()) resources.Remove(id);
    }
}

public sealed class AssetResource
{
    internal AssetResource(string id, string mediaType, byte[] data) { Id = id; MediaType = mediaType; Data = data; }
    public string Id { get; }
    public string MediaType { get; }
    public ReadOnlyMemory<byte> Data { get; }
}
