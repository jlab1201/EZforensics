using System.Globalization;
using EzForensics.Core.Execution;
using Sylvan.Data.Csv;

namespace EzForensics.Core.EventLogs;

/// <summary>One sample event for the map editor: its XML (namespaces removed) and the .evtx it came from.</summary>
public sealed record EvtxSampleRecord(long RecordId, string Xml, string SourceFile);

/// <summary>Reads the records of one event ID (and provider) from an .evtx file. Windows implementation: App (EventLogReader).</summary>
public interface IEvtxSampleSource
{
    IEnumerable<EvtxSampleRecord> Read(string evtxPath, int eventId, string? provider, CancellationToken ct);
}

public static class EvtxSamples
{
    /// <summary>Default number of sample records.</summary>
    public const int DefaultMax = 20;

    /// <summary>Windows file name of a channel's log: '/' becomes <c>%4</c> (<c>Microsoft-Windows-Sysmon%4Operational.evtx</c>).</summary>
    public static string LogFileName(string channel) => channel.Replace("/", "%4") + ".evtx";

    /// <summary>
    /// Up to <paramref name="max"/> records of the event from <paramref name="path"/> (an .evtx file, or a folder searched recursively). Files named after
    /// the channel are read first; the rest only when those had too few. Unreadable files go to <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyList<EvtxSampleRecord> Load(string path, EvtxEventKey key, IEvtxSampleSource source, int max, ICollection<string>? errors, CancellationToken ct)
    {
        List<string> files;
        if (File.Exists(path)) files = new List<string> { path };
        else if (Directory.Exists(path))
            files = Directory.EnumerateFiles(path, "*.evtx", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        else return Array.Empty<EvtxSampleRecord>();
        var logName = LogFileName(key.Channel);
        var ordered = files.Where(f => Path.GetFileName(f).Equals(logName, StringComparison.OrdinalIgnoreCase))
            .Concat(files.Where(f => !Path.GetFileName(f).Equals(logName, StringComparison.OrdinalIgnoreCase))).ToList();
        var result = new List<EvtxSampleRecord>();
        foreach (var f in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var r in source.Read(f, key.EventId, key.Provider.Length == 0 ? null : key.Provider, ct))
                {
                    if (!ChannelOf(r.Xml).Equals(key.Channel, StringComparison.OrdinalIgnoreCase)) break;   // same ID in another log
                    result.Add(r);
                    if (result.Count >= max) return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors?.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Channel of event XML (namespaces removed), empty when missing.</summary>
    public static string ChannelOf(string xml)
    {
        int i = xml.IndexOf("<Channel>", StringComparison.Ordinal);
        if (i < 0) return "";
        int j = xml.IndexOf("</Channel>", i, StringComparison.Ordinal);
        return j < 0 ? "" : System.Net.WebUtility.HtmlDecode(xml[(i + 9)..j]);
    }
}

/// <summary>A preview value that differs from what EvtxECmd wrote.</summary>
public sealed record EvtxCheckMismatch(long RecordId, string Column, string Preview, string Tool);

/// <summary>Outcome of <see cref="EvtxMapCheck.RunAsync"/>. <see cref="Ran"/> is false when EvtxECmd could not run (see <see cref="Note"/>).</summary>
public sealed record EvtxCheckResult(bool Ran, string Note, int Compared, IReadOnlyList<EvtxCheckMismatch> Mismatches, IReadOnlyList<string> ToolMessages)
{
    public bool Ok => Ran && Compared > 0 && Mismatches.Count == 0;
}

/// <summary>
/// Accuracy check of a new map: runs the installed EvtxECmd on the sample's .evtx file(s) with <c>--inc &lt;id&gt;</c> and a temporary Maps folder that
/// holds only the new map, then compares its CSV columns with the editor's preview, record by record (EventRecordId).
/// </summary>
public static class EvtxMapCheck
{
    public static async Task<EvtxCheckResult> RunAsync(string exePath, EvtxMap map, IReadOnlyList<EvtxSampleRecord> samples, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(exePath)) return new EvtxCheckResult(false, "EvtxECmd is not installed: check skipped.", 0, Array.Empty<EvtxCheckMismatch>(), Array.Empty<string>());
        if (samples.Count == 0) return new EvtxCheckResult(false, "No sample records: check skipped.", 0, Array.Empty<EvtxCheckMismatch>(), Array.Empty<string>());
        var temp = Path.Combine(Path.GetTempPath(), "EzForensics-mapcheck-" + Guid.NewGuid().ToString("N")[..8]);
        var mapsDir = Path.Combine(temp, "Maps");
        var outDir = Path.Combine(temp, "out");
        var messages = new List<string>();
        var mismatches = new List<EvtxCheckMismatch>();
        int compared = 0;
        try
        {
            Directory.CreateDirectory(mapsDir);
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(mapsDir, EvtxMapFiles.FileName(map.Key)), EvtxMapYaml.Write(map));
            var preview = samples.ToDictionary(s => (s.SourceFile, s.RecordId), s => EvtxMapEvaluator.Evaluate(map, s.Xml));
            int n = 0;
            foreach (var file in samples.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
            {
                var csvName = $"check{n++}.csv";
                var args = new List<string> { "-f", file, "--maps", mapsDir, "--csv", outDir, "--csvf", csvName, "--inc", map.EventId.ToString(CultureInfo.InvariantCulture) };
                var res = await ProcessRunner.RunAsync(exePath, args, Path.GetDirectoryName(exePath), (_, line) =>
                {
                    if (line.Contains("map", StringComparison.OrdinalIgnoreCase) && (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Maps loaded", StringComparison.OrdinalIgnoreCase)))
                        lock (messages) messages.Add(line.Trim());
                }, timeout, ct).ConfigureAwait(false);
                if (res.Cancelled) throw new OperationCanceledException(ct);
                if (res.TimedOut) return new EvtxCheckResult(false, "EvtxECmd timed out.", compared, mismatches, messages);
                var csv = Path.Combine(outDir, csvName);
                if (!File.Exists(csv)) { messages.Add($"EvtxECmd wrote no CSV for {Path.GetFileName(file)} (exit code {res.ExitCode})."); continue; }
                foreach (var row in ReadRows(csv))
                {
                    if (!row.TryGetValue("EventRecordId", out var idText) || !long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                    if (!preview.TryGetValue((file, id), out var want)) continue;
                    if (row.TryGetValue("Provider", out var prov) && map.Provider.Length > 0 && !prov.Equals(map.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                    compared++;
                    foreach (var kv in want)
                    {
                        var got = row.TryGetValue(kv.Key, out var g) ? g : "";
                        if (!string.Equals(Clean(got), Clean(kv.Value), StringComparison.Ordinal))
                            mismatches.Add(new EvtxCheckMismatch(id, kv.Key, kv.Value, got));
                    }
                }
            }
            var note = compared == 0
                ? "EvtxECmd ran, but none of the sample records came back in its CSV."
                : mismatches.Count == 0
                    ? $"EvtxECmd matches the preview ({compared} record(s))."
                    : $"{mismatches.Count} value(s) differ from the preview in {mismatches.Select(m => m.RecordId).Distinct().Count()} of {compared} record(s).";
            return new EvtxCheckResult(true, note, compared, mismatches, messages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new EvtxCheckResult(false, "Check failed: " + ex.Message, compared, mismatches, messages);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (Exception) { }
        }
    }

    /// <summary>EvtxECmd trims and collapses line breaks in the CSV; compare on that.</summary>
    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    internal static IEnumerable<Dictionary<string, string>> ReadRows(string csvPath)
    {
        using var reader = CsvDataReader.Create(csvPath, new CsvDataReaderOptions { HasHeaders = true });
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        while (reader.Read())
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length && i < reader.RowFieldCount; i++) d[names[i]] = reader.GetString(i);
            yield return d;
        }
    }
}
