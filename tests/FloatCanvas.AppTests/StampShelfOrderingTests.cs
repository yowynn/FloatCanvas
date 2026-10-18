using System.IO;
using System.Text.Json.Nodes;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class StampShelfOrderingTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        string root = Environment.GetEnvironmentVariable("FLOATCANVAS_STAMP_ORDER_TEST_DIR") ?? Path.GetTempPath();
        string profile = Path.Combine(root, "FloatCanvas-stamp-order-" + Guid.NewGuid().ToString("N"));
        var library = new StampLibrary(profile); await library.LoadAsync();
        var source = new EditorSession(); source.Add(new() { Kind = ElementKind.Rectangle, Width = 40, Height = 30 });
        var a = await library.SaveSelectionAsync(source, "A");
        var b = await library.SaveSelectionAsync(source, "B", StampSaveMode.RasterMerge);
        var c = await library.SaveSelectionAsync(source, "C");
        await library.PinAsync(9, a.Id); await library.PinAsync(0, b.Id);
        Guid?[] pins = library.Pins.ToArray();
        check("new stamps retain the existing newest-first initial presentation", library.Items.Select(item => item.Id).SequenceEqual([c.Id, b.Id, a.Id]));
        await library.MoveAsync(a.Id, c.Id);
        check("drag reorder moves the exact stamp before its target without altering pins or modes", library.Items.Select(item => item.Id).SequenceEqual([a.Id, c.Id, b.Id]) &&
            library.Pins.SequenceEqual(pins) && library.Items.Single(item => item.Id == b.Id).Mode == StampSaveMode.RasterMerge);
        await library.MoveAsync(a.Id, b.Id, after: true);
        await library.RenameAsync(c.Id, "Renamed C");
        library = await Reload(profile);
        check("manual order survives catalog reload and rename", library.Items.Select(item => item.Id).SequenceEqual([c.Id, b.Id, a.Id]) && library.Items[0].Name == "Renamed C");
        await library.MoveAsync(b.Id, c.Id);
        var replacement = await library.SaveSelectionAsync(source, "Overwrite A", replaceId: a.Id);
        library = await Reload(profile);
        check("overwriting a stamp preserves explicit order and every fixed slot", library.Items.Select(item => item.Id).SequenceEqual([b.Id, c.Id, a.Id]) && library.Pins.SequenceEqual(pins) &&
            library.Items[2].Revision == replacement.Revision);
        string indexPath = Path.Combine(library.DirectoryPath, "library.json");
        byte[] before = await File.ReadAllBytesAsync(indexPath);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledMove = false;
        try { await library.MoveAsync(a.Id, b.Id, ct: cancelled.Token); } catch (OperationCanceledException) { cancelledMove = true; }
        check("cancelled reorder cannot publish a partial order", cancelledMove && (await File.ReadAllBytesAsync(indexPath)).SequenceEqual(before) && library.Items[0].Id == b.Id);
        bool missing = false;
        try { await library.MoveAsync(a.Id, Guid.NewGuid()); } catch (KeyNotFoundException) { missing = true; }
        check("a vanished reorder target preserves the current catalog", missing && (await File.ReadAllBytesAsync(indexPath)).SequenceEqual(before));

        // Exercise a pre-order catalog: physical insertion order differs from the
        // historical UpdatedAtUtc presentation, and the first write must freeze it.
        var legacy = JsonNode.Parse(before)!.AsObject(); legacy.Remove("Order");
        var entries = legacy["Items"]!.AsArray();
        foreach (var entry in entries) entry!["UpdatedAtUtc"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(entry!["Id"]!.GetValue<Guid>() == a.Id ? 3 : entry["Id"]!.GetValue<Guid>() == b.Id ? 2 : 1);
        await File.WriteAllTextAsync(indexPath, legacy.ToJsonString());
        library = await Reload(profile);
        check("legacy catalogs migrate from their prior visible newest-first order", library.Items.Select(item => item.Id).SequenceEqual([a.Id, b.Id, c.Id]));
        await library.MoveAsync(c.Id, a.Id);
        library = await Reload(profile);
        check("the first manual move persists the migrated order independently of update times", library.Items.Select(item => item.Id).SequenceEqual([c.Id, a.Id, b.Id]) && library.Pins.SequenceEqual(pins));

        var malformed = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!;
        malformed["Order"] = new JsonArray(a.Id, a.Id, c.Id);
        await File.WriteAllTextAsync(indexPath, malformed.ToJsonString());
        byte[] broken = await File.ReadAllBytesAsync(indexPath); bool rejected = false;
        try { await Reload(profile); } catch (InvalidDataException) { rejected = true; }
        check("duplicate persisted order identities are rejected without rewriting user data", rejected && (await File.ReadAllBytesAsync(indexPath)).SequenceEqual(broken));
    }

    private static async Task<StampLibrary> Reload(string profile)
    { var copy = new StampLibrary(profile); await copy.LoadAsync(); return copy; }
}
