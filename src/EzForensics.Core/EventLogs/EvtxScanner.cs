namespace EzForensics.Core.EventLogs;

/// <summary>Reads the events of one .evtx file (channel, provider, ID per record). Windows implementation: App (EventLogReader).</summary>
public interface IEvtxEventSource
{
    IEnumerable<(string Channel, string Provider, int EventId)> Read(string evtxPath, CancellationToken ct);
}

public sealed record EvtxScanProgress(int FilesDone, int FilesTotal, string CurrentFile);

public sealed record EvtxScanResult(IReadOnlyList<EvtxFoundEvent> Events, int FilesScanned, IReadOnlyList<string> Errors);

/// <summary>Counts channels / provider / event IDs in the .evtx files of a folder. Off the UI thread, cancellable; an unreadable file is reported, not fatal.</summary>
public static class EvtxScanner
{
    public static EvtxScanResult Scan(string folder, IEvtxEventSource source, IProgress<EvtxScanProgress>? progress, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(folder, "*.evtx", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var counts = new Dictionary<EvtxEventKey, int>();
        var errors = new List<string>();
        int done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new EvtxScanProgress(done, files.Count, Path.GetFileName(f)));
            try
            {
                foreach (var (channel, provider, id) in source.Read(f, ct))
                {
                    var key = new EvtxEventKey(channel, provider, id);
                    counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            done++;
        }
        progress?.Report(new EvtxScanProgress(done, files.Count, ""));
        var events = counts.Select(kv => new EvtxFoundEvent(kv.Key.Channel, kv.Key.Provider, kv.Key.EventId, kv.Value))
            .OrderBy(e => e.Channel, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.EventId).ToList();
        return new EvtxScanResult(events, files.Count, errors);
    }
}
