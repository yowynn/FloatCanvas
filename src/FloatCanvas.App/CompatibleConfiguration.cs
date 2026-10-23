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
    private JsonObject? original;
    private JsonNode? baseline;
    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 128 };

    internal T Read<T>(JsonSerializerOptions options)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the size limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new() { MaxDepth = 128 });
        CheckDuplicates(document.RootElement);
        original = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException("Configuration must be an object.");
        var normalized = Normalize(original, typeof(T), Default(typeof(T)));
        var value = normalized!.Deserialize<T>(ReadOptions) ?? throw new InvalidDataException("Configuration is empty.");
        Capture(value, options);
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
            original = (JsonObject)merged; baseline = current;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var result = new JsonArray(); if (raw is not JsonArray array) return result;
            var itemType = type.GetGenericArguments()[0];
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
            object? defaultValue = fallback is null ? Default(property.PropertyType) : property.GetValue(fallback);
            var next = source.TryGetPropertyValue(name, out var supplied) ? Normalize(supplied, property.PropertyType, defaultValue) : JsonSerializer.SerializeToNode(defaultValue, property.PropertyType, ReadOptions);
            if (name == "Version" && (next is null || !int.TryParse(next.ToString(),out int version) || version<1 || version>Convert.ToInt32(defaultValue)) || !ValidValue(type, name, next))
                next = JsonSerializer.SerializeToNode(defaultValue, property.PropertyType, ReadOptions);
            normalized[name] = next;
        }
        if(type==typeof(ToolParameters) && !source.ContainsKey(nameof(ToolParameters.NumberColorRolesVersion)) && normalized[nameof(ToolParameters.Tool)]?.Deserialize<EditorTool>()==EditorTool.Number)
            normalized[nameof(ToolParameters.NumberColorRolesVersion)]=0;
        return normalized;
    }

    private static object? Default(Type type)
    {
        if (type == typeof(string)) return "";
        if (Nullable.GetUnderlyingType(type) is not null) return null;
        try { return Activator.CreateInstance(type, nonPublic: true); }
        catch (MissingMethodException) { return null; }
    }
    private static JsonNode? Fallback(object? value,Type type) => value is null ? null : JsonSerializer.SerializeToNode(value,type,ReadOptions);
    private static bool TryEnum(Type type, string value, out object? parsed) => Enum.TryParse(type, value, false, out parsed) && parsed is not null && Enum.IsDefined(type, parsed);

    private static bool ValidValue(Type owner, string name, JsonNode? value)
    {
        if (value is null) return true;
        if (owner == typeof(ElementStyle)) return name switch
        {
            "StrokeWidth" => Range(value, 0, 1024), "Opacity" => Range(value, 0, 1), "FontSize" => Range(value, double.Epsilon, 4096),
            "ArrowHeadSize" => Range(value, 0, 512), "FontFamily" => Text(value, 1024), _ => true
        };
        if (owner == typeof(ToolParameters)) return name switch
        {
            "DefaultWidth" or "DefaultHeight" => Range(value, 1, 1000000), "NumberStart" or "NumberStep" => Range(value, 1, 999999),
            "NumberColorRolesVersion" => Range(value,0,1),
            "EffectStrength" => Range(value, double.Epsilon, 1024), "CaptureBorderWidth" => Range(value, 0, 1024), "CornerRadius" => Range(value, 0, double.MaxValue),
            "Emoji" => Text(value, 32) && System.Globalization.StringInfo.GetNextTextElement(value.GetValue<string>()).Length == value.GetValue<string>().Length, _ => true
        };
        if (owner == typeof(CanvasPresetParameters)) return name switch
        { "Columns" or "Rows" => Range(value,1,CanvasCalibration.MaximumGridCount), "GridWidth" => Range(value,double.Epsilon,64), "FlatCellSize" => Range(value,CanvasGridGeometry.MinimumCellSize,CanvasGridGeometry.MaximumCellSize), _ => true };
        if (owner == typeof(UserSettings)) return name switch
        { "NumberStart" or "NumberStep" => Range(value,1,999999), "Theme" => new[]{"system","light","dark"}.Contains(value.GetValue<string>()), "Language" => new[]{"auto","zh-CN","en"}.Contains(value.GetValue<string>()), _ => true };
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
