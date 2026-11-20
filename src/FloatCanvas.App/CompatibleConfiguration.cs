using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FloatCanvas.Core;
using FloatCanvas.Windows;

namespace FloatCanvas.App;

/// <summary>Read the fields this version understands; retain the original JSON when saving unrelated edits.</summary>
internal sealed class CompatibleConfiguration(string path)
{
    internal static Func<string,IReadOnlyList<string>,bool>? ConfirmRepair { get; set; }
    internal bool NeedsCleanup { get; private set; }
    private JsonObject? original;
    private JsonNode? baseline;
    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 128 };

    internal T Read<T>(JsonSerializerOptions options)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the size limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new() { MaxDepth = 128 });
        CheckDuplicates(document.RootElement);
        original = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException("Configuration must be an object.");
        bool toolConfiguration = typeof(T).DeclaringType == typeof(ToolStateStore) || typeof(T).DeclaringType == typeof(ToolPresetLibrary);
        NeedsCleanup = toolConfiguration && FilterUnknownTools(original);
        var normalized = Normalize(original, typeof(T), Default(typeof(T)));
        if (toolConfiguration && normalized is JsonObject tools)
            tools["Version"] = 3;
        var value = normalized!.Deserialize<T>(ReadOptions) ?? throw new InvalidDataException("Configuration is empty.");
        Capture(value, options);
        // Migrate the owned portion of the in-memory merge source alongside the typed
        // snapshot. From here on edits and saves compare the same current field names.
        if (toolConfiguration)
            UpgradeToolSnapshots(original, baseline!);
        if (typeof(T) == typeof(UserSettings))
            RewritePointerAlpha(original["PresentationGlobal"]?["Pointer"], baseline!["PresentationGlobal"]?["Pointer"]);
        return value;
    }

    internal void Capture<T>(T value, JsonSerializerOptions options) => baseline = JsonSerializer.SerializeToNode(value, options);

    internal void Save<T>(T value, JsonSerializerOptions options)
    {
        if(original is null && File.Exists(path)) Read<T>(options);
        var current = JsonSerializer.SerializeToNode(value, options)!;
        var replacements=new List<string>();
        var merged = original is null || baseline is null ? current : Merge(original, baseline, current,replacements,"")!;
        if(replacements.Count>0 && ConfirmRepair?.Invoke(path,replacements)!=true)
            throw new IOException(FloatCanvas.Localization.AppText.Get("Configuration.SaveCancelled"));
        string serialized = merged.ToJsonString(new() { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, serialized); File.Move(temporary, path, true);
            original = (JsonObject)merged; baseline = current; NeedsCleanup = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool FilterUnknownTools(JsonObject root)
    {
        bool discarded = false;
        bool Unknown(JsonNode? node) => node is JsonObject item && item["Tool"] is { } tool && !TryEnum(typeof(EditorTool), tool.ToString(), out _);
        foreach (string key in new[] { "Tools", "Buttons", "Selections", "PreviewSources", "OfficialBaselines" })
            if (root[key] is JsonObject values)
                foreach (var (id, value) in values.ToArray())
                    if (key != "OfficialBaselines" && !TryEnum(typeof(EditorTool), id, out _) || Unknown(value) ||
                        key == "Buttons" && value is not null && !TryEnum(typeof(EditorTool), value.ToString(), out _)) { values.Remove(id); discarded = true; }
        if (root["Presets"] is JsonArray presets)
            foreach (var preset in presets.ToArray())
                if (preset is JsonObject item && Unknown(item["Parameters"])) { presets.Remove(preset); discarded = true; }
        return discarded;
    }

    private static JsonNode? Normalize(JsonNode? raw, Type type, object? fallback)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (raw is null) return Fallback(fallback,type);
        if (nullable is not null) type = nullable;
        if (type.IsEnum)
        {
            return TryEnum(type, raw.ToString(), out var value) ? JsonSerializer.SerializeToNode(value, type) : Fallback(fallback,type);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var result = new JsonObject(); if (raw is not JsonObject dictionary) return result;
            var arguments = type.GetGenericArguments();
            foreach (var (key, item) in dictionary)
            {
                if (arguments[0].IsEnum && (!TryEnum(arguments[0], key, out var enumKey) || key != enumKey!.ToString())) continue;
                if (arguments[1].IsEnum && (item is null || !TryEnum(arguments[1], item.ToString(), out _))) continue;
                object? itemDefault = Default(arguments[1]);
                if (itemDefault is ToolParameters parameters && Enum.TryParse<EditorTool>(key, out var tool)) parameters.Tool = tool;
                result[key] = Normalize(item, arguments[1], itemDefault);
            }
            return result;
        }
        if (type.IsSZArray || type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) ||
            type.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>)))
        {
            if (raw is not JsonArray array) return type.IsSZArray ? Fallback(fallback, type) : new JsonArray();
            var result = new JsonArray();
            var itemType = type.IsSZArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            foreach (var item in array) result.Add(Normalize(item, itemType, Default(itemType)));
            return result;
        }
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid))
        {
            try
            {
                var value = raw.Deserialize(type, ReadOptions);
                if (value is double number && !double.IsFinite(number)) return Fallback(fallback,type);
                return JsonSerializer.SerializeToNode(value, type, ReadOptions);
            }
            catch (JsonException) { return Fallback(fallback,type); }
        }
        if (raw is not JsonObject source) return Fallback(fallback,type);
        var normalized = new JsonObject(); fallback ??= Default(type);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null && p.GetIndexParameters().Length == 0))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
            string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            if (type == typeof(ElementStyle) && name is "Stroke" or "Fill" or "LinkColors" or "Foreground" or "Background") continue;
            object? defaultValue = fallback is null ? Default(property.PropertyType) : property.GetValue(fallback);
            var next = source.TryGetPropertyValue(name, out var supplied) ? Normalize(supplied, property.PropertyType, defaultValue) : JsonSerializer.SerializeToNode(defaultValue, property.PropertyType, ReadOptions);
            if (name == "Version" && (next is null || !int.TryParse(next.ToString(),out int version) || version<1 || version>Convert.ToInt32(defaultValue)) || !ValidValue(type, name, next))
                next = JsonSerializer.SerializeToNode(defaultValue, property.PropertyType, ReadOptions);
            normalized[name] = next;
        }
        if (type == typeof(ToolParameters)) NormalizeToolAppearance(source, normalized);
        if (type == typeof(PresentationPointerSettings)) normalized = NormalizePointerAlpha(source, normalized);
        return normalized;
    }

    private static JsonObject NormalizePointerAlpha(JsonObject source, JsonObject normalized)
    {
        if (!source.TryGetPropertyValue(nameof(PresentationPointerSettings.ColorAlphaVersion), out var marker) || marker?.ToString() == "0")
        {
            normalized[nameof(PresentationPointerSettings.ColorAlphaVersion)] = 0;
            normalized[nameof(PresentationPointerSettings.CursorColor)] = LegacyColor(source, nameof(PresentationPointerSettings.CursorColor), 0xFFFFD54F);
            normalized[nameof(PresentationPointerSettings.CursorOpacity)] = LegacyOpacity(source, nameof(PresentationPointerSettings.CursorOpacity), .24);
            normalized[nameof(PresentationPointerSettings.TrailColor)] = LegacyColor(source, nameof(PresentationPointerSettings.TrailColor), 0xFFFFD54F);
            normalized[nameof(PresentationPointerSettings.TrailOpacity)] = LegacyOpacity(source, nameof(PresentationPointerSettings.TrailOpacity), .9);
        }
        var settings = normalized.Deserialize<PresentationPointerSettings>(ReadOptions)!;
        settings.NormalizeColorAlpha();
        return JsonSerializer.SerializeToNode(settings, ReadOptions)!.AsObject();

        static uint LegacyColor(JsonObject values, string name, uint fallback)
        {
            try { return values[name]?.Deserialize<uint>() ?? fallback; }
            catch (JsonException) { return fallback; }
        }
        static double LegacyOpacity(JsonObject values, string name, double fallback)
        {
            try { return values[name]?.Deserialize<double>() is { } value && double.IsFinite(value) && value is >= 0 and <= 1 ? value : fallback; }
            catch (JsonException) { return fallback; }
        }
    }

    private static void RewritePointerAlpha(JsonNode? target, JsonNode? current)
    {
        if (target is not JsonObject pointer || current is not JsonObject canonical) return;
        foreach (string key in new[] { "ColorAlphaVersion", "CursorColor", "CursorOpacity", "TrailColor", "TrailOpacity" })
            if (canonical.ContainsKey(key)) pointer[key] = canonical[key]?.DeepClone();
        foreach (string key in new[] { "LeftClick", "RightClick", "MiddleClick" })
        {
            if (canonical[key] is not JsonObject style) continue;
            if (pointer[key] is not JsonObject existing) pointer[key] = existing = new JsonObject();
            existing["Color"] = style["Color"]?.DeepClone(); existing["Opacity"] = style["Opacity"]?.DeepClone();
        }
    }

    private static void NormalizeToolAppearance(JsonObject source, JsonObject normalized)
    {
        var parameters = normalized.Deserialize<ToolParameters>(ReadOptions)!;
        var rawStyle = source[nameof(ToolParameters.Style)] as JsonObject;
        uint Color(string key, uint fallback) => rawStyle?[key] is JsonValue value && value.TryGetValue<uint>(out uint color) ? color : fallback;
        bool primarySlots = rawStyle?.ContainsKey(nameof(ElementStyle.PrimaryColor)) == true || rawStyle?.ContainsKey(nameof(ElementStyle.SecondaryColor)) == true;
        if (!primarySlots && rawStyle is not null && (rawStyle.ContainsKey("Foreground") || rawStyle.ContainsKey("Background")))
        {
            parameters.Style.PrimaryColor = Color("Foreground", parameters.Style.PrimaryColor);
            parameters.Style.SecondaryColor = Color("Background", parameters.Style.SecondaryColor);
            AppearanceMigration.UpgradeNumberOutline(parameters.Style, parameters.AppearanceKind);
        }
        else if (!primarySlots)
        {
            string? variant = rawStyle?[nameof(ElementStyle.Variant)] is JsonValue variantValue && variantValue.TryGetValue<string>(out var id) ? id : null;
            bool linked = rawStyle?["LinkColors"] is JsonValue linkValue && linkValue.TryGetValue<bool>(out bool link) && link;
            int version = source["NumberColorRolesVersion"] is JsonValue roleValue && roleValue.TryGetValue<int>(out int roleVersion) && roleVersion == 1 ? 1 : 0;
            var legacy = new LegacyAppearance(Color("Stroke", parameters.Style.Foreground), Color("Fill", parameters.Style.Background), linked, variant);
            if (variant is not null && parameters.AppearanceKind is { } kind && AppearanceRegistry.Find(kind, variant) is null &&
                !(kind == ElementKind.Number && AppearanceMigration.IsLegacyNumberOutline(variant)))
            {
                // A newer recipe stays stored, but cannot execute in this version.
                parameters.Style.Foreground = legacy.Stroke; parameters.Style.Background = legacy.Fill; parameters.Style.Variant = variant;
            }
            else AppearanceMigration.MigrateToolStyle(parameters.Style, parameters.AppearanceKind, legacy, version);
        }
        if (!primarySlots && parameters.Tool == EditorTool.CaptureRegion)
            parameters.Style.PrimaryColor = source["CaptureBorderColor"] is JsonValue capture && capture.TryGetValue<uint>(out uint ink)
                ? ink : ColorLibrary.ForegroundRed;
        parameters.NormalizeAppearance();
        normalized[nameof(ToolParameters.Style)] = JsonSerializer.SerializeToNode(parameters.Style, ReadOptions);
        normalized[nameof(ToolParameters.PointerPresentation)] = JsonSerializer.SerializeToNode(parameters.PointerPresentation, ReadOptions);
    }

    // Upgrade the owned fields in the read-time merge source, keeping unknown siblings.
    private static void UpgradeToolSnapshots(JsonNode source, JsonNode current)
    {
        void Rewrite(JsonNode? target, JsonNode? known)
        {
            if (target is not JsonObject parameters || known is not JsonObject snapshot || snapshot["Tool"] is null || snapshot["Style"] is not JsonObject style) return;
            if (parameters["Style"] is not JsonObject targetStyle)
            {
                if (parameters["Style"] is not null) return;
                parameters["Style"] = targetStyle = new JsonObject();
            }
            RewritePointerAlpha(parameters[nameof(ToolParameters.PointerPresentation)], snapshot[nameof(ToolParameters.PointerPresentation)]);
            if (targetStyle.ContainsKey("PrimaryColor") || targetStyle.ContainsKey("SecondaryColor")) return;
            foreach (string key in new[] { "PrimaryColor", "SecondaryColor", "Variant" })
                if (style.ContainsKey(key)) targetStyle[key] = style[key]?.DeepClone();
            if (style["AdditionalColors"] is { } archived) targetStyle["AdditionalColors"] = archived.DeepClone();
            else targetStyle.Remove("AdditionalColors");
            foreach (string key in new[] { "Foreground", "Background", "Stroke", "Fill", "LinkColors" }) targetStyle.Remove(key);
            parameters.Remove("NumberColorRolesVersion");
            parameters.Remove("CaptureBorderColor");
            parameters["NumberTextSizeMode"] = snapshot["NumberTextSizeMode"]?.DeepClone();
        }
        foreach (string key in new[] { "Tools", "OfficialBaselines", "PreviewSources" })
            if (current[key] is JsonObject values && source[key] is JsonObject targets)
                foreach (var (id, value) in values) Rewrite(targets[id], value);
        if (current["Presets"] is JsonArray presets && source["Presets"] is JsonArray targetsList)
            foreach (var preset in presets.OfType<JsonObject>().Where(preset => preset["Id"] is not null))
                Rewrite(targetsList.OfType<JsonObject>().FirstOrDefault(target => JsonNode.DeepEquals(target["Id"], preset["Id"]))?["Parameters"], preset["Parameters"]);
        if (source is JsonObject root && (root["Version"] is not JsonValue versionValue ||
            !versionValue.TryGetValue<int>(out int version) || version <= 3)) root["Version"] = 3;
    }

    private static object? Default(Type type)
    {
        if (type == typeof(string)) return "";
        if (Nullable.GetUnderlyingType(type) is not null) return null;
        try { return Activator.CreateInstance(type, nonPublic: true); }
        catch (MissingMethodException) { return null; }
    }
    private static JsonNode? Fallback(object? value,Type type) => value is null ? null : JsonSerializer.SerializeToNode(value,type,ReadOptions);
    private static bool TryEnum(Type type, string value, out object? parsed) => Enum.TryParse(type, value, false, out parsed) && parsed is not null &&
        (type == typeof(PresentationModules) ? ((PresentationModules)parsed & ~(PresentationModules.Keys | PresentationModules.Cursor | PresentationModules.Clicks | PresentationModules.Trail)) == 0 :
         type == typeof(OverlayModifiers) ? ((OverlayModifiers)parsed & ~(OverlayModifiers.Alt | OverlayModifiers.Control | OverlayModifiers.Shift | OverlayModifiers.Windows)) == 0 :
         Enum.IsDefined(type, parsed));

    private static bool ValidValue(Type owner, string name, JsonNode? value)
    {
        if (value is null) return true;
        if (owner == typeof(NumberSequenceSettings) && name == nameof(NumberSequenceSettings.Items))
            return value is JsonArray labels && labels.All(item => item is JsonValue entry && entry.TryGetValue<string>(out var label) &&
                !string.IsNullOrWhiteSpace(label) && NumberSequence.IsSingleLineLabel(label));
        if (owner == typeof(ElementStyle)) return name switch
        {
            "StrokeWidth" or "Opacity" or "FontSize" or "ArrowHeadSize" => NumericParameterDomain.ForStyle(name).Contains(value.Deserialize<double>()),
            "LineArcBulge" => Range(value, -ElementGeometry.MaximumLineArcBulge, ElementGeometry.MaximumLineArcBulge),
            "FontFamily" => Text(value, 1024), _ => true
        };
        if (owner == typeof(PresentationSettings))
        {
            var settings = new PresentationSettings();
            var property = typeof(PresentationSettings).GetProperty(name);
            if (property is not null) property.SetValue(settings, value.Deserialize(property.PropertyType, ReadOptions));
            return settings.IsValid();
        }
        if (owner == typeof(PresentationGlobalSettings) || owner == typeof(PresentationPointerSettings) || owner == typeof(PresentationClickStyle))
        {
            var settings = Activator.CreateInstance(owner)!;
            var property = owner.GetProperty(name);
            if (property is not null) property.SetValue(settings, value.Deserialize(property.PropertyType, ReadOptions));
            return settings switch
            {
                PresentationGlobalSettings global => global.IsValid(),
                PresentationPointerSettings pointer => pointer.IsValid(),
                PresentationClickStyle click => click.IsValid(),
                _ => false
            };
        }
        if (owner == typeof(ToolParameters)) return name switch
        {
            "DefaultWidth" or "DefaultHeight" or "NumberStart" or "NumberStep" or "EffectStrength" or "CaptureBorderWidth" or "CornerRadius" => NumericParameterDomain.ForTool(name).Contains(value.Deserialize<double>()),
            "Emoji" => Text(value, 32) && System.Globalization.StringInfo.GetNextTextElement(value.GetValue<string>()).Length == value.GetValue<string>().Length, _ => true
        };
        if (owner == typeof(CanvasPresetParameters)) return name switch
        { "Columns" or "Rows" => Range(value,1,CanvasCalibration.MaximumGridCount), "GridWidth" => Range(value,double.Epsilon,64), "FlatCellSize" => Range(value,CanvasGridGeometry.MinimumCellSize,CanvasGridGeometry.MaximumCellSize), _ => true };
        if (owner == typeof(UserSettings)) return name switch
        { "NumberStart" or "NumberStep" => Range(value,-999999,999999), "Theme" => new[]{"system","light","dark"}.Contains(value.GetValue<string>()), "Language" => new[]{"auto","zh-CN","en"}.Contains(value.GetValue<string>()), _ => true };
        if(owner==typeof(HotkeyBinding))return name switch {"Modifiers"=>Range(value,0,15),"VirtualKey"=>Range(value,1,255),_=>true};
        return true;
    }
    private static bool Range(JsonNode value, double minimum, double maximum) => value.Deserialize<double>() is var number && double.IsFinite(number) && number >= minimum && number <= maximum;
    private static bool Text(JsonNode value,int maximum) => value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length<=maximum;

    private static JsonNode? Merge(JsonNode? raw, JsonNode? previous, JsonNode? current,List<string> replacements,string location)
    {
        if (JsonNode.DeepEquals(previous,current)) return raw?.DeepClone();
        if (raw is JsonObject oldObject && previous is JsonObject oldKnown && current is JsonObject newKnown)
        {
            var result = (JsonObject)oldObject.DeepClone();
            foreach (string key in oldKnown.Select(p=>p.Key).Union(newKnown.Select(p=>p.Key)))
            {
                if (!newKnown.ContainsKey(key)) { result.Remove(key); continue; }
                if(JsonNode.DeepEquals(oldKnown[key],newKnown[key]))continue;
                result[key] = Merge(oldObject[key],oldKnown[key],newKnown[key],replacements,location.Length==0?key:location+"."+key);
            }
            return result;
        }
        if (raw is JsonArray oldArray && previous is JsonArray oldItems && current is JsonArray newItems &&
            oldItems.Concat(newItems).All(item=>item is JsonObject record && record["Id"] is JsonValue))
        {
            var result=new JsonArray();
            foreach(var item in newItems)
            {
                string id=item!["Id"]!.ToString();
                result.Add(Merge(oldArray.FirstOrDefault(p=>p?["Id"]?.ToString()==id),oldItems.FirstOrDefault(p=>p?["Id"]?.ToString()==id),item,replacements,location+"["+id+"]"));
            }
            foreach(var item in oldArray.Where(item=>!oldItems.Any(known=>known?["Id"]?.ToString()==item?["Id"]?.ToString()))) result.Add(item?.DeepClone());
            return result;
        }
        if(raw is not null && !JsonNode.DeepEquals(raw,previous))replacements.Add(location);
        return current?.DeepClone();
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            { if(!names.Add(property.Name))throw new InvalidDataException("Configuration contains duplicate fields."); CheckDuplicates(property.Value); }
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())CheckDuplicates(item);
    }
}
