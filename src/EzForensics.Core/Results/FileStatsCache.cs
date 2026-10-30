using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Results;

/// <summary>
/// Cache of <see cref="FileStats"/>: in memory, then a sidecar next to the file (<c>Foo.csv</c> → <c>Foo.csv.ezstats.json</c>),
/// then <c>%LOCALAPPDATA%\EzForensicsData\cache</c> when the folder is read-only. An entry is valid only while the file's size and
/// last-write time (UTC) and the schema version match. Thread safe.
/// </summary>
public sealed class FileStatsCache
{
    public const string SidecarSuffix = ".ezstats.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly ConcurrentDictionary<string, FileStats> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _fallbackDir;
    private readonly TimeSpan _settle;

    /// <param name="fallbackDir">Used when the sidecar cannot be written (default <see cref="DefaultFallbackDir"/>).</param>
    /// <param name="settleTime">Files written more recently than this (possibly still growing) are cached in memory only (default 5 s).</param>
    public FileStatsCache(string? fallbackDir = null, TimeSpan? settleTime = null)
    {
        _fallbackDir = fallbackDir ?? DefaultFallbackDir;
        _settle = settleTime ?? TimeSpan.FromSeconds(5);
    }

    public static string DefaultFallbackDir =>
        EzForensics.Core.Settings.AppPaths.CacheDir;

    public static string SidecarPathFor(string path) => Path.GetFullPath(path) + SidecarSuffix;

    public static string FallbackPathFor(string path, string fallbackDir)
    {
        var key = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(fallbackDir, hash + SidecarSuffix);
    }

    /// <summary>Valid cached stats of <paramref name="path"/> (memory, sidecar, fallback), or null.</summary>
    public FileStats? TryGet(string path)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        long size = fi.Length;
        var mtime = fi.LastWriteTimeUtc;
        var full = Path.GetFullPath(path);
        if (_memory.TryGetValue(full, out var m) && m.Matches(size, mtime)) return m;
        foreach (var p in new[] { SidecarPathFor(full), FallbackPathFor(full, _fallbackDir) })
        {
            var s = ReadFile(p);
            if (s != null && s.Matches(size, mtime))
            {
                _memory[full] = s;
                return s;
            }
        }
        return null;
    }

    /// <summary>Cached stats, or computes (one pass over the file) and stores them.</summary>
    public FileStats GetOrCompute(string path, CancellationToken ct, out bool fromCache)
    {
        var cached = TryGet(path);
        fromCache = cached != null;
        if (cached != null) return cached;
        var s = FileStatsCalculator.Compute(path, ct);
        Store(path, s);
        return s;
    }

    public FileStats GetOrCompute(string path, CancellationToken ct = default) => GetOrCompute(path, ct, out _);

    /// <summary>
    /// Keeps <paramref name="stats"/> in memory and writes the sidecar atomically (or the fallback when the folder is
    /// read-only). Returns the file written, or null (memory only: file still settling, or nothing writable).
    /// </summary>
    public string? Store(string path, FileStats stats)
    {
        var full = Path.GetFullPath(path);
        _memory[full] = stats;
        if (DateTime.UtcNow - stats.LastWriteUtc < _settle) return null;
        var text = JsonSerializer.Serialize(stats, Json);
        foreach (var (target, isFallback) in new[] { (SidecarPathFor(full), false), (FallbackPathFor(full, _fallbackDir), true) })
        {
            try
            {
                if (isFallback) Directory.CreateDirectory(_fallbackDir);
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try { File.Move(tmp, target, overwrite: true); }
                catch { TryDelete(tmp); throw; }
                if (!isFallback) TryDelete(FallbackPathFor(full, _fallbackDir));
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Drops the in-memory entry (sidecars stay; they are revalidated on read).</summary>
    public void Forget(string path) => _memory.TryRemove(Path.GetFullPath(path), out _);

    private static FileStats? ReadFile(string p)
    {
        try
        {
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<FileStats>(File.ReadAllText(p), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
