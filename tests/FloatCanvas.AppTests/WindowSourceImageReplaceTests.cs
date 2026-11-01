using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class WindowSourceImageReplaceTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (bool billboard in new[] { false, true }) RoundTrip(billboard, check);
        Failures(check);
    }
    private static NormalizedImage Image(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 48)); surface.Canvas.Clear(color);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return AssetImport.Normalize(data.ToArray());
    }
    private static EditorSession Session(bool billboard = false)
    {
        var assets = new AssetLibrary(); var original = Image(SKColors.Crimson); string asset = assets.Add(original.PngBytes, original.MediaType);
        var group = new CanvasGroup { Name = "Nested source", OrderMode = GroupOrderMode.AtomicBlock, Transform = Affine2D.Translation(35, 45).Compose(Affine2D.Scale(2, 1.5)), ElevationScale = 1.25 };
        var element = new CanvasElement
        {
            Name = "Live source to replace", Kind = ElementKind.Image, AssetId = asset, GroupId = group.Id,
            Width = 314, Height = 180, Transform = Affine2D.Translation(14, 23).Compose(Affine2D.Rotation(.2)), ConstrainAspect = false,
            IsBillboard = billboard, BillboardAnchor = new(81, 112), BillboardHeight = 27,
            Style = new() { Foreground = 0xff13a66a, Background = 0x22334455, Opacity = .37, StrokeWidth = 2.5, AdditionalColors = new() { ["saved.tone"] = 0xff123456 } },
            ImageBorderWidth = 5.5, ImageCrop = new(.1, .2, .6, .5), ImageFrame = [new(.1, 0), new(.95, .15), new(.9, .9), new(0, .8)],
            Effects = [new() { Kind = ImageEffectKind.Blur, Bounds = new(.1, .15, .4, .35), Amount = 9 }],
            WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Synthetic source", ProcessNameHint = "synthetic.exe", WindowClassHint = "SyntheticWindow", Fit = WindowSourceFit.Contain }
        };
        group.ChildOrder = [element.Id]; var session = new EditorSession(new() { Elements = [element], Groups = [group], RootOrder = [group.Id] }, assets);
        session.EnterScope(group.Id); session.SetSelection([element.Id]); return session;
    }
    private static string Preserved(CanvasElement image) => JsonSerializer.Serialize(new
    {
        image.Id, image.Name, image.Kind, image.GroupId, image.Width, image.Height, image.Transform, image.ProjectivePlacement,
        image.ConstrainAspect, image.IsBillboard, image.BillboardAnchor, image.BillboardHeight, image.Style,
        image.ImageBorderWidth, image.ImageCrop, image.Effects, image.Points, image.Visible, image.Locked
    });
    private static void RoundTrip(bool billboard, Action<string, bool> check)
    {
        string label = billboard ? "billboard" : "planar"; var session = Session(billboard); var source = session.Document.Elements.Single();
        string original = JsonSerializer.Serialize(session.Document), preserved = Preserved(source), oldAsset = source.AssetId!; Guid intent = source.WindowSource!.BindingIntent;
        var replacement = Image(SKColors.CornflowerBlue);
        ImageImportCommand.Insert(session, [replacement], session.Document.Viewport, replace: true, opacity: .9);
        var current = session.Document.Elements.Single(); string newAsset = current.AssetId!;
        check(label + " file replacement converts a live window source into a static image", current.WindowSource is null && newAsset != oldAsset && session.Assets.Get(newAsset).Data.Span.SequenceEqual(replacement.PngBytes));
        check(label + " static replacement preserves local geometry placement opacity border effects and crop", Preserved(current) == preserved && current.Style.Opacity == .37 && current.ImageBorderWidth == 5.5 && current.ImageCrop == new RectD(.1, .2, .6, .5));
        check(label + " static replacement retains the existing cleared-contour contract and one undo", current.ImageFrame is null && session.UndoCount == 1 && session.Selection.SetEquals([current.Id]));
        check(label + " one Undo restores the exact live source intent and original asset", session.Undo() && JsonSerializer.Serialize(session.Document) == original && session.Document.Elements.Single().WindowSource?.BindingIntent == intent && session.Document.Elements.Single().AssetId == oldAsset);
        check(label + " Redo restores static replacement without reviving the source intent", session.Redo() && session.Document.Elements.Single().WindowSource is null && session.Document.Elements.Single().AssetId == newAsset && Preserved(session.Document.Elements.Single()) == preserved && session.Document.Elements.Single().ImageFrame is null);
    }
    private static void Failures(Action<string, bool> check)
    {
        var session = Session(); string original = JsonSerializer.Serialize(session.Document); int assets = session.Assets.Count; var replacement = Image(SKColors.CornflowerBlue); bool failed = false;
        try { ImageImportCommand.Insert(session, [replacement], session.Document.Viewport, replace: true, opacity: double.NaN); }
        catch (ArgumentOutOfRangeException) { failed = true; }
        check("invalid replacement parameters retain the live source and publish no asset or history", failed && JsonSerializer.Serialize(session.Document) == original && session.Assets.Count == assets && session.UndoCount == 0 && !session.IsTransactionActive);
        session.Document.Elements.Single().Locked = true; original = JsonSerializer.Serialize(session.Document); failed = false;
        try { ImageImportCommand.Insert(session, [replacement], session.Document.Viewport, replace: true); }
        catch (InvalidOperationException) { failed = true; }
        check("a rejected locked-image replacement does not lose the source descriptor or original pixels", failed && JsonSerializer.Serialize(session.Document) == original && session.Assets.Count == assets && session.UndoCount == 0 && !session.IsTransactionActive);
    }
}
