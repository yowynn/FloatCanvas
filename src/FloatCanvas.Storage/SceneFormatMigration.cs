using FloatCanvas.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Storage;

/// <summary>Upgrades a source-validated, owned document without recalibrating its content.</summary>
internal static class SceneFormatMigration
{
    internal static bool Supports(int version) => version == SceneDocument.CurrentVersion || version is 1 or 2 or 3 or 6 or 8 or 19 or 20;
    internal static bool SupportsPrefab(int version) => version == SceneDocument.CurrentVersion || version is 19 or 20;

    // Install the converter only on an old input payload. The serializer traverses every
    // element, including nested definitions, preview backgrounds and recovery drafts.
    internal static T ReadPayload<T>(JsonElement json, int version, JsonSerializerOptions currentOptions, bool usesPublishedColors)
    {
        var options = currentOptions;
        if (version <= 8 || usesPublishedColors)
        {
            options = new(currentOptions);
            options.Converters.Insert(0, new HistoricalElementReader(version, currentOptions));
        }
        var value = json.Deserialize<T>(options) ?? throw new InvalidDataException(L.Get("Project.EmptyContent"));
        if (version < SceneDocument.CurrentVersion) NormalizePresentationLayouts(value);
        return value;
    }

    private static void NormalizePresentationLayouts<T>(T value)
    {
        switch (value)
        {
            case SceneDocument document:
                Normalize(document);
                break;
            case PrefabPackage package:
                foreach (var definition in package.Definitions) Normalize(definition.Graph);
                break;
            case WorkspaceState workspace:
                Normalize(workspace.LiveDocument);
                if (workspace.CaptureDocument is { } capture) Normalize(capture);
                foreach (var draft in PrefabDraftState.Enumerate(workspace.PendingPrefabEdit)) Normalize(draft.DraftDocument);
                break;
        }
    }

    private static void Normalize(SceneDocument document)
    {
        NormalizeElements(document.Elements.Append(document.Background).OfType<CanvasElement>());
        foreach (var definition in document.Definitions) Normalize(definition.Graph);
    }

    private static void Normalize(SceneNodeGraph graph) => NormalizeElements(graph.Elements);

    private static void NormalizeElements(IEnumerable<CanvasElement> elements)
    {
        foreach (var settings in elements.Select(element => element.Presentation).OfType<PresentationSettings>())
        {
            var layout = settings.KeyboardLayout;
            if (layout is not null && IsLegacyUnusableLayout(layout))
                settings.KeyboardLayout = null;
        }
    }

    private static bool IsLegacyUnusableLayout(PresentationKeyboardLayout layout)
    {
        if (layout.Keys is not { } keys)
            return true;
        if (keys.Count == 0)
            return true;
        if (keys.Count > PresentationKeyboardLayout.MaximumKeys)
            return false;
        return keys.All(key => key is { Enabled: false } && key.IsValid()) &&
            keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count() == keys.Count;
    }

    private sealed class HistoricalElementReader(int version, JsonSerializerOptions currentOptions) : JsonConverter<CanvasElement>
    {
        public override CanvasElement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var element = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Expected an element.");
            var style = element["Style"] as JsonObject;
            if (style is null) element["Style"] = style = new JsonObject();
            var kind = (ElementKind)(element["Kind"]?.GetValue<int>() ?? 0);
            var migrated = new ElementStyle();
            if (version < 12)
            {
                bool foregroundBadge = element["NumberUsesForeground"]?.GetValue<bool>() ?? false;
                var legacy = new LegacyAppearance(style["Stroke"]?.GetValue<uint>() ?? migrated.PrimaryColor,
                    style["Fill"]?.GetValue<uint>() ?? 0, style["LinkColors"]?.GetValue<bool>() ?? false, style["Variant"]?.GetValue<string>());
                AppearanceMigration.MigrateToolStyle(migrated, kind, legacy, foregroundBadge ? 1 : 0);
                element.Remove("NumberUsesForeground");
                element["NumberTextSizeMode"] = (int)(foregroundBadge ? NumberTextSizeMode.FitBadge : NumberTextSizeMode.CapFontSize);
                style.Remove("Stroke"); style.Remove("Fill"); style.Remove("LinkColors");
                if (migrated.AdditionalColors.Count > 0) style["AdditionalColors"] = JsonSerializer.SerializeToNode(migrated.AdditionalColors);
            }
            else
            {
                migrated.PrimaryColor = style["Foreground"]!.GetValue<uint>();
                migrated.SecondaryColor = style["Background"]!.GetValue<uint>();
                migrated.Variant = style["Variant"]!.GetValue<string>();
                AppearanceMigration.UpgradeNumberOutline(migrated, kind);
                style.Remove("Foreground"); style.Remove("Background");
            }
            style["PrimaryColor"] = migrated.PrimaryColor; style["SecondaryColor"] = migrated.SecondaryColor;
            style["Variant"] = migrated.Variant;
            var result = element.Deserialize<CanvasElement>(currentOptions) ?? throw new JsonException("Empty migrated element.");
            // Published files saved emoji as plain text with this font and a single glyph.
            if (result.Kind == ElementKind.Text && result.Style.Variant == ElementAppearance.Default &&
                string.Equals(result.Style.FontFamily, "Segoe UI Emoji", StringComparison.OrdinalIgnoreCase) &&
                EmojiCatalog.TryFind(result.Text, out var emoji))
            {
                result.Kind = ElementKind.Emoji; result.Text = emoji.Glyph;
                result.Style.Opacity *= (result.Style.PrimaryColor >> 24) / 255d;
                result.ConstrainAspect ??= false; // Historical Text default.
                result.TextAutoWidth = result.TextFrameFixed = false;
            }
            return result;
        }
        public override void Write(Utf8JsonWriter writer, CanvasElement value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    internal static void Upgrade(SceneDocument document)
    {
        if (!Supports(document.Version)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", document.Version));
        if (document.Version == SceneDocument.CurrentVersion)
            return;
        // The public releases through v8 used a count-based plane and interleaved
        // draw order. Adopt the saved geometry and group order without recalibrating.
        if (document.Version <= 6) CanvasCalibration.AdoptReference(document.Canvas.Projection);
        if (document.Version <= 8)
        {
            CanvasProjectionGeometry.InitializePlane(document.Canvas);
            document.RootOrder = null;
            foreach (var group in document.Groups)
            {
                group.Transform = Affine2D.Identity; group.ElevationScale = 1; group.Visible = true; group.Locked = false;
                group.OrderMode = GroupOrderMode.LegacyPassThrough; group.ChildOrder = null;
            }
            document.AuthoringReference ??= CanvasAuthoringReference.FromSavedDocument(document);
        }
        document.Version = SceneDocument.CurrentVersion;
    }
    internal static void Upgrade(PrefabPackage package)
    {
        if (!SupportsPrefab(package.Version)) throw new InvalidDataException(L.Format("Scene.UnsupportedVersion", package.Version));
        package.Version = SceneDocument.CurrentVersion;
    }
}
