using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Results;

/// <summary>Identity of a CSV file: size, last write time (UTC) and SHA-256 of the first 64 KB.</summary>
public sealed record CsvFingerprint(long Size, DateTime LastWriteUtc, string HeadSha256)
{
    public const int HeadBytes = 64 * 1024;

    public static CsvFingerprint Compute(string path)
    {
        var fi = new FileInfo(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[HeadBytes];
        int got = 0, r;
        while (got < buf.Length && (r = fs.Read(buf, got, buf.Length - got)) > 0) got += r;
        var hash = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, got)));
        return new CsvFingerprint(fi.Length, fi.LastWriteTimeUtc, hash);
    }

    public bool Matches(long size, DateTime lastWriteUtc, string? headSha256) =>
        size == Size && lastWriteUtc.ToUniversalTime() == LastWriteUtc &&
        string.Equals(headSha256, HeadSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>On-disk format of a flag sidecar (<c>&lt;csv name&gt;.ezflags.json</c>).</summary>
public sealed class RowFlagFile
{
    public const int CurrentVersion = 3;
    public int Version { get; set; } = CurrentVersion;
    public string CsvFile { get; set; } = "";
    public long CsvSize { get; set; }
    public DateTime CsvLastWriteUtc { get; set; }
    public string CsvHeadSha256 { get; set; } = "";
    /// <summary>All flagged original (file) row indices, 0 = first data row, ascending (version 1 readers still see every flag here).</summary>
    public List<long> Rows { get; set; } = new();
    /// <summary>Rows per flag. Version 3: keyed by flag id (<c>{ "malicious": [..], "custom-1a2b3c4d": [..] }</c>); version 2: keyed by colour name
    /// (<c>red</c>, <c>blue</c>, ...), migrated on load by <see cref="FlagCatalog.MigrateLegacy"/>. A row listed in <see cref="Rows"/> only is a version 1 flag (<see cref="FlagCatalog.LegacyDefaultId"/>).</summary>
    public Dictionary<string, List<long>>? Colors { get; set; }
}

/// <summary>
/// Flagged rows of one CSV, keyed on the original file row index (so flags survive filtering and sorting).
/// Persisted to a sidecar next to the CSV (<c>Foo.csv</c> → <c>Foo.ezflags.json</c>), written atomically after a short
/// debounce. When the CSV folder is not writable the store falls back to <c>%LOCALAPPDATA%\EzForensicsData\flags\&lt;hash&gt;.json</c>.
/// Flags saved for a different version of the CSV (size, last write time or head hash changed) are dropped with a warning.
/// Each flagged row has one flag id (<see cref="FlagCatalog"/>); version 1 sidecars (plain row list) load as Malicious (was red), version 2 colour names are mapped to ids and rewritten as version 3.
/// Thread safe.
/// </summary>
public sealed class RowFlagStore : IDisposable
{
    public const string SidecarSuffix = ".ezflags.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<long, string> _rows = new();
    private readonly CsvFingerprint _fingerprint;
    private readonly string _fallbackPath;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private bool _dirty;
    private bool _disposed;

    private RowFlagStore(string csvPath, CsvFingerprint fp, string fallbackPath, TimeSpan debounce)
    {
        CsvPath = csvPath;
        _fingerprint = fp;
        _fallbackPath = fallbackPath;
        _debounce = debounce;
        SidecarPath = SidecarPathFor(csvPath);
        _timer = new Timer(_ => SaveQuietly(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string DefaultFallbackDir =>
        EzForensics.Core.Settings.AppPaths.FlagsDir;

    public static string SidecarPathFor(string csvPath) => Path.ChangeExtension(Path.GetFullPath(csvPath), null) + SidecarSuffix;

    public static string FallbackPathFor(string csvPath, string fallbackDir)
    {
        var key = Path.GetFullPath(csvPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(fallbackDir, hash + ".json");
    }

    public string CsvPath { get; }
    /// <summary>Where the flags are (or will be) saved.</summary>
    public string SidecarPath { get; private set; }
    public bool UsingFallback { get; private set; }
    /// <summary>Set when saved flags were dropped because the CSV changed.</summary>
    public string? StaleWarning { get; private set; }
    public Exception? LastSaveError { get; private set; }

    /// <summary>Raised (on any thread) after the set of flagged rows changes.</summary>
    public event Action? Changed;

    public int Count { get { lock (_gate) return _rows.Count; } }

    public static RowFlagStore Open(string csvPath, string? fallbackDir = null, TimeSpan? debounce = null)
    {
        var fp = CsvFingerprint.Compute(csvPath);
        var store = new RowFlagStore(csvPath, fp, FallbackPathFor(csvPath, fallbackDir ?? DefaultFallbackDir), debounce ?? TimeSpan.FromMilliseconds(500));
        store.Load();
        return store;
    }

    private void Load()
    {
        foreach (var (path, fallback) in new[] { (SidecarPath, false), (_fallbackPath, true) })
        {
            RowFlagFile? f;
            try
            {
                if (!File.Exists(path)) continue;
                f = JsonSerializer.Deserialize<RowFlagFile>(File.ReadAllText(path), Json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                EzForensics.Core.Diagnostics.CoreLog.Warn("Could not read row flags " + path, ex);
                continue;
            }
            if (f == null) continue;
            if (fallback) { SidecarPath = path; UsingFallback = true; }
            if (_fingerprint.Matches(f.CsvSize, f.CsvLastWriteUtc, f.CsvHeadSha256))
            {
                bool legacy = f.Version < 3;
                foreach (var r in f.Rows) if (r >= 0) _rows[r] = FlagCatalog.LegacyDefaultId;
                // Row flags of the retired "Spectre evidence" flag (older builds wrote it from Findings) are dropped; the examiner's own flags stay.
                HashSet<long>? dropped = null, colored = null;
                if (f.Colors != null)
                    foreach (var (name, rows) in f.Colors)
                    {
                        if (rows == null || string.IsNullOrWhiteSpace(name)) continue;
                        var id = FlagCatalog.IdFromStored(name, legacy);
                        if (id == FlagCatalog.RetiredSpectreEvidenceId) { dropped ??= new(); foreach (var r in rows) if (r >= 0) dropped.Add(r); continue; }
                        colored ??= new();
                        foreach (var r in rows) if (r >= 0) { _rows[r] = id; colored.Add(r); }
                    }
                if (dropped != null)
                {
                    foreach (var r in dropped) if (colored == null || !colored.Contains(r)) _rows.Remove(r);   // the flat Rows list held them too
                    _dirty = true; ScheduleSave();
                }
                if (legacy && _rows.Count > 0) { _dirty = true; ScheduleSave(); } // rewrite as version 3 (ids)
            }
            else if (f.Rows.Count > 0)
            {
                StaleWarning = $"{f.Rows.Count:N0} saved flag(s) were dropped because {Path.GetFileName(CsvPath)} changed since they were saved.";
                _dirty = true;
                ScheduleSave();
            }
            return;
        }
    }

    /// <summary>Flag id used by <see cref="Set"/>, <see cref="Toggle"/> and <see cref="SetMany"/> when flagging.</summary>
    public string DefaultFlagId { get; set; } = FlagCatalog.DefaultId;

    public bool IsFlagged(long row) { lock (_gate) return _rows.ContainsKey(row); }

    /// <summary>The row's flag id, or null when it is not flagged.</summary>
    public string? GetFlagId(long row) { lock (_gate) return _rows.TryGetValue(row, out var c) ? c : null; }

    /// <summary>Sets one row's flag; returns true when it changed. Flagging keeps the colour of a row that is already flagged.</summary>
    public bool Set(long row, bool flagged) => SetMany(new[] { row }, flagged) > 0;

    public bool Toggle(long row)
    {
        bool now;
        lock (_gate) now = !_rows.ContainsKey(row);
        Set(row, now);
        return now;
    }

    /// <summary>Flags (with <see cref="DefaultFlagId"/>; rows already flagged keep their colour) or unflags many rows; returns how many changed.</summary>
    public int SetMany(IEnumerable<long> rows, bool flagged)
    {
        int changed = 0;
        lock (_gate)
        {
            foreach (var r in rows)
            {
                if (r < 0) continue;
                if (flagged ? _rows.TryAdd(r, DefaultFlagId) : _rows.Remove(r)) changed++;
            }
            if (changed > 0) _dirty = true;
        }
        if (changed > 0) { ScheduleSave(); Changed?.Invoke(); }
        return changed;
    }

    /// <summary>Gives every row the flag <paramref name="flagId"/> (null = clear the flag); returns how many rows changed.</summary>
    public int SetFlag(IEnumerable<long> rows, string? flagId)
    {
        if (flagId != null && string.IsNullOrWhiteSpace(flagId)) throw new ArgumentException("Empty flag id", nameof(flagId));
        int changed = 0;
        lock (_gate)
        {
            foreach (var r in rows)
            {
                if (r < 0) continue;
                if (flagId is not { } c) { if (_rows.Remove(r)) changed++; }
                else if (!_rows.TryGetValue(r, out var old) || old != c) { _rows[r] = c; changed++; }
            }
            if (changed > 0) _dirty = true;
        }
        if (changed > 0) { ScheduleSave(); Changed?.Invoke(); }
        return changed;
    }

    /// <summary>Number of rows with flag <paramref name="flagId"/>.</summary>
    public int CountOf(string flagId) { lock (_gate) return _rows.Values.Count(c => c == flagId); }

    /// <summary>Rows per flag id (only ids in use).</summary>
    public Dictionary<string, int> Counts()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_gate) foreach (var c in _rows.Values) d[c] = d.TryGetValue(c, out var n) ? n + 1 : 1;
        return d;
    }

    /// <summary>A copy of row to flag id (safe to hand to a background filter or the exporter).</summary>
    public Dictionary<long, string> SnapshotFlags() { lock (_gate) return new Dictionary<long, string>(_rows); }

    public void Clear()
    {
        bool any;
        lock (_gate) { any = _rows.Count > 0; _rows.Clear(); if (any) _dirty = true; }
        if (any) { ScheduleSave(); Changed?.Invoke(); }
    }

    /// <summary>A copy of the flagged rows (safe to hand to a background filter).</summary>
    public HashSet<long> Snapshot() { lock (_gate) return new HashSet<long>(_rows.Keys); }

    /// <summary>Flagged rows, ascending; with <paramref name="flagIds"/> only the rows with those flags (null or empty = all).</summary>
    public long[] ToSortedArray(IReadOnlyCollection<string>? flagIds = null)
    {
        long[] a;
        lock (_gate)
            a = flagIds is { Count: > 0 } ? _rows.Where(kv => flagIds.Contains(kv.Value)).Select(kv => kv.Key).ToArray() : _rows.Keys.ToArray();
        Array.Sort(a);
        return a;
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        try { _timer.Change(_debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void SaveQuietly()
    {
        try { Flush(); } catch (Exception ex) { LastSaveError = ex; }
    }

    /// <summary>Writes pending changes now (atomically: temp file, then rename). No-op when nothing changed.</summary>
    public void Flush()
    {
        lock (_saveGate)
        {
            RowFlagFile file;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                file = new RowFlagFile
                {
                    CsvFile = Path.GetFileName(CsvPath),
                    CsvSize = _fingerprint.Size,
                    CsvLastWriteUtc = _fingerprint.LastWriteUtc,
                    CsvHeadSha256 = _fingerprint.HeadSha256,
                    Rows = _rows.Keys.OrderBy(r => r).ToList(),
                    Colors = _rows.GroupBy(kv => kv.Value).OrderBy(g => g.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(r => r).ToList()),
                };
            }
            var json = JsonSerializer.Serialize(file, Json);
            try
            {
                WriteAtomic(SidecarPath, json);
            }
            catch (Exception ex) when (!UsingFallback && (ex is UnauthorizedAccessException or IOException))
            {
                // CSV folder is read-only (evidence share, mounted image): use the per-user fallback.
                UsingFallback = true;
                SidecarPath = _fallbackPath;
                try { WriteAtomic(SidecarPath, json); }
                catch { lock (_gate) _dirty = true; throw; }
            }
            catch
            {
                lock (_gate) _dirty = true;
                throw;
            }
            LastSaveError = null;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        SaveQuietly();
    }
}
