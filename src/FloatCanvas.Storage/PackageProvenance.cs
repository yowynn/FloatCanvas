using System.Reflection;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.Storage;

internal static class PackageProvenance
{
    internal static string ApplicationVersion(string? supplied = null)
    {
        string version = supplied ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
        ValidateVersion(version, allowNull: false);
        return version;
    }

    internal static void ValidateVersion(string? version, bool allowNull)
    {
        if (version is null && allowNull) return;
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl))
            throw new InvalidDataException(L.Get("Project.InvalidManifest"));
    }
}
