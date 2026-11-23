using System.IO.Enumeration;

namespace EzForensics.Core.Explore;

/// <summary>
/// The name filter of the Explore toolbar. Case-insensitive. <c>;</c> separates alternatives (any one matching is enough); a part with
/// <c>*</c> or <c>?</c> is a wildcard against the whole name, any other part is a "contains".
/// </summary>
public static class NameFilter
{
    private static readonly char[] Wild = { '*', '?' };

    public static bool Matches(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        foreach (var raw in pattern.Split(';'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (part.IndexOfAny(Wild) >= 0
                ? FileSystemName.MatchesSimpleExpression(part, name, ignoreCase: true)
                : name.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="pattern"/> has no usable part (empty, blank or only separators).</summary>
    public static bool IsEmpty(string? pattern) => pattern == null || pattern.Split(';').All(p => p.Trim().Length == 0);
}
