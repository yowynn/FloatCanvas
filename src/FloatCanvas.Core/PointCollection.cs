using System.Collections;

namespace FloatCanvas.Core;

/// <summary>A mutable path whose snapshots share immutable chunks. Appending after a snapshot
/// copies at most one partial chunk, rather than the entire captured path.</summary>
public sealed class PointCollection : IList<PointD>, IReadOnlyList<PointD>
{
    private const int ChunkSize = 1024;
    private List<PointD[]> chunks = [];
    private int count, version;
    private bool sharedTable;
    private PointD[]? writableTail, contiguous;
    public PointCollection() { }
    public PointCollection(IEnumerable<PointD> points) => AddRange(points);
    public int Count => count;
    public bool IsReadOnly => false;
    public PointD this[int index]
    {
        get { ValidateIndex(index); return chunks[index / ChunkSize][index % ChunkSize]; }
        set
        {
            ValidateIndex(index); Changed(); OwnTable();
            int chunk = index / ChunkSize;
            var values = chunks[chunk];
            if (!ReferenceEquals(values, writableTail))
            {
                values = (PointD[])values.Clone(); chunks[chunk] = values;
                if (chunk == chunks.Count - 1) writableTail = values;
            }
            values[index % ChunkSize] = value;
        }
    }
    public PointCollection Snapshot()
    {
        sharedTable = true; writableTail = null;
        return new PointCollection { chunks = chunks, count = count, sharedTable = true, contiguous = contiguous };
    }
    public bool ContentEquals(PointCollection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (count != other.count) return false;
        if (ReferenceEquals(chunks, other.chunks)) return true;
        for (int index = 0; index < chunks.Count; index++)
            if (!ReferenceEquals(chunks[index], other.chunks[index]) &&
                !chunks[index].AsSpan(0, ChunkCount(index)).SequenceEqual(other.chunks[index].AsSpan(0, ChunkCount(index)))) return false;
        return true;
    }
    internal object StorageIdentity => chunks;
    /// <summary>A compatibility view. Chunk-aware callers should use SequenceEqual to avoid flattening.</summary>
    public ReadOnlySpan<PointD> AsSpan()
    {
        if (count == 0) return [];
        if (chunks.Count == 1) return chunks[0].AsSpan(0, count);
        if (contiguous is null) { contiguous = new PointD[count]; CopyTo(contiguous, 0); }
        return contiguous;
    }
    /// <summary>Compares a contiguous renderer path cache without allocating a flattened path.</summary>
    public bool SequenceEqual(ReadOnlySpan<PointD> other)
    {
        if (other.Length != count) return false;
        for (int index = 0; index < chunks.Count; index++)
        {
            int length = ChunkCount(index);
            if (!chunks[index].AsSpan(0, length).SequenceEqual(other.Slice(index * ChunkSize, length))) return false;
        }
        return true;
    }
    private int ChunkCount(int index) => Math.Min(ChunkSize, count - index * ChunkSize);
    private void ValidateIndex(int index)
    { if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private void Changed() { version++; contiguous = null; }
    private void OwnTable()
    {
        if (!sharedTable) return;
        chunks = new(chunks); sharedTable = false;
    }
    private void Append(PointD item, int capacity = 4)
    {
        OwnTable();
        int offset = count % ChunkSize;
        if (offset == 0) { writableTail = new PointD[capacity]; chunks.Add(writableTail); }
        else if (writableTail is null)
        { writableTail = (PointD[])chunks[^1].Clone(); chunks[^1] = writableTail; }
        if (offset == writableTail!.Length)
        {
            var grown = new PointD[Math.Min(ChunkSize, Math.Max(offset + 1, offset * 2))];
            writableTail.CopyTo(grown, 0); writableTail = grown; chunks[^1] = grown;
        }
        writableTail![offset] = item; count++;
    }
    public void Add(PointD item) { Changed(); Append(item); }
    public void AddRange(IEnumerable<PointD> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (ReferenceEquals(items, this)) items = Snapshot();
        int capacity = items.TryGetNonEnumeratedCount(out int additional)
            ? (int)Math.Clamp((long)count % ChunkSize + additional, 4, ChunkSize) : 4;
        foreach (var item in items) { Changed(); Append(item, capacity); }
    }
    public void Clear()
    {
        if (count == 0) return;
        Changed(); chunks = []; count = 0; sharedTable = false; writableTail = null;
    }
    public bool Contains(PointD item) => IndexOf(item) >= 0;
    public void CopyTo(PointD[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || count > array.Length - arrayIndex) throw new ArgumentException("The destination array is too small.", nameof(array));
        for (int index = 0; index < chunks.Count; index++)
            chunks[index].AsSpan(0, ChunkCount(index)).CopyTo(array.AsSpan(arrayIndex + index * ChunkSize));
    }
    public IEnumerator<PointD> GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(PointD item)
    {
        for (int index = 0; index < chunks.Count; index++)
        {
            int found = chunks[index].AsSpan(0, ChunkCount(index)).IndexOf(item);
            if (found >= 0) return index * ChunkSize + found;
        }
        return -1;
    }
    public void Insert(int index, PointD item)
    {
        if ((uint)index > (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == count) { Add(item); return; }
        var replacement = new PointCollection();
        int capacity = (int)Math.Clamp((long)count + 1, 4, ChunkSize);
        for (int i = 0; i < count; i++) { if (i == index) replacement.Append(item, capacity); replacement.Append(this[i], capacity); }
        Replace(replacement);
    }
    public bool Remove(PointD item)
    {
        int index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index); return true;
    }
    public void RemoveAt(int index)
    {
        ValidateIndex(index);
        var replacement = new PointCollection();
        int capacity = Math.Clamp(count - 1, 4, ChunkSize);
        for (int i = 0; i < count; i++) if (i != index) replacement.Append(this[i], capacity);
        Replace(replacement);
    }
    private void Replace(PointCollection replacement)
    {
        Changed(); chunks = replacement.chunks; count = replacement.count;
        sharedTable = false; writableTail = replacement.writableTail;
    }
    private sealed class Enumerator(PointCollection owner) : IEnumerator<PointD>
    {
        private readonly int expectedVersion = owner.version;
        private int index = -1;
        public PointD Current => index >= 0 && index < owner.count ? owner[index] : throw new InvalidOperationException();
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (expectedVersion != owner.version) throw new InvalidOperationException("Collection was modified during enumeration.");
            return ++index < owner.count;
        }
        public void Reset()
        {
            if (expectedVersion != owner.version) throw new InvalidOperationException("Collection was modified during enumeration.");
            index = -1;
        }
        public void Dispose() { }
    }
    public static implicit operator PointCollection(List<PointD> points) => new(points);
    public static implicit operator PointCollection(PointD[] points) => new(points);
}
