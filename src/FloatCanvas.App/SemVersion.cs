using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace FloatCanvas.App;

/// <summary>SemVer precedence; build metadata never changes release ordering.</summary>
internal sealed class SemVersion : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
    private readonly string[] core, pre;
    internal string Value { get; }
    internal bool IsPrerelease => pre.Length > 0;
    private SemVersion(string value, Match match)
    {
        Value = value;
        core = new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value };
        pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
    }
    internal static bool TryParse(string? value, out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > 180) return false;
        var match = Pattern.Match(value);
        if (!match.Success || match.Groups[4].Value.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit))) return false;
        version = new SemVersion(value, match); return true;
    }
    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < core.Length; i++) { int c = Number(core[i], other.core[i]); if (c != 0) return c; }
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (int i = 0; i < Math.Min(pre.Length, other.pre.Length); i++)
        {
            bool leftNumber = pre[i].All(char.IsAsciiDigit), rightNumber = other.pre[i].All(char.IsAsciiDigit);
            int c = leftNumber && rightNumber ? Number(pre[i], other.pre[i]) : leftNumber != rightNumber ? leftNumber ? -1 : 1 : string.CompareOrdinal(pre[i], other.pre[i]);
            if (c != 0) return c;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
    private static int Number(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
}
