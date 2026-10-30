using EzForensics.Core.Timeline;

namespace EzForensics.Core.Results;

/// <summary>Order of the entries in the Results case tree: super timeline output first, then folders, then files (each by name).</summary>
public static class CaseTreeOrder
{
    /// <summary>True for the case's <c>Timeline</c> folder and for <c>SuperTimeline_*.csv</c> and <c>Timeline_MACB_*.csv</c> files.</summary>
    public static bool IsSuperTimeline(string name, bool isDirectory) => isDirectory
        ? name.Equals(TimelineFiles.TimelineFolderName, StringComparison.OrdinalIgnoreCase)
        : TimelineFiles.IsTimelineOutput(name);

    /// <summary>
    /// True for the files the case tree lists: CSVs only. EZ Forensics' own sidecars (<c>*.ezflags.json</c>,
    /// <c>*.ezstats.json</c>), manifests, logs and other non-CSV files are not shown.
    /// </summary>
    public static bool IsListedFile(string fileName) =>
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && !fileName.EndsWith(FileStatsCache.SidecarSuffix, StringComparison.OrdinalIgnoreCase)
        && !fileName.EndsWith(RowFlagStore.SidecarSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for folders the case tree never lists: <c>logs</c>, <c>SystemInfo</c> (shown as Computer Details) and every dot-folder (<c>.ezstaging</c> job staging, <c>.ezcache</c>
    /// combined views), whether or not the Hidden attribute is set.
    /// </summary>
    public static bool IsHiddenFolder(string name) =>
        name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("SystemInfo", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.');

    /// <summary>The <c>_Case</c> folder: results whose source host could not be identified.</summary>
    public static bool IsCaseWide(string name) => Case.CaseIndex.IsCaseWideFolder(name);

    /// <summary>Display name of the <c>_Case</c> node: results whose source host could not be identified (every other result is under its host).</summary>
    public const string CaseWideDisplayName = "Case-wide (unattributed)";
    public const string CaseWideTooltip = "Results whose source host could not be identified";

    /// <summary>
    /// The host a case-level <c>&lt;case&gt;\Timeline</c> of an earlier version is shown under: the only host folder; null with none or
    /// several (the folder then stays at the top of the tree, never moved into <c>_Case</c>).
    /// </summary>
    public static string? LegacyTimelineHost(IReadOnlyList<string> hostFolders) => hostFolders.Count == 1 ? hostFolders[0] : null;

    /// <summary>0 = super timeline, 1 = folder, 2 = file.</summary>
    public static int Rank(string name, bool isDirectory) =>
        IsSuperTimeline(name, isDirectory) ? 0 : isDirectory ? 1 : 2;

    /// <summary>Sorts one level of the tree. Without a last-write time, super timeline CSVs are ordered by name, descending.</summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> name, Func<T, bool> isDirectory) =>
        Sort(items, name, isDirectory, null);

    /// <summary>
    /// Sorts one level of the tree: the Timeline folder, then super timeline CSVs newest first (by
    /// <paramref name="lastWriteUtc"/>, name as the tie-breaker; output names carry no timestamp any more, so
    /// <c>SuperTimeline_EZ (2).csv</c> is newer than <c>SuperTimeline_EZ.csv</c>), then folders, then files, each by name.
    /// </summary>
    public static List<T> Sort<T>(IEnumerable<T> items, Func<T, string> name, Func<T, bool> isDirectory, Func<T, DateTime>? lastWriteUtc)
    {
        var list = items.Select(i => (Item: i, Name: name(i), Dir: isDirectory(i))).ToList();
        var cmp = StringComparer.OrdinalIgnoreCase;
        var timelines = list.Where(x => !x.Dir && IsSuperTimeline(x.Name, false));
        var orderedTimelines = lastWriteUtc == null
            ? timelines.OrderByDescending(x => x.Name, cmp)
            : timelines.OrderByDescending(x => lastWriteUtc(x.Item)).ThenBy(x => x.Name, cmp);
        return list.Where(x => x.Dir && IsSuperTimeline(x.Name, true)).OrderBy(x => x.Name, cmp)
            .Concat(orderedTimelines)
            .Concat(list.Where(x => x.Dir && !IsSuperTimeline(x.Name, true) && !IsCaseWide(x.Name)).OrderBy(x => x.Name, cmp))
            .Concat(list.Where(x => x.Dir && IsCaseWide(x.Name)))   // "Case-wide" node after the hosts

            .Concat(list.Where(x => !x.Dir && !IsSuperTimeline(x.Name, false)).OrderBy(x => x.Name, cmp))
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Header-click sort of one level. <paramref name="defaultOrder"/> is the level in its default order (<see cref="Sort{T}(IEnumerable{T}, Func{T, string}, Func{T, bool}, Func{T, DateTime}?)"/>).
    /// With <paramref name="key"/> null (the Name column) ascending keeps the default order and descending reverses it.
    /// Otherwise items are ordered by key; items without a value (null, shown as "-") go last in both directions,
    /// and ties keep the default order. With <paramref name="pinSuperTimeline"/> (top level) the super timeline
    /// entries stay first, in their default order.
    /// </summary>
    public static List<T> SortBy<T>(IReadOnlyList<T> defaultOrder, Func<T, string> name, Func<T, bool> isDirectory,
        Func<T, IComparable?>? key, bool descending, bool pinSuperTimeline)
    {
        var indexed = defaultOrder.Select((x, i) => (Item: x, Index: i)).ToList();
        var pinned = pinSuperTimeline
            ? indexed.Where(x => IsSuperTimeline(name(x.Item), isDirectory(x.Item))).ToList()
            : new List<(T Item, int Index)>();
        var rest = indexed.Except(pinned).ToList();
        IEnumerable<(T Item, int Index)> sorted;
        if (key == null)
            sorted = descending ? rest.OrderByDescending(x => x.Index) : rest.OrderBy(x => x.Index);
        else
        {
            var withKey = rest.Select(x => (x.Item, x.Index, Key: key(x.Item))).ToList();
            var valued = withKey.Where(x => x.Key != null);
            var ordered = descending
                ? valued.OrderByDescending(x => x.Key, Comparer<IComparable?>.Default).ThenBy(x => x.Index)
                : valued.OrderBy(x => x.Key, Comparer<IComparable?>.Default).ThenBy(x => x.Index);
            sorted = ordered.Concat(withKey.Where(x => x.Key == null).OrderBy(x => x.Index))
                .Select(x => (x.Item, x.Index));
        }
        return pinned.Concat(sorted).Select(x => x.Item).ToList();
    }
}
