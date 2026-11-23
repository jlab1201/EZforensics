using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Explore.Search;

/// <summary>A named search kept for reuse.</summary>
public sealed record SavedSearch(string Name, SearchOptions Options, DateTime SavedUtc);

/// <summary>
/// Named searches in <c>&lt;data&gt;\explore\saved-searches.json</c>. Writes are atomic (temp file + replace). A corrupt file is renamed to
/// <c>saved-searches.corrupt-&lt;utc&gt;.json</c> and the list starts empty. Names are case-insensitive.
/// </summary>
public sealed class SavedSearches
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<SavedSearch> Searches { get; set; } = new();
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "explore", "saved-searches.json");

    public string FilePath { get; }

    /// <summary>Set when the last <see cref="Load"/> found a corrupt file: where it was moved.</summary>
    public string? CorruptBackupPath { get; private set; }

    public SavedSearches(string? filePath = null) => FilePath = filePath ?? DefaultPath;

    /// <summary>All saved searches, sorted by name; empty when the file is missing or corrupt.</summary>
    public IReadOnlyList<SavedSearch> Load()
    {
        CorruptBackupPath = null;
        if (!File.Exists(FilePath)) return Array.Empty<SavedSearch>();
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<SavedSearch>(); }
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(text, Json) ?? throw new JsonException("Empty file.");
            return model.Searches
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name) && s.Options != null)
                .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            BackUpCorrupt();
            return Array.Empty<SavedSearch>();
        }
    }

    public SavedSearch? Get(string name) =>
        Load().FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces <paramref name="name"/>.</summary>
    public SavedSearch Save(string name, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A saved search needs a name.", nameof(name));
        var item = new SavedSearch(name.Trim(), options, DateTime.UtcNow);
        var list = Load().Where(s => !string.Equals(s.Name, item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(item);
        Write(list);
        return item;
    }

    public bool Delete(string name)
    {
        var all = Load();
        var list = all.Where(s => !string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == all.Count) return false;
        Write(list);
        return true;
    }

    /// <summary>Replaces the whole file with <paramref name="searches"/>.</summary>
    public void Write(IEnumerable<SavedSearch> searches)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(dir);
        var model = new FileModel { Searches = searches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() };
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void BackUpCorrupt()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            var backup = Path.Combine(dir, Path.GetFileNameWithoutExtension(FilePath) + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json");
            File.Move(FilePath, backup);
            CorruptBackupPath = backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
