using System.Text;
using System.Text.Json;

namespace EzForensics.Core.Explore;

public sealed record ExifTag(string Name, string Value);

/// <summary>Tags of one ExifTool family-1 group (<c>System</c>, <c>File</c>, <c>EXE</c>, <c>EXIF</c>, <c>XMP-dc</c>...), in output order.</summary>
public sealed record ExifGroup(string Name, IReadOnlyList<ExifTag> Tags);

/// <summary>Parsed <c>exiftool -j -G1</c> output for one file.</summary>
public sealed record ExifResult(string? SourceFile, IReadOnlyList<ExifGroup> Groups)
{
    /// <summary>Value of <c>Group:Tag</c> (or just <c>Tag</c>, first group that has it); null when absent.</summary>
    public string? Get(string key)
    {
        int c = key.IndexOf(':');
        string? g = c > 0 ? key[..c] : null;
        string t = c > 0 ? key[(c + 1)..] : key;
        foreach (var grp in Groups)
        {
            if (g != null && !grp.Name.Equals(g, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var tag in grp.Tags)
                if (tag.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) return tag.Value;
        }
        return null;
    }

    public string? FileType => Get("File:FileType");
    public string? MimeType => Get("File:MIMEType");
    public string? Warning => Get("ExifTool:Warning");
    public string? Error => Get("ExifTool:Error");
}

/// <summary>
/// Runs ExifTool read-only: <c>exiftool -j -G1 -a -charset filename=utf8 -api LargeFileSupport=1 &lt;file&gt;</c>, with the arguments
/// passed in a UTF-8 <c>-@</c> argfile so non-ANSI evidence paths survive. Human-readable values (no <c>-n</c>), every duplicate tag
/// (<c>-a</c>), grouped by family 1 (<c>-G1</c>). ExifTool never writes unless asked to, and these options never ask.
/// </summary>
public sealed class ExifRunner
{
    public const string ToolKey = "exif";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public ExifRunner(string exifToolExe, string? version = null)
    {
        ExifToolExe = exifToolExe;
        Version = version;
    }

    public ExifRunner(ExploreToolLocator locator) : this(locator.ExifToolExe, locator.Version(ExploreTool.ExifTool)) { }

    public string ExifToolExe { get; }
    public string? Version { get; }

    /// <summary>Argfile lines (one argument per line) for <paramref name="file"/>.</summary>
    public static IReadOnlyList<string> BuildArgFile(string file) =>
        new[] { "-j", "-G1", "-a", "-charset", "filename=utf8", "-api", "LargeFileSupport=1", file };

    /// <summary>Runs ExifTool on <paramref name="file"/> (a reparse point or folder faults the task with <see cref="IOException"/>).</summary>
    public async Task<ExploreToolOutput> RunAsync(string file, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var full = ExploreToolProcess.CheckedEvidencePath(file);
        if (full.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("File names with line breaks are not supported.", nameof(file));
        var argFile = Path.Combine(Path.GetTempPath(), $"ezf-exif-{Guid.NewGuid():N}.args");
        await File.WriteAllLinesAsync(argFile, BuildArgFile(full), new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            return await ExploreToolProcess.RunAsync(ToolKey, Version, ExifToolExe, new[] { "-@", argFile }, timeout ?? DefaultTimeout, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(argFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Cached by SHA-256 in <paramref name="cache"/>; runs ExifTool only on a miss (or when <paramref name="refresh"/>).</summary>
    public async Task<(ExploreToolOutput Output, ExifResult? Result)> GetAsync(ResultCache cache, string file, string sha256,
        bool refresh = false, CancellationToken ct = default)
    {
        var o = await cache.GetOrRunAsync(sha256, ToolKey, c => RunAsync(file, null, c), refresh, ct).ConfigureAwait(false);
        return (o, o.Ok ? Parse(o.Raw) : null);
    }

    /// <summary>
    /// Parses <c>exiftool -j -G1</c> JSON (an array with one object of <c>"Group:Tag": value</c>); null when not ExifTool JSON.
    /// Groups keep first-seen order; <c>SourceFile</c> is returned separately; arrays are joined with ", ", objects kept as JSON.
    /// </summary>
    public static ExifResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? source = null;
            var order = new List<string>();
            var groups = new Dictionary<string, List<ExifTag>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name == "SourceFile") { source = p.Value.GetString(); continue; }
                int c = p.Name.IndexOf(':');
                string g = c > 0 ? p.Name[..c] : "";
                string t = c > 0 ? p.Name[(c + 1)..] : p.Name;
                if (!groups.TryGetValue(g, out var list)) { groups[g] = list = new List<ExifTag>(); order.Add(g); }
                list.Add(new ExifTag(t, Value(p.Value)));
            }
            return new ExifResult(source, order.Select(g => new ExifGroup(g, groups[g])).ToList());
        }
        catch (JsonException) { return null; }
    }

    private static string Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Value)),
        _ => v.GetRawText(),
    };
}
