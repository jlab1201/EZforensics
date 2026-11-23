using EzForensics.Core.Execution;

namespace EzForensics.Core.Spectre;

/// <summary>
/// The "Spectre analysis" job: hands the case to the engine (files and hashes), runs the analysis through phase 5, then reads the timeline
/// for a one-line result. It is an in-process job (<see cref="JobSpec.InProcess"/>), so it has a live log, Cancel and a
/// manifest record, but it is <see cref="JobSpec.Hidden"/>: the Findings page shows its progress, not the Jobs list. Queue it with <c>queue.Enqueue(SpectreAnalysisJob.CreateSpec(...), caseDir)</c>; it is Exclusive (one at a time).
/// </summary>
public static class SpectreAnalysisJob
{
    public const string ToolId = "spectre-analysis";
    public const string Label = "Spectre analysis";
    public const string Category = "Analysis";
    /// <summary>Last phase run: 5 is after the super timeline.</summary>
    public const int DefaultThroughPhase = 5;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan JobTimeout = TimeSpan.FromHours(3);

    public static bool Is(Job job) => job.Spec.ToolId == ToolId;

    /// <param name="client">Typed engine client (the same one the Findings page uses).</param>
    /// <param name="cancelCommand">argv run when the job is cancelled to stop the engine's running tool (kill the MCP server: <see cref="Assistant.SpectreMcp.Command"/>'s Cancel); null = just stop waiting.</param>
    public static JobSpec CreateSpec(string caseDir, SpectreFindingsClient client, IReadOnlyList<string>? cancelCommand = null, int throughPhase = DefaultThroughPhase)
    {
        var full = Path.GetFullPath(caseDir);
        return new JobSpec(ToolId, "", Array.Empty<string>(), Path.Combine(full, ".ezassistant", "spectre-job"), Label, Category, Exclusive: true,
            Timeout: JobTimeout, CancelCommand: cancelCommand,
            InProcess: (ctx, ct) => RunAsync(full, client, throughPhase, ctx.Log, s => ctx.Summary = s, ct), Hidden: true);
    }

    /// <summary>The job body (also callable without a queue). Throws <see cref="SpectreToolException"/> when the engine reports a problem.</summary>
    public static async Task RunAsync(string caseDir, SpectreFindingsClient client, int throughPhase, Action<string> log, Action<string> summary, CancellationToken ct)
    {
        log("Handing the case to Spectre (listing and hashing the case files)...");
        var caseId = await client.OpenCaseAsync(caseDir, ct).ConfigureAwait(false);
        log("The case is open in Spectre as " + caseId + ".");
        ct.ThrowIfCancellationRequested();

        log($"Running the analysis through phase {throughPhase}. This can take many minutes.");
        var started = DateTime.UtcNow;
        var run = client.RunAsync(caseId, throughPhase, ct);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(Heartbeat, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!run.IsCompleted)
            {
                var t = DateTime.UtcNow - started;
                log($"Still running... {(int)t.TotalMinutes}:{t.Seconds:00} elapsed.");
            }
        }
        var reply = await run.ConfigureAwait(false);
        if (reply.Ok == false) throw new SpectreToolException("spectre_run", "Spectre reported a problem: " + Short(reply.Text));
        log("The analysis finished.");

        string result = "Analysis finished.";
        try
        {
            var tl = await client.TimelineAsync(caseId, ct).ConfigureAwait(false);
            result = $"{tl.Findings.Count + tl.Undated.Count} finding(s), {tl.Moments.Count} key event(s).";
            log(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SpectreToolException or IOException or TimeoutException)
        {
            log("The timeline could not be read yet: " + ex.Message);   // advisory; the Findings page loads it again
        }
        summary(result);
    }

    private static string Short(string s) { s = s.Trim(); return s.Length <= 300 ? s : s[..300] + "..."; }
}
