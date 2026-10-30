using EzForensics.Core.Plaso;

namespace EzForensics.Core.Execution;

/// <summary>
/// Runs install / update processes (dotnet-install, --sync, venv, pip, apt in WSL, wsl --import, docker pull) on a PRIVATE
/// <see cref="JobQueue"/> that no page lists: the Jobs page shows only case work. Each process is still a logged job (its log in
/// <c>&lt;logDir&gt;\logs\&lt;id&gt;_&lt;tool&gt;.log</c>, plus a run manifest in <c>&lt;logDir&gt;</c>), and every output line is passed to the caller
/// (the setup step log, a Tools card log box). Construct it on the UI thread (the queue raises job events on that context).
/// </summary>
public sealed class MaintenanceRunner
{
    public MaintenanceRunner(JobQueue queue, string logRoot)
    {
        Queue = queue;
        LogRoot = logRoot;
    }

    /// <summary>The private queue (never bound to the Jobs page).</summary>
    public JobQueue Queue { get; }

    /// <summary>Default folder for maintenance logs (e.g. <c>%LOCALAPPDATA%\EzForensics\logs\maintenance</c>).</summary>
    public string LogRoot { get; }

    /// <summary>A new per-run log folder under <see cref="LogRoot"/>: <c>&lt;stamp&gt;_&lt;name&gt;</c>.</summary>
    public string NewLogDir(string name) => Path.Combine(LogRoot, $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}");

    /// <summary>Runs <paramref name="spec"/> on the private queue, logs under <paramref name="logDir"/> (default <see cref="LogRoot"/>).</summary>
    public Task<Job> RunAsync(JobSpec spec, Action<string>? log, CancellationToken ct = default, string? logDir = null) =>
        RunAsync(Queue, spec, logDir ?? LogRoot, log, ct);

    /// <summary>
    /// Enqueues <paramref name="spec"/> on <paramref name="queue"/> with <paramref name="logDir"/> as its log / manifest folder, forwards
    /// every output line to <paramref name="log"/>, and waits for the end. <paramref name="ct"/> cancels the job; the finished job is
    /// returned (state, exit code, StatusReason, LogPath), and <see cref="OperationCanceledException"/> is thrown after a cancel.
    /// </summary>
    public static async Task<Job> RunAsync(JobQueue queue, JobSpec spec, string logDir, Action<string>? log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(spec.OutputDir);
        var job = queue.Enqueue(spec, logDir);
        void OnOutput(Job _, string line) => log?.Invoke(line);
        job.Output += OnOutput;
        try
        {
            using (ct.Register(job.Cancel))
                await PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(true);
        }
        finally { job.Output -= OnOutput; }
        ct.ThrowIfCancellationRequested();
        return job;
    }
}
