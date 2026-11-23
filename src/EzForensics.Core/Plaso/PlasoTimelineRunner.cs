using System.ComponentModel;
using System.Diagnostics;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Plaso;

/// <summary>
/// Runs log2timeline then psort as two JobQueue jobs (logged and recorded in the run manifest like every other job):
/// <c>&lt;timeline&gt;\plaso\&lt;stamp&gt;.plaso</c> -> <c>&lt;timeline&gt;\SuperTimeline_plaso_&lt;stamp&gt;.csv</c>, where &lt;timeline&gt; is
/// <see cref="PlasoRunOptions.TimelineDir"/> (<c>&lt;case&gt;\&lt;Host&gt;\Timeline</c> from Ingest, else <c>&lt;case&gt;\Timeline</c>).
/// </summary>
public static class PlasoTimelineRunner
{
    public const string ToolIdLog2Timeline = "plaso-log2timeline";
    public const string ToolIdPsort = "plaso-psort";
    public const string Category = "Super timeline";

    public static JobSpec Log2TimelineSpec(PlasoInstall install, PlasoRunOptions o) => Log2TimelineSpec(install, o, 0);

    /// <summary>log2timeline job for the <paramref name="sourceIndex"/>-th of <see cref="PlasoRunOptions.AllSources"/>.</summary>
    public static JobSpec Log2TimelineSpec(PlasoInstall install, PlasoRunOptions o, int sourceIndex)
    {
        var inv = PlasoCommandBuilder.BuildLog2Timeline(install, o, sourceIndex);
        var sources = o.AllSources;
        var label = $"log2timeline {Path.GetFileName(sources[sourceIndex].TrimEnd('\\', '/'))}" + (sources.Count > 1 ? $" ({sourceIndex + 1}/{sources.Count})" : "");
        return new JobSpec(ToolIdLog2Timeline, Path.GetFileName(inv.Exe), inv.Args, o.PlasoDir, label, Category,
            ExePath: inv.Exe, Timeout: Timeout.InfiniteTimeSpan, CancelCommand: inv.CancelCommand,
            WorkingDir: o.PlasoDir, ExeVersion: install.Version, Environment: Unbuffered) { Provenance = o.Provenance };
    }

    /// <summary>Python block-buffers stdout into a pipe; without this the linear status arrives only at the end.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unbuffered = new Dictionary<string, string> { ["PYTHONUNBUFFERED"] = "1" };

    public static JobSpec PsortSpec(PlasoInstall install, PlasoRunOptions o)
    {
        var inv = PlasoCommandBuilder.BuildPsort(install, o);
        return new JobSpec(ToolIdPsort, Path.GetFileName(inv.Exe), inv.Args, o.TimelineDir,
            $"psort -o {(o.Format == PlasoOutputFormat.L2tCsv ? "l2tcsv" : "dynamic")} {o.Stamp}", Category,
            ExePath: inv.Exe, Timeout: Timeout.InfiniteTimeSpan, CancelCommand: inv.CancelCommand,
            WorkingDir: o.PlasoDir, ExeVersion: install.Version, Environment: Unbuffered) { Provenance = o.Provenance };
    }

    /// <summary>
    /// A super timeline that could not start (plaso not installed, no / missing source, an error before the first job) is still listed on
    /// the Ingest job list, as a Failed job holding the reason.
    /// </summary>
    public static Job EnqueueNotStarted(JobQueue queue, string caseDir, string outputDir, string host, string reason) =>
        queue.EnqueueRejected(new JobSpec(ToolIdLog2Timeline, "log2timeline", Array.Empty<string>(), outputDir, $"log2timeline {host}", Category), caseDir, reason);

    /// <param name="onJob">Called for each job right after it is queued.</param>
    /// <param name="onStatus">Parsed status lines (any thread).</param>
    public static async Task<PlasoRunResult> RunAsync(JobQueue queue, PlasoInstall install, PlasoRunOptions o,
        Action<Job>? onJob = null, Action<PlasoStatus>? onStatus = null, CancellationToken ct = default)
    {
        var result = new PlasoRunResult { StorageFile = o.StorageFile, OutputCsv = o.OutputCsv };
        Directory.CreateDirectory(o.PlasoDir);

        var startedUtc = DateTime.UtcNow.AddSeconds(-2);
        var sw = Stopwatch.StartNew();
        // One log2timeline run per source, sequentially, into the same storage file (each later run appends a session).
        var sources = o.AllSources;
        long extracted = 0;
        int? warnings = null;
        for (int i = 0; i < sources.Count; i++)
        {
            var l2t = await RunJobAsync(queue, Log2TimelineSpec(install, o, i), o.CaseDir, PlasoPhase.Extracting, result, onJob, onStatus, ct).ConfigureAwait(false);
            result.ExtractElapsed = sw.Elapsed;
            if (l2t.Job.State != JobState.Succeeded || !File.Exists(o.StorageFile))
            {
                var which = sources.Count > 1 ? $"log2timeline (source {i + 1} of {sources.Count}): " : "log2timeline: ";
                result.Cancelled = l2t.Job.State == JobState.Cancelled;
                result.Error = result.Cancelled ? "Cancelled" : which + (l2t.Job.State == JobState.Succeeded ? "no storage file was written" : l2t.Job.StatusReason ?? l2t.Job.State.ToString());
                if (l2t.Job.State is JobState.Cancelled or JobState.TimedOut) CleanupPartial(o, startedUtc);
                return result;
            }
            extracted += l2t.Status?.Events ?? 0;
            if (l2t.Status?.Warnings is { } w) warnings = (warnings ?? 0) + w;
            if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "Cancelled"; return result; }
        }
        result.ExtractedEvents = extracted;
        result.Warnings = warnings;

        sw.Restart();
        var ps = await RunJobAsync(queue, PsortSpec(install, o), o.CaseDir, PlasoPhase.Exporting, result, onJob, onStatus, ct).ConfigureAwait(false);
        result.ExportElapsed = sw.Elapsed;
        if (ps.Job.State != JobState.Succeeded || !File.Exists(o.OutputCsv))
        {
            result.Cancelled = ps.Job.State == JobState.Cancelled;
            result.Error = result.Cancelled ? "Cancelled" : "psort: " + (ps.Job.State == JobState.Succeeded ? "no CSV was written" : ps.Job.StatusReason ?? ps.Job.State.ToString());
            return result;
        }
        result.Events = JobOutcomeClassifier.CountCsvRows(o.OutputCsv);
        result.Succeeded = true;
        onStatus?.Invoke(new PlasoStatus(PlasoPhase.Done, "completed", null, result.Events, null, null, result.Warnings));
        return result;
    }

    private static async Task<(Job Job, PlasoStatus? Status)> RunJobAsync(JobQueue queue, JobSpec spec, string caseDir, PlasoPhase phase,
        PlasoRunResult result, Action<Job>? onJob, Action<PlasoStatus>? onStatus, CancellationToken ct)
    {
        var parser = new PlasoStatusParser(phase);
        var job = queue.Enqueue(spec, caseDir);
        result.Jobs.Add(job);
        job.Output += (_, line) =>
        {
            PlasoStatus? s;
            lock (parser) s = parser.Feed(line);
            if (s != null) onStatus?.Invoke(s);
        };
        onJob?.Invoke(job);
        using (ct.Register(job.Cancel))
            await WaitAsync(job).ConfigureAwait(false);
        lock (parser) return (job, parser.Last);
    }

    /// <summary>
    /// A killed log2timeline leaves a partial storage file and its tmp* task folder behind; remove them (the logs stay).
    /// Only this run's storage file and tmp folders created after it started are touched.
    /// </summary>
    internal static void CleanupPartial(PlasoRunOptions o, DateTime startedUtc)
    {
        try { if (File.Exists(o.StorageFile)) File.Delete(o.StorageFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(o.PlasoDir, "tmp*"))
                if (Directory.GetCreationTimeUtc(d) >= startedUtc)
                    try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }

    /// <summary>Completes when the job reaches a final state.</summary>
    public static Task WaitAsync(Job job)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? h = null;
        h = (_, e) =>
        {
            if (e.PropertyName is nameof(Job.State) or nameof(Job.IsFinished) && job.IsFinished)
            {
                job.PropertyChanged -= h;
                tcs.TrySetResult();
            }
        };
        job.PropertyChanged += h;
        if (job.IsFinished) { job.PropertyChanged -= h; tcs.TrySetResult(); }
        return tcs.Task;
    }
}
