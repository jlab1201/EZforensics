using EzForensics.Core.Execution;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Services;

/// <summary>
/// Queues the "Spectre analysis" job (<see cref="SpectreAnalysisJob"/>) for a case: after an Ingest run and from the Findings page
/// (Analyse case / Update). At most one runs or waits per case; asking again returns that one.
/// </summary>
public static class SpectreAnalysisQueue
{
    internal static bool SameDir(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The case's Spectre analysis job that is queued or running, if any.</summary>
    public static Job? Active(JobQueue queue, string? caseDir) =>
        queue.Jobs.LastOrDefault(j => SpectreAnalysisJob.Is(j) && !j.IsFinished && SameDir(j.CaseDir, caseDir));

    /// <summary>
    /// Queues the analysis when the engine is installed and reachable. Returns the queued (or already active) job, or null with the reason.
    /// Call on the UI thread.
    /// </summary>
    public static async Task<(Job? Job, string? Reason)> EnqueueAsync(JobQueue queue, string caseDir, CancellationToken ct)
    {
        if (Active(queue, caseDir) is { } active) return (active, null);
        if (!await AssistantSpectre.IsAvailableAsync(caseDir, ct)) return (null, "Spectre is not set up yet. Install it from the Tools page.");
        if (Active(queue, caseDir) is { } raced) return (raced, null);
        var cancel = await AssistantSpectre.CancelCommandAsync(ct);
        var client = AssistantSpectre.CreateFindingsClient(() => caseDir);
        var job = queue.Enqueue(SpectreAnalysisJob.CreateSpec(caseDir, client, cancel), caseDir);
        AppLog.Info($"Spectre analysis queued for {caseDir}.");
        return (job, null);
    }
}
