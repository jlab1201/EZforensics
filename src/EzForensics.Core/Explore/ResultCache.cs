using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Explore;

/// <summary>
/// Per-file tool output keyed by content: <c>&lt;case&gt;\_explore\results\&lt;sha256&gt;\&lt;tool&gt;.json</c>, so the same file found in
/// several sources is analysed once. Only successful runs are stored. Concurrent requests for the same key share one run.
/// </summary>
public sealed class ResultCache
{
    private static readonly Regex Sha256Rx = new("^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex ToolRx = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ResultCache(string caseDir) => Root = ExplorePaths.ResultsDir(caseDir);

    /// <summary><c>&lt;case&gt;\_explore\results</c>.</summary>
    public string Root { get; }

    public string PathFor(string sha256, string tool)
    {
        if (!Sha256Rx.IsMatch(sha256 ?? "")) throw new ArgumentException("Not a SHA-256 hex string.", nameof(sha256));
        if (!ToolRx.IsMatch(tool ?? "")) throw new ArgumentException("Invalid tool key.", nameof(tool));
        return Path.Combine(Root, sha256.ToLowerInvariant(), tool + ".json");
    }

    /// <summary>The stored output, or null when missing or unreadable.</summary>
    public ExploreToolOutput? TryGet(string sha256, string tool)
    {
        var p = PathFor(sha256, tool);
        try
        {
            if (!File.Exists(p)) return null;
            var o = JsonSerializer.Deserialize<ExploreToolOutput>(File.ReadAllText(p), Json);
            return o == null ? null : o with { FromCache = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Stores <paramref name="output"/> atomically (failed runs are not stored).</summary>
    public void Save(string sha256, ExploreToolOutput output)
    {
        if (!output.Ok) return;
        var p = PathFor(sha256, output.Tool);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(output with { FromCache = false }, Json), new UTF8Encoding(false));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Removes one stored result (or every tool's result for the hash when <paramref name="tool"/> is null).</summary>
    public void Invalidate(string sha256, string? tool = null)
    {
        try
        {
            if (tool != null) { File.Delete(PathFor(sha256, tool)); return; }
            var dir = Path.GetDirectoryName(PathFor(sha256, "x"))!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The cached output for (<paramref name="sha256"/>, <paramref name="tool"/>), else runs <paramref name="run"/>, stores a successful
    /// result and returns it. <paramref name="refresh"/> ignores the cached copy.
    /// </summary>
    public async Task<ExploreToolOutput> GetOrRunAsync(string sha256, string tool, Func<CancellationToken, Task<ExploreToolOutput>> run,
        bool refresh = false, CancellationToken ct = default)
    {
        var key = PathFor(sha256, tool);
        if (!refresh && TryGet(sha256, tool) is { } hit) return hit;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && TryGet(sha256, tool) is { } hit2) return hit2;
            var o = await run(ct).ConfigureAwait(false);
            if (!string.Equals(o.Tool, tool, StringComparison.Ordinal)) o = o with { Tool = tool };
            try { Save(sha256, o); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // read-only case: still return the result
            return o;
        }
        finally { gate.Release(); }
    }
}
