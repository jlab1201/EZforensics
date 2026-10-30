using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;
using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Triage;

/// <summary>
/// The Ingest steps planned around the tool jobs: Computer details (the first row, queued before every other job), the filesystem MACB
/// timelines (one per host with an $MFT row, built from the $MFT after the tool jobs) and the plaso super timelines, one per host (the last
/// rows, started once every other job of the run has finished, one plaso run at a time). Their rows are placeholders:
/// <see cref="JobSpec.Args"/> holds the source folders (the $MFT files for a MACB row), and the real jobs (one computer details job per
/// system; MFTECmd per $MFT; log2timeline + psort per timeline) are built at run time.
/// </summary>
public static class IngestSteps
{
    public const string MacbTimelineRuleId = "ingest-macb-timeline";
    public const string MacbTimelineLabel = "Filesystem timeline (MACB)";
    public const string MacbToolId = "MFTECmd";
    public const string MacbExeName = "MFTECmd.exe";
    /// <summary>Rule id of the $MFT rows (<c>Resources/triage/rules.json</c>) a MACB timeline is built from.</summary>
    public const string MftRuleId = "mft";
    public const string ComputerDetailsRuleId = "ingest-computer-details";
    public const string SupertimelineRuleId = "ingest-supertimeline";
    public const string ComputerDetailsLabel = "Computer details (offline registry)";
    public const string SupertimelineLabel = "Supertimeline (plaso)";
    public const string SupertimelineToolId = "plaso";
    /// <summary>Rule id of the event log rows (<c>Resources/triage/rules.json</c>): EvtxECmd output is a process source.</summary>
    public const string EvtxRuleId = "evtx";

    public static bool IsComputerDetails(PlannedJob p) => p.Artifact.RuleId == ComputerDetailsRuleId;
    public static bool IsSupertimeline(PlannedJob p) => p.Artifact.RuleId == SupertimelineRuleId;
    public static bool IsMacbTimeline(PlannedJob p) => p.Artifact.RuleId == MacbTimelineRuleId;
    /// <summary>A step row (not a tool job of the plan).</summary>
    public static bool IsStep(PlannedJob p) => IsComputerDetails(p) || IsMacbTimeline(p) || IsSupertimeline(p);

    /// <summary>The source folders a step row reads (its <see cref="JobSpec.Args"/>).</summary>
    public static IReadOnlyList<string> Sources(PlannedJob p) => p.Spec.Args;

    /// <summary>Computer details of the source: <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo</c> per system (ticked by default).</summary>
    public static PlannedJob ComputerDetails(string caseDir, IReadOnlyList<string> sources) =>
        Row(ComputerDetailsRuleId, ComputerDetailsStore.ToolId, ComputerDetailsLabel, "SystemInfo", caseDir, sources, selected: true);

    /// <summary>Label of the super timeline row of one host folder: "Supertimeline (plaso): WS01".</summary>
    public static string SupertimelineLabelFor(string hostFolder) => $"{SupertimelineLabel}: {HostDisplay(hostFolder)}";
    /// <summary>What the label and messages of the <c>_Case</c> super timeline name instead of a host.</summary>
    public const string UnattributedHostLabel = "unattributed evidence";
    /// <summary>A host folder as the timeline row and messages show it (<c>_Case</c> = <see cref="UnattributedHostLabel"/>).</summary>
    public static string HostDisplay(string hostFolder) => Case.CaseIndex.IsCaseWideFolder(hostFolder) ? UnattributedHostLabel : hostFolder;

    /// <summary>Label of the MACB timeline row of one host folder: "Filesystem timeline (MACB): WS01".</summary>
    public static string MacbTimelineLabelFor(string hostFolder) => $"{MacbTimelineLabel}: {HostDisplay(hostFolder)}";

    /// <summary>The host folder a super timeline or MACB timeline row writes to (<c>_Case</c> for evidence of no identified system).</summary>
    public static string SupertimelineHost(PlannedJob p) => p.Spec.Provenance?.Host is { Length: > 0 } h ? h : Case.CaseIndex.CaseWideFolder;

    /// <summary>
    /// The plaso super timelines of the plan, one per host: every system with disk rows (tool jobs reading its files, not memory, network
    /// or RITA rows) gets a row reading its root into <c>&lt;case&gt;\&lt;Host&gt;\Timeline\SuperTimeline_plaso_*.csv</c>; disk rows with no
    /// system at all add one row over the sources that hold no system, into <c>&lt;case&gt;\_Case\Timeline</c>. A system with only memory
    /// images or packet captures gets none. Not ticked by default (not part of the fast-forensics set, see <see cref="IngestDefaults"/>).
    /// </summary>
    public static List<PlannedJob> Supertimelines(string caseDir, IReadOnlyList<PlannedJob> jobs, IReadOnlyList<string> sources)
    {
        var hosts = new List<(string Host, List<string> Roots, DiscoveredSystem? System)>();
        bool unattributed = false;
        foreach (var j in jobs.Where(IsDiskRow))
        {
            var root = RootOf(j);
            if (string.IsNullOrEmpty(root)) { unattributed = true; continue; }
            var host = j.Spec.Provenance?.Host is { Length: > 0 } h ? h : TriagePlanner.HostOf(j.Artifact, root);
            var i = hosts.FindIndex(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            if (i < 0) hosts.Add((host, new List<string> { root }, j.Artifact.System));
            else if (!hosts[i].Roots.Contains(root, StringComparer.OrdinalIgnoreCase)) hosts[i].Roots.Add(root);
        }
        var rows = hosts.Select(x => TimelineRow(caseDir, x.Host, x.Roots, x.System)).ToList();
        if (unattributed)
        {
            var roots = hosts.SelectMany(x => x.Roots).ToList();
            var loose = sources.Where(s => !roots.Any(r => IsWithin(r, s) || IsWithin(s, r))).ToList();
            if (loose.Count > 0) rows.Add(TimelineRow(caseDir, Case.CaseIndex.CaseWideFolder, loose, null));
        }
        return rows;
    }

    /// <summary>
    /// The filesystem MACB timelines of the plan, one per host: every host with an <c>mft</c> row ($MFT of one of its systems) gets a row
    /// whose <see cref="JobSpec.Args"/> are that host's $MFT files, writing <c>&lt;case&gt;\&lt;Host&gt;\Timeline\Timeline_MACB_*.csv</c>.
    /// Ticked by default (part of <see cref="IngestDefaults.FastForensicsRuleIds"/>).
    /// </summary>
    public static List<PlannedJob> MacbTimelines(string caseDir, IReadOnlyList<PlannedJob> jobs)
    {
        var hosts = new List<(string Host, List<string> Mfts, DiscoveredSystem? System)>();
        foreach (var j in jobs.Where(j => !IsStep(j) && j.Artifact.RuleId == MftRuleId))
        {
            var mft = j.Artifact.MatchedPath;
            if (string.IsNullOrEmpty(mft)) continue;
            var host = j.Spec.Provenance?.Host is { Length: > 0 } h ? h : TriagePlanner.HostOf(j.Artifact, RootOf(j));
            var i = hosts.FindIndex(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            if (i < 0) hosts.Add((host, new List<string> { mft }, j.Artifact.System));
            else if (!hosts[i].Mfts.Contains(mft, StringComparer.OrdinalIgnoreCase)) hosts[i].Mfts.Add(mft);
        }
        return hosts.Select(x => MacbRow(caseDir, x.Host, x.Mfts, x.System)).ToList();
    }

    /// <summary>
    /// The host folders (absolute) of the plan that have a process source (the <c>CanShowProcessTree</c> rule): a host qualifies when the plan has an event log or
    /// memory row for it, or when its case folder already holds a pslist / psscan / pstree or EventLogs CSV. Not an Ingest row: the App queues one
    /// <see cref="Processes.ProcessCorrelator.Spec"/> job per returned folder after the run (skipping those where <see cref="Processes.ProcessCorrelator.IsUpToDate"/>).
    /// </summary>
    public static List<string> ProcessHosts(string caseDir, IReadOnlyList<PlannedJob> jobs)
    {
        var hosts = new List<(string Host, bool Source)>();
        foreach (var j in jobs.Where(j => !IsStep(j)))
        {
            var host = j.Spec.Provenance?.Host is { Length: > 0 } h ? h : TriagePlanner.HostOf(j.Artifact, RootOf(j));
            if (string.IsNullOrEmpty(host)) continue;
            bool source = j.Artifact.RuleId is EvtxRuleId or MemoryTriage.RuleId;
            var i = hosts.FindIndex(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            if (i < 0) hosts.Add((host, source));
            else hosts[i] = (host, hosts[i].Source || source);
        }
        return hosts.Where(x => x.Source || Processes.ProcessCorrelator.HasProcessSource(Path.Combine(caseDir, x.Host)))
            .Select(x => Path.Combine(caseDir, x.Host)).ToList();
    }

    private static PlannedJob MacbRow(string caseDir, string host, IReadOnlyList<string> mfts, DiscoveredSystem? system)
    {
        var label = MacbTimelineLabelFor(host);
        var outDir = Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, host, Timeline.TimelineFiles.TimelineFolderName);
        var spec = new JobSpec(MacbToolId, MacbExeName, mfts.ToList(), outDir, label, "Timeline")
        {
            Provenance = new JobProvenance
            {
                RuleId = MacbTimelineRuleId, Host = host, HostNameSource = system?.HostNameSource, SystemRoot = system?.RootPath,
                AcquisitionKind = system?.AcquisitionKind.ToString(), HashInputs = false, Archive = system?.Archive,
            },
        };
        var artifact = new DetectedArtifact(MacbTimelineRuleId, label, MacbToolId, "Timeline", PlasoRunOptions.JoinSources(mfts), false,
            SystemRoot: system != null ? system.RootPath : "", System: system);
        return new PlannedJob(spec, true, artifact, false);
    }

    private static PlannedJob TimelineRow(string caseDir, string host, IReadOnlyList<string> sources, DiscoveredSystem? system)
    {
        var label = SupertimelineLabelFor(host);
        var outDir = Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, host, Timeline.TimelineFiles.TimelineFolderName);
        var root = sources.Count == 1 && system != null ? sources[0] : "";
        var spec = new JobSpec(SupertimelineToolId, SupertimelineToolId, sources.ToList(), outDir, label, "Timeline")
        {
            Provenance = new JobProvenance
            {
                RuleId = SupertimelineRuleId, Host = host, HostNameSource = system?.HostNameSource, SystemRoot = root.Length > 0 ? root : null,
                AcquisitionKind = system?.AcquisitionKind.ToString(), HashInputs = false, Archive = system?.Archive,
            },
        };
        var artifact = new DetectedArtifact(SupertimelineRuleId, label, SupertimelineToolId, "Timeline", PlasoRunOptions.JoinSources(sources), false,
            SystemRoot: system != null ? system.RootPath : "", System: system);
        return new PlannedJob(spec, false, artifact, false);
    }

    /// <summary>A tool job reading a system's files (not a step, memory, network or RITA row).</summary>
    private static bool IsDiskRow(PlannedJob p) =>
        !IsStep(p) && p.Artifact.RuleId is not (MemoryTriage.RuleId or NetworkTriage.RuleId or Rita.RitaJobs.RuleId);

    private static string RootOf(PlannedJob p) =>
        p.Artifact.System?.RootPath is { Length: > 0 } r ? r
        : !string.IsNullOrEmpty(p.Artifact.SystemRoot) ? p.Artifact.SystemRoot
        : p.Spec.Provenance?.SystemRoot ?? "";

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or lies under it (ignoring case and a trailing separator).</summary>
    private static bool IsWithin(string path, string folder)
    {
        static string N(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            return p.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        return N(path).StartsWith(N(folder), StringComparison.OrdinalIgnoreCase);
    }

    private static PlannedJob Row(string ruleId, string toolId, string label, string category, string outDir, IReadOnlyList<string> sources, bool selected)
    {
        var shown = PlasoRunOptions.JoinSources(sources);
        var spec = new JobSpec(toolId, toolId, sources.ToList(), outDir, label, category);
        return new PlannedJob(spec, selected, new DetectedArtifact(ruleId, label, toolId, category, shown, false), false);
    }

    /// <summary>
    /// The full plan: Computer details first, the tool jobs, the MACB timelines (one per host with an $MFT row, see <see cref="MacbTimelines"/>), then the super timelines, one per host (only when plaso is available, see
    /// <see cref="Supertimelines"/>). No step is added without tool jobs or without a source folder. Step rows already in
    /// <paramref name="jobs"/> are replaced. Every row's default tick is <see cref="IngestDefaults.IsDefault"/> (the SANS fast-forensics disk set only).
    /// </summary>
    public static List<PlannedJob> Compose(IReadOnlyList<PlannedJob> jobs, string caseDir, IReadOnlyList<string> sources, bool plasoAvailable)
    {
        var tools = jobs.Where(j => !IsStep(j)).Select(IngestDefaults.Apply).ToList();
        if (tools.Count == 0 || sources.Count == 0) return tools;
        var plan = new List<PlannedJob> { ComputerDetails(caseDir, sources) };
        plan.AddRange(tools);
        plan.AddRange(MacbTimelines(caseDir, tools));
        if (plasoAvailable) plan.AddRange(Supertimelines(caseDir, tools, sources));
        return plan;
    }

    /// <summary>Stable order of a plan: Computer details rows first, then the tool rows, the MACB timeline rows, the super timeline rows.</summary>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, PlannedJob> planned) =>
        items.Select((x, i) => (x, i)).OrderBy(t => Rank(planned(t.x))).ThenBy(t => t.i).Select(t => t.x).ToList();

    private static int Rank(PlannedJob p) => IsComputerDetails(p) ? 0 : IsMacbTimeline(p) ? 2 : IsSupertimeline(p) ? 3 : 1;
}
