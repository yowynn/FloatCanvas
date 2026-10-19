using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Storage;
using SkiaSharp;

internal static class WorkspacePreviewTests
{
    public static async Task RunAsync(Action<string, bool> assert)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "workspace-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CorruptHistoryWindow(Path.Combine(root, "corrupt-window"), assert);
        var assets = new AssetLibrary();
        foreach (var (viewport, width, height) in new (RectD, int, int)[]
        {
            (new(-1920, -1080, 3840, 2160), 512, 288),
            (new(-1080, -1920, 2160, 3840), 288, 512),
            (new(-300, -200, 100, 60), 100, 60),
            (new(0, 0, 1, 100000), 1, 512),
            (new(0, 0, 512.5, 256.25), 512, 256)
        })
        {
            using var bitmap = Decode(WorkspacePreview.Render(new() { Viewport = viewport }, assets)!);
            assert($"preview {viewport.Width}x{viewport.Height} is bounded to {width}x{height}", bitmap.Width == width && bitmap.Height == height);
            assert("empty annotation preview has transparent pixels", bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).Alpha == 0);
        }
        assert("zero viewport has no preview", WorkspacePreview.Render(new() { Viewport = new(0, 0, 0, 100) }, assets) is null);
        assert("nonfinite viewport has no preview", WorkspacePreview.Render(new() { Viewport = new(0, 0, double.PositiveInfinity, 100) }, assets) is null);

        byte[] desktopPixels = ImageBytes(32, 16, (_, _) => SKColors.Blue);
        string desktopId = assets.Add(desktopPixels);
        var scene = new SceneDocument
        {
            Viewport = new(-200, -100, 400, 200),
            Background = new() { Kind = ElementKind.Image, AssetId = desktopId, Width = 400, Height = 200, Transform = Affine2D.Translation(-200, -100) },
            Elements = [Rectangle(-170, -80, 80, 60, SKColors.Red)]
        };
        byte[] redPreview = WorkspacePreview.Render(scene, assets)!;
        using (var bitmap = Decode(redPreview))
        {
            assert("negative monitor coordinates are translated exactly once", bitmap.GetPixel(50, 40) == SKColors.Red && bitmap.GetPixel(250, 140).Alpha == 0);
            assert("saved background is excluded from overlay preview", bitmap.GetPixel(390, 190).Alpha == 0 && bitmap.GetPixel(0, 0).Alpha == 0);
        }
        var imageScene = scene.Clone();
        imageScene.Elements.Clear();
        string imageId = assets.Add(ImageBytes(32, 16, (x, _) => x < 16 ? SKColors.Transparent : SKColors.Cyan));
        imageScene.Elements.Add(new() { Kind = ElementKind.Image, AssetId = imageId, Width = 80, Height = 60, Transform = Affine2D.Translation(-100, -50) });
        using (var bitmap = Decode(WorkspacePreview.Render(imageScene, assets)!))
        {
            assert("overlay image pixels remain visible in thumbnail", bitmap.GetPixel(160, 70) == SKColors.Cyan);
            assert("image alpha remains transparent without desktop matte", bitmap.GetPixel(120, 70).Alpha == 0 && bitmap.GetPixel(250, 140).Alpha == 0);
        }
        var opacityScene = scene.Clone();
        opacityScene.LayerOpacity = .5;
        opacityScene.Elements.Add(Rectangle(-130, -60, 80, 60, SKColors.Red));
        var hidden = Rectangle(50, 0, 60, 60, SKColors.Blue); hidden.Visible = false;
        opacityScene.Elements.Add(hidden);
        using (var bitmap = Decode(WorkspacePreview.Render(opacityScene, assets)!))
        {
            assert("global layer opacity is applied once across overlapping marks", Math.Abs(bitmap.GetPixel(50, 40).Alpha - 128) <= 1 && bitmap.GetPixel(50, 40).Alpha == bitmap.GetPixel(85, 55).Alpha);
            assert("hidden annotations are omitted from preview", bitmap.GetPixel(270, 120).Alpha == 0);
        }
        opacityScene.Elements[0].Style.Opacity = .5;
        using (var bitmap = Decode(WorkspacePreview.Render(opacityScene, assets)!))
            assert("element opacity combines with overlay opacity", Math.Abs(bitmap.GetPixel(50, 40).Alpha - 64) <= 1);
        opacityScene.LayerOpacity = 0;
        using (var bitmap = Decode(WorkspacePreview.Render(opacityScene, assets)!))
            assert("fully transparent overlay produces transparent preview", bitmap.Pixels.All(color => color.Alpha == 0));

        var wideScene = new SceneDocument { Viewport = new(-1920, -1080, 3840, 2160), Elements = [Rectangle(-1680, -840, 600, 360, SKColors.Red)] };
        using (var bitmap = Decode(WorkspacePreview.Render(wideScene, assets)!))
            assert("scaled negative monitor coordinates map into the thumbnail", bitmap.GetPixel(50, 50) == SKColors.Red && bitmap.GetPixel(300, 200).Alpha == 0);

        await LegacyHistoryAsync(Path.Combine(root, "legacy"), scene, assets, assert);
        await CurrentAndArchiveAsync(Path.Combine(root, "current"), scene, assets, assert);
        WriteRepresentativePreview(Path.Combine(root, "representative-overlay.png"));
        Console.WriteLine($"Workspace preview artifacts: {root}");
    }

    private static void CorruptHistoryWindow(string directory, Action<string, bool> assert)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var originalContext = SynchronizationContext.Current;
        var originalWindows = app.Windows.Cast<Window>().ToArray();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        Directory.CreateDirectory(directory);
        var brokenStore = new RecoveryStore(Path.Combine(directory, "broken"));
        Directory.CreateDirectory(brokenStore.DirectoryPath);
        File.WriteAllBytes(brokenStore.CurrentPath, [1, 2, 3]);
        File.WriteAllBytes(brokenStore.BackupPath, [4, 5, 6]);
        var brokenHistory = new WorkspaceRecoveryHistory(brokenStore);
        assert("corrupt recovery packages are excluded from the history list", brokenHistory.GetEntries().Count == 0);
        var brokenEntry = new WorkspaceRecoveryEntry(brokenStore.DirectoryPath, DateTime.UtcNow, true);
        var window = new WorkspaceHistoryWindow(brokenHistory, [brokenEntry]);
        var image = new Image();
        var placeholder = new TextBlock { Text = FloatCanvas.Localization.AppText.Get("Recovery.PreviewLoading") };
        try
        {
            var loader = typeof(WorkspaceHistoryWindow).GetMethod("LoadPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            AwaitUi((Task)loader.Invoke(window, [brokenEntry, image, placeholder])!);
            assert("corrupt history generations are contained by the actual window preview loader", placeholder.Text == FloatCanvas.Localization.AppText.Get("Recovery.NoPreview") && image.Source is null);
            assert("failed preview leaves hidden history window usable and selection intact", !window.IsVisible && window.IsEnabled && ReferenceEquals(window.SelectedEntry, brokenEntry));
            var validStore = new RecoveryStore(Path.Combine(directory, "valid"));
            var state = new WorkspaceState { LiveDocument = new() { Viewport = new(0, 0, 200, 100), Elements = [Rectangle(20, 20, 60, 40, SKColors.Red)] } };
            var assets = new AssetLibrary();
            AwaitUi(validStore.SaveWithPreviewAsync(state, assets, WorkspacePreview.Render(state.LiveDocument, assets)));
            var validEntry = new WorkspaceRecoveryHistory(validStore).GetEntries().Single();
            AwaitUi((Task)loader.Invoke(window, [validEntry, image, placeholder])!);
            assert("history preview loader releases its gate after failure and loads the next valid row", image.Source is not null && placeholder.Visibility == Visibility.Collapsed);
        }
        finally
        {
            window.Close();
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
        assert("detached history failure fixture leaves no additional windows", app.Windows.Cast<Window>().SequenceEqual(originalWindows));
    }

    private static void AwaitUi(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { if (task.IsCompleted || watch.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            if (!task.IsCompleted) throw new TimeoutException("Detached history preview loader did not complete.");
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task LegacyHistoryAsync(string directory, SceneDocument scene, AssetLibrary assets, Action<string, bool> assert)
    {
        var store = new RecoveryStore(directory);
        var state = new WorkspaceState { LiveDocument = scene.Clone() };
        await store.SaveAsync(state, assets);
        state.LiveDocument.NextNumber++;
        await store.SaveAsync(state, assets);
        byte[] current = await File.ReadAllBytesAsync(store.CurrentPath), previous = await File.ReadAllBytesAsync(store.BackupPath);
        DateTime currentTime = File.GetLastWriteTimeUtc(store.CurrentPath), previousTime = File.GetLastWriteTimeUtc(store.BackupPath);
        var history = new WorkspaceRecoveryHistory(store);
        var entry = history.GetEntries().Single();
        byte[]? preview = await history.LoadPreviewAsync(entry);
        assert("legacy history lazily renders a missing thumbnail", preview is not null);
        using (var bitmap = Decode(preview!))
            assert("legacy preview comes from saved overlay and excludes background", bitmap.GetPixel(50, 40) == SKColors.Red && bitmap.GetPixel(390, 190).Alpha == 0);
        assert("legacy preview does not rewrite either recovery generation", current.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath)) &&
            previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.BackupPath)));
        assert("legacy preview preserves archive timestamps and creates no sidecar", File.GetLastWriteTimeUtc(store.CurrentPath) == currentTime && File.GetLastWriteTimeUtc(store.BackupPath) == previousTime && Directory.GetFiles(directory).Length == 2);
    }

    private static async Task CurrentAndArchiveAsync(string directory, SceneDocument scene, AssetLibrary assets, Action<string, bool> assert)
    {
        var store = new RecoveryStore(directory);
        var history = new WorkspaceRecoveryHistory(store);
        var state = new WorkspaceState { LiveDocument = scene.Clone() };
        byte[] red = WorkspacePreview.Render(state.LiveDocument, assets)!;
        await store.SaveWithPreviewAsync(state, assets, red);
        state.LiveDocument.Elements[0].Style.Fill = 0xff0000ff;
        state.LiveDocument.NextNumber = 10;
        byte[] blue = WorkspacePreview.Render(state.LiveDocument, assets)!;
        await store.SaveWithPreviewAsync(state, assets, blue);
        var entry = history.GetEntries().Single();
        assert("embedded history thumbnail is returned byte exactly", (await history.LoadPreviewAsync(entry))!.SequenceEqual(blue));
        byte[] current = await File.ReadAllBytesAsync(store.CurrentPath), previous = await File.ReadAllBytesAsync(store.BackupPath);
        await RewriteEntryAsync(store.CurrentPath, "workspace.json", [1, 2, 3]);
        byte[] damaged = await File.ReadAllBytesAsync(store.CurrentPath);
        using (var bitmap = Decode((await history.LoadPreviewAsync(entry))!))
            assert("damaged current uses the actual previous workspace preview", bitmap.GetPixel(50, 40) == SKColors.Red);
        assert("fallback preview does not repair or overwrite damaged source", damaged.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath)) &&
            previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.BackupPath)));
        await File.WriteAllBytesAsync(store.CurrentPath, current);
        await RewriteEntryAsync(store.CurrentPath, "preview.png", [1, 2, 3]);
        byte[] badPreview = await File.ReadAllBytesAsync(store.CurrentPath);
        using (var bitmap = Decode((await history.LoadPreviewAsync(entry))!))
            assert("invalid thumbnail is regenerated from valid current workspace", bitmap.GetPixel(50, 40) == SKColors.Blue);
        assert("regenerating bad optional preview leaves package byte exact", badPreview.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(store.CurrentPath)));
        await File.WriteAllBytesAsync(store.CurrentPath, current);
        await history.PreserveCurrentAsync();
        var archive = history.GetEntries().Single(item => !item.IsCurrent);
        assert("history archive copies both complete thumbnail-bearing generations", current.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(archive.DirectoryPath, "workspace.floatcanvas"))) &&
            previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(archive.DirectoryPath, "workspace.previous.floatcanvas"))));
        assert("history archive retains its matching embedded preview", (await history.LoadPreviewAsync(archive))!.SequenceEqual(blue));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool observed = false;
        try { await history.LoadPreviewAsync(archive, canceled.Token); } catch (OperationCanceledException) { observed = true; }
        assert("canceling an obsolete history preview request is observed", observed);
    }

    private static void WriteRepresentativePreview(string path)
    {
        var assets = new AssetLibrary();
        string image = assets.Add(ImageBytes(90, 60, (x, y) => x < 12 || y < 8 ? SKColors.Transparent : SKColors.CornflowerBlue));
        var scene = new SceneDocument
        {
            Viewport = new(-1600, -900, 1600, 900),
            Elements =
            [
                Rectangle(-1500, -780, 420, 270, SKColors.Red),
                new() { Kind = ElementKind.Arrow, Width = 350, Height = 130, Transform = Affine2D.Translation(-1020, -700), Style = new() { Stroke = 0xffe53935, StrokeWidth = 16 } },
                new() { Kind = ElementKind.Text, Text = "FloatCanvas", Width = 650, Height = 130, Transform = Affine2D.Translation(-1450, -370), Style = new() { Stroke = 0xffe53935, FontSize = 94 } },
                new() { Kind = ElementKind.Image, AssetId = image, Width = 350, Height = 230, Transform = Affine2D.Translation(-620, -500) }
            ]
        };
        File.WriteAllBytes(path, WorkspacePreview.Render(scene, assets)!);
    }

    private static CanvasElement Rectangle(double x, double y, double width, double height, SKColor color) => new()
    {
        Kind = ElementKind.Rectangle, Width = width, Height = height, Transform = Affine2D.Translation(x, y),
        Style = new() { Fill = (uint)color, StrokeWidth = 0, Stroke = 0 }
    };

    private static byte[] ImageBytes(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Preview PNG is not decodable.");

    private static async Task RewriteEntryAsync(string path, string entryName, byte[] bytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }
}
