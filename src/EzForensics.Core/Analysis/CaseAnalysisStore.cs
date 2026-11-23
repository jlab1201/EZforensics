using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Analysis;

/// <summary>
/// The case analysis file: <c>&lt;case root&gt;\.ezforensics\analysis.json</c>, versioned, written atomically (temp file, then rename).
/// When the case folder is not writable it falls back to <c>&lt;DataRoot&gt;\analysis\&lt;folder hash&gt;\analysis.json</c>, the same way flags do.
/// A file written by a newer version is never overwritten (<see cref="ReadOnlyReason"/>); an unreadable file is kept as <c>.corrupt</c> before a new one is written.
/// </summary>
public sealed class CaseAnalysisStore
{
    public const string FolderName = ".ezforensics";
    public const string FileName = "analysis.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly object _gate = new();

    private CaseAnalysisStore(string root, string primaryPath, string fallbackPath)
    {
        Root = root; PrimaryPath = primaryPath; FallbackPath = fallbackPath; Path = primaryPath;
    }

    public string Root { get; }
    public string PrimaryPath { get; }
    public string FallbackPath { get; }
    /// <summary>Where the data was loaded from / will be saved.</summary>
    public string Path { get; private set; }
    public bool UsingFallback { get; private set; }
    public CaseAnalysis Data { get; private set; } = new();
    /// <summary>Set when the file could not be read or is from a newer version; null otherwise.</summary>
    public string? LoadWarning { get; private set; }
    /// <summary>Set when the file is from a newer version: saving is refused.</summary>
    public string? ReadOnlyReason { get; private set; }

    public static string PathFor(string caseRoot) => System.IO.Path.Combine(System.IO.Path.GetFullPath(caseRoot), FolderName, FileName);

    public static string DefaultFallbackDir => System.IO.Path.Combine(AppPaths.DataRoot, "analysis");

    public static string FallbackPathFor(string caseRoot, string fallbackDir)
    {
        var key = System.IO.Path.GetFullPath(caseRoot).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return System.IO.Path.Combine(fallbackDir, hash, FileName);
    }

    public static CaseAnalysisStore Open(string caseRoot, string? fallbackDir = null)
    {
        var full = System.IO.Path.GetFullPath(caseRoot);
        var s = new CaseAnalysisStore(full, PathFor(full), FallbackPathFor(full, fallbackDir ?? DefaultFallbackDir));
        s.Load();
        return s;
    }

    /// <summary>True when a file exists for the case (primary or fallback).</summary>
    public static bool Exists(string caseRoot, string? fallbackDir = null) =>
        File.Exists(PathFor(caseRoot)) || File.Exists(FallbackPathFor(caseRoot, fallbackDir ?? DefaultFallbackDir));

    private void Load()
    {
        foreach (var (p, fb) in new[] { (PrimaryPath, false), (FallbackPath, true) })
        {
            if (!File.Exists(p)) continue;
            try
            {
                var text = File.ReadAllText(p);
                var node = JsonNode.Parse(text) ?? throw new JsonException("empty");
                CaseAnalysis? d;
                bool migrated = false;
                if (node["version"]?.GetValue<int>() == 1)
                {
                    var backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p)!, "analysis.v1.json");
                    if (!File.Exists(backup)) File.Copy(p, backup);
                    d = CaseAnalysisMigration.FromV1(node, DateTimeOffset.UtcNow);
                    migrated = true;
                }
                else d = JsonSerializer.Deserialize<CaseAnalysis>(text, Json);
                if (d == null) throw new JsonException("empty");
                Path = p; UsingFallback = fb;
                if (d.Version > CaseAnalysis.CurrentVersion)
                {
                    ReadOnlyReason = $"analysis.json is version {d.Version}; this app understands up to {CaseAnalysis.CurrentVersion}. It is shown but not changed.";
                    LoadWarning = ReadOnlyReason;
                }
                Data = Normalize(d);
                if (migrated && ReadOnlyReason == null) Save();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoadWarning = $"Could not read {p}: {ex.Message}";
                EzForensics.Core.Diagnostics.CoreLog.Warn("Could not read " + p, ex);
                try { File.Copy(p, p + ".corrupt", overwrite: true); }
                catch (Exception cex) { EzForensics.Core.Diagnostics.CoreLog.Warn("Could not keep a .corrupt copy of " + p, cex); }
            }
        }
        Path = PrimaryPath;
    }

    private static CaseAnalysis Normalize(CaseAnalysis d)
    {
        d.Workspace ??= new(); d.Workspace.Findings ??= new(); d.Items ??= new(); d.KeyEvents ??= new(); d.Meta ??= new();
        foreach (var i in d.Items)
        {
            i.Mitre ??= new(); i.SourceRows ??= new(); i.RowEdits ??= new(); i.RowEdits.Added ??= new(); i.RowEdits.Removed ??= new(); i.RowEdits.PerRow ??= new();
            i.DupDecisions ??= new(); i.EditedFields ??= new(); i.History ??= new(); i.Source ??= new();
        }
        foreach (var k in d.Workspace.Findings) { k.Mitre ??= new(); k.Rows ??= new(); }
        foreach (var k in d.KeyEvents) { k.Mitre ??= new(); k.FindingIds ??= new(); k.EditedFields ??= new(); k.SkippedFindingIds ??= new(); k.History ??= new(); k.Source ??= new(); }
        return d;
    }

    /// <summary>Writes the data atomically. Falls back to the per-user folder when the case folder is read-only.</summary>
    public void Save()
    {
        lock (_gate)
        {
            if (ReadOnlyReason != null) throw new InvalidOperationException(ReadOnlyReason);
            Data.Version = CaseAnalysis.CurrentVersion;
            var json = JsonSerializer.Serialize(Data, Json);
            try { WriteAtomic(Path, json); }
            catch (Exception ex) when (!UsingFallback && (ex is UnauthorizedAccessException or IOException))
            {
                UsingFallback = true; Path = FallbackPath;
                WriteAtomic(Path, json);
            }
        }
    }

    /// <summary>Deep copy of the data (for previews and tests).</summary>
    public static CaseAnalysis Clone(CaseAnalysis d) =>
        JsonSerializer.Deserialize<CaseAnalysis>(JsonSerializer.Serialize(d, Json), Json)!;

    public static string ToJson(CaseAnalysis d) => JsonSerializer.Serialize(d, Json);

    private static void WriteAtomic(string path, string content)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
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
}
