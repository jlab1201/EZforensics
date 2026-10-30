namespace EzForensics.Core.Plaso;

/// <summary>How the super timeline of one host ended (one Ingest timeline row).</summary>
public enum SupertimelineState { Built, Failed, Cancelled, NotStarted }

/// <summary>The outcome of one host's super timeline: <paramref name="Host"/> as shown (host folder, or "unattributed evidence").</summary>
public sealed record SupertimelineOutcome(string Host, SupertimelineState State, long Events = 0, string? OutputCsv = null, string? Error = null);

/// <summary>The one completion message of the super timelines of an Ingest run (one per host, built one after the other).</summary>
public static class SupertimelineSummary
{
    /// <summary>
    /// "Super timelines built for 2 hosts (WS01 12,345 rows; DC01 678 rows)." (one host: "Super timeline built for WS01: 12,345 rows (file).")
    /// followed by each failure, cancellation or not-started reason. <c>Ok</c> = every timeline was built.
    /// </summary>
    public static (string Text, bool Ok) Describe(IReadOnlyList<SupertimelineOutcome> outcomes)
    {
        var parts = new List<string>();
        var built = outcomes.Where(o => o.State == SupertimelineState.Built).ToList();
        if (built.Count == 1)
        {
            var b = built[0];
            var file = string.IsNullOrEmpty(b.OutputCsv) ? "" : $" ({Path.GetFileName(b.OutputCsv)})";
            parts.Add($"Super timeline built for {b.Host}: {b.Events:N0} rows{file}.");
        }
        else if (built.Count > 1)
            parts.Add($"Super timelines built for {built.Count} hosts ({string.Join("; ", built.Select(b => $"{b.Host} {b.Events:N0} rows"))}).");
        foreach (var o in outcomes.Where(o => o.State != SupertimelineState.Built))
            parts.Add(o.State switch
            {
                SupertimelineState.Cancelled => $"The super timeline of {o.Host} was cancelled.",
                SupertimelineState.NotStarted => $"The super timeline of {o.Host} was not built. {o.Error}".TrimEnd(),
                _ => $"The plaso super timeline of {o.Host} failed: {o.Error}".TrimEnd(),
            });
        if (outcomes.Any(o => o.State == SupertimelineState.Failed))
            parts.Add("Click its Result to open the log on the Jobs page.");
        if (built.Count > 0) parts.Add("Open the Analyze page to review the results.");
        return (string.Join(" ", parts), outcomes.Count > 0 && built.Count == outcomes.Count);
    }
}
