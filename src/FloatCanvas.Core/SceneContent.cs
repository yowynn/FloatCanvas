using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>Value comparison of the persisted scene, without serializing or copying path buffers.</summary>
internal static class SceneContent
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> properties = new();
    internal static bool Equal(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.GetType() != right.GetType()) return false;
        if (left is PointCollection points) return points.ContentEquals((PointCollection)right);
        if (left is SceneDocument scene && scene.IndependentCanvasOrigin != ((SceneDocument)right).IndependentCanvasOrigin) return false;
        var type = left.GetType();
        if (type.IsValueType || left is string) return left.Equals(right);
        if (left is IDictionary leftMap && right is IDictionary rightMap)
        {
            if (leftMap.Count != rightMap.Count) return false;
            foreach (DictionaryEntry pair in leftMap)
                if (!rightMap.Contains(pair.Key) || !Equal(pair.Value, rightMap[pair.Key])) return false;
            return true;
        }
        if (left is IList a && right is IList b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!Equal(a[i], b[i])) return false;
            return true;
        }
        foreach (var property in properties.GetOrAdd(type, static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray()))
            if (!Equal(property.GetValue(left), property.GetValue(right))) return false;
        return true;
    }
}
