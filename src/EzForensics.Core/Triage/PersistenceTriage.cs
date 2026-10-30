using EzForensics.Core.Execution;
using EzForensics.Core.Persistence;

namespace EzForensics.Core.Triage;

/// <summary>
/// Persistence part of triage. For every Windows system root in the source: one built-in EZ Forensics job (always available, no install,
/// no admin). Output (case layout v2): <c>&lt;case&gt;\&lt;Host&gt;\Persistence\Persistence.csv</c> (+ <c>Persistence_ScheduledTasks.csv</c>, <c>Persistence_Wmi.csv</c>).
/// </summary>
public static class PersistenceTriage
{
    public const string RuleId = "persistence";

    /// <summary>The detected roots that really hold a Windows system (hives, tasks, WMI repository or user profiles).</summary>
    public static IReadOnlyList<string> SystemRoots(SourceLayout layout) => layout.SystemRoots.Where(PersistenceCollector.HasWindowsSystem).ToList();

    /// <summary>Headless built-in job: <c>&lt;exe&gt; --persistence --root &lt;root&gt; --out &lt;dir&gt;</c> (the EZ Forensics executable runs the collector).</summary>
    public static JobSpec BuiltInSpec(string exe, string systemRoot, string outDir, string? label = null) =>
        new(PersistenceCollector.ToolId, Path.GetFileName(exe), new[] { PersistenceCollector.Switch, "--root", systemRoot, "--out", outDir }, outDir,
            label ?? "Persistence (EZ Forensics built-in: registry autostarts, tasks, startup folders, WMI)", PersistenceCollector.Category,
            ExePath: exe, Timeout: TimeSpan.FromMinutes(30), ExeVersion: typeof(PersistenceTriage).Assembly.GetName().Version?.ToString(),
            ManifestDetails: new Dictionary<string, string>
            {
                ["engine"] = "EZ Forensics built-in persistence collector (Eric Zimmerman Registry library " + typeof(Registry.RegistryHive).Assembly.GetName().Version + ")",
                ["evidenceAccess"] = "read-only; hives and transaction logs copied to a temporary folder before parsing",
                ["evidence.systemRoot"] = systemRoot,
            });

    /// <summary>Layout v2 output base names: <c>Persistence.csv</c> (+ <c>_ScheduledTasks</c>, <c>_Wmi</c>).</summary>
    public const string PersistenceName = "Persistence";

    /// <param name="appExe">Executable that runs <see cref="PersistenceCollector.RunCommandLine"/> for <see cref="PersistenceCollector.Switch"/>.</param>
    /// <remarks>Roots only (no host discovery): the host folder is the root's folder name. Prefer the <see cref="DiscoveredSystem"/> overload.</remarks>
    public static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<string> systemRoots, string caseDir, string appExe)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systems = new List<DiscoveredSystem>();
        foreach (var root in systemRoots)
        {
            var s = ArtifactScanner.SystemFor(root, readHive: false);
            var name = s.HostName;
            for (int n = 2; !used.Add(name); n++) name = $"{s.HostName} ({n})";
            systems.Add(s with { HostName = name });
        }
        return Plan(systems, caseDir, appExe);
    }

    /// <summary>
    /// For every system: the built-in job (<c>&lt;case&gt;\&lt;Host&gt;\Persistence\Persistence.csv</c>).
    /// </summary>
    public static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<DiscoveredSystem> systems, string caseDir, string appExe)
    {
        var list = new List<PlannedJob>();
        bool multi = systems.Count > 1;
        foreach (var system in systems)
        {
            var root = system.RootPath;
            var host = TriagePlanner.HostFolder(system.HostName);
            var finalDir = Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, host, PersistenceCollector.Category);
            var prefix = multi ? host + " · " : "";

            var ezStaging = TriagePlanner.StagingDir(caseDir, host, RuleId, null, root);
            var builtIn = BuiltInSpec(appExe, root, ezStaging, multi ? prefix + "Persistence (EZ Forensics built-in: registry autostarts, tasks, startup folders, WMI)" : null) with
            {
                OutputDir = finalDir,
                Output = new OutputPlan(ezStaging, PersistenceName, "persistence"),
                Provenance = Provenance(system, host, RuleId, "persistence"),
            };
            list.Add(new PlannedJob(builtIn, true,
                new DetectedArtifact(RuleId, "Persistence", PersistenceCollector.ToolId, PersistenceCollector.Category, root, false, 1, root, System: system), false));
        }
        return list;
    }

    private static JobProvenance Provenance(DiscoveredSystem s, string host, string ruleId, string artifactId) => new()
    {
        RuleId = ruleId,
        ArtifactId = artifactId,
        Host = host,
        HostNameSource = s.HostNameSource,
        SystemRoot = s.RootPath,
        AcquisitionKind = s.AcquisitionKind.ToString(),
        OsFamily = s.OsFamily.ToString(),
        Archive = s.Archive,
    };
}
