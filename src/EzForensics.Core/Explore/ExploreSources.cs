using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Explore;

/// <summary>Where Explore keeps its per-case data: <c>&lt;case&gt;\_explore\</c>.</summary>
public static class ExplorePaths
{
    public const string FolderName = "_explore";

    public static string Root(string caseDir) => Path.Combine(caseDir, FolderName);
    public static string SourcesFile(string caseDir) => Path.Combine(Root(caseDir), "sources.json");
    public static string ResultsDir(string caseDir) => Path.Combine(Root(caseDir), "results");
    public static string ExportsDir(string caseDir) => Path.Combine(Root(caseDir), "exports");
}

public enum ExploreSourceKind
{
    /// <summary>The case folder itself; always listed, never persisted.</summary>
    Case,
    Folder,
    File,
}

/// <summary>A folder or single file the user browses in Explore.</summary>
public sealed record ExploreSource(string Id, ExploreSourceKind Kind, string Path, string Label, string? AddedUtc = null)
{
    [JsonIgnore] public bool IsCase => Kind == ExploreSourceKind.Case;
}

/// <summary>
/// The sources of one case: the case folder (auto-listed) plus folders and files the user added, persisted in
/// <c>&lt;case&gt;\_explore\sources.json</c>. Not thread-safe; the App owns one instance per open case.
/// </summary>
public sealed class ExploreSources
{
    public const string CaseSourceId = "case";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<ExploreSource> _added = new();

    private ExploreSources(string caseDir) => CaseDir = Path.GetFullPath(caseDir);

    public string CaseDir { get; }

    public ExploreSource CaseSource => new(CaseSourceId, ExploreSourceKind.Case, CaseDir, Path.GetFileName(CaseDir.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : CaseDir);

    /// <summary>The case folder first, then added sources in the order they were added.</summary>
    public IReadOnlyList<ExploreSource> All => (CaseHidden ? _added : new[] { CaseSource }.Concat(_added)).ToList();

    /// <summary>True when the user removed the case folder from the list (it is only hidden; see <see cref="ShowCase"/>).</summary>
    public bool CaseHidden { get; private set; }

    /// <summary>Only the user-added sources.</summary>
    public IReadOnlyList<ExploreSource> Added => _added;

    /// <summary>Loads <c>sources.json</c>; a missing or unreadable file gives just the case source.</summary>
    public static ExploreSources Load(string caseDir)
    {
        var s = new ExploreSources(caseDir);
        try
        {
            var p = ExplorePaths.SourcesFile(caseDir);
            if (File.Exists(p) && JsonSerializer.Deserialize<SourcesFile>(File.ReadAllText(p), Json) is { } f)
            {
                s.CaseHidden = f.HideCase;
                foreach (var e in f.Sources ?? new())
                    if (e.Kind != ExploreSourceKind.Case && !string.IsNullOrWhiteSpace(e.Path) && !string.IsNullOrWhiteSpace(e.Id)
                        && s.Find(e.Path) == null)
                        s._added.Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return s;
    }

    /// <summary>
    /// Adds a folder or file (kind detected from disk) and saves. Returns the existing source when the path is already listed
    /// (or is the case folder). Throws <see cref="FileNotFoundException"/> when the path does not exist.
    /// </summary>
    public ExploreSource Add(string path, string? label = null)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        if (Find(full) is { } existing)
        {
            if (existing.IsCase) ShowCase();
            return existing;
        }
        ExploreSourceKind kind = Directory.Exists(full) ? ExploreSourceKind.Folder
            : File.Exists(full) ? ExploreSourceKind.File
            : throw new FileNotFoundException("The source does not exist.", full);
        var src = new ExploreSource(Guid.NewGuid().ToString("N")[..12], kind, full,
            string.IsNullOrWhiteSpace(label) ? DefaultLabel(full) : label.Trim(), DateTime.UtcNow.ToString("o"));
        _added.Add(src);
        Save();
        return src;
    }

    /// <summary>Removes an added source and saves; removing the case source hides it instead. True when something changed.</summary>
    public bool Remove(string id)
    {
        if (id == CaseSourceId)
        {
            if (CaseHidden) return false;
            CaseHidden = true;
            Save();
            return true;
        }
        int n = _added.RemoveAll(s => s.Id == id);
        if (n > 0) Save();
        return n > 0;
    }

    /// <summary>Brings the hidden case folder back into the list and saves.</summary>
    public void ShowCase()
    {
        if (!CaseHidden) return;
        CaseHidden = false;
        Save();
    }

    /// <summary>The source whose path equals <paramref name="path"/> (case-insensitive, full path), or null.</summary>
    public ExploreSource? Find(string path)
    {
        var full = Norm(path);
        return new[] { CaseSource }.Concat(_added).FirstOrDefault(s => Norm(s.Path) == full);
    }

    /// <summary>The source that contains <paramref name="path"/> (deepest match), or null.</summary>
    public ExploreSource? Owner(string path)
    {
        var full = Norm(path);
        return All.Where(s => full == Norm(s.Path) || full.StartsWith(Norm(s.Path) + "\\", StringComparison.Ordinal))
                  .OrderByDescending(s => s.Path.Length).FirstOrDefault();
    }

    /// <summary>Writes <c>sources.json</c> atomically.</summary>
    public void Save()
    {
        var p = ExplorePaths.SourcesFile(CaseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new SourcesFile { Sources = _added.ToList(), HideCase = CaseHidden }, Json), new UTF8Encoding(false));
        File.Move(tmp, p, overwrite: true);
    }

    private static string DefaultLabel(string full)
    {
        var n = Path.GetFileName(full.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(n) ? full : n;
    }

    private static string Norm(string p)
    {
        try { p = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return p.TrimEnd('\\', '/').ToUpperInvariant();
    }

    private sealed class SourcesFile
    {
        public int Version { get; set; } = 1;
        public List<ExploreSource>? Sources { get; set; }
        public bool HideCase { get; set; }
    }
}
