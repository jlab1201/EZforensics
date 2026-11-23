using System.Text;
using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Execution;

namespace EzForensics.App.ViewModels;

/// <summary>What Spectre sees and may propose on Ingest: the job list (numbered as shown), retry and cancel.</summary>
public sealed partial class JobListViewModel
{
    private const int AssistantMaxJobs = 25;

    private static T OnUi<T>(Func<T> read)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        return d == null || d.CheckAccess() ? read() : d.Invoke(read);
    }

    public string? AssistantState() => OnUi(() =>
    {
        if (Rows.Count == 0) return "Jobs: none yet.";
        var sb = new StringBuilder($"Jobs ({Rows.Count}, as listed; number. label - tool - state):");
        int n = 0;
        foreach (var r in Rows.Take(AssistantMaxJobs))
        {
            var j = r.Job;
            sb.Append('\n').Append(++n).Append(". ").Append(j.Spec.Label).Append(" - ").Append(j.Spec.ToolId).Append(" - ").Append(j.State);
            if (j.ExitCode is { } code && j.IsFinished) sb.Append(", exit ").Append(code);
            if (j.State is JobState.Failed or JobState.TimedOut && !string.IsNullOrWhiteSpace(j.StatusReason)) sb.Append(": ").Append(Short(j.StatusReason!));
        }
        if (Rows.Count > AssistantMaxJobs) sb.Append($"\n+{Rows.Count - AssistantMaxJobs} more");
        if (Selected != null) sb.Append("\nSelected: ").Append(Rows.IndexOf(Selected) + 1).Append(". ").Append(Selected.Job.Spec.Label);
        if (FailureAdvice() is { Length: > 0 } advice) sb.Append("\nAdvice for it: ").Append(Short(advice));
        return sb.ToString();
    });

    private static string Short(string s) { s = s.ReplaceLineEndings(" "); return s.Length > 160 ? s[..160] + "…" : s; }

    public IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        yield return SpectrePageTools.Create(SpectrePageTools.JobsRetrySpec, host, (id, input) => OnUi(() =>
        {
            if (JobAt(input) is not { } job) return null;
            return new PageActionProposal(id, $"Retry job {Number(job)}: {job.Spec.Label}", job.Spec.ToolId, () =>
            {
                if (!job.IsFinished) return Task.FromResult<string?>("The job is still running.");
                ShowJob(S.Queue.Enqueue(job.Spec, job.CaseDir));
                return Task.FromResult<string?>(null);
            }, AutoAllowed: false);
        }), input => OnUi(() => JobAt(input) is not { } j ? NoJob() : !j.IsFinished ? $"Job {Number(j)} is still {j.State}; only finished jobs can be retried." : null));

        yield return SpectrePageTools.Create(SpectrePageTools.JobsCancelSpec, host, (id, input) => OnUi(() =>
        {
            if (JobAt(input) is not { } job) return null;
            return new PageActionProposal(id, $"Stop job {Number(job)}: {job.Spec.Label}", job.Spec.ToolId, () =>
            {
                if (job.IsFinished) return Task.FromResult<string?>("The job already finished.");
                job.Cancel();
                return Task.FromResult<string?>(null);
            }, AutoAllowed: false);
        }), input => OnUi(() => JobAt(input) is not { } j ? NoJob() : j.IsFinished ? $"Job {Number(j)} already finished ({j.State})." : null));
    }

    /// <summary>The job with the 1-based number the model gave (as numbered in <see cref="AssistantState"/>).</summary>
    private Job? JobAt(JsonObject input)
    {
        var n = AssistantGridTools.AsLong(input["job"]);
        return n is { } i && i >= 1 && i <= Rows.Count ? Rows[(int)i - 1].Job : null;
    }

    private int Number(Job job) => Rows.Select(r => r.Job).ToList().IndexOf(job) + 1;

    private string NoJob() => Rows.Count == 0 ? "There are no jobs." : $"Give a job number from 1 to {Rows.Count} (as listed in the app state).";
}
