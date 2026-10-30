using EzForensics.Core.Execution;
using EzForensics.Core.Network;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Triage;

/// <summary>
/// Network part of triage: packet captures found in the source (<see cref="PcapScanner"/>) become one Zeek job each
/// (case layout v2: <c>&lt;case&gt;\&lt;Host&gt;\Network\Zeek_&lt;Log&gt;_&lt;capture&gt;.csv</c>), ticked by default like the other planned jobs.
/// </summary>
public static class NetworkTriage
{
    public const string RuleId = "pcap";

    /// <summary>
    /// One planned Zeek job per capture. With <paramref name="install"/> null (Zeek not set up yet) the jobs are still listed
    /// so the user sees them; their spec is a placeholder (no <see cref="JobSpec.ExePath"/>) that <see cref="Resolve"/>
    /// replaces once Zeek is available.
    /// </summary>
    /// <summary>Layout v2 base name: <c>Zeek_&lt;Log&gt;_&lt;capture&gt;.csv</c> (and <c>.log</c>) in <c>&lt;case&gt;\&lt;Host&gt;\Network</c>.</summary>
    public const string OutputName = "Zeek";

    /// <param name="systems">Discovered systems of the source (with their interface IPs).</param>
    /// <param name="evidence">Host evidence of a capture (<see cref="PcapHostEvidence"/>), asked only for captures not under a system root;
    /// null or a null result = no evidence (the capture is then case-wide unless it sits under a system root).</param>
    /// <param name="caseHosts">Hosts of an earlier ingest of the case (<see cref="SystemInfo.ComputerDetailsStore.KnownHosts"/>).</param>
    /// <param name="options">Zeek options of the planned jobs (the user's additional arguments); null = defaults.</param>
    /// <remarks>
    /// Each capture goes to the host it was collected on (<see cref="PcapHostMatcher"/>); a capture with no identified host (e.g. a router or
    /// firewall capture) goes to the case-wide folder <c>&lt;case&gt;\_Case\Network</c>.
    /// </remarks>
    public static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<PcapFile> pcaps, string caseDir, ZeekInstall? install, string? sourceRoot = null,
        IReadOnlyList<DiscoveredSystem>? systems = null, Func<string, PcapEvidence?>? evidence = null, IReadOnlyList<SystemInfo.KnownHost>? caseHosts = null,
        ZeekRunOptions? options = null)
    {
        var list = new List<PlannedJob>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = Candidates(systems, caseHosts);
        foreach (var p in pcaps)
        {
            var match = MatchHost(p.Path, candidates, evidence);
            var system = match.Host?.System;
            var host = match.Host == null ? RitaJobs.CaseWideHost : TriagePlanner.HostFolder(match.Host.HostName);
            var finalDir = Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, host, ZeekJobs.Category);
            var qualifier = TriagePlanner.UniqueQualifier(used, host, RuleId, ArtifactNaming.Qualifier(Path.GetFileNameWithoutExtension(p.Path)) ?? "capture");
            var staging = TriagePlanner.StagingDir(caseDir, host, RuleId, qualifier, p.Path);
            var how = match.Host == null ? $" · case-wide: {match.Method}" : $" · host {host} ({match.Method})";
            var label = $"Packet capture ({p.FormatLabel}, {FormatSize(p.Size)}): {RelativeTo(p.Path, sourceRoot)}{how}";
            JobSpec spec;
            try { spec = install != null ? ZeekJobs.CreateSpec(install, p.Path, staging, options, label) : Placeholder(p.Path, staging, label); }
            catch (ArgumentException) { spec = Placeholder(p.Path, staging, label); } // e.g. a UNC path WSL cannot read: reported at run time
            spec = spec with
            {
                OutputDir = finalDir,
                Output = new OutputPlan(staging, OutputName, "zeek", qualifier),
                Provenance = new JobProvenance
                {
                    RuleId = RuleId, ArtifactId = "zeek", Host = host, HostNameSource = system?.HostNameSource, SystemRoot = system?.RootPath,
                    AcquisitionKind = system?.AcquisitionKind.ToString(), OsFamily = system?.OsFamily.ToString(), Archive = system?.Archive,
                    Inputs = new[] { new InputSelector(p.Path) }, HashInputs = false,   // the Zeek job hashes the capture itself (input.sha256)
                },
            };
            var artifact = new DetectedArtifact(RuleId, "Packet capture", ZeekJobs.ToolId, ZeekJobs.Category, p.Path, true, 1, system?.RootPath ?? sourceRoot ?? "", System: system);
            list.Add(new PlannedJob(spec, true, artifact, false));
        }
        return list;
    }

    /// <summary>The source's systems plus the case's earlier hosts (merged by host folder: the IPs and names of both).</summary>
    public static IReadOnlyList<PcapHostCandidate> Candidates(IReadOnlyList<DiscoveredSystem>? systems, IReadOnlyList<SystemInfo.KnownHost>? caseHosts)
    {
        var list = (systems ?? Array.Empty<DiscoveredSystem>()).Select(PcapHostCandidate.From).ToList();
        foreach (var k in caseHosts ?? Array.Empty<SystemInfo.KnownHost>())
        {
            if (Case.CaseIndex.IsCaseWideFolder(k.Host)) continue;
            int i = list.FindIndex(c => string.Equals(TriagePlanner.HostFolder(c.HostName), k.Host, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                list[i] = list[i] with
                {
                    IpAddresses = list[i].IpAddresses.Concat(k.IpAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Names = list[i].Names.Concat(k.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            else list.Add(new PcapHostCandidate(k.Host, null, k.IpAddresses) { Names = k.Names });
        }
        return list;
    }

    /// <summary>True when the capture sits under one of the systems' roots (its evidence need not be read).</summary>
    public static bool IsUnderSystem(string pcap, IReadOnlyList<DiscoveredSystem>? systems) =>
        systems?.Any(s => PcapHostMatcher.IsUnder(pcap, s.RootPath)) == true;

    private static PcapHostMatch MatchHost(string pcap, IReadOnlyList<PcapHostCandidate> candidates, Func<string, PcapEvidence?>? evidence)
    {
        var byLocation = PcapHostMatcher.Match(pcap, null, candidates);
        if (byLocation.Host != null || evidence == null) return byLocation;
        PcapEvidence? e;
        try { e = evidence(pcap); }
        catch (Exception ex) when (ex is not OperationCanceledException) { e = null; }
        return PcapHostMatcher.Match(pcap, e, candidates);
    }

    /// <summary>
    /// Reads the host evidence of every capture that is not under a system root (<see cref="PcapHostEvidence"/>, at most
    /// <paramref name="perCapture"/> each, default 5 s, 4 at a time); slow I/O: call it off the UI thread.
    /// </summary>
    public static Dictionary<string, PcapEvidence> ReadEvidence(IEnumerable<PcapFile> pcaps, IReadOnlyList<DiscoveredSystem>? systems,
        TimeSpan? perCapture = null, CancellationToken ct = default)
    {
        var options = new PcapEvidenceOptions { MaxTime = perCapture ?? TimeSpan.FromSeconds(5) };
        var todo = pcaps.Where(p => !IsUnderSystem(p.Path, systems)).Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, PcapEvidence>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            p => result[p] = PcapHostEvidence.Read(p, options, ct));
        return new Dictionary<string, PcapEvidence>(result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for a Zeek job planned before Zeek was available.</summary>
    public static bool IsPlaceholder(JobSpec spec) => spec.ToolId == ZeekJobs.ToolId && spec.ExePath == null;

    /// <summary>The real job for a planned Zeek entry (same output folder, staging folder, naming and label) with the current install.</summary>
    /// <param name="options">Zeek options (the user's additional arguments); null = defaults.</param>
    public static JobSpec Resolve(PlannedJob planned, ZeekInstall install, ZeekRunOptions? options = null)
    {
        var p = planned.Spec;
        var spec = ZeekJobs.CreateSpec(install, planned.Artifact.MatchedPath, p.Output?.StagingDir ?? p.OutputDir, options, p.Label);
        return p.Output == null ? spec : spec with { OutputDir = p.OutputDir, Output = p.Output, Provenance = p.Provenance };
    }

    private static JobSpec Placeholder(string pcap, string outDir, string label) =>
        new(ZeekJobs.ToolId, "zeek", new[] { "-C", "-r", pcap }, outDir, label, ZeekJobs.Category);

    // ------------------------------------------------------------------ RITA (after Zeek)

    /// <summary>True for a planned RITA row (<see cref="PlanRita"/>).</summary>
    public static bool IsRita(PlannedJob planned) => planned.Artifact.RuleId == RitaJobs.RuleId;

    /// <summary>True for the RITA row of the case-wide folder (<c>&lt;case&gt;\_Case\Network</c>: the captures with no identified host).</summary>
    public static bool IsCaseWideRita(PlannedJob planned) => IsRita(planned) &&
        Case.CaseIndex.IsCaseWideFolder(planned.Spec.Provenance?.Host ?? planned.Artifact.MatchedPath);

    /// <summary>
    /// RITA rows for the planned Zeek jobs: one per Zeek host folder (its captures), the case-wide <c>_Case</c> folder included (the
    /// captures with no identified host); there is no analysis over all captures. A RITA job waits for Zeek jobs that only exist once the
    /// run starts, so these rows are always placeholders (no <see cref="JobSpec.ExePath"/>) that <see cref="ResolveRita"/> turns into the
    /// real job at run time. Ticked by default when RITA is installed (<paramref name="install"/> not null); unticked otherwise.
    /// </summary>
    public static IReadOnlyList<PlannedJob> PlanRita(IReadOnlyList<PlannedJob> zeekPlan, string caseDir, RitaInstall? install)
    {
        var zeek = zeekPlan.Where(p => p.Artifact.RuleId == RuleId).ToList();
        var list = new List<PlannedJob>();
        if (zeek.Count == 0) return list;
        bool on = install != null;
        foreach (var g in zeek.GroupBy(HostOf, StringComparer.OrdinalIgnoreCase))
        {
            int n = g.Count();
            var captures = $"{n} capture{(n == 1 ? "" : "s")}";
            bool caseWide = Case.CaseIndex.IsCaseWideFolder(g.Key);
            var label = caseWide
                ? $"RITA beacons / long connections / DNS tunneling: case-wide ({captures} with no identified host, after Zeek)"
                : $"RITA beacons / long connections / DNS tunneling: {g.Key} ({captures}, after Zeek)";
            list.Add(RitaRow(caseDir, caseWide ? null : g.Key, label, n, on, caseWide ? null : g.First().Artifact.System));
        }
        return list;
    }

    private static string HostOf(PlannedJob zeek) => ZeekHost(zeek);

    /// <summary>The host folder of a planned Zeek row (the per-host RITA row of that host reads its output).</summary>
    public static string ZeekHost(PlannedJob zeek) => zeek.Spec.Provenance?.Host is { Length: > 0 } h ? h : RitaJobs.CaseWideHost;

    private static PlannedJob RitaRow(string caseDir, string? host, string label, int captures, bool selected, DiscoveredSystem? system)
    {
        var folder = host ?? RitaJobs.CaseWideHost;
        var outDir = Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, folder, RitaJobs.Category);
        var spec = new JobSpec(RitaJobs.ToolId, "rita", Array.Empty<string>(), outDir, label, RitaJobs.Category)
        {
            Provenance = new JobProvenance { RuleId = RitaJobs.RuleId, ArtifactId = RitaJobs.DefaultArtifactId, Host = folder, HashInputs = false },
        };
        var artifact = new DetectedArtifact(RitaJobs.RuleId, "RITA analysis", RitaJobs.ToolId, RitaJobs.Category, folder, false, captures,
            system?.RootPath ?? "", System: system);
        return new PlannedJob(spec, selected, artifact, false);
    }

    /// <summary>The Zeek jobs of this run a RITA row analyses: those of its host folder (<c>_Case</c> included). Empty when the row has nothing to wait for.</summary>
    public static IReadOnlyList<Job> ZeekJobsFor(PlannedJob rita, IEnumerable<(PlannedJob Planned, Job Job)> zeekRun)
    {
        var run = zeekRun.Where(z => z.Planned.Artifact.RuleId == RuleId).ToList();
        var host = rita.Spec.Provenance?.Host ?? rita.Artifact.MatchedPath;
        return run.Where(z => string.Equals(HostOf(z.Planned), host, StringComparison.OrdinalIgnoreCase)).Select(z => z.Job).ToList();
    }

    /// <summary>The real RITA job for a planned row: waits for <paramref name="zeekJobs"/> (already enqueued); same output folder, label and provenance.</summary>
    public static JobSpec ResolveRita(PlannedJob planned, RitaInstall install, string caseDir, IReadOnlyList<Job> zeekJobs, RitaOptions options)
    {
        var p = planned.Spec;
        long hint = 0;
        foreach (var j in zeekJobs)
            foreach (var input in j.Spec.Provenance?.Inputs ?? Array.Empty<InputSelector>())
            {
                try { if (File.Exists(input.Path)) hint += new FileInfo(input.Path).Length; } catch (Exception) { }
            }
        return RitaJobs.CreateSpec(install, new RitaJobRequest
        {
            CaseDir = caseDir,
            Host = IsCaseWideRita(planned) ? null : p.Provenance?.Host ?? planned.Artifact.MatchedPath,
            OutputDir = p.OutputDir,
            ZeekJobs = zeekJobs,
            Options = options,
            InputBytesHint = hint,
            Label = p.Label,
            Provenance = p.Provenance,
        });
    }

    public static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes / 1024.0):0} KB";

    private static string RelativeTo(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return Path.GetFileName(path);
        var r = root.TrimEnd('\\', '/');
        return path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) ? path[(r.Length + 1)..] : Path.GetFileName(path);
    }
}
