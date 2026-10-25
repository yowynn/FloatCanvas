using System.Text.Json;
using FloatCanvas.Core;

internal static class PointCollectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (int count in new[] { 256_000, 1_999_900 })
        {
            var longPath = new PointCollection(Enumerable.Range(0, count).Select(i => new PointD(i, -i)));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long beforeAppend = GC.GetAllocatedBytesForCurrentThread();
            var frozen = longPath.Snapshot();
            longPath.Add(new(count, -count));
            long appendedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAppend;
            timer.Stop();
            Console.WriteLine($"POINT_SNAPSHOT_APPEND points={count} allocatedBytes={appendedBytes} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
            check(appendedBytes < 64 * 1024, "snapshot followed by append allocates bounded chunk storage, not a whole long path " + count);
            check(frozen.Count == count && frozen[^1] == new PointD(count - 1, 1 - count), "long-path append leaves the captured prefix unchanged " + count);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var shortPath = new PointCollection([new(1, 2), new(3, 4)]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(allocated < 1024 && shortPath.Count == 2, "ordinary two-point shapes do not allocate a full-sized path chunk");

        var source = new PointCollection(Enumerable.Range(0, 2050).Select(i => new PointD(i, i)));
        var first = source.Snapshot();
        var second = source.Snapshot();
        source[0] = new(-1, -1); source[1024] = new(-2, -2); source.Add(new(2050, 2050));
        first[2049] = new(-3, -3); first.Add(new(5000, 5000));
        second.Add(new(6000, 6000));
        check(source[0].X == -1 && first[0].X == 0 && second[0].X == 0 &&
            source[1024].X == -2 && first[1024].X == 1024 && second[1024].X == 1024,
            "mutations in different chunks do not leak into shared snapshots");
        check(source[2049].X == 2049 && first[2049].X == -3 && second[2049].X == 2049 &&
            source[^1].X == 2050 && first[^1].X == 5000 && second[^1].X == 6000,
            "branches independently modify and append to a formerly shared tail");

        var flattened = source.AsSpan().ToArray();
        check(source.AsSpan().SequenceEqual(flattened), "span view contains all chunks in order");
        source[1023] = new(77, 88);
        check(source.AsSpan()[1023] == new PointD(77, 88), "index mutation invalidates a cached contiguous view");
        source.Add(new(99, 100));
        check(source.AsSpan().Length == 2052 && source.AsSpan()[^1] == new PointD(99, 100), "append invalidates a cached contiguous view");
        var comparison = source.AsSpan().ToArray();
        before = GC.GetAllocatedBytesForCurrentThread();
        bool matches = source.SequenceEqual(comparison);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(matches && allocated == 0, "renderer cache comparison reads chunks without allocating a contiguous copy");
        var copy = new PointD[source.Count + 4]; source.CopyTo(copy, 2);
        check(copy.AsSpan(2, source.Count).SequenceEqual(source.AsSpan()), "CopyTo respects its destination offset across chunks");
        check(source.ContentEquals(source.Snapshot()) && !source.ContentEquals(first), "content comparison recognizes equal snapshots and diverged branches");
        string json = JsonSerializer.Serialize(source);
        var restored = JsonSerializer.Deserialize<PointCollection>(json)!;
        check(json == JsonSerializer.Serialize(source.ToList()) && restored.ContentEquals(source), "chunked paths preserve the JSON array format and round-trip exactly");
        var duplicate = new PointCollection([new(1, 2), new(3, 4)]); duplicate.AddRange(duplicate);
        check(duplicate.Count == 4 && duplicate[0] == duplicate[2] && duplicate[1] == duplicate[3], "AddRange can append a collection to itself");
        var lazySelf = new PointCollection([new(1, 2)]);
        int selfReads = 0; bool invalidated = false;
        try
        {
            lazySelf.AddRange(lazySelf.Select(point =>
            {
                // Bound the broken behavior so this regression can fail without growing forever.
                if (++selfReads > 8) throw new InvalidDataException("Lazy self enumeration did not invalidate.");
                return point;
            }));
        }
        catch (InvalidOperationException) { invalidated = true; }
        catch (InvalidDataException) { }
        check(invalidated && selfReads == 1 && lazySelf.Count == 2,
            "lazy self AddRange invalidates its enumerator after the first appended item like List");

        var expected = Enumerable.Range(0, 2049).Select(i => new PointD(i, i)).ToList();
        var actual = new PointCollection(expected);
        var saved = new List<(PointCollection Points, PointD[] Values)>();
        var random = new Random(1729);
        for (int n = 0; n < 500; n++)
        {
            if (n % 31 == 0) saved.Add((actual.Snapshot(), expected.ToArray()));
            int index = random.Next(expected.Count + 1);
            var point = new PointD(n, -n);
            switch (n % 4)
            {
                case 0: actual.Insert(index, point); expected.Insert(index, point); break;
                case 1: index %= expected.Count; actual.RemoveAt(index); expected.RemoveAt(index); break;
                case 2: index %= expected.Count; actual[index] = point; expected[index] = point; break;
                default: actual.Add(point); expected.Add(point); break;
            }
        }
        check(actual.SequenceEqual(expected) && saved.All(item => item.Points.SequenceEqual(item.Values)), "mixed indexed edits preserve list behavior and every previous snapshot");
        var beforeClear = actual.Snapshot(); actual.Clear(); actual.Add(new(7, 8));
        check(actual.Count == 1 && beforeClear.SequenceEqual(expected), "clear and subsequent append preserve prior snapshots");
        var published = source.Snapshot(); var publishedValues = published.ToArray();
        var reading = Task.Run(() => Enumerable.Range(0, 500).All(_ => published.SequenceEqual(publishedValues)));
        for (int n = 0; n < 500; n++) { source[n] = new(-n, n); source.Add(new(n, n)); }
        check(reading.GetAwaiter().GetResult(), "a published path remains stable while another thread appends and edits its source");
    }
}
