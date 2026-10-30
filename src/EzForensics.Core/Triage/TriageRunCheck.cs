using EzForensics.Core.Execution;
using EzForensics.Core.Tools;

namespace EzForensics.Core.Triage;

/// <summary>Why the Ingest page's Run selected button is disabled (null = it can run).</summary>
/// <param name="PlannedCount">Rows in the plan.</param>
/// <param name="SelectedCount">Ticked rows.</param>
/// <param name="SystemErrorText">First error of a checked system (e.g. an empty or duplicate host label), or null.</param>
/// <param name="CaseRunActive">A run of the same case folder is still queued or running.</param>
/// <param name="MemoryPlanning">The memory image jobs are being planned in the background.</param>
/// <param name="CaseFolderEmpty">The case folder box is empty.</param>
public sealed record RunGate(int PlannedCount, int SelectedCount, string? SystemErrorText, bool CaseRunActive, bool MemoryPlanning, bool CaseFolderEmpty);

/// <summary>What a Run selected click skipped (or could not queue), used to explain the result on the page and in the log.</summary>
public sealed class RunSkips
{
    public int ZeekNotSetUp { get; set; }
    public int MemoryNotSetUp { get; set; }
    public List<string> ZeekErrors { get; } = new();
    public List<string> RitaNotes { get; } = new();
    /// <summary>Executables that were not found (EZ tools under the tools root, or an explicit exe that no longer exists).</summary>
    public List<string> MissingTools { get; } = new();
    /// <summary>Jobs not queued because their tool is missing.</summary>
    public int MissingToolJobs { get; set; }

    public bool Any => ZeekNotSetUp > 0 || MemoryNotSetUp > 0 || ZeekErrors.Count > 0 || RitaNotes.Count > 0 || MissingToolJobs > 0;
}

/// <summary>
/// Pure checks behind the Ingest page's Run selected: why the button is disabled, which selected jobs cannot run because their tool
/// is missing, and the plain-language result line. Run selected never does "nothing": every outcome produces a message (fresh-install bug).
/// </summary>
public static class TriageRunCheck
{
    /// <summary>The reason Run selected is disabled, or null when it can run.</summary>
    public static string? BlockedReason(RunGate g)
    {
        if (g.PlannedCount == 0) return "Nothing to run yet: choose a source and click Scan first.";
        if (g.SelectedCount == 0) return "No jobs are ticked: tick the jobs to run (or click Defaults).";
        if (!string.IsNullOrEmpty(g.SystemErrorText)) return "Fix the system name first: " + g.SystemErrorText;
        if (g.CaseFolderEmpty) return "The case folder is empty: choose a case folder.";
        if (g.CaseRunActive) return "A run of this case folder is still in progress: wait for it to finish (Jobs page) or pick another case folder.";
        if (g.MemoryPlanning) return "Planning the memory image jobs… Run selected is available in a moment.";
        return null;
    }

    /// <summary>
    /// The executables of <paramref name="specs"/> that cannot be found: EZ tool names the locator does not resolve (or resolves to a
    /// file that is gone), and explicit absolute exe paths that do not exist. Distinct, in first-seen order.
    /// </summary>
    public static IReadOnlyList<string> MissingTools(IEnumerable<JobSpec> specs, IToolLocator locator, BuildFlavor? preferred = null)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            var name = ToolMissing(s, locator, preferred);
            if (name != null && seen.Add(name)) missing.Add(name);
        }
        return missing;
    }

    /// <summary>The missing executable of one job (null = it can be found). Same rule as <see cref="JobQueue"/> uses when the job starts.</summary>
    public static string? ToolMissing(JobSpec spec, IToolLocator locator, BuildFlavor? preferred = null)
    {
        if (spec.ExePath != null)
        {
            // Bare names (resolved by the runner itself) are not checked here; a rooted path must exist.
            return Path.IsPathRooted(spec.ExePath) && !File.Exists(spec.ExePath) ? spec.ExePath : null;
        }
        if (string.IsNullOrWhiteSpace(spec.ExeName)) return null;
        var tool = locator.Resolve(spec.ExeName, preferred);
        return tool == null || !File.Exists(tool.Path) ? spec.ExeName : null;
    }

    /// <summary>The result line after Run selected queued <paramref name="queued"/> jobs (0 = nothing was queued: always says why).</summary>
    public static string Describe(int queued, RunSkips skips, string caseDir)
    {
        var parts = new List<string>();
        if (skips.MissingToolJobs > 0)
            parts.Add($"{skips.MissingToolJobs} job(s) skipped because their tool is not installed ({string.Join(", ", skips.MissingTools.Take(6))}{(skips.MissingTools.Count > 6 ? ", …" : "")}). Click Open setup to install the tools, then Run selected again.");
        if (skips.ZeekNotSetUp > 0)
            parts.Add($"{skips.ZeekNotSetUp} packet capture job(s) skipped because Zeek is not set up yet (Set up Zeek, then run them again).");
        if (skips.MemoryNotSetUp > 0)
            parts.Add($"{skips.MemoryNotSetUp} memory image job(s) skipped because Volatility 3 is not set up yet (Set up Volatility, then run them again).");
        if (skips.ZeekErrors.Count > 0) parts.Add("Skipped: " + string.Join("; ", skips.ZeekErrors) + ".");
        if (skips.RitaNotes.Count > 0) parts.Add("RITA: " + string.Join(" ", skips.RitaNotes));

        if (queued > 0)
            return ($"Queued {queued} job(s). Output goes to {caseDir}. " + string.Join(" ", parts)).TrimEnd();
        if (parts.Count == 0)
            parts.Add("none of the selected jobs can run. Tick at least one job (or click Defaults) and try again.");
        return "Nothing was queued: " + string.Join(" ", parts);
    }
}
