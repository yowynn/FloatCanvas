using System.Text.Json;
using FloatCanvas.App;
using FloatCanvas.Core;

internal static class WindowSourceInsertionTests
{
    private sealed record Snapshot(string Document, Guid[] Selection, long ContentVersion, long SceneRevision, long ScopeVersion,
        int Undo, int Redo, int Assets, bool Transaction, long TransactionId);
    private static Snapshot Capture(EditorSession target) => new(JsonSerializer.Serialize(target.Document), target.Selection.Order().ToArray(),
        target.ContentVersion, target.SceneRevision, target.EditScopeVersion, target.UndoCount, target.RedoCount, target.Assets.Count, target.IsTransactionActive, target.TransactionId);
    private static bool Same(EditorSession target, Snapshot before)
    {
        var after = Capture(target); return after with { Selection = before.Selection } == before && after.Selection.SequenceEqual(before.Selection);
    }
    private static bool Near(double left, double right) => Math.Abs(left - right) < 1e-6;
    private static bool Near(RectD left, RectD right) => Near(left.X, right.X) && Near(left.Y, right.Y) && Near(left.Width, right.Width) && Near(left.Height, right.Height);
    private static WindowSourceDescriptor Descriptor() => new() { BindingIntent = Guid.NewGuid(), TitleHint = "Synthetic window", ProcessNameHint = "synthetic.exe", WindowClassHint = "SyntheticWindow" };
    private static EditorSession Flat()
    {
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 20, Height = 30 };
        var session = new EditorSession(new() { Viewport = new(0, 0, 1000, 800), LayerOpacity = .5, Elements = [rectangle] });
        session.Execute("baseline history", document => document.NextNumber = 38); session.SetSelection([rectangle.Id]); return session;
    }
    private static EditorSession Perspective()
    {
        var document = new SceneDocument { Viewport = new(0, 0, 800, 700), LayerOpacity = .6 };
        document.Canvas.Projection.Enabled = true; document.Canvas.Projection.Matrix = new(1, 0, 0, 0, -.4, 700, 0, -.002, 1);
        document.Canvas.Projection.Quad = [new(0, 0), new(800, 0), new(800, 700), new(0, 700)]; return new(document);
    }
    internal static void Run(Action<string, bool> check)
    {
        var target = Flat(); var before = Capture(target); var descriptor = Descriptor(); var crop = new RectD(.25, .25, .5, .5);
        var image = WindowSourceInsertion.Prepare(target, descriptor, 4000, 2000, crop, new(100, 200, 1000, 600), opacity: .37);
        check("window insertion fits the cropped native source ratio into eighty percent of the plane placement", Near(ElementGeometry.Bounds(image, false), new(200, 300, 800, 400)) && image.ImageCrop == crop);
        check("window insertion prepares an unbound owned Image and applies opacity once", image.Kind == ElementKind.Image && image.AssetId is null && image.GroupId is null && image.Style.Opacity == .37 && image.ImageBorderWidth == 0 && image.Effects.Count == 0 && image.WindowSource!.BindingIntent == descriptor.BindingIntent && !ReferenceEquals(image.WindowSource, descriptor));
        check("successful preparation leaves the full owner graph selection assets scope and history unchanged", Same(target, before));
        image.WindowSource!.TitleHint = "Owned draft change";
        check("prepared source descriptor is independently owned", descriptor.TitleHint == "Synthetic window");
        var small = WindowSourceInsertion.Prepare(target, descriptor, 640, 360, null, new(0, 0, 1920, 1080));
        check("native logical size is retained without upscaling or consulting fallback PNG dimensions", small.Width == 640 && small.Height == 360 && small.Transform == Affine2D.Translation(640, 360) && small.ImageCrop is null);
        descriptor.Fit = WindowSourceFit.Stretch; var stretch = WindowSourceInsertion.Prepare(target, descriptor, 4000, 2000, crop, new(100, 200, 1000, 600));
        check("explicit Stretch intent survives while the initial frame still follows cropped source aspect", stretch.WindowSource!.Fit == WindowSourceFit.Stretch && Near(ElementGeometry.Bounds(stretch, false), new(200, 300, 800, 400)));
        Scope(check, target, descriptor);
        Billboards(check);
        Invalid(check);
    }
    private static void Scope(Action<string, bool> check, EditorSession target, WindowSourceDescriptor descriptor)
    {
        var graph = new SceneGraph(target.Document); var group = graph.InsertGroup(new() { Name = "Affine scope", OrderMode = GroupOrderMode.AtomicBlock,
            ChildOrder = [], Transform = Affine2D.Translation(50, 80).Compose(Affine2D.Rotation(.3)).Compose(Affine2D.Scale(2, 3)), ElevationScale = 2.5 }); target.EnterScope(group.Id);
        var before = Capture(target); var image = WindowSourceInsertion.Prepare(target, descriptor, 800, 400, null, new(100, 200, 1000, 600));
        var candidate = target.Document.Clone(); var localGraph = new SceneGraph(candidate); var local = localGraph.InsertElement(image, group.Id); var world = localGraph.ToWorld(local);
        check("affine-scope preparation returns world geometry for exactly one later inverse-parent conversion", image.GroupId is null && Near(ElementGeometry.Bounds(world, false), new(200, 300, 800, 400)) && local.GroupId == group.Id && local.Transform != image.Transform);
        check("testing insertion on a private candidate leaves the real scoped owner untouched", Same(target, before));
    }
    private static void Billboards(Action<string, bool> check)
    {
        var target = Perspective(); var descriptor = Descriptor(); var mapping = CanvasMapping.Create(target.Document); var before = Capture(target);
        var image = WindowSourceInsertion.Prepare(target, descriptor, 200, 300, null, new(0, 0, 500, 500), billboard: true, screenCenter: new(200, 250), opacity: .4);
        check("screen-first window billboard preserves the intended screen frame even above the horizon", image.IsBillboard && Near(mapping.ProjectedBounds(image, false), new(100, 100, 200, 300)) && !mapping.IsScreenSafe(new(200, 100)) && mapping.IsElementSafe(image));
        check("screen-first window billboard uses the existing bottom-center ground and logical elevation", Near(image.BillboardAnchor.X, 500) && Near(image.BillboardAnchor.Y, -750) && Near(image.BillboardHeight, 375) && image.Style.Opacity == .4 && image.AssetId is null);
        check("billboard preparation has no authored or resource side effects", Same(target, before));
        check("fixed-depth fixture resolves an existing safe M5 depth line", mapping.TryGetEqualDepthLine(new(200, 400), out var depth));
        var fixedImage = WindowSourceInsertion.Prepare(target, descriptor, 200, 300, null, new(0, 0, 500, 500), billboard: true, depth: depth, screenCenter: new(200, 250));
        check("fixed-depth window source preserves advanced workflow logical sizing and screen placement", fixedImage.IsBillboard && Near(fixedImage.Width, 500) && Near(fixedImage.Height, 750) && Near(mapping.ProjectedBounds(fixedImage, false), new(100, 100, 200, 300)) && Near(fixedImage.BillboardHeight, 375));
        var graph = new SceneGraph(target.Document); var group = graph.InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [],
            Transform = Affine2D.Translation(25, -50).Compose(Affine2D.Scale(2, 3)), ElevationScale = 2.5 }); target.EnterScope(group.Id); before = Capture(target);
        var scoped = WindowSourceInsertion.Prepare(target, descriptor, 200, 300, null, new(0, 0, 500, 500), billboard: true, screenCenter: new(200, 250));
        var candidate = target.Document.Clone(); var localGraph = new SceneGraph(candidate); var local = localGraph.InsertElement(scoped, group.Id); var restored = localGraph.ToWorld(local);
        check("nested billboard insertion divides parent placement and elevation once", local.GroupId == group.Id && Near(local.BillboardHeight, 150) && Near(restored.BillboardHeight, 375) && Near(mapping.ProjectedBounds(restored, false), new(100, 100, 200, 300)));
        check("nested billboard preparation and private materialization do not mutate the owner", Same(target, before));
    }
    private static void Invalid(Action<string, bool> check)
    {
        var target = Flat(); var descriptor = Descriptor(); var placement = new RectD(0, 0, 1000, 800);
        void Reject(string label, Action action)
        {
            var before = Capture(target); string source = JsonSerializer.Serialize(descriptor); bool rejected = false;
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { rejected = true; }
            check(label, rejected && Same(target, before) && JsonSerializer.Serialize(descriptor) == source);
        }
        Reject("empty source binding intent is rejected without mutation", () => WindowSourceInsertion.Prepare(target, new(), 100, 100, null, placement));
        Reject("invalid source hint is rejected without mutation", () => WindowSourceInsertion.Prepare(target, new() { BindingIntent = Guid.NewGuid(), TitleHint = "invalid\nname" }, 100, 100, null, placement));
        Reject("unknown source fit is rejected without mutation", () => WindowSourceInsertion.Prepare(target, new() { BindingIntent = Guid.NewGuid(), Fit = (WindowSourceFit)99 }, 100, 100, null, placement));
        foreach (var dimensions in new[] { (0, 100), (100, 0), (-1, 100), (100, -1) })
            Reject("invalid native content dimensions are rejected " + dimensions, () => WindowSourceInsertion.Prepare(target, descriptor, dimensions.Item1, dimensions.Item2, null, placement));
        foreach (var crop in new[] { new RectD(-.1, 0, .5, .5), new RectD(0, 0, 0, .5), new RectD(.8, .1, .4, .5), new RectD(0, double.NaN, .5, .5) })
            Reject("invalid normalized source crop is rejected " + crop, () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, crop, placement));
        foreach (var bounds in new[] { new RectD(0, 0, 0, 10), new RectD(0, 0, -10, 10), new RectD(double.NaN, 0, 100, 100), new RectD(0, 0, double.PositiveInfinity, 100), new RectD(1e20, 0, 100, 100) })
            Reject("invalid or unsafe placement is rejected " + bounds, () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, bounds));
        foreach (double opacity in new[] { -.1, 1.1, double.NaN })
            Reject("invalid source opacity is rejected " + opacity, () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, placement, opacity: opacity));
        target.BeginTransaction("foreign"); Reject("preparation cannot write or interfere with an active foreign transaction", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, placement)); target.Cancel();
        target = Perspective();
        Reject("a planar source beyond the safe plane is rejected without mutation", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, new(0, 600, 500, 500)));
        Reject("unsafe final ground cancels window billboard preparation", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, new(0, 0, 500, 500), billboard: true, screenCenter: new(200, 100)));
        Reject("a nonfinite billboard center is rejected without mutation", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, placement, billboard: true, screenCenter: new(double.NaN, 250)));
        Reject("an unrepresentable fixed depth line is rejected without mutation", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, placement, billboard: true, depth: new(new(200, 400), new(0, 1), .4), screenCenter: new(200, 250)));
        var group = new SceneGraph(target.Document).InsertGroup(new() { OrderMode = GroupOrderMode.AtomicBlock, ChildOrder = [] }); target.EnterScope(group.Id); group.Locked = true;
        Reject("a locked insertion scope cannot be changed by preparation", () => WindowSourceInsertion.Prepare(target, descriptor, 100, 100, null, placement));
    }
}
