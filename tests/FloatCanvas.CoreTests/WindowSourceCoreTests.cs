using FloatCanvas.Core;
using System.Text.Json;

internal static class WindowSourceCoreTests
{
    internal static void Run(Action<bool, string> check)
    {
        var descriptor = new WindowSourceDescriptor();
        check(descriptor.BindingIntent == Guid.Empty && descriptor.Fit == WindowSourceFit.Contain && !descriptor.IsValid(),
            "window intent defaults empty and is never randomly repaired on load");
        descriptor.BindingIntent = Guid.NewGuid(); descriptor.TitleHint = new('t', 512); descriptor.ProcessNameHint = new('p', 260); descriptor.WindowClassHint = new('c', 256);
        check(descriptor.IsValid(), "window metadata accepts its exact bounded lengths");
        foreach (var change in new Action<WindowSourceDescriptor>[] { d => d.TitleHint += "x", d => d.ProcessNameHint += "x", d => d.WindowClassHint += "x",
            d => d.TitleHint = "hello\nsecret", d => d.ProcessNameHint = "x\0y", d => d.WindowClassHint = null!, d => d.Fit = (WindowSourceFit)2 })
        {
            var invalid = descriptor.Clone(); change(invalid); check(!invalid.IsValid(), "window metadata rejects invalid hidden hint/enum without sanitizing source");
        }
        var source = Live(); var document = new SceneDocument { Elements = [source] };
        var clone = document.Clone(true);
        check(clone.Elements[0].Id != source.Id && clone.Elements[0].WindowSource!.BindingIntent == source.WindowSource!.BindingIntent &&
            !ReferenceEquals(clone.Elements[0].WindowSource, source.WindowSource), "host duplication retains authored intent in an owned descriptor, not a runtime connection");
        clone.Elements[0].WindowSource!.TitleHint = "independent";
        check(source.WindowSource!.TitleHint == "Window", "descriptor clone owns editable hint metadata");
        foreach (var change in new Action<CanvasElement>[] { e => e.Kind = ElementKind.Rectangle, e => e.Width = 0, e => e.Height = double.NaN })
        {
            var invalid = source.Clone(); change(invalid); check(!WindowSourceLimits.IsValidElement(invalid), "window source requires a positive finite image region");
        }
        var invalidBackground = document.Clone(); invalidBackground.Background = Live();
        Reject(() => SceneLimits.ValidateCapacity(invalidBackground), check, "live window is rejected as the document background");
        string serialized = JsonSerializer.Serialize(document);
        check(!serialized.Contains("HWND") && !serialized.Contains("ProcessId") && !serialized.Contains("RuntimeEpoch") && !serialized.Contains("Frame"),
            "source wire contains intent and hints, never window handles or frame/runtime state");

        var assets = new AssetLibrary(); string a = assets.Add([1, 2, 3]), b = assets.Add([4, 5, 6]), c = assets.Add([7, 8, 9]); source.AssetId = a;
        var changedFallback = document.Clone(); changedFallback.Elements[0].AssetId = b;
        check(AuthoredSceneContent.Equal(document, changedFallback) && AuthoredSceneContent.Fingerprint(document) == AuthoredSceneContent.Fingerprint(changedFallback),
            "only live-image fallback pixels are excluded from authored equality and fingerprint");
        string originalHash = AuthoredSceneContent.Fingerprint(document);
        foreach (var change in new Action<CanvasElement>[] { e => e.WindowSource!.BindingIntent = Guid.NewGuid(), e => e.WindowSource!.TitleHint += " edited",
            e => e.WindowSource!.ProcessNameHint += "2", e => e.WindowSource!.WindowClassHint += "2", e => e.WindowSource!.Fit = WindowSourceFit.Stretch,
            e => e.ImageCrop = new(.1, .2, .7, .6), e => e.Width++, e => e.Transform = Affine2D.Translation(4, 5), e => e.Locked = true,
            e => e.Style.Foreground++, e => e.Effects.Add(new() { Kind = ImageEffectKind.Blur, Bounds = new(0, 0, 1, 1) }), e => e.WindowSource = null })
        {
            var edited = document.Clone(); change(edited.Elements[0]);
            check(!AuthoredSceneContent.Equal(document, edited) && AuthoredSceneContent.Fingerprint(edited) != originalHash,
                "window rebind, hints, layout, crop, effects, flags and styling remain authored content");
        }
        var staticA = document.Clone(); staticA.Elements[0].WindowSource = null; var staticB = staticA.Clone(); staticB.Elements[0].AssetId = b;
        check(!AuthoredSceneContent.Equal(staticA, staticB) && AuthoredSceneContent.Fingerprint(staticA) != AuthoredSceneContent.Fingerprint(staticB),
            "ordinary image pixels remain strict author content");
        var projected = AuthoredSceneContent.CreateProjection(document); projected.Elements[0].WindowSource!.TitleHint = "projection edit";
        check(projected.Elements[0].AssetId is null && source.AssetId == a && source.WindowSource!.TitleHint == "Window", "authored projection cannot mutate the active document");
        check(AuthoredSceneContent.Equal(null, null) && !AuthoredSceneContent.Equal(document, null), "authored equality has explicit null semantics");
        var zero = document.Clone(); zero.Canvas.Grid.FlatTranslation = new(-0d, -0d);
        check(AuthoredSceneContent.Equal(document, zero) && AuthoredSceneContent.Fingerprint(document) == AuthoredSceneContent.Fingerprint(zero), "signed zero has one byte-stable authored fingerprint");
        var ordered = document.Clone(); ordered.Elements[0].Style.AdditionalColors = new() { ["accent.2"] = 2, ["accent.1"] = 1 };
        var reversed = ordered.Clone(); reversed.Elements[0].Style.AdditionalColors = new() { ["accent.1"] = 1, ["accent.2"] = 2 };
        check(AuthoredSceneContent.Equal(ordered, reversed) && AuthoredSceneContent.Fingerprint(ordered) == AuthoredSceneContent.Fingerprint(reversed), "unordered named roles have a deterministic authored hash");

        var session = new EditorSession(document.Clone(), assets); session.Execute("strict cache comparison", d => d.Elements[0].AssetId = b);
        check(session.UndoCount == 1 && session.PreviewElementsRevision > 0 && session.Undo() && session.Document.Elements[0].AssetId == a,
            "existing session strict no-op and rendering classification still notice fallback bytes");
        long beforeEstimate = EditorSession.EstimateBytes(document); var longer = document.Clone(); longer.Elements[0].WindowSource!.TitleHint += "12345";
        check(EditorSession.EstimateBytes(longer) == beforeEstimate + 10, "history capacity accounts for descriptor strings exactly once");

        // The same naked node ID in host and definition is legal; fallback lookup must use its namespace.
        var child = Live(); child.Id = source.Id; child.AssetId = a;
        var definition = new PrefabDefinition { Graph = new() { Elements = [child] } };
        var unused = new PrefabDefinition { Name = "Unused snapshot", Graph = new() { Elements = [Live()] } };
        var instance = new PrefabInstance { DefinitionId = definition.Id };
        var hosted = new SceneDocument { Elements = [source], Instances = [instance], RootOrder = [source.Id, instance.Id], Definitions = [definition, unused] };
        string hostBefore = JsonSerializer.Serialize(hosted); int assetsBefore = assets.Count;
        var plan = WindowSourceFallbackPlanner.Prepare(hosted, new Dictionary<SourceNodeAddress, AssetResource>
            { [new(null, source.Id)] = assets.Get(b), [new(definition.Id, child.Id)] = assets.Get(c) }, assets.Resources);
        check(plan.Document.Elements[0].AssetId == b && plan.Document.Definitions[0].Graph.Elements[0].AssetId == c && plan.Document.Definitions[1].Id == unused.Id &&
            plan.Document.ResourceId == hosted.ResourceId && plan.Document.Definitions[0].Revision == definition.Revision,
            "fallback planner uses full source addresses and preserves unused definitions, owner identity and authored revision");
        check(JsonSerializer.Serialize(hosted) == hostBefore && assets.Count == assetsBefore && plan.Resources.Select(r => r.Id).ToHashSet().SetEquals([b, c]),
            "package fallback planning publishes neither source edits nor assets and returns exactly final references");
        check(AuthoredSceneContent.Equal(hosted, plan.Document) && AuthoredSceneContent.Fingerprint(hosted) == AuthoredSceneContent.Fingerprint(plan.Document),
            "definition frame projection has the same authored savepoint");
        plan.Document.Definitions[0].Graph.Elements[0].WindowSource!.TitleHint = "package only";
        check(child.WindowSource!.TitleHint == "Window" && !ReferenceEquals(plan.Resources[0], assets.Get(plan.Resources[0].Id)), "fallback result owns DTO containers and resource bytes");
        var oldPlan = WindowSourceFallbackPlanner.Prepare(hosted, new Dictionary<SourceNodeAddress, AssetResource>(), assets.Resources);
        check(oldPlan.Resources.Count == 1 && oldPlan.Resources[0].Id == a && oldPlan.Document.Definitions.Count == 2, "unavailable live frames retain the last fallback without dropping unused source graphs");
        Reject(() => WindowSourceFallbackPlanner.Prepare(hosted, new Dictionary<SourceNodeAddress, AssetResource> { [new(null, Guid.NewGuid())] = assets.Get(b) }, assets.Resources), check,
            "unknown occurrence/runtime addresses cannot write a source fallback");
        Reject(() => WindowSourceFallbackPlanner.Prepare(staticA, new Dictionary<SourceNodeAddress, AssetResource> { [new(null, source.Id)] = assets.Get(b) }, assets.Resources), check,
            "fallback planner cannot normalize ordinary image assets");
        Reject(() => WindowSourceFallbackPlanner.Prepare(hosted, new Dictionary<SourceNodeAddress, AssetResource>(), []), check, "missing retained package resources fail before publication");
        var nonPng = new AssetLibrary(); string jpeg = nonPng.Add([1, 3, 5], "image/jpeg");
        Reject(() => WindowSourceFallbackPlanner.Prepare(hosted, new Dictionary<SourceNodeAddress, AssetResource> { [new(null, source.Id)] = nonPng.Get(jpeg) }, assets.Resources), check,
            "full-source fallback contract rejects non-PNG resources");
        var import = PrefabClosure.PrepareImport(new(), hosted, hosted.Definitions);
        check(import.Graph.Elements[0].WindowSource!.BindingIntent == source.WindowSource!.BindingIntent &&
            import.DefinitionsToAdd[0].Graph.Elements[0].WindowSource!.BindingIntent == child.WindowSource!.BindingIntent,
            "cross-document graph/closure import preserves authored intent without granting a runtime binding");
        var runtime = SceneEvaluation.Build(hosted).RenderDocument;
        Reject(() => AuthoredSceneContent.Fingerprint(runtime), check, "authored fingerprint rejects evaluated runtime graphs");
        Reject(() => WindowSourceFallbackPlanner.Prepare(runtime, new Dictionary<SourceNodeAddress, AssetResource>(), assets.Resources), check, "fallback planner cannot save evaluated runtime graphs");
    }

    private static CanvasElement Live() => new() { Kind = ElementKind.Image, Width = 320, Height = 180,
        WindowSource = new() { BindingIntent = Guid.NewGuid(), TitleHint = "Window", ProcessNameHint = "program", WindowClassHint = "Class" } };
    private static void Reject(Action action, Action<bool, string> check, string message)
    {
        bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } check(rejected, message);
    }
}
