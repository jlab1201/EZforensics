namespace EzForensics.App.Services;

/// <summary>Default column order and hidden columns for a CSV with no saved layout. Pure: header names in, CSV column indices out.</summary>
public static class ColumnPresets
{
    /// <summary><see cref="Front"/>: CSV column indices to show first, in this order (the rest follow in file order). <see cref="Hide"/>: record-id columns to hide.</summary>
    public sealed record Preset(IReadOnlyList<int> Front, IReadOnlyList<int> Hide)
    {
        public static readonly Preset None = new(Array.Empty<int>(), Array.Empty<int>());
        public bool IsEmpty => Front.Count == 0 && Hide.Count == 0;
    }

    private static readonly string[] EvtxFront = { "TimeCreated", "EventId", "Channel", "Computer", "MapDescription", "UserName", "PayloadData1", "PayloadData2", "PayloadData3", "PayloadData4", "PayloadData5", "PayloadData6" };
    private static readonly string[] EvtxHide = { "RecordNumber", "EventRecordId" };
    private static readonly string[] MftFront = { "Created0x10", "FileName", "ParentPath", "Extension", "FileSize", "InUse", "IsDirectory" };
    private static readonly string[] MftHide = { "SequenceNumber", "ParentSequenceNumber" };
    private static readonly string[] PfFront = { "LastRun", "ExecutableName", "RunCount", "SourceFilename" };

    private static int Find(IReadOnlyList<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++) if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static Preset Pick(IReadOnlyList<string> headers)
    {
        if (headers.Count < 2) return Preset.None;
        bool Has(string n) => Find(headers, n) >= 0;

        if (Has("TimeCreated") && Has("EventId") && (Has("Channel") || Has("Provider"))) return Build(headers, EvtxFront, EvtxHide);   // EvtxECmd
        if (Has("EntryNumber") && Has("ParentEntryNumber") && Has("FileName")) return Build(headers, MftFront, MftHide);              // MFTECmd
        if (Has("ExecutableName") && Has("RunCount") && Has("LastRun")) return Build(headers, PfFront, Array.Empty<string>());      // PECmd

        // generic: the first column that looks like a time goes first
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (h.Contains("Time", StringComparison.OrdinalIgnoreCase) || h.Contains("Date", StringComparison.OrdinalIgnoreCase))
                return i == 0 ? Preset.None : new Preset(new[] { i }, Array.Empty<int>());
        }
        return Preset.None;
    }

    private static Preset Build(IReadOnlyList<string> headers, string[] front, string[] hide)
    {
        var f = front.Select(n => Find(headers, n)).Where(i => i >= 0).Distinct().ToList();
        var h = hide.Select(n => Find(headers, n)).Where(i => i >= 0).Except(f).ToList();
        if (h.Count >= headers.Count) h.Clear();
        return new Preset(f, h);
    }

    /// <summary>All CSV indices in display order: <see cref="Preset.Front"/> first, then the rest in file order.</summary>
    public static int[] DisplayOrder(Preset p, int count) =>
        p.Front.Where(i => (uint)i < (uint)count).Concat(Enumerable.Range(0, count).Where(i => !p.Front.Contains(i))).ToArray();
}
