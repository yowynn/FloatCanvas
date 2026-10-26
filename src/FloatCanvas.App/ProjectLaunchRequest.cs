using System.IO;
using L = FloatCanvas.Localization.AppText;

namespace FloatCanvas.App;

internal static class ProjectLaunchRequest
{
    internal static string? Parse(string[] args)
    {
        if (args.Contains("--desktop-mode") && args.Contains("--retry-fullscreen"))
            throw new ArgumentException(L.Get("Shell.InvalidArguments"));
        string? project = null;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument is "--profile" or "--smoke-seconds")
            {
                if (++i == args.Length) throw new ArgumentException(L.Get("Shell.InvalidArguments"));
                continue;
            }
            if (argument is "--recover" or "--primary-screen" or "--all-screens" or "--smoke" or "--startup" or
                "--desktop-mode" or "--retry-fullscreen") continue;
            if (argument == "--open")
            {
                if (++i == args.Length) throw new ArgumentException(L.Get("Shell.InvalidArguments"));
                argument = args[i];
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(L.Get("Shell.InvalidArguments"));
            if (project is not null) throw new ArgumentException(L.Get("Shell.OneProject"));
            project = NormalizePath(argument);
        }
        return project;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0 ||
            !string.Equals(Path.GetExtension(path), ".floatcanvas", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(L.Get("Shell.InvalidProject"));
        return Path.GetFullPath(path);
    }

    internal static bool AllowsRegistration(string[] args)
    {
        if (args.Contains("--smoke")) return false;
        int profile = Array.IndexOf(args, "--profile");
        // Both restart paths explicitly retain the profile; that alone does not make it isolated.
        return profile < 0 || (profile + 1 < args.Length && !string.IsNullOrWhiteSpace(args[profile + 1]) &&
            string.Equals(ApplicationPaths.CanonicalDirectory(args[profile + 1]), ApplicationPaths.DefaultProfileDirectory, StringComparison.OrdinalIgnoreCase));
    }
}
