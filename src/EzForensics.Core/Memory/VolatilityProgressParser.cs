using System.Globalization;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Memory;

/// <summary>Volatility prints <c>Progress:  45.12\t\tScanning ...</c> on stderr (no <c>-q</c>).</summary>
public static class VolatilityProgressParser
{
    private static readonly Regex Rx = new(@"^\s*Progress:\s*(?<p>[0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Percentage 0..100, or null when the line is not a progress line.</summary>
    public static double? Parse(string? stderrLine)
    {
        if (string.IsNullOrEmpty(stderrLine)) return null;
        var m = Rx.Match(stderrLine);
        if (!m.Success || !double.TryParse(m.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return Math.Clamp(v, 0, 100);
    }

    /// <summary>The text after the percentage (<c>Scanning layer ...</c>), or null.</summary>
    public static string? Status(string? stderrLine)
    {
        if (Parse(stderrLine) == null) return null;
        var m = Rx.Match(stderrLine!);
        var rest = stderrLine![(m.Index + m.Length)..].Trim();
        return rest.Length == 0 ? null : rest;
    }
}
