using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Analysis;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>One CSV of the finding's rows in the editor, as a range text ("12-15, 21", row numbers as the grid shows them).</summary>
public sealed partial class FindingRowsLine : ObservableObject
{
    public FindingRowsLine(string csvRelPath, string rangeText, bool editable)
    {
        CsvRelPath = csvRelPath; FileName = Path.GetFileName(csvRelPath.TrimEnd('/', '\\')); Original = rangeText; _rangeText = rangeText; IsEditable = editable;
    }
    public string CsvRelPath { get; }
    public string FileName { get; }
    public string Original { get; }
    public bool IsEditable { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Error), nameof(HasError), nameof(IsDirty))] private string _rangeText;
    public bool IsDirty => IsEditable && !string.Equals(RangeText.Trim(), Original.Trim(), StringComparison.Ordinal);
    /// <summary>Why the text is not a row list; empty when it is fine.</summary>
    public string Error { get { RowRanges.Parse(RangeText, out var e); return e ?? ""; } }
    public bool HasError => IsEditable && Error.Length > 0;
    /// <summary>The rows, 0-based; empty when the text is not valid.</summary>
    public List<long> Rows0 => (RowRanges.Parse(RangeText, out _) ?? new List<long>()).Select(r => r - 1).ToList();
}

/// <summary>The finding's rows in the Details editor: one range box per CSV (editable for a finding made in Analyze, read only for Spectre's).</summary>
public sealed partial class FindingsViewModel
{
    public ObservableCollection<FindingRowsLine> RowLines { get; } = new();
    public bool HasRowLines => RowLines.Count > 0;
    public bool RowsEditable => RowLines.Count > 0 && RowLines.All(l => l.IsEditable);
    public bool RowsDirty => RowLines.Any(l => l.IsDirty);
    /// <summary>Bad range text, or every row taken out (a finding needs at least one).</summary>
    public bool RowsInvalid => RowsEditable && (RowLines.Any(l => l.HasError) || (RowsDirty && RowLines.All(l => l.Rows0.Count == 0)));
    public string RowsError => RowLines.FirstOrDefault(l => l.HasError) is { } bad ? (RowLines.Count > 1 ? bad.FileName + ": " : "") + bad.Error
        : RowsInvalid ? "A finding needs at least one row." : "";

    private void LoadRowLines(FindingRowViewModel? row)
    {
        foreach (var l in RowLines) l.PropertyChanged -= RowLineChanged;
        RowLines.Clear();
        if (row?.Item is { } item && Analysis is { } a)
        {
            var ws = WorkspaceFindingOf(row);
            IEnumerable<AnalysisRow> rows = ws != null ? ws.Rows : a.EffectiveRows(item);
            foreach (var g in rows.GroupBy(r => r.Key.Csv).OrderBy(g => g.First().CsvRelPath, StringComparer.OrdinalIgnoreCase))
            {
                var line = new FindingRowsLine(g.First().CsvRelPath, RowRanges.Format(g.Select(r => r.Row + 1)), ws != null);
                line.PropertyChanged += RowLineChanged;
                RowLines.Add(line);
            }
        }
        OnPropertyChanged(nameof(HasRowLines)); OnPropertyChanged(nameof(RowsEditable));
        RowsChanged();
    }

    private void RowLineChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => RowsChanged();

    private void RowsChanged()
    {
        foreach (var n in new[] { nameof(RowsDirty), nameof(RowsInvalid), nameof(RowsError), nameof(IsDirty), nameof(CanSave) }) OnPropertyChanged(n);
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes the edited ranges into the Analyze finding; rows it gained are flagged like "Add to finding" does. False when nothing could be saved.</summary>
    private bool SaveRowEdits(FindingRowViewModel row)
    {
        var id = row.Item?.Source.WorkspaceId;
        if (id == null || !RowsDirty || RowsInvalid) return true;
        var edits = RowLines.Where(l => l.IsDirty).Select(l => (l.CsvRelPath, Rows: l.Rows0)).ToList();
        var added = new List<(string Rel, List<long> Rows)>();
        var ok = Mutate(a =>
        {
            if (a.Workspace.Findings.All(w => w.Id != id)) return false;
            foreach (var (rel, rows) in edits) added.Add((rel, a.SetFindingFileRows(id, rel, rows)));
            AnalysisSync.Sync(a, SyncOptionsFor(null));
            return true;
        });
        if (ok) FlagAddedRows(added);
        return ok;
    }

    /// <summary>Flags rows a finding gained (Suspicious when the flag set has it, else the last used flag).</summary>
    private void FlagAddedRows(IEnumerable<(string Rel, List<long> Rows)> added)
    {
        if (CaseDir is not { } root) return;
        var flag = FlagCatalog.IndexOf(Services.AppServices.Current.Settings.Flags, FlagCatalog.Suspicious) >= 0 ? FlagCatalog.Suspicious : ResultsViewModel.EffectiveLastFlagId();
        foreach (var (rel, rows) in added)
            FindingRowFlags.FlagNew(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), rows, flag);
    }
}
