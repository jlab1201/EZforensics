using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

public sealed partial class EvidencePanelViewModel
{
    /// <summary>The examiner's label (confirmed / not relevant / added) and note per row, keyed by the file's case path and the row.</summary>
    private readonly Dictionary<(string CasePath, long Row), SpectreEvidenceFlag> _labels = new(), _notes = new();

    /// <summary>The engine has no flag tools (older Spectre): the panel is read-only and says so.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFlag))] private bool _isReadOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFlag))] private bool _isSaving;

    [ObservableProperty] private bool _isNoteOpen;
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _isNewFindingOpen;
    [ObservableProperty] private string _newFindingTitle = "";
    [ObservableProperty] private string _newFindingSeverity = "Medium";
    [ObservableProperty] private string _newFindingNote = "";
    public static IReadOnlyList<string> Severities { get; } = SpectreFinding.Severities.Reverse().ToList();

    public bool CanFlag => !IsReadOnly && !IsSaving;

    partial void OnSelectedRowsChanged(IReadOnlyList<long> value)
    {
        if (value.Count > 0 && (CurrentRow == null || !value.Contains(CurrentRow.Value))) CurrentRow = value[^1];
    }

    private static (string, long) Key(string casePath, long row) => (casePath.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant(), row);

    /// <summary>The marks of one row of <paramref name="f"/> for the gutter; null when it has none.</summary>
    internal EvidenceRowMark? MarkOf(EvidencePanelFile f, long row)
    {
        var key = Key(f.CasePath, row);
        _labels.TryGetValue(key, out var label);
        _notes.TryGetValue(key, out var note);
        var labelText = label?.Label ?? (f.AddedRows.Contains(row) ? EvidenceFlagLabels.Added : null);
        var mark = new EvidenceRowMark(f.SpectreRows.Contains(row), labelText, label?.Analyst, label?.At, note?.Note, note?.Analyst, note?.At);
        return mark.IsEmpty ? null : mark;
    }

    /// <summary>The label of a row as the examiner set it (null = none).</summary>
    internal string? LabelOf(EvidencePanelFile f, long row) => MarkOf(f, row)?.Label;

    /// <summary>Reads the examiner's current flags of these findings from the case analysis (row edits of the items).</summary>
    private Task LoadFlagsAsync(IEnumerable<string> findingIds, IReadOnlyList<(string FindingId, EvidenceSource Source)> findings)
    {
        IsReadOnly = !_host.CanFlag;
        var ids = new HashSet<string>(findingIds.Where(i => !string.IsNullOrEmpty(i)), StringComparer.OrdinalIgnoreCase);
        if (_host.CanFlag)
            foreach (var f in _host.EvidenceFlagsOf(ids))
                if (f.FindingId == null || ids.Count == 0 || ids.Contains(f.FindingId)) Apply(f);
        RefreshCounts();
        return Task.CompletedTask;
    }

    /// <summary>Puts one flag from the engine into the panel (keyed to the panel's own spelling of its file).</summary>
    private void Apply(SpectreEvidenceFlag flag)
    {
        var file = Files.FirstOrDefault(f => SameFile(flag.CasePath, f));
        var key = Key(file?.CasePath ?? flag.CasePath, flag.Row);
        switch (flag.Label)
        {
            case EvidenceFlagLabels.Note:
                if (string.IsNullOrWhiteSpace(flag.Note)) _notes.Remove(key); else _notes[key] = flag;
                break;
            case EvidenceFlagLabels.Clear:
                _labels.Remove(key);
                if (file != null && !file.SpectreRows.Contains(flag.Row)) file.AddedRows.Remove(flag.Row);
                break;
            default:
                if (!EvidenceFlagLabels.IsAnalystLabel(flag.Label)) return;
                _labels[key] = flag;
                if (file != null && flag.Label != EvidenceFlagLabels.Added && !file.SpectreRows.Contains(flag.Row)) file.AddedRows.Remove(flag.Row);
                break;
        }
    }

    private void RefreshCounts()
    {
        foreach (var f in Files)
        {
            var rows = new HashSet<long>(f.AddedRows.Where(r => !_labels.ContainsKey(Key(f.CasePath, r))));
            foreach (var k in _labels.Keys.Concat(_notes.Keys)) if (k.CasePath == Key(f.CasePath, 0).Item1) rows.Add(k.Row);
            f.AnalystCount = rows.Count;
        }
        OnPropertyChanged(nameof(FileLine));
    }

    // ------------------------------------------------------------ label commands (toolbar, right-click, keys C N A M Del)

    [RelayCommand] private Task Confirm() => SetLabelAsync(EvidenceFlagLabels.Confirmed);
    [RelayCommand] private Task NotRelevant() => SetLabelAsync(EvidenceFlagLabels.NotRelevant);
    [RelayCommand] private Task Add() => SetLabelAsync(EvidenceFlagLabels.Added);
    [RelayCommand] private Task Clear() => SetLabelAsync(EvidenceFlagLabels.Clear);

    /// <summary>Opens the note box for the selected rows (with the current row's note, if any).</summary>
    [RelayCommand]
    private void Note()
    {
        if (!Guard()) return;
        IsNewFindingOpen = false;
        NoteText = CurrentFile is { } f && CurrentRow is { } r && _notes.TryGetValue(Key(f.CasePath, r), out var n) ? n.Note ?? "" : "";
        IsNoteOpen = true;
    }

    [RelayCommand]
    private async Task SaveNote()
    {
        if (NoteText.Trim().Length == 0) { Message = "Write a note first."; return; }
        await SetLabelAsync(EvidenceFlagLabels.Note, NoteText.Trim());
        if (Message.Length == 0) IsNoteOpen = false;
    }

    [RelayCommand] private void CancelNote() => IsNoteOpen = false;

    /// <summary>True when labels can be set now; otherwise says why.</summary>
    private bool Guard()
    {
        if (IsReadOnly) { Message = UpdateEngineText; return false; }
        if (CurrentFile == null || SelectedRows.Count == 0) { Message = "Select one or more rows first."; return false; }
        return !IsSaving;
    }

    /// <summary>Saves <paramref name="label"/> on every selected row as a row edit of the item (history, survives every sync and import).</summary>
    internal async Task SetLabelAsync(string label, string? note = null)
    {
        if (!Guard() || CurrentFile is not { } file) return;
        var rows = SelectedRows.ToList();
        var targets = rows.Select(r => new RowTarget(file.RowFinding.TryGetValue(r, out var fid) ? fid : FindingId, file.CasePath, r)).ToList();
        IsSaving = true; Message = "";
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var res = _host.SetRowLabels(targets, label, note);
            if (!res.Ok) Message = "Could not save the flag: " + (res.Reason ?? "no reason given");
            else
                foreach (var t in targets)
                {
                    var flag = res.Flags.FirstOrDefault(f => f.Row == t.Row) ?? new SpectreEvidenceFlag(t.FindingId, file.CasePath, t.Row, label, note, _host.ExaminerName, DateTime.UtcNow.ToString("o"));
                    Apply(flag);
                    if (label == EvidenceFlagLabels.Added && !file.SpectreRows.Contains(t.Row)) file.AddedRows.Add(t.Row);
                    changed.Add(t.FindingId);
                }
        }
        finally { IsSaving = false; }
        if (changed.Count == 0) return;
        Rows?.NotifyFlagsChanged(rows);
        RefreshCounts();
        UpdateDetail();
        await ApplyViewAsync();
        try { await _host.FindingFlagsChangedAsync(changed); }
        catch (Exception ex) { AppLog.Warn("Evidence panel: could not refresh the finding: " + ex.Message); }
    }

    // ------------------------------------------------------------ make a new finding from the selected rows

    [RelayCommand]
    private void NewFinding()
    {
        if (!Guard()) return;
        IsNoteOpen = false;
        NewFindingTitle = ""; NewFindingSeverity = "Medium"; NewFindingNote = "";
        IsNewFindingOpen = true;
    }

    [RelayCommand] private void CancelNewFinding() => IsNewFindingOpen = false;

    [RelayCommand]
    internal async Task CreateFinding()
    {
        if (!Guard() || CurrentFile is not { } file) return;
        var title = NewFindingTitle.Trim();
        if (title.Length == 0) { Message = "Give the new key event a title."; return; }
        var rows = SelectedRows.ToList();
        IsSaving = true; Message = "";
        string? created = null;
        try
        {
            var res = await _host.CreateKeyEventAsync(title, NewFindingSeverity, file.CasePath, rows, NewFindingNote.Trim().Length > 0 ? NewFindingNote.Trim() : null);
            if (!res.Ok) { Message = "Could not create the key event: " + (res.Reason ?? "no reason given"); return; }
            created = res.Id ?? "";
            IsNewFindingOpen = false;
            Message = $"Created a key event from {rows.Count:N0} row{(rows.Count == 1 ? "" : "s")}. It is also in Analyze.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Could not create the key event. " + ex.Message;
            AppLog.Warn("Evidence panel: new key event: " + ex.Message);
        }
        finally { IsSaving = false; }
        if (created == null) return;
        try { await _host.FindingCreatedAsync(created); }
        catch (Exception ex) { AppLog.Warn("Evidence panel: could not reload the findings: " + ex.Message); }
    }
}
