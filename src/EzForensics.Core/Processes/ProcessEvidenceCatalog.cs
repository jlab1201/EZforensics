using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Processes;

/// <summary>How a source row is tied to a process (the strongest key the source offers).</summary>
public enum EvidenceJoinKind { PidTime, Guid, PathTime, Socket }

/// <summary>The process detail tab a source feeds.</summary>
public enum EvidenceTab { Overview, Timeline, Network, Files, Registry, Modules, Threats, Scripts, Identity, Persistence, BinaryOnDisk }

/// <summary>Column aliases of a source; headers are compared lower case with everything but letters and digits removed (see <see cref="ProcessLogText.Norm"/>).</summary>
public sealed record EvidenceColumns(
    IReadOnlyList<string> Pid, IReadOnlyList<string> Guid, IReadOnlyList<string> Path, IReadOnlyList<string> PathJoin, IReadOnlyList<string> Name,
    IReadOnlyList<string> Time, IReadOnlyList<string> Display, bool PathList, bool ExeFromCommand);

/// <summary>One entry of <c>Resources/processes/sources.json</c>.</summary>
public sealed record EvidenceSourceDef(
    string Id, string Label, string ArtifactId, Regex FileRegex, bool IsCsv, EvidenceJoinKind JoinKind, EvidenceTab Tab, string HowToGet,
    bool Verified, Regex? RowFilter, EvidenceColumns Columns, TimeFit? Fit = null)
{
    public bool Matches(string fileName)
    {
        try { return FileRegex.IsMatch(fileName); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>The embedded catalog of process evidence sources (<c>Resources/processes/sources.json</c>), loaded once.</summary>
public static class ProcessEvidenceCatalog
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Lazy<IReadOnlyList<EvidenceSourceDef>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<EvidenceSourceDef> Sources => Embedded.Value;

    public static EvidenceSourceDef? Find(string id) => Sources.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The CSV-read source a file name belongs to (first catalog match), or null.</summary>
    public static EvidenceSourceDef? ForFile(string fileName, IReadOnlyList<EvidenceSourceDef>? sources = null) =>
        (sources ?? Sources).FirstOrDefault(s => s.IsCsv && s.Matches(fileName));

    public static IReadOnlyList<EvidenceSourceDef> Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new InvalidDataException("sources.json is empty");
        var list = new List<EvidenceSourceDef>();
        foreach (var e in file.Sources ?? new())
        {
            if (e == null) continue;
            if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.FileRegex) || string.IsNullOrWhiteSpace(e.HowToGet))
                throw new InvalidDataException("sources.json: every source needs id, fileRegex and howToGet");
            var c = e.Columns ?? new ColumnsEntry();
            var opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
            list.Add(new EvidenceSourceDef(
                e.Id, e.Label ?? e.Id, e.ArtifactId ?? e.Id, new Regex(e.FileRegex, opts, RegexTimeout),
                !(string.Equals(e.Reader, "evtx", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Reader, "zeek", StringComparison.OrdinalIgnoreCase)), ParseJoin(e.JoinKind), ParseTab(e.Tab), e.HowToGet, e.Verified,
                string.IsNullOrWhiteSpace(e.RowFilter) ? null : new Regex(e.RowFilter, opts, RegexTimeout),
                new EvidenceColumns(Norm(c.Pid), Norm(c.Guid), Norm(c.Path), Norm(c.PathJoin), Norm(c.Name), Norm(c.Time), c.Display ?? new List<string>(), c.PathList, c.ExeFromCommand),
                ParseFit(e)));
        }
        var dup = list.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException("sources.json: duplicate id " + dup.Key);
        return list;
    }

    private static IReadOnlyList<string> Norm(List<string>? names) => (names ?? new List<string>()).Select(ProcessLogText.Norm).ToList();

    private static EvidenceJoinKind ParseJoin(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "pid-time" => EvidenceJoinKind.PidTime,
        "guid" => EvidenceJoinKind.Guid,
        "path-time" => EvidenceJoinKind.PathTime,
        "socket" => EvidenceJoinKind.Socket,
        _ => throw new InvalidDataException("sources.json: unknown joinKind '" + s + "'"),
    };

    /// <summary>The time rule of a path-time source (<c>timeRole</c> start | during, <c>toleranceSec</c> [min, max] for start); null for the other join kinds.</summary>
    private static TimeFit? ParseFit(SourceEntry e)
    {
        if (!string.Equals(e.JoinKind, "path-time", StringComparison.OrdinalIgnoreCase)) return null;
        switch ((e.TimeRole ?? "").ToLowerInvariant())
        {
            case "start":
                var tol = e.ToleranceSec;
                if (tol == null || tol.Count != 2 || tol[0] > tol[1]) throw new InvalidDataException($"sources.json: {e.Id} needs toleranceSec [min, max] for timeRole start");
                return new TimeFit(TimeRole.Start, tol[0], tol[1]);
            case "during":
                return TimeFit.During;
            default:
                throw new InvalidDataException($"sources.json: {e.Id} is path-time and needs timeRole start or during");
        }
    }

    private static EvidenceTab ParseTab(string? s) =>
        Enum.TryParse<EvidenceTab>(s, true, out var t) ? t : throw new InvalidDataException("sources.json: unknown tab '" + s + "'");

    private static IReadOnlyList<EvidenceSourceDef> LoadEmbedded()
    {
        var asm = typeof(ProcessEvidenceCatalog).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("processes.sources.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(s);
    }

    private sealed class CatalogFile
    {
        public int Version { get; set; }
        public List<SourceEntry> Sources { get; set; } = new();
    }

    private sealed class SourceEntry
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public string? ArtifactId { get; set; }
        public string FileRegex { get; set; } = "";
        public string? Reader { get; set; }
        public string? JoinKind { get; set; }
        public string? Tab { get; set; }
        public string HowToGet { get; set; } = "";
        public bool Verified { get; set; } = true;
        public string? RowFilter { get; set; }
        public string? TimeRole { get; set; }
        public List<double>? ToleranceSec { get; set; }
        public ColumnsEntry? Columns { get; set; }
    }

    private sealed class ColumnsEntry
    {
        public List<string>? Pid { get; set; }
        public List<string>? Guid { get; set; }
        public List<string>? Path { get; set; }
        public List<string>? PathJoin { get; set; }
        public List<string>? Name { get; set; }
        public List<string>? Time { get; set; }
        public List<string>? Display { get; set; }
        public bool PathList { get; set; }
        public bool ExeFromCommand { get; set; }
    }
}
