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
        var type = left.GetType();
        if (type.IsValueType || left is string) return left.Equals(right);
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
