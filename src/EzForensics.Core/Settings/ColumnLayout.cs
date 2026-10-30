using System.Security.Cryptography;
using System.Text;

namespace EzForensics.Core.Settings;

/// <summary>Analyze grid: hidden columns and user column widths of one CSV header signature.</summary>
public sealed class ColumnLayout
{
    /// <summary>CSV column indices that are hidden.</summary>
    public List<int> Hidden { get; set; } = new();
    /// <summary>User widths (device-independent pixels) by CSV column index; columns not listed use the default width.</summary>
    public Dictionary<int, double> Widths { get; set; } = new();
    /// <summary>Last use (UTC ticks) so the oldest layouts are dropped first.</summary>
    public long LastUsedTicks { get; set; }

    public bool IsEmpty => Hidden.Count == 0 && Widths.Count == 0;
}

public static class ColumnLayoutStore
{
    public const int MaxEntries = 200;
    public const double MinWidth = 40, MaxWidth = 800;

    /// <summary>Stable key for a header list (SHA-256 of the names, first 16 hex characters).</summary>
    public static string Signature(IReadOnlyList<string> headers)
    {
        var sb = new StringBuilder();
        foreach (var h in headers) sb.Append(h).Append('\u001f');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static double ClampWidth(double w) => double.IsNaN(w) ? MinWidth : Math.Clamp(w, MinWidth, MaxWidth);

    /// <summary>Stores <paramref name="layout"/> under <paramref name="key"/> (empty layouts are removed) and keeps at most <paramref name="max"/> entries, dropping the least recently used.</summary>
    public static void Put(Dictionary<string, ColumnLayout> all, string key, ColumnLayout layout, int max = MaxEntries)
    {
        if (layout.IsEmpty) { all.Remove(key); return; }
        layout.LastUsedTicks = DateTime.UtcNow.Ticks;
        all[key] = layout;
        Trim(all, max);
    }

    public static void Trim(Dictionary<string, ColumnLayout> all, int max = MaxEntries)
    {
        if (all.Count <= max) return;
        foreach (var k in all.OrderBy(kv => kv.Value.LastUsedTicks).Take(all.Count - max).Select(kv => kv.Key).ToList())
            all.Remove(k);
    }
}
