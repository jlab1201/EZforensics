using System.Diagnostics.Eventing.Reader;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.Services;

/// <summary>Reads the records of one event ID (and provider) from an .evtx file with the Windows event log reader (XPath query, file path mode).</summary>
public sealed class EventLogSampleSource : IEvtxSampleSource
{
    public IEnumerable<EvtxSampleRecord> Read(string evtxPath, int eventId, string? provider, CancellationToken ct)
    {
        var filterProvider = provider != null && !provider.Contains('\'');
        var query = filterProvider
            ? $"*[System[Provider[@Name='{provider}'] and (EventID={eventId})]]"
            : $"*[System[(EventID={eventId})]]";
        using var reader = new EventLogReader(new EventLogQuery(evtxPath, PathType.FilePath, query));
        EventRecord? rec;
        while ((rec = ReadNext(reader)) != null)
        {
            using (rec)
            {
                ct.ThrowIfCancellationRequested();
                if (provider != null && !filterProvider && !string.Equals(rec.ProviderName, provider, StringComparison.OrdinalIgnoreCase)) continue;
                string xml;
                try { xml = rec.ToXml(); }
                catch (EventLogException) { continue; }
                yield return new EvtxSampleRecord(rec.RecordId ?? 0, EvtxMapEvaluator.Normalize(xml), evtxPath);
            }
        }
    }

    private static EventRecord? ReadNext(EventLogReader reader)
    {
        try { return reader.ReadEvent(); }
        catch (EventLogException) { return null; }
    }
}
