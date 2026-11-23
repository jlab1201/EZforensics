using System.Globalization;

namespace EzForensics.Core.EventLogs;

/// <summary>
/// Builds EvtxECmd's event-ID filter from an <see cref="EvtxSelection"/>. EvtxECmd has no channel filter: <c>--inc</c> is a global ID list
/// that applies to every log of the run, so the list is the union of the ticked IDs (an ID ticked for one channel is also kept for
/// another channel that uses the same number). Evidence is read with <c>-d</c> (all logs in one run), which rules out a per-channel list.
/// </summary>
public static class EvtxArgs
{
    /// <summary>Sorted, distinct ticked IDs, comma-separated (<c>1102,4624,4625</c>); empty when nothing is ticked.</summary>
    public static string IncludeList(EvtxSelection selection) =>
        string.Join(",", selection.AllEvents.Where(e => e.Selected).Select(e => e.EventId).Distinct().OrderBy(i => i)
            .Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Adds <c>--inc &lt;ids&gt;</c> to <paramref name="args"/> when filtering is on, something is ticked and the arguments do not already
    /// carry <c>--inc</c> or <c>--exc</c> (a user-typed filter wins). Returns the same list otherwise.
    /// </summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> args, bool useRecommended, EvtxSelection? selection)
    {
        if (!useRecommended || selection == null) return args;
        if (args.Any(a => a.Equals("--inc", StringComparison.OrdinalIgnoreCase) || a.Equals("--exc", StringComparison.OrdinalIgnoreCase))) return args;
        var list = IncludeList(selection);
        if (list.Length == 0) return args;
        return new List<string>(args) { "--inc", list };
    }

    /// <summary>
    /// Expands <c>{eventIds}</c> in an explicit command: with the recommended selection on and something ticked it becomes the ID list;
    /// otherwise the token is dropped together with a preceding <c>--inc</c>/<c>--exc</c>. Without the token nothing is added.
    /// </summary>
    public static IReadOnlyList<string> ExpandEventIds(IReadOnlyList<string> args, bool useRecommended, EvtxSelection? selection)
    {
        var list = useRecommended && selection != null ? IncludeList(selection) : "";
        return EzForensics.Core.Triage.CommandVariables.Expand(args, "eventIds", list, "--inc", "--exc");
    }

    /// <summary>"28 of 50 events · 10 groups".</summary>
    public static string Summary(EvtxSelection s) => $"{s.SelectedCount} of {s.TotalCount} {(s.TotalCount == 1 ? "event" : "events")} · {EzForensics.Core.Common.Plural.Of(s.GroupCount, "group")}";
}
