namespace EzForensics.Core.Timeline;

/// <summary>
/// Where super timelines live in a case: <c>&lt;case&gt;\&lt;Host&gt;\Timeline\SuperTimeline_*.csv</c>, one per host (plaso writes
/// <c>SuperTimeline_plaso_&lt;stamp&gt;.csv</c>; evidence of no identified host goes to <c>&lt;case&gt;\_Case\Timeline</c>). Cases from older
/// versions hold one case-level <c>&lt;case&gt;\Timeline</c>, and may also hold <c>SuperTimeline_EZ*.csv</c> from the removed EZ CSV merge,
/// which still open and sort first.
/// </summary>
public static class TimelineFiles
{
    public const string TimelineFolderName = "Timeline";
    public const string OutputPrefix = "SuperTimeline_";
    /// <summary>Filesystem MACB timeline built from the $MFT (<c>Timeline_MACB_&lt;stamp&gt;.csv</c>, see <c>MacbTimelineRunner</c>); listed beside the super timelines.</summary>
    public const string MacbPrefix = "Timeline_MACB_";

    /// <summary>True for a timeline output CSV: <c>SuperTimeline_*.csv</c> or <c>Timeline_MACB_*.csv</c>.</summary>
    public static bool IsTimelineOutput(string fileName) =>
        (fileName.StartsWith(OutputPrefix, StringComparison.OrdinalIgnoreCase) || fileName.StartsWith(MacbPrefix, StringComparison.OrdinalIgnoreCase))
        && fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
}
