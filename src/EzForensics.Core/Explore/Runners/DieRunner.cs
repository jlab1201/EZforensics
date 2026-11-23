using System.Text.Json;

namespace EzForensics.Core.Explore;

/// <summary>One Detect It Easy detection (compiler, linker, packer, protector, installer, sign tool...).</summary>
/// <param name="Type">Lower-case kind without the heuristic marker (<c>packer</c>, <c>compiler</c>...).</param>
/// <param name="Heuristic">Found by DIE's heuristic scan (<c>~type</c> / <c>(Heur)</c> in the output).</param>
/// <param name="Text">DIE's own one-line text, e.g. <c>Packer: UPX(3.96)[NRV,brute]</c>.</param>
public sealed record DieDetection(string FileType, string Type, string Name, string? Version, string? Info, string Text, bool Heuristic)
{
    public bool IsPacker => Type.Contains("packer", StringComparison.OrdinalIgnoreCase);
    public bool IsProtector => Type.Contains("protector", StringComparison.OrdinalIgnoreCase) || Type.Contains("cryptor", StringComparison.OrdinalIgnoreCase);
    public bool IsUnknown => Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parsed <c>diec -j</c> output.</summary>
/// <param name="FileTypes">Detected container types (<c>PE64</c>, <c>ZIP</c>, <c>ELF64</c>...).</param>
public sealed record DieResult(IReadOnlyList<string> FileTypes, IReadOnlyList<DieDetection> Detections)
{
    public IEnumerable<DieDetection> Known => Detections.Where(d => !d.IsUnknown);
    public bool IsPacked => Detections.Any(d => d.IsPacker || d.IsProtector);

    /// <summary>Short verdict for chips, e.g. <c>Packer: UPX</c>; null when nothing notable.</summary>
    public string? Verdict =>
        Detections.FirstOrDefault(d => d.IsProtector) is { } p ? $"Protector: {p.Name}"
        : Detections.FirstOrDefault(d => d.IsPacker) is { } k ? $"Packer: {k.Name}"
        : null;
}

/// <summary>Runs Detect It Easy's console scanner (<c>diec -j -d -u &lt;file&gt;</c>: JSON, deep and heuristic scan). Static only: DIE never runs the file.</summary>
public sealed class DieRunner
{
    public const string ToolKey = "die";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public DieRunner(string diecExe, string? version = null)
    {
        DiecExe = diecExe;
        Version = version;
    }

    public DieRunner(ExploreToolLocator locator) : this(locator.DiecExe, locator.Version(ExploreTool.Die)) { }

    public string DiecExe { get; }
    public string? Version { get; }

    public static IReadOnlyList<string> BuildArgs(string file) => new[] { "-j", "-d", "-u", file };

    /// <summary>Runs DIE on <paramref name="file"/> (a reparse point or folder faults the task with <see cref="IOException"/>).</summary>
    public async Task<ExploreToolOutput> RunAsync(string file, TimeSpan? timeout = null, CancellationToken ct = default) =>
        await ExploreToolProcess.RunAsync(ToolKey, Version, DiecExe, BuildArgs(ExploreToolProcess.CheckedEvidencePath(file)),
            timeout ?? DefaultTimeout, ct).ConfigureAwait(false);

    /// <summary>Cached by SHA-256 in <paramref name="cache"/>; runs DIE only on a miss (or when <paramref name="refresh"/>).</summary>
    public async Task<(ExploreToolOutput Output, DieResult? Result)> GetAsync(ResultCache cache, string file, string sha256,
        bool refresh = false, CancellationToken ct = default)
    {
        var o = await cache.GetOrRunAsync(sha256, ToolKey, c => RunAsync(file, null, c), refresh, ct).ConfigureAwait(false);
        return (o, o.Ok ? Parse(o.Raw) : null);
    }

    /// <summary>Parses <c>diec -j</c> JSON (<c>{"detects":[{"filetype":..,"values":[{type,name,version,info,string}]}]}</c>); null when not DIE JSON.</summary>
    public static DieResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("detects", out var detects)
                || detects.ValueKind != JsonValueKind.Array) return null;
            var types = new List<string>();
            var list = new List<DieDetection>();
            foreach (var d in detects.EnumerateArray())
            {
                var ft = Str(d, "filetype") ?? "";
                if (ft.Length > 0 && !types.Contains(ft)) types.Add(ft);
                if (!d.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in values.EnumerateArray())
                {
                    var type = Str(v, "type") ?? "";
                    var text = Str(v, "string") ?? "";
                    bool heur = type.StartsWith('~') || text.StartsWith("(Heur)", StringComparison.OrdinalIgnoreCase);
                    list.Add(new DieDetection(ft, type.TrimStart('~').Trim().ToLowerInvariant(), Str(v, "name") ?? "",
                        NullIfEmpty(Str(v, "version")), NullIfEmpty(Str(v, "info")), text, heur));
                }
            }
            return new DieResult(types, list);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
