namespace EzForensics.Core.Execution;

/// <summary>Short outcome text of a job (the Result column of the Ingest planned jobs), and which job stands for a row of several.</summary>
public static class JobResultText
{
    /// <summary>Queued / Running / Done / Failed (exit 2) / Timed out / Cancelled (a skipped job reads Cancelled too).</summary>
    public static string Text(JobState state, int? exitCode) => state switch
    {
        JobState.Queued => "Queued",
        JobState.Running => "Running",
        JobState.Succeeded => "Done",
        JobState.Failed => exitCode is { } c and not 0 ? $"Failed (exit {c})" : "Failed",
        JobState.TimedOut => "Timed out",
        JobState.Cancelled => "Cancelled",
        JobState.Skipped => "Cancelled",
        _ => state.ToString(),
    };

    /// <summary>
    /// Index of the job a row of several jobs shows: the first running, else the first queued, else the first that did not succeed, else the
    /// last; -1 when there are none.
    /// </summary>
    public static int RepresentativeIndex(IReadOnlyList<JobState> states)
    {
        if (states.Count == 0) return -1;
        foreach (var wanted in new Func<JobState, bool>[] { s => s == JobState.Running, s => s == JobState.Queued, s => s != JobState.Succeeded })
            for (int i = 0; i < states.Count; i++)
                if (wanted(states[i])) return i;
        return states.Count - 1;
    }
}
