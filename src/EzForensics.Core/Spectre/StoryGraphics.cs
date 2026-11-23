namespace EzForensics.Core.Spectre;

/// <summary>One plain stage of the story with what was found there. <see cref="MaxSeverityRank"/> is -1 when nothing was found.</summary>
public sealed record StageSummary(string Stage, int Order, int Findings, int Moments, int MaxSeverityRank)
{
    public bool HasEvidence => Findings > 0 || Moments > 0;
    /// <summary>Highest severity word ("High", ...), or "" when nothing was found.</summary>
    public string Severity => MaxSeverityRank >= 0 && MaxSeverityRank < SpectreFinding.Severities.Count ? SpectreFinding.Severities[MaxSeverityRank] : "";
    public string Glyph => StoryGraphics.Glyph(Stage);
}

/// <summary>An item of the stage strip: a stage, or a gap standing for <see cref="Hidden"/> stages with nothing found.</summary>
public sealed record StageStripItem(StageSummary? Stage, IReadOnlyList<string> Hidden)
{
    public bool IsGap => Stage == null;
}

/// <summary>One computer of the case with what was found on it, by severity (counts indexed like <see cref="SpectreFinding.Severities"/>).</summary>
public sealed record HostSummary(string Host, int Findings, int Moments, int MaxSeverityRank, IReadOnlyList<int> BySeverity, IReadOnlyList<string>? Aliases = null)
{
    /// <summary>"Names seen in the records: A, B" when the records name the computer differently; "" otherwise.</summary>
    public string AliasLine => Aliases is { Count: > 0 } ? "Names seen in the records: " + string.Join(", ", Aliases) : "";
    public bool HasEvidence => Findings > 0 || Moments > 0;
    public string Severity => MaxSeverityRank >= 0 && MaxSeverityRank < SpectreFinding.Severities.Count ? SpectreFinding.Severities[MaxSeverityRank] : "";
}

/// <summary>One cell of the activity matrix: what was found on a computer at a stage. <see cref="MaxSeverityRank"/> is -1 when nothing was.</summary>
public sealed record MatrixCell(string Host, string Stage, int Findings, int Moments, int MaxSeverityRank)
{
    public bool HasEvidence => Findings > 0 || Moments > 0;
    /// <summary>What the cell shows: its findings, or its key events when the findings are not known.</summary>
    public int Count => Math.Max(Findings, Moments);
    public string Severity => MaxSeverityRank >= 0 && MaxSeverityRank < SpectreFinding.Severities.Count ? SpectreFinding.Severities[MaxSeverityRank] : "";
}

/// <summary>
/// Computers (rows, in <see cref="StoryGraphics.Hosts"/> order) by stages with evidence (columns, in reference order); <c>Cells[row][column]</c>.
/// </summary>
public sealed record ActivityMatrix(IReadOnlyList<HostSummary> Hosts, IReadOnlyList<StageSummary> Stages, IReadOnlyList<IReadOnlyList<MatrixCell>> Cells)
{
    public static readonly ActivityMatrix Empty = new(Array.Empty<HostSummary>(), Array.Empty<StageSummary>(), Array.Empty<IReadOnlyList<MatrixCell>>());
    public bool IsEmpty => Hosts.Count == 0 || Stages.Count == 0;
}

/// <summary>
/// The tactic radar's data: the 12 ATT&amp;CK tactics in kill-chain order (<see cref="StoryGraphics.RadarAxes"/>), the findings counted at each,
/// and the findings with no tactic (<see cref="Unmapped"/>, not an axis).
/// </summary>
public sealed record TacticRadarData(IReadOnlyList<string> Axes, IReadOnlyList<int> Counts, int Unmapped)
{
    public int Max => Counts.Count == 0 ? 0 : Counts.Max();
    public int Total => Counts.Sum();
    public int CountOf(string axis)
    {
        for (int i = 0; i < Axes.Count; i++)
            if (string.Equals(Axes[i], axis, StringComparison.OrdinalIgnoreCase)) return Counts[i];
        return 0;
    }
}

/// <summary>
/// Counts behind the Findings story graphics: the stage journey strip and the hosts map. The stage order is the ATT&amp;CK reference
/// order, not a proven sequence. Works without any dates.
/// </summary>
public static class StoryGraphics
{
    /// <summary>The ATT&amp;CK tactics in kill-chain order (reference order only). "Unmapped" comes last.</summary>
    public static readonly IReadOnlyList<string> StageOrder = new[]
    {
        "Initial Access", "Execution", "Persistence", "Privilege Escalation", "Defense Evasion", "Credential Access", "Discovery",
        "Lateral Movement", "Collection", "Command and Control", "Exfiltration", "Impact", PlainLanguage.OtherActivity,
    };

    public const string NoHost = "Unknown host";

    /// <summary>The radar's fixed axes: the 12 ATT&amp;CK tactics in kill-chain order ("Unmapped" is not one).</summary>
    public static IReadOnlyList<string> RadarAxes { get; } = StageOrder.Take(12).ToList();

    /// <summary>
    /// Findings per ATT&amp;CK tactic for the radar, optionally for one computer only (<paramref name="host"/>; null counts all).
    /// A finding counts under its own tactic; findings with none (or an unknown one) are counted apart as unmapped.
    /// </summary>
    public static TacticRadarData Radar(IEnumerable<SpectreTimelineFinding> findings, string? host = null, StoryHosts? resolved = null)
    {
        resolved ??= StoryHosts.None;
        var counts = new int[RadarAxes.Count];
        int unmapped = 0;
        foreach (var f in findings)
        {
            if (host != null && !string.Equals(resolved.ComputerOf(f), host, StringComparison.OrdinalIgnoreCase)) continue;
            int i = OrderOf(f.Phase ?? PlainLanguage.Phase(f.Tactic, f.Technique));
            if (i < counts.Length) counts[i]++; else unmapped++;
        }
        return new TacticRadarData(RadarAxes, counts, unmapped);
    }

    /// <summary>Segoe MDL2/Fluent glyph for a plain stage.</summary>
    public static string Glyph(string? stage) => StageOf(stage) switch
    {
        "Initial Access" => "",            // unlock
        "Execution" => "",      // command prompt
        "Persistence" => "",            // pin
        "Privilege Escalation" => "",       // admin
        "Defense Evasion" => "",       // delete
        "Credential Access" => "",    // key
        "Discovery" => "",        // search
        "Lateral Movement" => "", // connect
        "Collection" => "",        // folder
        "Command and Control" => "",        // globe
        "Exfiltration" => "",       // upload
        "Impact" => "",                // warning
        _ => "",                       // info
    };

    // ------------------------------------------------------------------ technique icons

    /// <summary>Technique icon and the tactics it is shown for. "T1059.*" matches every sub-technique; an exact id wins.</summary>
    private static readonly IReadOnlyDictionary<string, (string Glyph, string[] Tactics)> TechniqueIcons = new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
    {
        ["T1105"] = ("", new[] { "Command and Control" }),                                      // download (Ingress Tool Transfer)
        ["T1059.*"] = ("", new[] { "Execution" }),                                             // command prompt
        ["T1547.001"] = ("", new[] { "Persistence", "Privilege Escalation" }),                  // run key: play
        ["T1136.*"] = ("", new[] { "Persistence" }),                                           // add user
        ["T1053.*"] = ("", new[] { "Execution", "Persistence", "Privilege Escalation" }),       // scheduled task: clock
        ["T1543.*"] = ("", new[] { "Persistence", "Privilege Escalation" }),                    // service: gear
        ["T1569.*"] = ("", new[] { "Execution" }),
        ["T1548.*"] = ("", new[] { "Privilege Escalation", "Defense Evasion" }),                // admin
        ["T1027.*"] = ("", new[] { "Defense Evasion" }),                                       // code
        ["T1546.*"] = ("", new[] { "Persistence", "Privilege Escalation" }),                    // event-triggered: bolt
    };

    /// <summary>Technique ids out of "T1027, T1059.001" style text (or one id per item), upper-cased.</summary>
    public static IEnumerable<string> SplitTechniques(IEnumerable<string>? techniques)
    {
        if (techniques == null) yield break;
        foreach (var t in techniques)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (var part in t.Split(new[] { ',', ';', ' ', '	', '/' }, StringSplitOptions.RemoveEmptyEntries))
                yield return part.Trim().ToUpperInvariant();
        }
    }

    private static bool TryTechniqueIcon(string id, out (string Glyph, string[] Tactics) icon)
    {
        if (TechniqueIcons.TryGetValue(id, out icon)) return true;
        var dot = id.IndexOf('.');
        return TechniqueIcons.TryGetValue((dot > 0 ? id[..dot] : id) + ".*", out icon);
    }

    /// <summary>
    /// Glyph for a moment: the first technique with its own icon whose tactics include the shown stage, else the stage glyph.
    /// The shown label stays the tactic; only the picture follows the technique.
    /// </summary>
    public static string Icon(string? stage, IEnumerable<string>? techniques)
    {
        var st = StageOf(stage);
        foreach (var id in SplitTechniques(techniques))
            if (TryTechniqueIcon(id, out var ic) && ic.Tactics.Contains(st, StringComparer.OrdinalIgnoreCase)) return ic.Glyph;
        return Glyph(st);
    }

    /// <summary>Scene name for the slide: "Ingress Tool Transfer" for T1105 under Command and Control, else the stage.</summary>
    public static string SceneKey(string? stage, IEnumerable<string>? techniques)
    {
        var st = StageOf(stage);
        if (st == "Command and Control" && SplitTechniques(techniques).Any(t => t == "T1105" || t.StartsWith("T1105.", StringComparison.Ordinal))) return "Ingress Tool Transfer";
        return st;
    }

    /// <summary>Techniques of a moment's findings, the main finding's first.</summary>
    public static IReadOnlyList<string> MomentTechniques(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        var list = new List<string>();
        void Add(string? id)
        {
            if (id == null || !byId.TryGetValue(id, out var f)) return;
            foreach (var t in SplitTechniques(new[] { f.Technique ?? "" }))
                if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);   // one entry per technique id, first seen wins
        }
        Add(m.PrimaryFindingId);
        foreach (var id in m.FindingIds) Add(id);
        return list;
    }

    /// <summary>Position of a stage in <see cref="StageOrder"/>; unknown names count as "Unmapped".</summary>
    public static int OrderOf(string? stage)
    {
        for (int i = 0; i < StageOrder.Count; i++)
            if (string.Equals(StageOrder[i], PlainLanguage.MigratePhase(stage), StringComparison.OrdinalIgnoreCase)) return i;
        return StageOrder.Count - 1;
    }

    /// <summary>Normalised stage name of a finding or moment (empty or unknown gives "Unmapped").</summary>
    public static string StageOf(string? phase) => StageOrder[OrderOf(string.IsNullOrWhiteSpace(phase) ? null : phase.Trim())];

    /// <summary>Every stage in order with its finding and moment counts and highest severity (moments take their findings' severity).</summary>
    public static IReadOnlyList<StageSummary> Stages(IEnumerable<SpectreTimelineFinding> findings, IEnumerable<SpectreMoment> moments)
    {
        var fl = findings.ToList();
        var byId = ById(fl);
        var counts = new int[StageOrder.Count];
        var mcounts = new int[StageOrder.Count];
        var max = Enumerable.Repeat(-1, StageOrder.Count).ToArray();
        foreach (var f in fl)
        {
            var i = OrderOf(f.Phase ?? PlainLanguage.Phase(f.Tactic, f.Technique));
            counts[i]++;
            max[i] = Math.Max(max[i], f.SeverityRank);
        }
        foreach (var m in moments)
        {
            var i = OrderOf(m.Phase);
            mcounts[i]++;
            max[i] = Math.Max(max[i], MomentSeverityRank(m, byId));
        }
        return StageOrder.Select((s, i) => new StageSummary(s, i, counts[i], mcounts[i], max[i])).ToList();
    }

    /// <summary>
    /// The strip to draw: stages with evidence plus their direct neighbours (as context); runs of other stages fold into one gap.
    /// When that is more than <paramref name="maxItems"/>, the neighbours are dropped. "Unmapped" shows only when it has evidence.
    /// </summary>
    public static IReadOnlyList<StageStripItem> Strip(IReadOnlyList<StageSummary> stages, int maxItems = 11)
    {
        var main = stages.Where(s => s.Stage != PlainLanguage.OtherActivity).OrderBy(s => s.Order).ToList();
        var other = stages.FirstOrDefault(s => s.Stage == PlainLanguage.OtherActivity && s.HasEvidence);
        if (!main.Any(s => s.HasEvidence))
            return other != null ? new[] { new StageStripItem(other, Array.Empty<string>()) } : Array.Empty<StageStripItem>();

        var withNeighbours = Build(main, other, neighbours: true);
        return withNeighbours.Count <= maxItems ? withNeighbours : Build(main, other, neighbours: false);
    }

    private static List<StageStripItem> Build(List<StageSummary> main, StageSummary? other, bool neighbours)
    {
        var keep = new bool[main.Count];
        for (int i = 0; i < main.Count; i++)
        {
            if (!main[i].HasEvidence) continue;
            keep[i] = true;
            if (neighbours) { if (i > 0) keep[i - 1] = true; if (i + 1 < main.Count) keep[i + 1] = true; }
        }
        var items = new List<StageStripItem>();
        var hidden = new List<string>();
        for (int i = 0; i < main.Count; i++)
        {
            if (keep[i])
            {
                if (hidden.Count > 0 && items.Count > 0) items.Add(new StageStripItem(null, hidden.ToList()));
                hidden.Clear();
                items.Add(new StageStripItem(main[i], Array.Empty<string>()));
            }
            else hidden.Add(main[i].Stage);
        }
        if (other != null) items.Add(new StageStripItem(other, Array.Empty<string>()));
        return items;
    }

    /// <summary>Each computer with its counts by severity (most severe first, then name); findings without a host go to "Unknown host".</summary>
    public static IReadOnlyList<HostSummary> Hosts(IEnumerable<SpectreTimelineFinding> findings, IEnumerable<SpectreMoment> moments, StoryHosts? resolved = null)
    {
        var fl = findings.ToList();
        resolved ??= StoryHosts.None;
        var byId = ById(fl);
        var map = new Dictionary<string, (int f, int m, int max, int[] sev)>(StringComparer.OrdinalIgnoreCase);
        (int f, int m, int max, int[] sev) Get(string h) => map.TryGetValue(h, out var v) ? v : (0, 0, -1, new int[SpectreFinding.Severities.Count]);
        foreach (var f in fl)
        {
            var h = resolved.ComputerOf(f);
            var v = Get(h);
            if (f.SeverityRank >= 0) v.sev[f.SeverityRank]++;
            map[h] = (v.f + 1, v.m, Math.Max(v.max, f.SeverityRank), v.sev);
        }
        foreach (var m in moments)
        {
            var h = resolved.ComputerOf(m, byId);
            var v = Get(h);
            map[h] = (v.f, v.m + 1, Math.Max(v.max, MomentSeverityRank(m, byId)), v.sev);
        }
        return map.Select(kv => new HostSummary(kv.Key, kv.Value.f, kv.Value.m, kv.Value.max, kv.Value.sev, resolved.AliasesOf(kv.Key)))
                  .OrderByDescending(h => h.MaxSeverityRank).ThenBy(h => h.Host == NoHost).ThenBy(h => h.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The activity matrix: every computer with evidence by every stage with evidence. A cell counts the findings on that computer at that
    /// stage and the key events there (a moment's computer and severity come from its findings); empty cells have nothing found.
    /// </summary>
    public static ActivityMatrix Matrix(IEnumerable<SpectreTimelineFinding> findings, IEnumerable<SpectreMoment> moments, StoryHosts? resolved = null)
    {
        var fl = findings.ToList();
        var ml = moments.ToList();
        resolved ??= StoryHosts.None;
        var byId = ById(fl);
        var hosts = Hosts(fl, ml, resolved).Where(h => h.HasEvidence).ToList();
        var stages = Stages(fl, ml).Where(s => s.HasEvidence).OrderBy(s => s.Order).ToList();
        if (hosts.Count == 0 || stages.Count == 0) return ActivityMatrix.Empty;
        var row = hosts.Select((h, i) => (h.Host, i)).ToDictionary(x => x.Host, x => x.i, StringComparer.OrdinalIgnoreCase);
        var col = stages.Select((s, i) => (s.Stage, i)).ToDictionary(x => x.Stage, x => x.i, StringComparer.OrdinalIgnoreCase);
        var f = new int[hosts.Count, stages.Count];
        var m = new int[hosts.Count, stages.Count];
        var max = new int[hosts.Count, stages.Count];
        for (int r = 0; r < hosts.Count; r++) for (int c = 0; c < stages.Count; c++) max[r, c] = -1;
        foreach (var x in fl)
        {
            if (!row.TryGetValue(resolved.ComputerOf(x), out var r) || !col.TryGetValue(StageOf(x.Phase ?? PlainLanguage.Phase(x.Tactic, x.Technique)), out var c)) continue;
            f[r, c]++;
            max[r, c] = Math.Max(max[r, c], x.SeverityRank);
        }
        foreach (var x in ml)
        {
            if (!row.TryGetValue(resolved.ComputerOf(x, byId), out var r) || !col.TryGetValue(StageOf(x.Phase), out var c)) continue;
            m[r, c]++;
            max[r, c] = Math.Max(max[r, c], MomentSeverityRank(x, byId));
        }
        var cells = hosts.Select((h, r) => (IReadOnlyList<MatrixCell>)stages.Select((s, c) => new MatrixCell(h.Host, s.Stage, f[r, c], m[r, c], max[r, c])).ToList()).ToList();
        return new ActivityMatrix(hosts, stages, cells);
    }

    /// <summary>The severity levels (ranks into <see cref="SpectreFinding.Severities"/>) that have at least one finding on these computers, highest first.</summary>
    public static IReadOnlyList<int> SeverityColumns(IEnumerable<HostSummary> hosts)
    {
        var hl = hosts.ToList();
        return Enumerable.Range(0, SpectreFinding.Severities.Count).Reverse()
            .Where(r => hl.Any(h => r < h.BySeverity.Count && h.BySeverity[r] > 0)).ToList();
    }

    /// <summary>The ranks (into <see cref="SpectreFinding.Severities"/>) of a moment's findings; a moment with none listed reports no ranks.</summary>
    public static IReadOnlySet<int> MomentSeverityRanks(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        var set = new HashSet<int>();
        foreach (var id in m.FindingIds)
            if (byId.TryGetValue(id, out var f) && f.SeverityRank >= 0) set.Add(f.SeverityRank);
        return set;
    }

    /// <summary>The computer a moment belongs to: its primary finding's host, else the first listed finding with one.</summary>
    public static string MomentHost(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId, StoryHosts? resolved = null)
    {
        if (resolved != null) return resolved.ComputerOf(m, byId);
        if (m.PrimaryFindingId != null && byId.TryGetValue(m.PrimaryFindingId, out var p) && !string.IsNullOrWhiteSpace(p.Host)) return p.Host!.Trim();
        foreach (var id in m.FindingIds)
            if (byId.TryGetValue(id, out var f) && !string.IsNullOrWhiteSpace(f.Host)) return f.Host!.Trim();
        return NoHost;
    }

    public static string HostName(string? host) => string.IsNullOrWhiteSpace(host) ? NoHost : host.Trim();

    /// <summary>Findings by both their timeline id and finding id.</summary>
    public static IReadOnlyDictionary<string, SpectreTimelineFinding> ById(IEnumerable<SpectreTimelineFinding> findings)
    {
        var d = new Dictionary<string, SpectreTimelineFinding>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.Id.Length > 0) d.TryAdd(f.Id, f);
            if (f.FindingId.Length > 0) d.TryAdd(f.FindingId, f);
        }
        return d;
    }

    private static int MomentSeverityRank(SpectreMoment m, IReadOnlyDictionary<string, SpectreTimelineFinding> byId)
    {
        int r = -1;
        if (m.PrimaryFindingId != null && byId.TryGetValue(m.PrimaryFindingId, out var p)) r = p.SeverityRank;
        foreach (var id in m.FindingIds)
            if (byId.TryGetValue(id, out var f)) r = Math.Max(r, f.SeverityRank);
        return r;
    }
}
