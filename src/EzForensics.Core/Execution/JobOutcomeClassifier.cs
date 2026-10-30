namespace EzForensics.Core.Execution;

/// <summary>Final state plus a human-readable reason.</summary>
public sealed record JobOutcome(JobState State, string Reason);

/// <summary>Turns a finished process (exit code, captured output, output files) into a job state and a one-line reason.</summary>
public static class JobOutcomeClassifier
{
    public const string AdminReason = "Administrator privileges required — restart EZ Forensics as Administrator";
    public const string DirtyHiveReason = "Hive is dirty and its transaction logs (.LOG1/.LOG2) were not found — re-run with --nl to ignore logs, or supply the logs";
    public const string MissingToolReason = "Tool not installed — download it from Tools";
    public const string CancelledReason = "Cancelled by user";
    /// <summary>Reason when the tool found nothing to read in the source (state stays Succeeded). The UI shows it in warning colour.</summary>
    public const string NoInputReason = "No input files found in the source: pick the folder that holds the evidence.";
    private static readonly string[] NoInputMarkers = { "Found 0 files", "Processed 0 files", "Processed 0 out of 0" };

    /// <summary>True when <paramref name="reason"/> is the no-input warning (with or without the admin suffix).</summary>
    public static bool IsNoInput(string? reason) => reason != null && reason.StartsWith(NoInputReason, StringComparison.Ordinal);

    private static readonly string[] ErrorWords = { "error", "exception", "failed", "unable", "not found" };

    /// <param name="lines">All captured output lines (stdout and stderr).</param>
    /// <param name="stdErrLines">Only the stderr lines (optional; used as a fallback for the error line).</param>
    /// <param name="outputFiles">Files the job produced.</param>
    public static JobOutcome Classify(int exitCode, bool timedOut, bool cancelled,
        IReadOnlyList<string> lines, IReadOnlyList<string> outputFiles,
        TimeSpan? timeout = null, IReadOnlyList<string>? stdErrLines = null, bool toolMissing = false)
    {
        if (toolMissing) return new(JobState.Failed, MissingToolReason);
        if (timedOut)
        {
            var t = timeout is { } ts && ts > TimeSpan.Zero ? $" after {FormatMinutes(ts)} min" : "";
            return new(JobState.TimedOut, "Timed out" + t);
        }
        if (cancelled) return new(JobState.Cancelled, CancelledReason);

        // A dirty hive with no logs is a genuine abort and is the real cause even when the tool also printed the admin warning.
        if (lines.Any(l => l.Contains("Registry hive is dirty", StringComparison.OrdinalIgnoreCase)
                           && (l.Contains("Aborting", StringComparison.OrdinalIgnoreCase)
                               || l.Contains("no transaction logs", StringComparison.OrdinalIgnoreCase))))
            return new(JobState.Failed, DirtyHiveReason);

        // EZ tools print "Warning: Administrator privileges not found!" at startup whenever they are not elevated, even
        // though most of them then run fine. Only treat it as fatal when the tool actually bailed out ("Exiting").
        bool adminWarn = lines.Any(l => l.Contains("Administrator privileges not found", StringComparison.OrdinalIgnoreCase));
        bool exitingLine = lines.Any(l => l.Contains("Exiting", StringComparison.OrdinalIgnoreCase));

        long rows = 0; long dataFiles = 0;
        foreach (var f in outputFiles)
        {
            if (f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var r = CountCsvRows(f); rows += r; if (r > 0) dataFiles++;
            }
            else if (HasContent(f)) dataFiles++;
        }
        bool hasData = dataFiles > 0;

        if (adminWarn && exitingLine && !hasData && exitCode == 0)
            return new(JobState.Failed, AdminReason);

        if (exitCode != 0)
        {
            var reason = $"Exited with code {exitCode}";
            var err = LastErrorLine(lines, stdErrLines);
            if (err != null) reason += ": " + err;
            return new(JobState.Failed, reason);
        }

        if (!hasData && lines.Any(l => NoInputMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))))
            return new(JobState.Succeeded, adminWarn ? NoInputReason + " (ran without admin)" : NoInputReason);

        const string AdminNote = "Completed (ran without admin; some data may be missing)";
        if (outputFiles.Count == 0)
        {
            var err = FirstToolErrorLine(lines);
            var baseReason = "Completed but produced no output files";
            if (adminWarn) baseReason += " (ran without admin)";
            return new(JobState.Succeeded, err != null ? baseReason + ": " + err : baseReason);
        }
        if (!hasData)
        {
            var empty = $"{outputFiles.Count} file(s), 0 rows (no matching data)";
            return new(JobState.Succeeded, adminWarn ? $"{AdminNote}: {empty}" : $"Completed: {empty}");
        }
        var summary = $"{outputFiles.Count} output file(s), {rows} CSV rows";
        return new(JobState.Succeeded, adminWarn ? $"{AdminNote}: {summary}" : $"Completed: {summary}");
    }

    private static bool HasContent(string path)
    {
        try { return new FileInfo(path).Length > 0; } catch (Exception) { return false; }
    }

    /// <summary>First line where the tool itself reported an error or exception (used to explain an empty result).</summary>
    private static string? FirstToolErrorLine(IReadOnlyList<string> lines)
    {
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.StartsWith("There was an error", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return Clip(l);
        }
        return null;
    }

    private static string FormatMinutes(TimeSpan ts)
    {
        var m = ts.TotalMinutes;
        return m >= 10 || Math.Abs(m - Math.Round(m)) < 0.05 ? Math.Round(m).ToString("0") : m.ToString("0.#");
    }

    private static string? LastErrorLine(IReadOnlyList<string> lines, IReadOnlyList<string>? stdErr)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (ErrorWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))) return Clip(l);
        }
        if (stdErr != null)
            for (int i = stdErr.Count - 1; i >= 0; i--)
                if (!string.IsNullOrWhiteSpace(stdErr[i])) return Clip(stdErr[i].Trim());
        return null;
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>
    /// Counts CSV data records (minus the header) in one streaming pass over the bytes: a line break inside a quoted field does not end a record
    /// (RFC 4180; an escaped quote <c>""</c> toggles twice). Blank lines are not records.
    /// </summary>
    public static long CountCsvRows(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (fs.Length == 0) return 0;
            var buf = new byte[1 << 16];
            long records = 0; int n; bool inQuote = false, lineHasData = false;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                var span = buf.AsSpan(0, n);
                int i = 0;
                while (i < span.Length)
                {
                    if (inQuote)
                    {
                        int q = span[i..].IndexOf((byte)'"');
                        if (q < 0) break;
                        i += q + 1; inQuote = false; continue;
                    }
                    byte c = span[i++];
                    if (c == (byte)'"') { inQuote = true; lineHasData = true; }
                    else if (c == (byte)'\n') { if (lineHasData) records++; lineHasData = false; }
                    else if (c != (byte)'\r') lineHasData = true;
                }
            }
            if (lineHasData) records++; // final record without a line break
            return Math.Max(0, records - 1);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
