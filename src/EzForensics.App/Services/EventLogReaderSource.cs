using System.Diagnostics.Eventing.Reader;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.Services;

/// <summary>Reads (channel, provider, event ID) of every record of an .evtx file with the Windows event log reader (file path mode).</summary>
public sealed class EventLogReaderSource : IEvtxEventSource
{
    public IEnumerable<(string Channel, string Provider, int EventId)> Read(string evtxPath, CancellationToken ct)
    {
        using var reader = new EventLogReader(new EventLogQuery(evtxPath, PathType.FilePath));
        EventRecord? rec;
        while ((rec = ReadNext(reader)) != null)
        {
            using (rec)
            {
                ct.ThrowIfCancellationRequested();
                yield return (rec.LogName ?? "", rec.ProviderName ?? "", rec.Id);
            }
        }
    }

    private static EventRecord? ReadNext(EventLogReader reader)
    {
        try { return reader.ReadEvent(); }
        catch (EventLogException) { return null; }   // truncated / corrupt tail: keep what was counted
    }
}
