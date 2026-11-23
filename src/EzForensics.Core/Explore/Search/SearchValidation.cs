using System.Text.RegularExpressions;

namespace EzForensics.Core.Explore.Search;

/// <summary>User-readable checks of <see cref="SearchOptions"/> before a search starts.</summary>
public static class SearchValidation
{
    /// <summary>Every problem found; empty when the search can run.</summary>
    /// <param name="checkRootsExist">Also report roots that do not exist (off for saving a search for later).</param>
    public static IReadOnlyList<string> Validate(SearchOptions o, bool checkRootsExist = true)
    {
        var e = new List<string>();

        var roots = o.Roots?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>();
        if (roots.Count == 0) e.Add("Choose at least one folder or file to search in.");
        foreach (var r in roots)
        {
            string full;
            try { full = Path.GetFullPath(r.Trim()); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                e.Add($"\"{r}\" is not a valid path: {ex.Message}");
                continue;
            }
            if (checkRootsExist && !Directory.Exists(full) && !File.Exists(full)) e.Add($"\"{r}\" does not exist.");
        }

        if (o.HasNameCriteria && !o.NameAsFilter)
        {
            switch (o.NameMode)
            {
                case NameMatchMode.Regex:
                    if (RegexError(o.NameText!, o.NameMatchCase) is { } nr) e.Add("File name regular expression: " + nr);
                    break;
                case NameMatchMode.Wildcard:
                    if (!SearchExpression.TryParse(o.NameText, true, true, out _, out var nw)) e.Add("File name: " + nw);
                    break;
                default:
                    if (!SearchExpression.TryParse(o.NameText, false, false, out _, out var nc)) e.Add("File name: " + nc);
                    break;
            }
        }

        if (o.HasContentCriteria)
        {
            if (o.Target == SearchTarget.Folders)
                e.Add("Containing text only applies to files: clear it or search for files too.");
            switch (o.ContentMode)
            {
                case ContentMatchMode.Regex:
                    var pattern = o.ContentWholeWord ? @"\b(?:" + o.ContentText + @")\b" : o.ContentText!;
                    if (RegexError(pattern, o.ContentMatchCase) is { } cr) e.Add("Containing text regular expression: " + cr);
                    break;
                case ContentMatchMode.Boolean:
                    if (!SearchExpression.TryParse(o.ContentText, false, false, out _, out var cb)) e.Add("Containing text: " + cb);
                    break;
            }
            if (o.MaxFileSizeForContent <= 0) e.Add("The content size limit must be more than 0 bytes.");
        }

        if (o.MinSize < 0) e.Add("Minimum size cannot be negative.");
        if (o.MaxSize < 0) e.Add("Maximum size cannot be negative.");
        if (o.MinSize is { } min && o.MaxSize is { } max && min > max) e.Add("Minimum size is larger than the maximum size.");
        Range(e, "Modified", o.ModifiedAfter, o.ModifiedBefore);
        Range(e, "Created", o.CreatedAfter, o.CreatedBefore);
        Range(e, "Accessed", o.AccessedAfter, o.AccessedBefore);

        var both = o.AttributesMustHave & o.AttributesMustNotHave;
        if (both != 0) e.Add($"Attribute {both} is both required and excluded.");
        if (!o.IncludeHidden && (o.AttributesMustHave & FileAttributes.Hidden) != 0) e.Add("Hidden items are required but hidden items are excluded.");
        if (!o.IncludeSystem && (o.AttributesMustHave & FileAttributes.System) != 0) e.Add("System items are required but system items are excluded.");

        if (o.MaxResults <= 0) e.Add("Maximum results must be at least 1.");
        if (o.MaxHitsPerFile <= 0) e.Add("Maximum hits per file must be at least 1.");
        if (o.ContextLines is < 0 or > 50) e.Add("Context lines must be between 0 and 50.");
        if (o.MaxParallelism is < 1 or > 16) e.Add("Parallel files must be between 1 and 16.");
        return e;
    }

    private static void Range(List<string> e, string what, DateTime? after, DateTime? before)
    {
        if (after is { } a && before is { } b && a > b) e.Add($"{what} after is later than {what.ToLowerInvariant()} before.");
    }

    /// <summary>The .NET parser message for a bad pattern; null when it compiles.</summary>
    public static string? RegexError(string pattern, bool matchCase)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant | (matchCase ? 0 : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
