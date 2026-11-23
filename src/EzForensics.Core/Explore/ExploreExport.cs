using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Explore;

/// <summary>One file to export and where it goes under the export folder.</summary>
public sealed record ExportItem(string SourcePath, string RelativePath);

/// <summary>One exported file in <c>manifest.json</c>.</summary>
public sealed class ExportedFile
{
    public string Source { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? Sha256 { get; set; }
    public string? CreatedUtc { get; set; }
    public string? ModifiedUtc { get; set; }
    /// <summary>The copy was re-read and its SHA-256 matches the source read.</summary>
    public bool Verified { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>Contents of <c>&lt;export&gt;\manifest.json</c>.</summary>
public sealed class ExportManifest
{
    public const string ManifestKind = "EzForensics.ExploreExport";

    public string Kind { get; set; } = ManifestKind;
    public string AppVersion { get; set; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public string Machine { get; set; } = Environment.MachineName;
    public string User { get; set; } = Environment.UserName;
    public bool IsElevated { get; set; } = ManifestWriter.DetectElevated();
    public string? CaseDir { get; set; }
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public List<ExportedFile> Files { get; set; } = new();
}

public sealed record ExportResult(string Folder, string ManifestPath, ExportManifest Manifest)
{
    public int Copied => Manifest.Files.Count(f => f.Error == null);
    public int Failed => Manifest.Files.Count(f => f.Error != null);
}

/// <summary>
/// Copies selected evidence files to <c>&lt;case&gt;\_explore\exports\&lt;yyyyMMdd-HHmmss&gt;\</c>, keeping their relative paths, and writes
/// <c>manifest.json</c> with MD5 / SHA-1 / SHA-256 of each file. Sources are read once (read-only, shared, never through a reparse point)
/// and hashed while copying; the copy is re-hashed to verify it. Only the main data stream is copied.
/// </summary>
public static class ExploreExport
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Relative export path for <paramref name="path"/>: <c>&lt;source label&gt;\&lt;path inside the source&gt;</c> when a source owns it,
    /// else the full path with the drive colon removed (<c>C\Users\...</c>).
    /// </summary>
    public static string RelativeFor(string path, ExploreSources? sources = null)
    {
        var full = Path.GetFullPath(path);
        if (sources?.Owner(full) is { } src)
        {
            var label = Sanitize(src.Label);
            if (src.Kind == ExploreSourceKind.File) return Path.Combine(label, Path.GetFileName(full));
            var rel = Path.GetRelativePath(src.Path, full);
            return rel == "." ? label : Path.Combine(label, rel);
        }
        var root = Path.GetPathRoot(full) ?? "";
        var rest = full[root.Length..];
        var drive = root.TrimEnd('\\', '/').Replace(":", "").Replace(@"\\?\", "").Replace(@"\\", "UNC\\");
        return string.IsNullOrEmpty(drive) ? rest : Path.Combine(drive, rest);
    }

    public static ExportResult Export(string caseDir, IReadOnlyList<ExportItem> items, IProgress<double>? progress = null,
        CancellationToken ct = default, DateTime? now = null)
    {
        var folder = NewFolder(ExplorePaths.ExportsDir(caseDir), (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss"));
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var manifest = new ExportManifest { CaseDir = caseDir };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var it = items[i];
            var rec = new ExportedFile { Source = it.SourcePath };
            manifest.Files.Add(rec);
            try
            {
                var rel = SafeRelative(it.RelativePath);
                var dest = Path.GetFullPath(Path.Combine(root, rel));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Unsafe export path: {it.RelativePath}");
                dest = Unique(dest, used);
                rec.RelativePath = Path.GetRelativePath(root, dest);
                CopyAndHash(it.SourcePath, dest, rec, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                rec.Error = ex.Message;
            }
            progress?.Report((i + 1.0) / Math.Max(1, items.Count));
        }
        var manifestPath = Path.Combine(folder, ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestWriter.JsonOptions), new UTF8Encoding(false));
        return new ExportResult(folder, manifestPath, manifest);
    }

    /// <summary>Exports <paramref name="paths"/> with relative paths from <see cref="RelativeFor"/>.</summary>
    public static ExportResult Export(ExploreSources sources, IEnumerable<string> paths, IProgress<double>? progress = null, CancellationToken ct = default) =>
        Export(sources.CaseDir, paths.Select(p => new ExportItem(p, RelativeFor(p, sources))).ToList(), progress, ct);

    public static ExportManifest? ReadManifest(string exportFolder)
    {
        try
        {
            return JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(Path.Combine(exportFolder, ManifestFileName)), ManifestWriter.ReadOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void CopyAndHash(string source, string dest, ExportedFile rec, CancellationToken ct)
    {
        var entry = FileEntry.FromPath(source) ?? throw new FileNotFoundException("File not found.", source);
        if (entry.IsDirectory) throw new IOException($"{source} is a folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var tmp = dest + ".part";
        try
        {
            using (var src = FileFactsReader.OpenEvidence(source, 1 << 20))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[1 << 20];
                int r;
                while ((r = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var span = buf.AsSpan(0, r);
                    md5.AppendData(span); sha1.AppendData(span); sha256.AppendData(span);
                    dst.Write(span);
                    size += r;
                }
            }
            File.Move(tmp, dest);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        rec.Size = size;
        rec.Md5 = Convert.ToHexString(md5.GetHashAndReset());
        rec.Sha1 = Convert.ToHexString(sha1.GetHashAndReset());
        rec.Sha256 = Convert.ToHexString(sha256.GetHashAndReset());
        rec.CreatedUtc = entry.CreatedUtc.ToString("o");
        rec.ModifiedUtc = entry.ModifiedUtc.ToString("o");
        try
        {
            File.SetCreationTimeUtc(dest, entry.CreatedUtc);
            File.SetLastWriteTimeUtc(dest, entry.ModifiedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        rec.Verified = string.Equals(ManifestWriter.Hash(dest, SHA256.Create()), rec.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewFolder(string exportsDir, string stamp)
    {
        var p = Path.Combine(exportsDir, stamp);
        for (int n = 2; Directory.Exists(p); n++) p = Path.Combine(exportsDir, $"{stamp}-{n}");
        Directory.CreateDirectory(p);
        return p;
    }

    private static string Unique(string dest, HashSet<string> used)
    {
        var p = dest;
        var dir = Path.GetDirectoryName(dest)!;
        var stem = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int n = 2; used.Contains(p) || File.Exists(p); n++) p = Path.Combine(dir, $"{stem} ({n}){ext}");
        used.Add(p);
        return p;
    }

    /// <summary>Drops rooted prefixes, <c>..</c> segments and characters Windows does not allow in names.</summary>
    private static string SafeRelative(string rel)
    {
        var parts = rel.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(Sanitize)
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 0) throw new ArgumentException("Empty export path.");
        return Path.Combine(parts);
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }
}
