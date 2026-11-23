using System.Windows;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>The grid as the AI chat panel sees it: a read-only snapshot plus the two things the examiner can let it apply (a filter, flags).</summary>
public sealed partial class ResultsViewModel : IAssistantGridTarget
{
    private const int AssistantSampleRows = 20;
    private const int AssistantCellChars = 200;
    private const int AssistantSelectedRows = 10;

    public override SpectrePage AssistantPage => SpectrePage.Analyze;
    public override bool ShowsAssistant => true;

    /// <summary>Where the examiner is on Analyze (case, selected file, open CSV, case file inventory), sent with the chat message after "Page: Analyze".</summary>
    public override string? AssistantState()
    {
        var dir = CaseFolder;
        string Rel(string p) => dir != null && AssistantCaseTools.RelativeTo(dir, p) is { } r ? r : p;
        var sb = new System.Text.StringBuilder();
        if (dir == null) return sb.Append("Case: none open").ToString();
        sb.Append("Case: ").Append(Path.GetFileName(dir.TrimEnd('\\', '/'))).Append(" (").Append(dir).AppendLine(")");
        if (SelectedNode is { } n) sb.Append("Selected in case tree: ").Append(Rel(n.FullPath)).Append(n.IsFile ? "" : " (folder)").AppendLine();
        if (CurrentFile != null) sb.Append("Open CSV: ").AppendLine(Rel(CurrentFile));
        sb.Append(CaseInventory.AppStateText(dir));
        return sb.ToString();
    }

    async Task<string?> IAssistantGridTarget.OpenFileAsync(string path, FilterGroup? filter)
    {
        if (!File.Exists(path)) return "The file no longer exists.";
        if (CaseFolder is { } dir) await ShowCsvAsync(dir, path); else await OpenCsvAsync(path);
        if (_index == null || Rows == null || !string.Equals(CurrentFile, path, StringComparison.OrdinalIgnoreCase)) return "Could not open the file.";
        // rows are counted in the background after the file opens; a flag on a late row would be refused as out of range
        var idx = _index;
        try { await idx.IndexingCompleted.WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException) { return "The file is still being indexed; try Apply again in a moment."; }
        catch (OperationCanceledException) { return "Another file was opened meanwhile."; }
        if (!ReferenceEquals(idx, _index)) return "Another file was opened meanwhile.";
        if (filter == null) return null;
        if (AssistantGrid.MissingColumns(filter, Headers) is { } err) return err;
        ApplyBuilderResult((FilterGroup)filter.Clone(), "", null, keepFlagWhenAbsent: true);
        return null;
    }

    string? IAssistantGridTarget.CurrentCsv => _index != null ? CurrentFile : null;

    IReadOnlyList<string> IAssistantGridTarget.FlagIds => S.Settings.Flags.Select(f => f.Id).ToList();

    string IAssistantGridTarget.FlagLabel(string flagId) => FlagLabel(flagId);

    /// <summary>Called by the assistant's tools on a background thread: reads the grid on the UI thread.</summary>
    GridContext IAssistantGridTarget.GetContext()
    {
        var d = Application.Current?.Dispatcher;
        return d == null || d.CheckAccess() ? ReadAssistantContext() : d.Invoke(ReadAssistantContext);
    }

    private GridContext ReadAssistantContext()
    {
        var idx = _index;
        var rows = Rows;
        if (idx == null || rows == null || CurrentFile == null)
            return new GridContext(null, Array.Empty<string>(), 0, 0, "", Array.Empty<GridSampleRow>());
        static GridSampleRow Sample(RowItem r) => new(r.Source, r.Cells.Select(c => c.Length > AssistantCellChars ? c[..AssistantCellChars] : c).ToList());
        var sample = new List<GridSampleRow>();
        if (!rows.IsGrouped)
            for (int i = 0; i < Math.Min(rows.Count, AssistantSampleRows); i++)
                if (rows[i] is RowItem r) sample.Add(Sample(r));
        var selected = (AssistantSelection?.Invoke() ?? Array.Empty<RowItem>()).Take(AssistantSelectedRows).Select(Sample).ToList();
        return new GridContext(CurrentFile, Headers, idx.RowCount, rows.Count, FilterDisplayText, sample, selected);
    }

    /// <summary>Set by the grid view: the rows the examiner has selected (sent to the assistant with each message).</summary>
    public Func<IReadOnlyList<RowItem>>? AssistantSelection { get; set; }

    void IAssistantGridTarget.ApplyFilter(FilterGroup filter) =>
        ApplyBuilderResult((FilterGroup)filter.Clone(), "", null, keepFlagWhenAbsent: true); // replaces the expression like the filter builder's OK

    string? IAssistantGridTarget.ApplyFlag(IReadOnlyList<long> sourceRows, string? flagId)
    {
        if (_flags == null || _index == null) return "No CSV is open.";
        var err = (flagId == null ? null : AssistantGrid.ValidateFlag(flagId, S.Settings.Flags.Select(f => f.Id))) ?? AssistantGrid.ValidateRows(sourceRows, _index.RowCount);
        if (err != null) return err;
        if (flagId == null && sourceRows.Any(r => KeyEventTextOf(r) != null && _flags.IsFlagged(r)))
            return "Some of these rows are in a key event; they stay flagged. The examiner can unflag them in Analyze, which asks first.";
        _flags.SetFlag(sourceRows, flagId);
        AfterFlagEdit(sourceRows.ToList());
        return null;
    }

    IReadOnlyList<string?> IAssistantGridTarget.GetFlags(IReadOnlyList<long> rows) => rows.Select(r => _flags?.GetFlagId(r)).ToList();

    /// <summary>Everything "Clear all filters" resets, so Undo can put it back.</summary>
    private sealed record FilterSnapshot(FilterGroup Expression, string GlobalText, string? SavedName, bool? FlagFilter, IReadOnlySet<string> FlagIds, bool Enabled);

    object IAssistantGridTarget.CaptureFilter() => new FilterSnapshot(BuildExpression(), GlobalText, ActiveSavedFilter, FlagFilter, SelectedFlagIds, FiltersEnabled);

    void IAssistantGridTarget.RestoreFilter(object snapshot)
    {
        if (snapshot is not FilterSnapshot s) return;
        EditFilters(() =>
        {
            LoadExpression((FilterGroup)s.Expression.Clone());
            GlobalText = s.GlobalText;
            ActiveSavedFilter = s.SavedName;
            _syncingFlagOptions = true;
            try { foreach (var o in FlagOptions) o.IsSelected = s.FlagIds.Contains(o.Id); FlagFilter = s.FlagFilter; }
            finally { _syncingFlagOptions = false; }
            UpdateFlagFilterText();
            FiltersEnabled = s.Enabled;
        });
    }

    void IAssistantGridTarget.ShowFlag(string flagId) => SetFlagFilter(flagId);

    void IAssistantGridTarget.ClearFilter() => ClearFilters();

    (string? Column, bool Descending) IAssistantGridTarget.GetSort() => (SortColumn, SortDescending);

    async Task<string?> IAssistantGridTarget.SortAsync(string? column, bool descending)
    {
        if (_index == null) return "No CSV is open.";
        if (column != null && !Headers.Contains(column, StringComparer.OrdinalIgnoreCase)) return $"Unknown column '{column}'.";
        await SortAsync(column!, descending); // null = file order (ApplyFilterAsync checks SortColumn)
        return null;
    }

    /// <summary>Set by the grid view: selects the row at a display position and scrolls it into view.</summary>
    public Func<int, bool>? AssistantGoTo { get; set; }

    string? IAssistantGridTarget.GoToRow(long sourceRow)
    {
        var rows = Rows;
        if (rows == null || _index == null) return "No CSV is open.";
        if (sourceRow < 0 || sourceRow >= _index.RowCount) return $"Row {sourceRow} is out of range.";
        if (rows.IsGrouped) return "The table is grouped; ungroup it to jump to a row.";
        var map = rows.Map;
        int pos = map == null ? (int)sourceRow : Array.IndexOf(map, (int)sourceRow);
        if (pos < 0 || pos >= rows.Count) return $"Row {sourceRow} is hidden by the current filter.";
        return AssistantGoTo?.Invoke(pos) == true ? null : "The table is not showing, so it could not move to the row.";
    }
}
