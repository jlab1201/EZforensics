using EzForensics.Core.Case;

namespace EzForensics.Core.Spectre;

/// <summary>
/// What the case knows about its evidence systems, for placing findings on computers: the host of a case-relative evidence CSV
/// (null when unknown or case-wide) and the one system of the case when it has exactly one.
/// </summary>
public sealed class CaseHostInfo
{
    public CaseHostInfo(Func<string, string?> hostForCasePath, string? singleSystem)
    {
        HostForCasePath = hostForCasePath;
        SingleSystem = string.IsNullOrWhiteSpace(singleSystem) ? null : singleSystem.Trim();
    }

    public Func<string, string?> HostForCasePath { get; }
    /// <summary>The only evidence system of the case (from its manifests and host folders); null when there are none or several.</summary>
    public string? SingleSystem { get; }

    /// <summary>Reads the case index (disk work: call off the UI thread). Null when the folder does not exist.</summary>
    public static CaseHostInfo? Load(string? caseDir)
    {
        if (string.IsNullOrWhiteSpace(caseDir) || !Directory.Exists(caseDir)) return null;
        var index = CaseIndex.Load(caseDir);
        var root = index.CaseDir;
        string? HostFor(string casePath)
        {
            if (string.IsNullOrWhiteSpace(casePath)) return null;
            try
            {
                var p = index.Describe(Path.IsPathRooted(casePath) ? casePath : Path.Combine(root, casePath.Replace('/', '\\')));
                return p.IsCaseWide || string.IsNullOrWhiteSpace(p.Host) ? null : p.Host;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
        }
        return new CaseHostInfo(HostFor, index.Hosts.Count == 1 ? index.Hosts[0] : null);
    }
}

/// <summary>
/// Which computer each timeline finding belongs to. The engine's <c>host</c> is the evtx record's own Computer field, so other artifacts
/// have none and a renamed machine shows under two names. The case knows the evidence system of every output file, so that wins:
/// (a) the case host of the finding's evidence file, else (b) the case's only evidence system, else (c) the record's own host,
/// else "Unknown computer". Record names that differ from the computer are kept as <see cref="AliasesOf"/> (a list of names seen, not a claim).
/// </summary>
public sealed class StoryHosts
{
    private readonly Dictionary<SpectreTimelineFinding, string> _computer = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<string>> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>No case information: every finding keeps its record host.</summary>
    public static StoryHosts None { get; } = new();

    public bool UsesCase { get; private init; }

    public static StoryHosts Resolve(IEnumerable<SpectreTimelineFinding> findings, CaseHostInfo? info) =>
        Resolve(findings, info?.HostForCasePath, info?.SingleSystem);

    public static StoryHosts Resolve(IEnumerable<SpectreTimelineFinding> findings, Func<string, string?>? hostForCasePath, string? singleSystem = null)
    {
        var r = new StoryHosts { UsesCase = hostForCasePath != null || !string.IsNullOrWhiteSpace(singleSystem) };
        var single = string.IsNullOrWhiteSpace(singleSystem) ? null : singleSystem.Trim();
        foreach (var f in findings)
        {
            string? computer = null;
            if (hostForCasePath != null)
                foreach (var p in CasePaths(f))
                    if (hostForCasePath(p) is { Length: > 0 } h && !string.IsNullOrWhiteSpace(h)) { computer = h.Trim(); break; }
            computer ??= single;
            var record = string.IsNullOrWhiteSpace(f.Host) ? null : f.Host.Trim();
            computer ??= record;
            computer ??= StoryGraphics.NoHost;
            r._computer[f] = computer;
            if (record != null && !string.Equals(record, computer, StringComparison.OrdinalIgnoreCase))
            {
                if (!r._aliases.TryGetValue(computer, out var list)) r._aliases[computer] = list = new List<string>();
                if (!list.Contains(record, StringComparer.OrdinalIgnoreCase)) list.Add(record);
            }
        }
        foreach (var l in r._aliases.Values) l.Sort(StringComparer.OrdinalIgnoreCase);
        return r;
    }

    private static IEnumerable<string> CasePaths(SpectreTimelineFinding f)
    {
        if (!string.IsNullOrWhiteSpace(f.CasePath)) yield return f.CasePath!;
        foreach (var e in f.Evidence)
            if (!string.IsNullOrWhiteSpace(e.CasePath)) yield return e.CasePath;
    }

    /// <summary>The computer of a finding.</summary>
    public string ComputerOf(SpectreTimelineFinding f) =>
        _computer.TryGetValue(f, out var c) ? c : StoryGraphics.HostName(f.Host);

    /// <summary>The computer of a moment: its primary finding's, else the first listed finding with one known.</summary>
    public string ComputerOf(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        string? fallback = null;
        IEnumerable<string> ids = m.PrimaryFindingId != null ? new[] { m.PrimaryFindingId }.Concat(m.FindingIds) : m.FindingIds;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var f)) continue;
            var c = ComputerOf(f);
            if (c != StoryGraphics.NoHost) return c;
            fallback = c;
        }
        return fallback ?? StoryGraphics.NoHost;
    }

    /// <summary>Distinct record host names (other than the computer's own name), sorted; empty when none.</summary>
    public IReadOnlyList<string> AliasesOf(string computer) =>
        _aliases.TryGetValue(computer, out var l) ? l : Array.Empty<string>();

    /// <summary>"Names seen in the records: A, B" or "" when the records add no other name.</summary>
    public string AliasLine(string computer) => AliasesOf(computer) is { Count: > 0 } a ? "Names seen in the records: " + string.Join(", ", a) : "";

    /// <summary>Number of distinct computers that are not "Unknown computer".</summary>
    public int NamedComputerCount => _computer.Values.Where(c => c != StoryGraphics.NoHost).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
