using System.Collections;

namespace FloatCanvas.Core;

/// <summary>A mutable path whose snapshots share storage until a path is changed.</summary>
public sealed class PointCollection : IList<PointD>, IReadOnlyList<PointD>
{
    private List<PointD> values = [];
    private bool shared;
    public PointCollection() { }
    public PointCollection(IEnumerable<PointD> points) => values = [.. points];
    public int Count => values.Count;
    public bool IsReadOnly => false;
    public PointD this[int index] { get => values[index]; set { Own(); values[index] = value; } }
    public PointCollection Snapshot()
    {
        shared = true;
        return new PointCollection { values = values, shared = true };
    }
    public bool ContentEquals(PointCollection other) => ReferenceEquals(values, other.values) || values.SequenceEqual(other.values);
    internal object StorageIdentity => values;
    public ReadOnlySpan<PointD> AsSpan() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);
    private void Own() { if (!shared) return; values = [.. values]; shared = false; }
    public void Add(PointD item) { Own(); values.Add(item); }
    public void AddRange(IEnumerable<PointD> items) { Own(); values.AddRange(items); }
    public void Clear() { if (values.Count == 0) return; values = []; shared = false; }
    public bool Contains(PointD item) => values.Contains(item);
    public void CopyTo(PointD[] array, int arrayIndex) => values.CopyTo(array, arrayIndex);
    public IEnumerator<PointD> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(PointD item) => values.IndexOf(item);
    public void Insert(int index, PointD item) { Own(); values.Insert(index, item); }
    public bool Remove(PointD item) { Own(); return values.Remove(item); }
    public void RemoveAt(int index) { Own(); values.RemoveAt(index); }
    public static implicit operator PointCollection(List<PointD> points) => new(points);
    public static implicit operator PointCollection(PointD[] points) => new(points);
}
