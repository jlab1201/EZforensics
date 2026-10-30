using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Results;

/// <summary>One column of a saved view: name, visibility and optional width (device-independent pixels; null = default).</summary>
public sealed class SavedViewColumn
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double? Width { get; set; }
}

/// <summary>A saved column layout: columns in display order, optionally the Pivot grouping. No filters.</summary>
public sealed class SavedView
{
    public string Name { get; set; } = "";
    public string CategorySignature { get; set; } = "";
    /// <summary>Columns in display order.</summary>
    public List<SavedViewColumn> Columns { get; set; } = new();
    /// <summary>Pivot group columns, outermost first; null = the view does not touch grouping.</summary>
    public List<PivotGroupSpec>? Groups { get; set; }
    /// <summary>
    /// Keys of the collapsed pivot groups, in memory only (the view builder's working copy). Never written or exported: a view always opens
    /// fully collapsed and the collapse state during analysis is session-only.
    /// </summary>
    [JsonIgnore]
    public List<string>? CollapsedGroups { get; set; }
    public DateTime SavedUtc { get; set; }

    /// <summary>Short summary, e.g. "12 of 30 columns · grouped by EventId, Channel".</summary>
    public string Summary()
    {
        var vis = Columns.Count(c => c.Visible);
        var s = $"{vis} of {Columns.Count} columns";
        if (Groups is { Count: > 0 }) s += " · grouped by " + string.Join(", ", Groups.Select(g => g.Column));
        return s;
    }
}

/// <summary>All saved views, grouped by category (same shape as <see cref="SavedFilterLibrary"/>). Names are unique per category (case-insensitive).</summary>
public sealed class SavedViewLibrary
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedFilterCategory> Categories { get; set; } = new();
    public List<SavedView> Views { get; set; } = new();

    public SavedFilterCategory? Category(string signature) =>
        Categories.FirstOrDefault(c => Same(c.Signature, signature));

    public SavedFilterCategory EnsureCategory(string signature, string name, IEnumerable<string> columns)
    {
        var c = Category(signature);
        if (c != null) return c;
        c = new SavedFilterCategory { Signature = signature, Name = name, Columns = columns.ToList() };
        Categories.Add(c);
        return c;
    }

    /// <summary>Views of the category, ordered by name (natural number order).</summary>
    public List<SavedView> ForSignature(string signature) =>
        Views.Where(v => Same(v.CategorySignature, signature)).OrderBy(v => v.Name, SavedFilterLibrary.NaturalComparer.Instance).ToList();

    public SavedView? Find(string signature, string name) =>
        Views.FirstOrDefault(v => Same(v.CategorySignature, signature) && Same(v.Name, name));

    /// <summary>Adds the view, replacing the one with the same category and name.</summary>
    public void Put(SavedView view)
    {
        Views.RemoveAll(v => Same(v.CategorySignature, view.CategorySignature) && Same(v.Name, view.Name));
        Views.Add(view);
    }

    public bool Remove(string signature, string name) =>
        Views.RemoveAll(v => Same(v.CategorySignature, signature) && Same(v.Name, name)) > 0;

    public bool Rename(string signature, string oldName, string newName)
    {
        newName = newName.Trim();
        var v = Find(signature, oldName);
        if (v == null || newName.Length == 0) return false;
        var other = Find(signature, newName);
        if (other != null && !ReferenceEquals(other, v)) return false;
        v.Name = newName;
        return true;
    }

    public bool RenameCategory(string signature, string newName)
    {
        newName = newName.Trim();
        var c = Category(signature);
        if (c == null || newName.Length == 0) return false;
        c.Name = newName;
        return true;
    }

    public bool RemoveCategory(string signature)
    {
        var n = Categories.RemoveAll(c => Same(c.Signature, signature));
        Views.RemoveAll(v => Same(v.CategorySignature, signature));
        return n > 0;
    }

    /// <summary><c>View N</c> with N one above the highest number used in the category ("View 1" when none).</summary>
    public string NextNumberedName(string signature)
    {
        var max = 0;
        foreach (var v in ForSignature(signature))
        {
            if (!v.Name.StartsWith("View ", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(v.Name.AsSpan(5).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }
        return "View " + (max + 1);
    }

    /// <summary>Merges the <paramref name="selected"/> views of <paramref name="incoming"/> (see <see cref="SavedFilterLibrary.Merge"/>).</summary>
    public MergeResult Merge(SavedViewLibrary incoming, IEnumerable<SavedView> selected, ConflictMode mode)
    {
        int added = 0, replaced = 0, skipped = 0;
        foreach (var v in selected)
        {
            var inCat = incoming.Category(v.CategorySignature);
            EnsureCategory(v.CategorySignature, inCat?.Name is { Length: > 0 } n ? n : "Imported layout", inCat?.Columns ?? new List<string>());
            var copy = new SavedView
            {
                Name = v.Name, CategorySignature = v.CategorySignature, Columns = v.Columns, Groups = v.Groups,
                SavedUtc = v.SavedUtc,
            };
            if (Find(copy.CategorySignature, copy.Name) == null) { Put(copy); added++; continue; }
            switch (mode)
            {
                case ConflictMode.Replace: Put(copy); replaced++; break;
                case ConflictMode.Skip: skipped++; break;
                default:
                    var baseName = copy.Name;
                    for (int i = 2; ; i++)
                    {
                        copy.Name = $"{baseName} ({i})";
                        if (Find(copy.CategorySignature, copy.Name) == null) break;
                    }
                    Put(copy); added++;
                    break;
            }
        }
        return new MergeResult(added, replaced, skipped);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads and writes <see cref="SavedViewLibrary"/> (<c>saved-views.json</c>; atomic write; a missing, corrupt or newer-version file gives an empty library).</summary>
public static class SavedViewStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly object Gate = new();

    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataRoot, "saved-views.json");

    public static SavedViewLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new SavedViewLibrary();
            var lib = JsonSerializer.Deserialize<SavedViewLibrary>(File.ReadAllText(path), Json);
            if (lib == null) return new SavedViewLibrary();
            if (lib.Version > SavedViewLibrary.CurrentVersion) { KeepCopy(path); return new SavedViewLibrary(); }
            Sanitize(lib);
            return lib;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            if (ex is JsonException or NotSupportedException) KeepCopy(path);
            return new SavedViewLibrary();
        }
    }

    /// <summary>A file that cannot be used is copied to <c>.bad</c> before the next Save replaces it with an empty library.</summary>
    private static void KeepCopy(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Sanitize(SavedViewLibrary lib)
    {
        lib.Categories ??= new();
        lib.Views ??= new();
        lib.Categories.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Signature));
        foreach (var c in lib.Categories) c.Columns ??= new();
        lib.Views.RemoveAll(v => v == null || string.IsNullOrEmpty(v.Name) || string.IsNullOrEmpty(v.CategorySignature));
        foreach (var v in lib.Views)
        {
            v.Columns ??= new();
            v.Columns.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Name));
            v.Groups?.RemoveAll(g => g == null || string.IsNullOrEmpty(g.Column));
        }
    }

    /// <summary>Kind marker of the exchange file (<c>*.ezviews.json</c>).</summary>
    public const string ExchangeKind = "ezforensics-saved-views";

    private sealed class Exchange
    {
        public int Version { get; set; }
        public string? Kind { get; set; }
        public List<SavedFilterCategory>? Categories { get; set; }
        public List<SavedView>? Views { get; set; }
    }

    /// <summary>Writes an exchange file with the given views and only the categories they use.</summary>
    public static void Export(SavedViewLibrary lib, IEnumerable<SavedView> views, string path)
    {
        var list = views.ToList();
        var ex = new Exchange { Version = SavedViewLibrary.CurrentVersion, Kind = ExchangeKind, Views = list };
        ex.Categories = lib.Categories
            .Where(c => list.Any(v => string.Equals(v.CategorySignature, c.Signature, StringComparison.OrdinalIgnoreCase))).ToList();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(ex, Json), new UTF8Encoding(false));
    }

    /// <summary>Reads an exchange file; false with a readable <paramref name="error"/> for a wrong kind, newer version or unreadable file.</summary>
    public static bool TryImport(string path, out SavedViewLibrary lib, out string error)
    {
        lib = new SavedViewLibrary();
        error = "";
        try
        {
            var ex = JsonSerializer.Deserialize<Exchange>(File.ReadAllText(path), Json);
            if (ex == null) { error = "The file is empty."; return false; }
            if (!string.Equals(ex.Kind, ExchangeKind, StringComparison.Ordinal))
            { error = "This is not a saved-views file (wrong or missing kind)."; return false; }
            if (ex.Version > SavedViewLibrary.CurrentVersion || ex.Version < 1)
            { error = $"This file uses version {ex.Version}, which this app does not understand. Update EzForensics to import it."; return false; }
            lib = new SavedViewLibrary { Version = ex.Version, Categories = ex.Categories ?? new(), Views = ex.Views ?? new() };
            Sanitize(lib);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = e is JsonException ? "The file is corrupt or not valid JSON." : "The file could not be read: " + e.Message;
            return false;
        }
    }

    public static void Save(SavedViewLibrary lib, string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
            var json = JsonSerializer.Serialize(lib, Json);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
