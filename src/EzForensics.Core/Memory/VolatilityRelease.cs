using System.Net.Http;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Memory;

/// <summary>Latest volatility3 on PyPI (same JSON shape as plaso's lookup).</summary>
public static class VolatilityReleaseCheck
{
    public const string PyPIJson = "https://pypi.org/pypi/volatility3/json";

    public static PlasoRelease? ParsePyPiJson(string json) => PlasoReleaseCheck.ParsePyPiJson(json);

    public static async Task<PlasoRelease> GetLatestAsync(HttpClient http, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? PlasoReleaseCheck.DefaultTimeout);
        using var resp = await http.GetAsync(PyPIJson, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return ParsePyPiJson(json) ?? throw new InvalidDataException("PyPI returned no usable volatility3 version.");
    }

    /// <summary>True when <paramref name="latest"/> is a newer dotted version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? installed, string? latest) =>
        Version.TryParse(installed, out var a) && Version.TryParse(latest, out var b) && b > a;
}
