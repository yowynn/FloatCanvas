using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ImagePresetOpacityTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<string, bool> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FloatCanvas-image-opacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var library = new ToolPresetLibrary(Path.Combine(folder, "presets.json"));
            var source = Image(SKColors.Blue);
            foreach (string id in new[] { "official.image", "official.image-half", "official.image-faint" })
            {
                var parameters = library.Apply(id);
                var editor = new EditorController(new()); editor.ApplyParameters(parameters);
                var memory = new Dictionary<EditorTool, ToolParameters> { [EditorTool.Image] = new() { Tool = EditorTool.Image } };
                var desktop = Desktop(editor, library, memory);
                double opacity = Opacity(desktop);
                check(id + " insertion reads the active image parameters ahead of stale toolbar memory", opacity == parameters.Style.Opacity);
                var target = new EditorSession(new() { Viewport = new(0, 0, 100, 80) });
                ImageImportCommand.Insert(target, [source], target.Document.Viewport, opacity: opacity);
                var inserted = target.Document.Elements.Single();
                check(id + " creates one editable image with the chosen opacity and original encoded resource", inserted.Style.Opacity == opacity &&
                    target.Assets.Get(inserted.AssetId!).Data.Span.SequenceEqual(source.PngBytes) && target.UndoCount == 1 && target.Selection.SetEquals([inserted.Id]));
                using var renderer = new SceneRenderer();
                using var pixels = SKBitmap.Decode(renderer.ExportPng(target.Document, target.Assets, transparent: true));
                check(id + " changes the actual exported image alpha", Math.Abs(pixels.GetPixel(50, 40).Alpha - Math.Round(255 * opacity)) <= 1);
                check(id + " image opacity survives one-step undo and redo", target.Undo() && target.Document.Elements.Count == 0 && target.Redo() &&
                    target.Document.Elements.Single().Style.Opacity == opacity);

                var projected = new EditorSession(Projected());
                ImageImportCommand.Insert(projected, [source], projected.Document.Viewport, opacity: opacity);
                check(id + " planar insertion retains its opacity while perspective is enabled", !projected.Document.Elements.Single().IsBillboard &&
                    projected.Document.Elements.Single().Style.Opacity == opacity);
                var billboard = new EditorSession(Projected());
                check(id + " has a valid billboard depth fixture", CanvasMapping.Create(billboard.Document).TryGetEqualDepthLine(new(150, 250), out var depth));
                ImageImportCommand.InsertBillboard(billboard, [source], billboard.Document.Viewport, depth, new(300, 200), opacity);
                check(id + " billboard insertion retains opacity without changing source pixels", billboard.Document.Elements.Single() is { IsBillboard: true } standing &&
                    standing.Style.Opacity == opacity && billboard.Assets.Get(standing.AssetId!).Data.Span.SequenceEqual(source.PngBytes) && billboard.UndoCount == 1);
            }

            var working = library.Apply("official.image-faint");
            var state = new ToolStateStore(Path.Combine(folder, "tool-state.json"));
            state.Tools[EditorTool.Image] = working; state.Save();
            var restored = new ToolStateStore(Path.Combine(folder, "tool-state.json"));
            var pen = new EditorController(new()); pen.Style.Opacity = .1;
            var otherTool = Desktop(pen, library, restored.Tools);
            check("imports from another active tool use the restored image opacity rather than pen opacity", Opacity(otherTool) == .25 && pen.Style.Opacity == .1);
            restored.Tools.Clear();
            check("an image tool without saved state defaults to full opacity while another tool is active", Opacity(otherTool) == 1 && pen.Style.Opacity == .1);

            var dropTarget = new EditorSession();
            using var drop = new ImageDropRequest(dropTarget, dropTarget.Document.Viewport, CancellationToken.None, .5);
            check("the direct drop commit path applies its captured image opacity atomically", drop.TryCommit(drop, dropTarget, null, [source]) &&
                dropTarget.Document.Elements.Single().Style.Opacity == .5 && dropTarget.UndoCount == 1);
            using var deferred = new ImageDropRequest(dropTarget, dropTarget.Document.Viewport, CancellationToken.None, .25);
            check("a deferred billboard drop retains the captured opacity after completion", deferred.TryCompleteDeferred(deferred, dropTarget, null) && deferred.Opacity == .25);

            var replacement = new EditorSession();
            ImageImportCommand.Insert(replacement, [source], replacement.Document.Viewport);
            var original = replacement.Document.Elements.Single();
            check("callers omitting opacity retain the existing full-opacity default", original.Style.Opacity == 1);
            original.Style.Opacity = .7; original.Style.Stroke = 0xFF123456; original.ImageBorderWidth = 5;
            original.Effects.Add(new() { Kind = ImageEffectKind.Blur, Bounds = new(1, 1, 4, 4), Amount = 6 });
            string before = JsonSerializer.Serialize(original);
            string oldAsset = original.AssetId!;
            var newSource = Image(SKColors.Red);
            ImageImportCommand.Insert(replacement, [newSource], replacement.Document.Viewport, replace: true, opacity: .25);
            var replaced = original.Clone(); replaced.AssetId = oldAsset;
            check("replacing pixels preserves the target opacity border effects geometry and selection", JsonSerializer.Serialize(replaced) == before &&
                replacement.Assets.Get(original.AssetId!).Data.Span.SequenceEqual(newSource.PngBytes) && replacement.Selection.SetEquals([original.Id]));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static NormalizedImage Image(SKColor color)
    {
        using var pixels = new SKBitmap(20, 12); pixels.Erase(color);
        using var image = SKImage.FromBitmap(pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(data.ToArray(), 20, 12);
    }

    private static SceneDocument Projected() => new()
    {
        Viewport = new(0, 0, 800, 600),
        Canvas = new() { Projection = new() { Enabled = true, Matrix = new(1, .08, 0, 0, 1, 0, 0, .001, 1),
            Quad = [new(0, 0), new(800, 0), new(600, 400), new(0, 400)] } }
    };

    private static DesktopSession Desktop(EditorController editor, ToolPresetLibrary library, Dictionary<EditorTool, ToolParameters> memory)
    {
        var desktop = (DesktopSession)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSession));
        typeof(DesktopSession).GetField("_editor", Private)!.SetValue(desktop, editor);
        typeof(DesktopSession).GetField("_toolPresets", Private)!.SetValue(desktop, library);
        typeof(DesktopSession).GetField("_toolbarToolMemory", Private)!.SetValue(desktop, memory);
        return desktop;
    }
    private static double Opacity(DesktopSession desktop) => (double)typeof(DesktopSession).GetMethod("ImageInsertionOpacity", Private)!.Invoke(desktop, null)!;
}
