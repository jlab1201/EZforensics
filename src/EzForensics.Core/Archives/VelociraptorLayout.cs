using System.Text.Json;
using System.Text.RegularExpressions;
using EzForensics.Core.Triage;

namespace EzForensics.Core.Archives;

/// <summary>Maps an archive entry name to the name it is extracted under (or null = skip, with a reason).</summary>
public interface IExtractionLayout
{
    string? Name { get; }
    (string? Path, string? Reason) Map(ArchiveEntryInfo entry);
}

/// <summary>Entries are extracted under their own names.</summary>
public sealed class IdentityLayout : IExtractionLayout
{
    public static readonly IdentityLayout Instance = new();
    public string? Name => null;
    public (string? Path, string? Reason) Map(ArchiveEntryInfo entry) => (entry.Name, null);
}

/// <summary>
/// Velociraptor offline collector zips (<c>Collection-*.zip</c>: client_info.json or collection_context.json plus uploads/).
/// Path components are percent-decoded, <c>C%3A</c> and <c>%5C%5C.%5CC%3A</c> map to <c>C</c>, and the auto / ntfs / file accessors are merged
/// into <c>&lt;Host&gt;\C\...</c> (on a clash the ntfs copy wins; the others are recorded as duplicates). Shadow copies go to
/// <c>&lt;Host&gt;\_velociraptor\vss\...</c> (so they are not discovered as systems); results, logs and json go to <c>&lt;Host&gt;\_velociraptor\</c>.
/// </summary>
public sealed partial class VelociraptorLayout : IExtractionLayout
{
    public const string LayoutName = "velociraptor";
    public string? Name => LayoutName;
    public string Host { get; }

    private readonly Dictionary<int, string> _losers = new();

    private VelociraptorLayout(string host) => Host = host;

    public static bool IsMatch(ArchiveInspection insp)
    {
        bool marker = false, uploads = false;
        foreach (var e in insp.Entries)
        {
            var n = Norm(e.Name);
            if (n.Equals("client_info.json", StringComparison.OrdinalIgnoreCase) || n.Equals("collection_context.json", StringComparison.OrdinalIgnoreCase)) marker = true;
            if (n.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)) uploads = true;
        }
        return marker && uploads;
    }

    /// <summary>Builds the layout (reads the host name from client_info.json; falls back to <paramref name="fallbackHost"/>).</summary>
    public static VelociraptorLayout Create(IArchiveEngine engine, string archivePath, string? password, ArchiveInspection insp, string fallbackHost)
    {
        var host = ReadHostName(engine, archivePath, password, insp) ?? fallbackHost;
        var layout = new VelociraptorLayout(HostDiscovery.Sanitize(host));
        // ntfs wins over auto over file when several accessors uploaded the same path.
        var best = new Dictionary<string, (int Rank, int Index, string Accessor)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in insp.Entries.Where(x => !x.IsDirectory))
        {
            var (path, _, accessor) = layout.MapCore(e.Name);
            if (path == null || accessor == null) continue;
            int rank = AccessorRank(accessor);
            if (best.TryGetValue(path, out var cur))
            {
                if (rank < cur.Rank) { layout._losers[cur.Index] = $"duplicate of the {accessor} accessor copy ({accessor} wins)"; best[path] = (rank, e.Index, accessor); }
                else layout._losers[e.Index] = $"duplicate of the {cur.Accessor} accessor copy ({cur.Accessor} wins)";
            }
            else best[path] = (rank, e.Index, accessor);
        }
        return layout;
    }

    public (string? Path, string? Reason) Map(ArchiveEntryInfo entry)
    {
        if (!entry.IsDirectory && _losers.TryGetValue(entry.Index, out var why)) return (null, why);
        var (path, reason, _) = MapCore(entry.Name);
        return (path, reason);
    }

    private (string? Path, string? Reason, string? Accessor) MapCore(string name)
    {
        var n = Norm(name);
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, "empty name", null);
        if (parts[0].Equals("uploads", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
        {
            var accessor = parts[1].ToLowerInvariant();
            var device = ArchivePaths.PercentDecode(parts[2]);
            var rest = parts.Skip(3).Select(DecodeComponent).ToList();
            var drive = DriveRegex().Match(device);
            if (drive.Success)
                return (Join(new[] { Host, drive.Groups["d"].Value.ToUpperInvariant() }.Concat(rest)), null, accessor);
            var vss = VssRegex().Match(device);
            if (vss.Success)
                return (Join(new[] { Host, "_velociraptor", "vss", vss.Value }.Concat(rest)), "Velociraptor shadow copy kept apart", accessor);
            return (Join(new[] { Host, "_velociraptor", "uploads", accessor, EscapeSeparators(device) }.Concat(rest)), "Velociraptor upload (unmapped device)", null);
        }
        return (Join(new[] { Host, "_velociraptor" }.Concat(parts.Select(DecodeComponent))), null, null);
    }

    private static int AccessorRank(string a) => a switch
    {
        "ntfs" or "lazy_ntfs" or "raw_ntfs" or "mft" => 0,
        "auto" => 1,
        "file" => 2,
        _ => 3,
    };

    private static string? ReadHostName(IArchiveEngine engine, string archivePath, string? password, ArchiveInspection insp)
    {
        var ci = insp.Entries.FirstOrDefault(e => Norm(e.Name).Equals("client_info.json", StringComparison.OrdinalIgnoreCase));
        if (ci == null || ci.Size > 1 << 20) return null;
        try
        {
            using var s = engine.OpenEntry(archivePath, ci, password);
            using var doc = JsonDocument.Parse(s);
            return FindHost(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArchiveIngestException) { return null; }
    }

    private static string? FindHost(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "Hostname", "hostname", "HostName" })
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString();
        foreach (var key in new[] { "Fqdn", "fqdn" })
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString()!.Split('.')[0];
        foreach (var p in el.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && FindHost(p.Value) is { } h) return h;
        return null;
    }

    private static string Norm(string n) => n.Replace('\\', '/').TrimStart('/');

    /// <summary>Percent-decodes one component; decoded '/' or '\' are re-escaped so the component boundary survives.</summary>
    private static string DecodeComponent(string c) => EscapeSeparators(ArchivePaths.PercentDecode(c));

    private static string EscapeSeparators(string s) => s.Replace("/", "%2F").Replace("\\", "%5C");

    private static string Join(IEnumerable<string> parts) => string.Join('/', parts.Where(p => p.Length > 0));

    [GeneratedRegex(@"^(\\\\[.?]\\)?(?<d>[A-Za-z]):?$")]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"HarddiskVolumeShadowCopy\d+", RegexOptions.IgnoreCase)]
    private static partial Regex VssRegex();
}
