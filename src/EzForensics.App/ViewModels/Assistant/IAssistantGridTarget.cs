using EzForensics.Core.Assistant;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>The results grid as the assistant sees it. Implemented by <see cref="ResultsViewModel"/>; faked in tests.</summary>
public interface IAssistantGridTarget
{
    /// <summary>The CSV open in the grid (null = none).</summary>
    string? CurrentCsv { get; }
    /// <summary>Current grid state. Safe to call from any thread.</summary>
    GridContext GetContext();
    IReadOnlyList<string> FlagIds { get; }
    string FlagLabel(string flagId);
    /// <summary>Sets the Analyze filter as if the examiner had built it. UI thread only.</summary>
    void ApplyFilter(FilterGroup filter);
    /// <summary>Gives source rows of the open CSV a flag (null = remove the flag). Returns an error text, or null when applied. UI thread only.</summary>
    string? ApplyFlag(IReadOnlyList<long> rows, string? flagId);
    /// <summary>Opens a case CSV in the grid and then applies <paramref name="filter"/> (validated against that file's columns). Returns an error text, or null when done. UI thread only.</summary>
    Task<string?> OpenFileAsync(string path, FilterGroup? filter);

    // ---- what auto-mode needs to undo and to do the other grid actions (UI thread only)

    /// <summary>Opaque snapshot of the filter state (expression, search text, saved-filter name, flag filter) for <see cref="RestoreFilter"/>.</summary>
    object CaptureFilter();
    void RestoreFilter(object snapshot);
    /// <summary>Sets the flag filter to rows with this flag only, so the examiner sees what was just flagged.</summary>
    void ShowFlag(string flagId);
    /// <summary>Clears every filter and the search box.</summary>
    void ClearFilter();
    /// <summary>The current flag id of each row (null = not flagged), in the order of <paramref name="rows"/>.</summary>
    IReadOnlyList<string?> GetFlags(IReadOnlyList<long> rows);
    (string? Column, bool Descending) GetSort();
    /// <summary>Sorts by <paramref name="column"/> (null = file order). Returns an error text, or null when done.</summary>
    Task<string?> SortAsync(string? column, bool descending);
    /// <summary>Selects a source row and scrolls to it. Returns an error text (e.g. the row is hidden by the filter), or null when done.</summary>
    string? GoToRow(long row);
}
