using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Results;

/// <summary>A named kind of CSV layout (by header signature) that saved filters belong to.</summary>
public sealed class SavedFilterCategory
{
    public string Signature { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The layout's column names (for display and column matching).</summary>
    public List<string> Columns { get; set; } = new();
}

/// <summary>One saved filter: the filter-builder expression plus the search-box text.</summary>
public sealed class SavedFilter
{
    public string Name { get; set; } = "";
    public string CategorySignature { get; set; } = "";
    public AnalyzeFilterNode? Expression { get; set; }
    public string GlobalText { get; set; } = "";
    public DateTime SavedUtc { get; set; }
}

/// <summary>What Merge does when an imported name already exists in the category.</summary>
public enum ConflictMode { Replace, KeepBoth, Skip }

/// <summary>Counts of an import merge: new (incl. renamed copies), replaced, skipped.</summary>
public readonly record struct MergeResult(int Added, int Replaced, int Skipped);

/// <summary>All saved filters, grouped by category. Filter names are unique per category (case-insensitive).</summary>
public sealed class SavedFilterLibrary
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedFilterCategory> Categories { get; set; } = new();
    public List<SavedFilter> Filters { get; set; } = new();

    public SavedFilterCategory? Category(string signature) =>
        Categories.FirstOrDefault(c => Same(c.Signature, signature));

    /// <summary>The category for <paramref name="signature"/>, created with <paramref name="name"/> and <paramref name="columns"/> when missing.</summary>
    public SavedFilterCategory EnsureCategory(string signature, string name, IEnumerable<string> columns)
    {
        var c = Category(signature);
        if (c != null) return c;
        c = new SavedFilterCategory { Signature = signature, Name = name, Columns = columns.ToList() };
        Categories.Add(c);
        return c;
    }

    /// <summary>Filters of the category, ordered by name (natural number order: Filter 2 before Filter 10).</summary>
    public List<SavedFilter> ForSignature(string signature) =>
        Filters.Where(f => Same(f.CategorySignature, signature)).OrderBy(f => f.Name, NaturalComparer.Instance).ToList();

    public SavedFilter? Find(string signature, string name) =>
        Filters.FirstOrDefault(f => Same(f.CategorySignature, signature) && Same(f.Name, name));

    /// <summary>Adds the filter, replacing the one with the same category and name.</summary>
    public void Put(SavedFilter filter)
    {
        Filters.RemoveAll(f => Same(f.CategorySignature, filter.CategorySignature) && Same(f.Name, filter.Name));
        Filters.Add(filter);
    }

    public bool Remove(string signature, string name) =>
        Filters.RemoveAll(f => Same(f.CategorySignature, signature) && Same(f.Name, name)) > 0;

    /// <summary>Renames a filter; false when it does not exist, the new name is blank, or another filter in the category has it.</summary>
    public bool Rename(string signature, string oldName, string newName)
    {
        newName = newName.Trim();
        var f = Find(signature, oldName);
        if (f == null || newName.Length == 0) return false;
        var other = Find(signature, newName);
        if (other != null && !ReferenceEquals(other, f)) return false;
        f.Name = newName;
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

    /// <summary>Removes the category and all its filters.</summary>
    public bool RemoveCategory(string signature)
    {
        var n = Categories.RemoveAll(c => Same(c.Signature, signature));
        Filters.RemoveAll(f => Same(f.CategorySignature, signature));
        return n > 0;
    }

    /// <summary><c>Filter N</c> with N one above the highest number used in the category (gaps are not reused; "Filter 1" when none).</summary>
    public string NextNumberedName(string signature)
    {
        var max = 0;
        foreach (var f in ForSignature(signature))
        {
            if (!f.Name.StartsWith("Filter ", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(f.Name.AsSpan(7).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        }
        return "Filter " + (max + 1);
    }

    /// <summary>
    /// Merges the <paramref name="selected"/> filters of <paramref name="incoming"/> into this library. Categories missing here are added with their
    /// name and columns (existing local names are kept). A name that already exists in the category is replaced, kept as "Name (2)", "(3)"... or skipped.
    /// </summary>
    public MergeResult Merge(SavedFilterLibrary incoming, IEnumerable<SavedFilter> selected, ConflictMode mode)
    {
        int added = 0, replaced = 0, skipped = 0;
        foreach (var f in selected)
        {
            var inCat = incoming.Category(f.CategorySignature);
            EnsureCategory(f.CategorySignature, inCat?.Name is { Length: > 0 } n ? n : "Imported layout", inCat?.Columns ?? new List<string>());
            var copy = new SavedFilter
            {
                Name = f.Name, CategorySignature = f.CategorySignature, Expression = f.Expression, GlobalText = f.GlobalText, SavedUtc = f.SavedUtc,
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

    /// <summary>Case-insensitive comparer that orders digit runs by value.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}

/// <summary>Reads and writes <see cref="SavedFilterLibrary"/> (atomic write; a missing, corrupt or newer-version file gives an empty library).</summary>
public static class SavedFilterStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly object Gate = new();

    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataRoot, "saved-filters.json");

    public static SavedFilterLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new SavedFilterLibrary();
            var lib = JsonSerializer.Deserialize<SavedFilterLibrary>(File.ReadAllText(path), Json);
            if (lib == null) return new SavedFilterLibrary();
            if (lib.Version > SavedFilterLibrary.CurrentVersion) { KeepCopy(path); return new SavedFilterLibrary(); }
            Sanitize(lib);
            return lib;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            if (ex is JsonException or NotSupportedException) KeepCopy(path);
            return new SavedFilterLibrary();
        }
    }

    /// <summary>A file that cannot be used is copied to <c>.bad</c> before the next Save replaces it with an empty library.</summary>
    private static void KeepCopy(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Sanitize(SavedFilterLibrary lib)
    {
        lib.Categories ??= new();
        lib.Filters ??= new();
        lib.Categories.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Signature));
        foreach (var c in lib.Categories) c.Columns ??= new();
        lib.Filters.RemoveAll(f => f == null || string.IsNullOrEmpty(f.Name) || string.IsNullOrEmpty(f.CategorySignature));
    }

    /// <summary>Kind marker of the exchange file (<c>*.ezfilters.json</c>).</summary>
    public const string ExchangeKind = "ezforensics-saved-filters";

    private sealed class Exchange
    {
        public int Version { get; set; }
        public string? Kind { get; set; }
        public List<SavedFilterCategory>? Categories { get; set; }
        public List<SavedFilter>? Filters { get; set; }
    }

    /// <summary>Writes an exchange file with the given filters and only the categories they use.</summary>
    public static void Export(SavedFilterLibrary lib, IEnumerable<SavedFilter> filters, string path)
    {
        var list = filters.ToList();
        var ex = new Exchange { Version = SavedFilterLibrary.CurrentVersion, Kind = ExchangeKind, Filters = list };
        ex.Categories = lib.Categories
            .Where(c => list.Any(f => string.Equals(f.CategorySignature, c.Signature, StringComparison.OrdinalIgnoreCase))).ToList();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(ex, Json), new UTF8Encoding(false));
    }

    /// <summary>Reads an exchange file; false with a readable <paramref name="error"/> for a wrong kind, newer version or unreadable file.</summary>
    public static bool TryImport(string path, out SavedFilterLibrary lib, out string error)
    {
        lib = new SavedFilterLibrary();
        error = "";
        try
        {
            var ex = JsonSerializer.Deserialize<Exchange>(File.ReadAllText(path), Json);
            if (ex == null) { error = "The file is empty."; return false; }
            if (!string.Equals(ex.Kind, ExchangeKind, StringComparison.Ordinal))
            { error = "This is not a saved-filters file (wrong or missing kind)."; return false; }
            if (ex.Version > SavedFilterLibrary.CurrentVersion || ex.Version < 1)
            { error = $"This file uses version {ex.Version}, which this app does not understand. Update EzForensics to import it."; return false; }
            lib = new SavedFilterLibrary { Version = ex.Version, Categories = ex.Categories ?? new(), Filters = ex.Filters ?? new() };
            Sanitize(lib);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = e is JsonException ? "The file is corrupt or not valid JSON." : "The file could not be read: " + e.Message;
            return false;
        }
    }

    /// <summary>Writes the library atomically (temp file, then rename). Serialises writers.</summary>
    public static void Save(SavedFilterLibrary lib, string? path = null)
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

    /// <summary>The distinct column names (case-insensitive, first spelling kept) a filter tree refers to.</summary>
    public static IReadOnlyList<string> Columns(FilterNode n)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string col) { if (!string.IsNullOrEmpty(col) && col != FilterNode.AnyColumn && seen.Add(col)) list.Add(col); }
        void Walk(FilterNode x)
        {
            switch (x)
            {
                case FilterGroup g: foreach (var c in g.Children) Walk(c); break;
                case FilterCondition c: Add(c.Column); break;
                case FilterValueSet v: Add(v.Column); break;
                case FilterDateSet d: Add(d.Column); break;
            }
        }
        Walk(n);
        return list;
    }
}
