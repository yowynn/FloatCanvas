using System.Text.Json;
using FloatCanvas.Core;

internal static class CanvasResolutionConversionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var original = new SceneDocument { Viewport = new(0, 0, 3840, 2160), AuthoringReference = new(new(0, 0, 3840, 2160), new(0, 0, 3840, 2160)) };
        var image = new CanvasElement { Kind = ElementKind.Image, Width = 400, Height = 200, AssetId = new string('a', 64),
            Transform = Affine2D.Translation(600, 400), ImageCrop = new(.1, .2, .7, .6), ImageBorderWidth = 3, Locked = true, Visible = false,
            Style = new() { Opacity = .6 } };
        original.Elements = [image];
        string before = JsonSerializer.Serialize(original);
        var half = CanvasResolutionConversion.Prepare(original, new(0, 0, 1920, 1080));
        var oldWidth = ElementGeometry.Bounds(half.Document.Elements.Single(), includeStroke: false).Width;
        var fresh = new CanvasElement { Kind = ElementKind.Rectangle, Width = 400, Height = 100 };
        check(Math.Abs(oldWidth - 200) < 1e-7 && CanvasMapping.Create(half.Document).ProjectedBounds(fresh, includeStroke: false).Width == 400,
            "4K to 1080 converts existing 400px to effective 200px while a new 400px object remains 400 physical pixels");
        check(half.Document.AuthoringReference!.Frame == new RectD(0, 0, 1920, 1080) && half.Document.Viewport == new RectD(0, 0, 1920, 1080),
            "resolution conversion updates both authored frame and target output viewport");
        check(!CanvasResolutionConversion.NeedsConversion(half.Document, new(-1920, 200, 1920, 1080)) && CanvasResolutionConversion.NeedsConversion(original, new(0, 0, 1920, 1080)),
            "physical screen dimensions drive compatibility independently of desktop origin");
        var changed = half.Document.Elements.Single();
        check(changed.AssetId == image.AssetId && changed.ImageCrop == image.ImageCrop && changed.Style.Opacity == image.Style.Opacity && changed.Locked && !changed.Visible &&
            changed.Width == image.Width && changed.ImageBorderWidth == image.ImageBorderWidth,
            "conversion changes placement scale once, preserving raw pixels, normalized crop, opacity, flags and local geometry");
        check(JsonSerializer.Serialize(original) == before && half.Document.ResourceId != original.ResourceId && !half.Document.ResourceIdentityPersistent,
            "conversion creates a new unsaved resource identity without mutating the source project");
        var current = original;
        foreach (var target in new[] { new RectD(0, 0, 1920, 1200), new RectD(0, 0, 3440, 1440), new RectD(0, 0, 1280, 1024), new RectD(0, 0, 3840, 2160) })
        {
            current = CanvasResolutionConversion.Prepare(current, target).Document;
            check(Math.Abs(current.AuthoringReference!.Frame.Width / current.AuthoringReference.Frame.Height - 16d / 9) < 1e-9 &&
                !CanvasResolutionConversion.NeedsConversion(current, target), "aspect-ratio bars are excluded from the authored frame but the actual target screen is remembered");
        }
        check(Math.Abs(ElementGeometry.Bounds(current.Elements.Single(), includeStroke: false).Width - 400) < 1e-6 && current.AuthoringReference!.Frame == original.AuthoringReference.Frame,
            "repeated 16:9, 16:10, ultrawide and 5:4 conversions return to the original size without cumulative shrinking");
        var large = original.Clone(); large.Viewport = new(0, 0, 1920, 1080);
        check(CanvasAuthoringReference.FromSavedDocument(large).Frame == original.Viewport &&
            CanvasResolutionConversion.Prepare(large, new(0, 0, 1920, 1080)).Document.AuthoringReference!.Frame.Width == 1920,
            "a runtime viewport resize cannot replace the saved 4K conversion basis");
        var legacy = new SceneDocument { Viewport = new(-1920, 0, 5760, 2160), DisplayLayout = [new() { Bounds = new(0, 0, 3840, 2160) }] };
        var multiple = CanvasResolutionConversion.Prepare(legacy, new(0, 0, 1920, 1080));
        check(multiple.Document.AuthoringReference!.Frame == new RectD(-960, 0, 2880, 1080), "multi-screen legacy conversion uses the saved reference display and scales the full authored frame uniformly");
        check(CanvasAuthoringReference.FromSavedDocument(new()).Frame == new RectD(0, 0, 1920, 1080), "legacy files without layouts use their saved viewport rather than the opening machine");
        var tiny = CanvasResolutionConversion.Prepare(original, new(0, 0, 16, 9));
        check(Math.Abs(ElementGeometry.Bounds(tiny.Document.Elements.Single(), includeStroke: false).Width - 400d / 240) < 1e-7,
            "authored resolution conversion is independent of the editor camera's one-percent zoom limit");
        var dependency = new PrefabDefinition { Graph = new() { Elements = [new() { Kind = ElementKind.Text, Text = "resource", Width = 40, Height = 20 }] } };
        var instance = new PrefabInstance { DefinitionId = dependency.Id, Transform = Affine2D.Translation(100, 200) };
        var owner = original.Clone(); owner.Elements.Clear(); owner.Definitions = [dependency]; owner.Instances = [instance]; owner.RootOrder = [instance.Id];
        var convertedOwner = CanvasResolutionConversion.Prepare(owner, new(0, 0, 1920, 1080)).Document;
        check(JsonSerializer.Serialize(convertedOwner.Definitions) == JsonSerializer.Serialize(owner.Definitions) && convertedOwner.Instances.Single().DefinitionId == dependency.Id &&
            convertedOwner.Instances.Single().Transform.M11 == .5, "resolution conversion preserves shared resource dependencies and scales only their owned instances");
        original.Kind = DocumentKind.FrozenCapture;
        check(!CanvasResolutionConversion.NeedsConversion(original, new(0, 0, 1920, 1080)), "frozen captures retain their separate original-pixel semantics");
    }
}
