using EzForensics.Core.Case;
using EzForensics.Core.Execution;

namespace EzForensics.Core.EventLogs;

public enum EvtxRerunChoice { Replace, CreateNew, Cancel }

/// <summary>
/// What to do when EvtxECmd is about to run and its output is already in the case. The decision (which files count as "existing", how
/// Replace deletes them, what Create new produces) lives here; the UI only asks the question.
/// </summary>
public static class EvtxOutputPolicy
{
    private static readonly string[] Stems = { "EventLogs", "EvtxECmd_Output" };
    private static readonly string[] Exts = { ".csv", ".json", ".xml" };

    /// <summary>True for a previous EvtxECmd output file: <c>EventLogs[_N].csv</c> (case layout), or <c>[stamp_]EvtxECmd_Output[ (N)].csv</c>.</summary>
    public static bool IsEvtxOutput(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (OutputNaming.IsEzForensicsSidecar(name)) return false;
        var ext = Path.GetExtension(name);
        if (!Exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(OutputNaming.CanonicalName(name));
        // Case layout numbering: EventLogs_2.csv
        var us = stem.LastIndexOf('_');
        if (us > 0 && stem[(us + 1)..].All(char.IsDigit) && stem.Length > us + 1) stem = stem[..us];
        return Stems.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Existing EvtxECmd output files directly inside <paramref name="outputDir"/> (the host's EventLogs folder, or a Tools-page output folder).</summary>
    public static IReadOnlyList<string> FindExisting(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(outputDir, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(IsEvtxOutput).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Deletes the files and the sidecars (row flags, stats cache) that follow them. Returns the files deleted; failures are skipped and logged.</summary>
    public static IReadOnlyList<string> Replace(IEnumerable<string> existing, Action<string>? log = null)
    {
        var deleted = new List<string>();
        foreach (var f in existing)
        {
            try
            {
                if (!File.Exists(f)) continue;
                var sidecars = f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? CaseFileOperations.SidecarsOf(f) : Array.Empty<string>();
                File.Delete(f);
                foreach (var s in sidecars) { try { File.Delete(s); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log?.Invoke($"# Could not delete {Path.GetFileName(s)}: {ex.Message}"); } }
                deleted.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"# Could not delete the previous event log output {Path.GetFileName(f)}: {ex.Message}");
            }
        }
        if (deleted.Count > 0) log?.Invoke($"# Replaced previous event log output: deleted {string.Join(", ", deleted.Select(Path.GetFileName))}");
        return deleted;
    }

    /// <summary>
    /// The name the next run's output gets when the previous files are kept (Create new): the publisher numbers a taken name
    /// (<c>EventLogs.csv</c> → <c>EventLogs_2.csv</c>), never overwriting.
    /// </summary>
    public static string NewOutputPath(string finalPath)
    {
        for (int n = 1; n < 100_000; n++)
        {
            var p = ArtifactNaming.WithNumber(finalPath, n);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
        throw new IOException("No free file name for " + finalPath);
    }

    /// <summary>
    /// Wraps a job's pre-process step so that, for <see cref="EvtxRerunChoice.Replace"/>, the previous output is deleted just before the run
    /// starts (a cancelled prompt or a job that never starts deletes nothing). <see cref="EvtxRerunChoice.CreateNew"/> returns the spec unchanged.
    /// </summary>
    public static JobSpec Apply(JobSpec spec, EvtxRerunChoice choice)
    {
        if (choice != EvtxRerunChoice.Replace) return spec;
        var inner = spec.PreProcess;
        var dir = spec.OutputDir;
        return spec with
        {
            PreProcess = async (ctx, ct) =>
            {
                Replace(FindExisting(dir), l => ctx.Log(l));
                if (inner != null) await inner(ctx, ct).ConfigureAwait(false);
            },
        };
    }
}
