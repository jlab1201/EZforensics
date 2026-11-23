using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;

namespace EzForensics.App.ViewModels;

public enum ProposalState { Pending, Applied, Dismissed, Undone }

/// <summary>
/// An action Spectre suggests for the grid. In the default mode nothing happens until the examiner clicks Apply; in auto-mode the
/// chat applies it at once with <see cref="AutoApplyAsync"/> and the card offers Undo. Either way it only works while the CSV
/// the proposal was made for is still the one open in the grid.
/// </summary>
public sealed partial class ProposalCardViewModel : ObservableObject
{
    private const int MaxRowsShown = 12;
    private readonly AssistantProposal _proposal;
    private readonly IAssistantGridTarget? _grid;
    private Func<Task<string?>>? _undo;

    public ProposalCardViewModel(AssistantProposal proposal, IAssistantGridTarget? grid)
    {
        _proposal = proposal; _grid = grid;
        (Title, Detail, Reason) = proposal switch
        {
            FilterProposal f => ("Filter", f.Display, ""),
            OpenFileProposal o => ($"Open {Path.GetFileName(o.File)}", o.Filter == null ? "" : "Filtered by " + o.Display, o.Reason),
            FlagProposal g => ($"{OpenFirst(g.CsvPath, grid)}{(OpenFirst(g.CsvPath, grid).Length > 0 ? "flag" : "Flag")} {RowsTitle(g.Rows)} as {grid?.FlagLabel(g.FlagId) ?? g.FlagId} and show them", RowsText(g.Rows), g.Reason),
            UnflagProposal u => ($"{OpenFirst(u.CsvPath, grid)}{(OpenFirst(u.CsvPath, grid).Length > 0 ? "unflag" : "Unflag")} {RowsTitle(u.Rows)}", RowsText(u.Rows), u.Reason),
            ClearFilterProposal => ("Clear all filters", "", ""),
            SortProposal s => ($"Sort by {s.Column}", s.Descending ? "Descending" : "Ascending", ""),
            GoToRowProposal r => ($"Go to row {AssistantGrid.Shown(r.Row)}", "", ""),
            PageActionProposal a => (a.Title, a.Detail, ""),
            _ => ("Suggestion", "", ""),
        };
    }

    /// <summary>Proposal type for the log ("FilterProposal"); never its content.</summary>
    private string Kind => _proposal.GetType().Name;

    /// <summary>Id of the model's tool call; names the card in the transcript page.</summary>
    public string Id => _proposal.ToolCallId;
    /// <summary>Unique per card (tool call ids can repeat between rounds); names the card in the transcript page.</summary>
    public string Key { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; }
    /// <summary>The filter text, or the row ids to flag.</summary>
    public string Detail { get; }
    public string Reason { get; }
    public bool HasReason => Reason.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(CanUndo))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DismissCommand), nameof(UndoCommand))]
    private ProposalState _state;

    /// <summary>Why Apply did nothing, or what happened ("Applied", "Dismissed").</summary>
    [ObservableProperty] private string _note = "";

    public bool IsPending => State == ProposalState.Pending;

    /// <summary>True after auto-mode applied it and there is something to undo.</summary>
    public bool CanUndo => State == ProposalState.Applied && _undo != null;

    public string? CsvPath => _proposal switch
    {
        FilterProposal f => f.CsvPath, FlagProposal g => g.CsvPath, UnflagProposal u => u.CsvPath,
        ClearFilterProposal c => c.CsvPath, SortProposal s => s.CsvPath, GoToRowProposal r => r.CsvPath, _ => null,
    };

    /// <summary>True when both paths name the same file (case-insensitive, normalised).</summary>
    public static bool SameCsv(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private async Task ApplyAsync()
    {
        if (!IsPending) return;
        var err = await RunAsync(captureUndo: false);
        AppLog.Info(err == null ? $"Spectre card Apply ({Kind}): applied." : $"Spectre card Apply ({Kind}): NOT applied: {err}");
        if (err != null) { Note = err; return; }
        State = ProposalState.Applied;
        Note = "Applied";
    }

    /// <summary>
    /// Auto-mode: applies the proposal now, remembering how to undo it. Returns null when applied, else why not (the card then
    /// stays pending with the reason, so it can still be applied by hand once the cause is gone).
    /// </summary>
    public async Task<string?> AutoApplyAsync()
    {
        if (!IsPending) return "Already handled.";
        if (_proposal is PageActionProposal { AutoAllowed: false }) { Note = "Waiting for your Apply click"; return "this action always waits for the examiner's Apply click"; }
        var err = await RunAsync(captureUndo: true);
        if (err != null) { Note = "Not applied: " + err; return err; }
        State = ProposalState.Applied;
        Note = "Applied: " + Title;
        return null;
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Dismiss()
    {
        if (!IsPending) return;
        AppLog.Info($"Spectre card Dismiss ({Kind}).");
        State = ProposalState.Dismissed;
        Note = "Dismissed";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var undo = _undo;
        if (undo == null || State != ProposalState.Applied) return;
        string? err;
        try { err = await undo(); }
        catch (Exception ex) { err = "Could not undo: " + ex.Message; }
        AppLog.Info(err == null ? $"Spectre card Undo ({Kind}): undone." : $"Spectre card Undo ({Kind}): failed: {err}");
        if (err != null) { Note = err; return; }
        _undo = null;
        State = ProposalState.Undone;
        Note = "Undone";
    }

    private async Task<string?> RunAsync(bool captureUndo)
    {
        if (_proposal is PageActionProposal a)
        {
            try
            {
                var err = await a.Run();
                if (err == null && captureUndo) _undo = a.Undo;
                return err;
            }
            catch (Exception ex) { return "Could not apply: " + ex.Message; }
        }
        if (_grid == null) return "Open the results table in Analyze to apply this.";
        try
        {
            if (_proposal is OpenFileProposal o) return await TryOpenAsync(o, captureUndo);
            var csv = CsvPath;
            // flags may be proposed for another case file: Apply opens it first (only while it is still on disk)
            if (_proposal is FlagProposal or UnflagProposal && csv != null && !SameCsv(csv, _grid.CurrentCsv) && File.Exists(csv))
            {
                var openErr = await _grid.OpenFileAsync(csv, null);
                if (openErr != null) return openErr;
            }
            if (!SameCsv(csv, _grid.CurrentCsv))
                return csv == null ? "No CSV was open when this was suggested." : $"Open {Path.GetFileName(csv)} to apply this.";
            return await TryApplyAsync(captureUndo);
        }
        catch (Exception ex) { return "Could not apply: " + ex.Message; }
    }

    /// <summary>Opens the file in Analyze (only if it is still in the case folder on disk) and applies its filter.</summary>
    private async Task<string?> TryOpenAsync(OpenFileProposal o, bool captureUndo)
    {
        var grid = _grid!;
        if (!File.Exists(o.File)) return $"{Path.GetFileName(o.File)} is no longer in the case.";
        var before = grid.CurrentCsv;
        var filterBefore = captureUndo && before != null ? grid.CaptureFilter() : null;
        var err = await grid.OpenFileAsync(o.File, o.Filter);
        if (err != null || !captureUndo) return err;
        _undo = async () =>
        {
            if (!SameCsv(o.File, grid.CurrentCsv)) return $"Open {Path.GetFileName(o.File)} to undo this.";
            if (before == null) return "No CSV was open before, so there is nothing to go back to.";
            var back = await grid.OpenFileAsync(before, null);
            if (back == null && filterBefore != null) grid.RestoreFilter(filterBefore);
            return back;
        };
        return null;
    }

    private async Task<string?> TryApplyAsync(bool captureUndo)
    {
        var grid = _grid!;
        string? err;
        Func<Task<string?>>? undo = null;
        switch (_proposal)
        {
            case FilterProposal f:
            {
                var snap = grid.CaptureFilter();
                grid.ApplyFilter(f.Filter);
                undo = RestoreFilterAsync(snap);
                err = null;
                break;
            }
            case ClearFilterProposal:
            {
                var snap = grid.CaptureFilter();
                grid.ClearFilter();
                undo = RestoreFilterAsync(snap);
                err = null;
                break;
            }
            case FlagProposal g:
            {
                // flag, then filter to that flag so the examiner sees what was flagged; Undo puts both back
                var before = grid.GetFlags(g.Rows);
                var snap = grid.CaptureFilter();
                err = grid.ApplyFlag(g.Rows, g.FlagId);
                if (err == null) grid.ShowFlag(g.FlagId);
                var flagsBack = RestoreFlagsAsync(g.Rows, before);
                var filterBack = RestoreFilterAsync(snap);
                undo = async () => await flagsBack() ?? await filterBack();
                break;
            }
            case UnflagProposal u:
            {
                var before = grid.GetFlags(u.Rows);
                err = grid.ApplyFlag(u.Rows, null);
                undo = RestoreFlagsAsync(u.Rows, before);
                break;
            }
            case SortProposal s:
            {
                var (col, desc) = grid.GetSort();
                err = await grid.SortAsync(s.Column, s.Descending);
                undo = () => SameCsv(CsvPath, grid.CurrentCsv) ? grid.SortAsync(col, desc) : Task.FromResult<string?>($"Open {Path.GetFileName(CsvPath)} to undo this.");
                break;
            }
            case GoToRowProposal r:
                err = grid.GoToRow(r.Row); // moving the selection needs no undo
                break;
            default:
                err = "Unknown suggestion.";
                break;
        }
        if (err == null && captureUndo) _undo = undo;
        return err;
    }

    private Func<Task<string?>> RestoreFilterAsync(object snapshot) => () =>
    {
        if (!SameCsv(CsvPath, _grid!.CurrentCsv)) return Task.FromResult<string?>($"Open {Path.GetFileName(CsvPath)} to undo this.");
        _grid.RestoreFilter(snapshot);
        return Task.FromResult<string?>(null);
    };

    /// <summary>Gives every row its earlier flag back (rows are grouped by earlier flag so each group is one call).</summary>
    private Func<Task<string?>> RestoreFlagsAsync(IReadOnlyList<long> rows, IReadOnlyList<string?> before) => () =>
    {
        if (!SameCsv(CsvPath, _grid!.CurrentCsv)) return Task.FromResult<string?>($"Open {Path.GetFileName(CsvPath)} to undo this.");
        foreach (var group in rows.Select((r, i) => (Row: r, Flag: i < before.Count ? before[i] : null)).GroupBy(x => x.Flag))
        {
            var err = _grid.ApplyFlag(group.Select(x => x.Row).ToList(), group.Key);
            if (err != null) return Task.FromResult<string?>(err);
        }
        return Task.FromResult<string?>(null);
    };

    /// <summary>"Open X.csv and " when the rows are in a file other than the open one; else empty.</summary>
    private static string OpenFirst(string? csv, IAssistantGridTarget? grid) =>
        csv != null && grid != null && !SameCsv(csv, grid.CurrentCsv) ? $"Open {Path.GetFileName(csv)} and " : "";

    private static string RowsTitle(IReadOnlyList<long> rows) => $"{rows.Count:N0} row{(rows.Count == 1 ? "" : "s")}";

    private static string RowsText(IReadOnlyList<long> rows) =>
        "Rows " + string.Join(", ", rows.Take(MaxRowsShown).Select(AssistantGrid.Shown)) + (rows.Count > MaxRowsShown ? $" (+{rows.Count - MaxRowsShown})" : "");
}
