using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;

namespace EzForensics.Core.Timeline;

/// <summary>How the MACB timeline of one host ended (one Ingest MACB row).</summary>
public enum MacbTimelineState { Built, Failed, Cancelled, NotStarted }

/// <summary>The outcome of one host's MACB timeline: <paramref name="Host"/> as shown (host folder, or "unattributed evidence").</summary>
public sealed record MacbTimelineOutcome(string Host, MacbTimelineState State, long Rows = 0, string? OutputCsv = null, string? Error = null);

/// <summary>Result of <see cref="MacbTimelineRunner.RunAsync"/>.</summary>
public sealed class MacbRunResult
{
    public bool Succeeded { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public long Rows { get; set; }
    public string? OutputCsv { get; set; }
    /// <summary>The MFTECmd jobs queued for the run, in order.</summary>
    public List<Job> Jobs { get; } = new();
}

/// <summary>
/// Builds the filesystem MACB timeline of a host: one real <see cref="JobQueue"/> MFTECmd job per $MFT
/// (<c>-f &lt;$MFT&gt; --body &lt;timeline&gt;\macb --bodyf &lt;stamp&gt;_&lt;n&gt;.body --bdl &lt;drive&gt;</c>; logged and recorded in the run manifest like every
/// other job), then <see cref="MactimeConverter"/> merges the bodyfiles into <c>&lt;timeline&gt;\Timeline_MACB_&lt;stamp&gt;.csv</c> and deletes them.
/// </summary>
public static class MacbTimelineRunner
{
    public const string Category = "Timeline";
    public const string BodyFolderName = "macb";

    /// <summary>The output CSV of the run <paramref name="stamp"/> in <paramref name="timelineDir"/>.</summary>
    public static string OutputCsvFor(string timelineDir, string stamp) => Path.Combine(timelineDir, $"{TimelineFiles.MacbPrefix}{stamp}.csv");

    /// <summary>
    /// Drive letter MFTECmd writes into the bodyfile paths: the system root's folder name when it is a single letter (<c>...\C</c>) or a bare drive root (<c>E:\</c>), else "C".
    /// </summary>
    public static string DriveFor(string? systemRoot)
    {
        var trimmed = (systemRoot ?? "").TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0])) return char.ToUpperInvariant(trimmed[0]).ToString();   // E:\
        var name = Path.GetFileName(trimmed);
        return name.Length == 1 && char.IsAsciiLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "C";
    }

    /// <summary>The MFTECmd bodyfile job for the <paramref name="index"/>-th of <paramref name="count"/> $MFT files.</summary>
    public static JobSpec MftBodySpec(string mft, string timelineDir, string stamp, int index, int count, string drive, JobProvenance? provenance)
    {
        var bodyDir = Path.Combine(timelineDir, BodyFolderName);
        var args = new[] { "-f", mft, "--body", bodyDir, "--bodyf", BodyFileName(stamp, index), "--bdl", drive };
        var label = "MFTECmd body file $MFT" + (count > 1 ? $" ({index + 1}/{count})" : "");
        return new JobSpec(Triage.IngestSteps.MacbToolId, Triage.IngestSteps.MacbExeName, args, bodyDir, label, Category) { Provenance = provenance };
    }

    public static string BodyFileName(string stamp, int index) => $"{stamp}_{index + 1}.body";

    /// <summary>
    /// A MACB timeline that could not start (no $MFT file, MFTECmd not installed) is still listed on the Jobs page, as a Failed job holding the reason.
    /// </summary>
    public static Job EnqueueNotStarted(JobQueue queue, string caseDir, string outputDir, string host, string reason) =>
        queue.EnqueueRejected(new JobSpec(Triage.IngestSteps.MacbToolId, Triage.IngestSteps.MacbExeName, Array.Empty<string>(), outputDir,
            $"MFTECmd body file {host}", Category), caseDir, reason);

    /// <param name="mftFiles">The host's $MFT files (each is one MFTECmd job, run one after the other).</param>
    /// <param name="onJob">Called for each job right after it is queued.</param>
    public static async Task<MacbRunResult> RunAsync(JobQueue queue, string caseDir, string timelineDir, IReadOnlyList<string> mftFiles, string drive,
        JobProvenance? provenance = null, string? stamp = null, Action<Job>? onJob = null, CancellationToken ct = default)
    {
        stamp ??= DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var csv = OutputCsvFor(timelineDir, stamp);
        var result = new MacbRunResult { OutputCsv = csv };
        var bodyDir = Path.Combine(timelineDir, BodyFolderName);
        var bodies = new List<string>();
        try
        {
            Directory.CreateDirectory(bodyDir);
            for (int i = 0; i < mftFiles.Count; i++)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; result.Error = "Cancelled"; return result; }
                var job = queue.Enqueue(MftBodySpec(mftFiles[i], timelineDir, stamp, i, mftFiles.Count, drive, provenance), caseDir);
                result.Jobs.Add(job);
                onJob?.Invoke(job);
                using (ct.Register(job.Cancel))
                    await PlasoTimelineRunner.WaitAsync(job).ConfigureAwait(false);
                var body = Path.Combine(bodyDir, BodyFileName(stamp, i));
                if (job.State != JobState.Succeeded || !File.Exists(body))
                {
                    result.Cancelled = job.State == JobState.Cancelled;
                    result.Error = result.Cancelled ? "Cancelled"
                        : "MFTECmd: " + (job.State == JobState.Succeeded ? "no bodyfile was written" : job.StatusReason ?? job.State.ToString());
                    return result;
                }
                bodies.Add(body);
            }
            result.Rows = await Task.Run(() => MactimeConverter.Convert(bodies, csv, ct: ct), ct).ConfigureAwait(false);
            result.Succeeded = true;
            foreach (var b in bodies) try { File.Delete(b); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { if (!Directory.EnumerateFileSystemEntries(bodyDir).Any()) Directory.Delete(bodyDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException) { result.Cancelled = true; result.Error = "Cancelled"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Error = "Converting the bodyfile: " + ex.Message;
        }
        return result;
    }
}

/// <summary>The one completion text of the MACB timelines of an Ingest run (one per host, built one after the other).</summary>
public static class MacbTimelineSummary
{
    /// <summary>
    /// "Filesystem timelines built for 2 hosts (WS01 12,345 rows; DC01 678 rows)." (one host: "Filesystem timeline built for WS01: 12,345 rows (file).")
    /// followed by each failure, cancellation or not-started reason. <c>Ok</c> = every timeline was built.
    /// </summary>
    public static (string Text, bool Ok) Describe(IReadOnlyList<MacbTimelineOutcome> outcomes)
    {
        var parts = new List<string>();
        var built = outcomes.Where(o => o.State == MacbTimelineState.Built).ToList();
        if (built.Count == 1)
        {
            var b = built[0];
            var file = string.IsNullOrEmpty(b.OutputCsv) ? "" : $" ({Path.GetFileName(b.OutputCsv)})";
            parts.Add($"Filesystem timeline built for {b.Host}: {b.Rows:N0} rows{file}.");
        }
        else if (built.Count > 1)
            parts.Add($"Filesystem timelines built for {built.Count} hosts ({string.Join("; ", built.Select(b => $"{b.Host} {b.Rows:N0} rows"))}).");
        foreach (var o in outcomes.Where(o => o.State != MacbTimelineState.Built))
            parts.Add(o.State switch
            {
                MacbTimelineState.Cancelled => $"The filesystem timeline of {o.Host} was cancelled.",
                MacbTimelineState.NotStarted => $"The filesystem timeline of {o.Host} was not built. {o.Error}".TrimEnd(),
                _ => $"The filesystem timeline of {o.Host} failed: {o.Error}".TrimEnd(),
            });
        return (string.Join(" ", parts), outcomes.Count > 0 && built.Count == outcomes.Count);
    }
}
