using System.Net.Http;
using System.Text.Json;

namespace EzForensics.Core.Plaso;

/// <summary>The latest plaso release on PyPI.</summary>
public sealed record PlasoRelease(string Version, DateTime? UploadedUtc, long? WheelSize, string? RequiresPython);

/// <summary>Latest-version lookup (PyPI JSON API) and plaso version comparison.</summary>
public static class PlasoReleaseCheck
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Parses <c>https://pypi.org/pypi/plaso/json</c>. <c>info.version</c> is PyPI's latest non-pre-release, non-yanked
    /// version; the wheel size/upload time come from <c>urls</c> (the files of that version). Null when unusable.
    /// </summary>
    public static PlasoRelease? ParsePyPiJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
            var version = Str(info, "version");
            if (string.IsNullOrWhiteSpace(version)) return null;
            DateTime? uploaded = null;
            long? wheel = null;
            if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in urls.EnumerateArray())
                {
                    if (f.TryGetProperty("yanked", out var y) && y.ValueKind == JsonValueKind.True) continue;
                    var type = Str(f, "packagetype");
                    var t = Str(f, "upload_time_iso_8601") ?? Str(f, "upload_time");
                    if (uploaded == null && t != null && DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                        uploaded = dt;
                    if (type == "bdist_wheel" && f.TryGetProperty("size", out var s) && s.TryGetInt64(out var n)) wheel = n;
                }
            }
            return new PlasoRelease(version.Trim(), uploaded, wheel, Str(info, "requires_python"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>GETs the PyPI JSON API with a timeout (the caller's HttpClient carries the proxy settings).</summary>
    public static async Task<PlasoRelease> GetLatestAsync(HttpClient http, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var resp = await http.GetAsync(PlasoLinks.PyPIJson, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParsePyPiJson(json) ?? throw new InvalidDataException("PyPI returned no usable plaso version.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"PyPI did not answer within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Compares plaso versions: date versions ("20260720", "20260928.post1") by their 8-digit date, then the rest
    /// ordinally; other strings as dotted numbers. Unknown/empty sorts lowest.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        a = Normalize(a); b = Normalize(b);
        if (a == null || b == null) return (a == null ? 0 : 1) - (b == null ? 0 : 1);
        long da = PlasoLocator.VersionNumber(a), db = PlasoLocator.VersionNumber(b);
        if (da != 0 && db != 0)
        {
            int c = da.CompareTo(db);
            if (c != 0) return c;
            return string.CompareOrdinal(a[8..].TrimStart('.', '-'), b[8..].TrimStart('.', '-')) switch { < 0 => -1, > 0 => 1, _ => 0 };
        }
        if (da != 0 || db != 0) return da != 0 ? 1 : -1;
        var pa = a.Split('.', '-'); var pb = b.Split('.', '-');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length && long.TryParse(pa[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var xi) ? xi : 0;
            long y = i < pb.Length && long.TryParse(pb[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var yi) ? yi : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static bool IsNewer(string? latest, string? installed) => Normalize(latest) != null && Normalize(installed) != null && Compare(latest, installed) > 0;

    private static string? Normalize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.StartsWith("plaso-", StringComparison.OrdinalIgnoreCase)) v = v[6..];
        return v.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What the Tools shows for log2timeline / plaso.</summary>
public enum PlasoToolStatus { Detecting, Installed, UpdateAvailable, NotInstalled, PythonMissing }

public static class PlasoToolState
{
    /// <summary>
    /// Installed (latest or unknown latest) / UpdateAvailable (PyPI is newer) / NotInstalled / PythonMissing (no plaso
    /// and no usable Python: Install then downloads EZ Forensics' private Python first).
    /// </summary>
    public static PlasoToolStatus Evaluate(bool detecting, string? installedVersion, bool installed, string? latestVersion, bool? pythonAvailable)
    {
        if (detecting && !installed) return PlasoToolStatus.Detecting;
        if (installed) return PlasoReleaseCheck.IsNewer(latestVersion, installedVersion) ? PlasoToolStatus.UpdateAvailable : PlasoToolStatus.Installed;
        return pythonAvailable == false ? PlasoToolStatus.PythonMissing : PlasoToolStatus.NotInstalled;
    }

    public static string Label(PlasoToolStatus s) => s switch
    {
        PlasoToolStatus.Detecting => "Detecting…",
        PlasoToolStatus.Installed => "Installed",
        PlasoToolStatus.UpdateAvailable => "Update available",
        PlasoToolStatus.NotInstalled => "Not installed",
        _ => "Not installed (no Python)",
    };
}
