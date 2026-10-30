using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;

namespace EzForensics.App.Services;

/// <summary>
/// Computer details as a lightweight built-in job: EZ Forensics runs itself headless
/// (<c>EzForensics.exe --computer-details --source &lt;src&gt; --out &lt;dir&gt; [--host &lt;host&gt;]</c>) through the normal job queue, so the
/// step is logged like any tool, shows on the Jobs page, and the run manifest records its argv, exe hash and the SHA-256 of
/// ComputerDetails.json / .csv. No Execution code is involved beyond <see cref="JobQueue.Enqueue"/>.
/// Layout v2 case (<c>case.json</c>): one job per Windows system, written to <c>&lt;case&gt;\&lt;Host&gt;\SystemInfo</c>.
/// Older case: one job for the whole source, written to <c>&lt;case&gt;\SystemInfo</c>.
/// </summary>
public static class ComputerDetailsJob
{
    public const string Switch = "--computer-details";

    /// <summary>
    /// Queues the step(s) and returns the jobs queued (empty when none could be queued). <paramref name="systems"/> are the hosts to read
    /// (host labels as chosen on the Ingest page); null = discover them from <paramref name="source"/>.
    /// </summary>
    public static IReadOnlyList<Job> Enqueue(AppServices s, string source, string caseDir, IReadOnlyList<DiscoveredSystem>? systems = null) =>
        Enqueue(s, new[] { source }, caseDir, systems);

    /// <summary>
    /// Several source folders (e.g. the extracted roots of this ingest's archives). <paramref name="systems"/> null = discovered in each source,
    /// host names kept unique across them (as <see cref="ArtifactScanner.ScanSources"/> does). Older case: one job for the first source.
    /// </summary>
    public static IReadOnlyList<Job> Enqueue(AppServices s, IReadOnlyList<string> sources, string caseDir, IReadOnlyList<DiscoveredSystem>? systems = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return Array.Empty<Job>();
        if (!(CaseLayout.ReadMarker(caseDir) is { LayoutVersion: >= CaseLayout.CurrentVersion }))
        {
            var src = sources.FirstOrDefault() ?? systems?.FirstOrDefault()?.RootPath;
            return src == null ? Array.Empty<Job>() : new[] { EnqueueOne(s, exe, src, caseDir, ComputerDetailsStore.FolderFor(caseDir), null) };
        }

        systems ??= DiscoverAll(sources);
        var jobs = new List<Job>();
        // A host with a memory image but no registry hives: write its details from the windows.info output (cheap, no job needed).
        foreach (var sys in systems.Where(x => ComputerDetailsCollector.FindSystemRoots(x.RootPath).Count == 0))
        {
            try { MemoryDetailsProvider.WriteForMemoryOnlyHost(caseDir, TriagePlanner.HostFolder(sys.HostName)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var sys in systems.Where(x => ComputerDetailsCollector.FindSystemRoots(x.RootPath).Count > 0))
        {
            var host = TriagePlanner.HostFolder(sys.HostName);
            jobs.Add(EnqueueOne(s, exe, sys.RootPath, caseDir, ComputerDetailsStore.HostFolderFor(caseDir, host), host));
        }
        return jobs;
    }

    private static List<DiscoveredSystem> DiscoverAll(IReadOnlyList<string> sources)
    {
        var all = new List<DiscoveredSystem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources)
            foreach (var sys in ArtifactScanner.DiscoverSystems(src, null, 3, out _))
            {
                var name = sys.HostName;
                for (int i = 2; !used.Add(name); i++) name = $"{sys.HostName} ({i})";
                all.Add(sys with { HostName = name });
            }
        return all;
    }

    /// <summary>The systems recorded in the case's run manifests (exact system roots and host folders), as discovered systems.</summary>
    public static IReadOnlyList<DiscoveredSystem> SystemsFromManifests(string caseDir) =>
        ComputerDetailsStore.FindSystemsFromManifests(caseDir)
            .Select(m => new DiscoveredSystem { RootPath = m.RootPath, HostName = m.Host, HostNameSource = "run manifest" }).ToList();

    private static Job EnqueueOne(AppServices s, string exe, string source, string caseDir, string outDir, string? host)
    {
        var args = new List<string> { Switch, "--source", source, "--out", outDir };
        if (host != null) { args.Add("--host"); args.Add(host); }
        var spec = new JobSpec(
            ComputerDetailsStore.ToolId, Path.GetFileName(exe), args,
            outDir, host == null ? "Computer details (offline registry)" : $"Computer details: {host}", "SystemInfo",
            ExePath: exe,
            Timeout: TimeSpan.FromMinutes(10),
            ExeVersion: typeof(ComputerDetailsJob).Assembly.GetName().Version?.ToString(),
            ManifestDetails: new Dictionary<string, string>
            {
                ["engine"] = "EZ Forensics built-in (Eric Zimmerman Registry library " + typeof(Registry.RegistryHive).Assembly.GetName().Version + ")",
                ["evidenceAccess"] = "read-only; hives and transaction logs copied to a temporary folder before parsing",
            });
        return s.Queue.Enqueue(spec, caseDir);
    }

    /// <summary>True for jobs of this step.</summary>
    public static bool Is(Job job) => job.Spec.ToolId == ComputerDetailsStore.ToolId;
}
