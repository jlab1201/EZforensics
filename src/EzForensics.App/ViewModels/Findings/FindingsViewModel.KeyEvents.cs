using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Analysis;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Findings;

/// <summary>The Key events list next to the findings list, and its editor (Title, Time, Severity, Mapping, Notes, the findings it holds).</summary>
public sealed partial class FindingsViewModel
{
    public const string KeyEventTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Every key event that is not removed, in time order (no time last).</summary>
    public ObservableCollection<KeyEventRowViewModel> KeyEventRows { get; } = new();
    public bool HasKeyEvents => KeyEventRows.Count > 0;
    public string KeyEventsTitle => $"Key events ({KeyEventRows.Count})";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedKeyEvent), nameof(ShowKeyEventEditor), nameof(ShowNoKeyEventSelected))] private KeyEventRowViewModel? _selectedKeyEvent;
    public bool HasSelectedKeyEvent => SelectedKeyEvent != null;

    public const int ListTabFindings = 0, ListTabKeyEvents = 1;
    /// <summary>Which list the Details area shows (tabs): the findings or the key events; the editor on the right follows it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowKeyEventEditor), nameof(ShowNoKeyEventSelected), nameof(ShowFindingEditor), nameof(ShowNoFindingSelected))]
    private int _detailListTab;
    public bool ShowKeyEventEditor => DetailListTab == ListTabKeyEvents && SelectedKeyEvent != null;
    public bool ShowNoKeyEventSelected => DetailListTab == ListTabKeyEvents && SelectedKeyEvent == null;
    public bool ShowFindingEditor => DetailListTab == ListTabFindings && Selected != null;
    public bool ShowNoFindingSelected => DetailListTab == ListTabFindings && Selected == null;

    // ---- editor drafts
    [ObservableProperty] private string _keyEventDraftTitle = "";
    /// <summary>UTC, <see cref="KeyEventTimeFormat"/>; empty = no time.</summary>
    [ObservableProperty] private string _keyEventDraftTime = "";
    [ObservableProperty] private string _keyEventDraftSeverity = "Medium";
    [ObservableProperty] private string _keyEventDraftMitre = "";
    [ObservableProperty] private string _keyEventDraftNotes = "";
    [ObservableProperty] private string _keyEventEditMessage = "";

    // "Saved." / "Approved." style results fade away after 4 s (the text is only cleared if nothing newer replaced it).
    partial void OnKeyEventEditMessageChanged(string value) => ClearAfterDelay(value, () => KeyEventEditMessage, () => KeyEventEditMessage = "");
    partial void OnEditMessageChanged(string value) => ClearAfterDelay(value, () => EditMessage, () => EditMessage = "");

    private static async void ClearAfterDelay(string value, Func<string> current, Action clear)
    {
        try
        {
            if (value.Length == 0 || value.EndsWith('…')) return;   // "Saving…" is progress, not a result
            await Task.Delay(TimeSpan.FromSeconds(4));
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return;
            await d.InvokeAsync(() => { if (current() == value) clear(); });
        }
        catch (Exception ex) { EzForensics.App.Services.AppLog.Warn("Findings: could not clear the status text: " + ex.Message); }
    }
    public ObservableCollection<KeyEventFindingLine> KeyEventFindings { get; } = new();
    private bool _loadingKeyEvent;

    partial void OnKeyEventDraftTitleChanged(string value) => KeyEventEditorChanged();
    partial void OnKeyEventDraftTimeChanged(string value) => KeyEventEditorChanged();
    partial void OnKeyEventDraftSeverityChanged(string value) => KeyEventEditorChanged();
    partial void OnKeyEventDraftMitreChanged(string value) => KeyEventEditorChanged();
    partial void OnKeyEventDraftNotesChanged(string value) => KeyEventEditorChanged();
    partial void OnSelectedKeyEventChanged(KeyEventRowViewModel? value) => LoadKeyEventEditor(value);

    /// <summary>Parses the editor's time box (UTC; a trailing "UTC" or "Z" is allowed). Empty is a valid "no time".</summary>
    internal static bool TryParseKeyEventTime(string text, out DateTimeOffset? time)
    {
        time = null;
        var t = text.Trim();
        if (t.EndsWith("UTC", StringComparison.OrdinalIgnoreCase)) t = t[..^3].Trim();
        t = t.TrimEnd('Z').Replace('T', ' ');
        if (t.Length == 0) return true;
        foreach (var f in new[] { KeyEventTimeFormat, "yyyy-MM-dd HH:mm", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(t, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            { time = new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)); return true; }
        return false;
    }

    internal static string KeyEventTimeText(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString(KeyEventTimeFormat, CultureInfo.InvariantCulture) : "";

    public bool KeyEventTimeInvalid => !TryParseKeyEventTime(KeyEventDraftTime, out _);
    public bool KeyEventMitreInvalid => TechniqueCatalog.Invalid(FindingDraft.ParseMitre(KeyEventDraftMitre)).Count > 0;
    public bool IsKeyEventDirty
    {
        get
        {
            if (SelectedKeyEvent is not { } r) return false;
            var v = r.Values;
            TryParseKeyEventTime(KeyEventDraftTime, out var t);
            return KeyEventDraftTitle.Trim() != v.Title || KeyEventTimeInvalid || t != v.Time
                   || !string.Equals(KeyEventDraftSeverity, v.Severity, StringComparison.OrdinalIgnoreCase)
                   || !FindingDraft.ParseMitre(KeyEventDraftMitre).SequenceEqual(v.Mitre.Select(m => m.ToUpperInvariant()))
                   || KeyEventDraftNotes.Trim() != r.KeyEvent.Notes;
        }
    }
    public bool CanSaveKeyEvent => IsKeyEventDirty && !KeyEventTimeInvalid && !KeyEventMitreInvalid && KeyEventDraftTitle.Trim().Length > 0;

    private void KeyEventEditorChanged()
    {
        if (_loadingKeyEvent) return;
        foreach (var n in new[] { nameof(KeyEventTimeInvalid), nameof(KeyEventMitreInvalid), nameof(IsKeyEventDirty), nameof(CanSaveKeyEvent) }) OnPropertyChanged(n);
        SaveKeyEventCommand.NotifyCanExecuteChanged();
    }

    private void LoadKeyEventEditor(KeyEventRowViewModel? row)
    {
        _loadingKeyEvent = true;
        try
        {
            var v = row?.Values;
            KeyEventDraftTitle = v?.Title ?? "";
            KeyEventDraftTime = KeyEventTimeText(v?.Time);
            KeyEventDraftSeverity = v != null && SpectreFinding.Rank(v.Severity) >= 0 ? SpectreFinding.Severities[SpectreFinding.Rank(v.Severity)] : "Medium";
            KeyEventDraftMitre = FindingDraft.MitreText(v?.Mitre ?? Array.Empty<string>());
            KeyEventDraftNotes = row?.KeyEvent.Notes ?? "";
            KeyEventEditMessage = "";
            KeyEventFindings.Clear();
            if (row != null && Analysis is { } a)
                foreach (var id in row.KeyEvent.FindingIds)
                    if (a.Find(id) is { } f) KeyEventFindings.Add(new KeyEventFindingLine(row.Id, f.Id, AnalysisAdapter.SourceId(f), f.Title, f.Severity));
        }
        finally { _loadingKeyEvent = false; }
        KeyEventEditorChanged();
    }

    /// <summary>Rebuilds the Key events list from the store, keeping the row objects and the selection.</summary>
    private void RebuildKeyEventRows(CaseAnalysis a)
    {
        var keep = SelectedKeyEvent?.Id;
        var old = KeyEventRows.ToDictionary(r => r.Id);
        var next = a.KeyEvents.Where(k => !k.Dismissed)
            .Select(k => (k, e: KeyEventOps.Effective(a, k)))
            .OrderBy(x => x.e.Time == null).ThenBy(x => x.e.Time).ThenBy(x => x.e.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.k.Id, StringComparer.Ordinal)
            .Select(x =>
            {
                if (old.TryGetValue(x.k.Id, out var r)) { r.Update(a, x.k); return r; }
                var id = x.k.Id;
                return new KeyEventRowViewModel(a, x.k) { AddHereCommand = new RelayCommand(() => AddToKeyEvent(id)) };
            }).ToList();
        if (!next.SequenceEqual(KeyEventRows))
        {
            KeyEventRows.Clear();
            foreach (var r in next) KeyEventRows.Add(r);
        }
        var sel = keep != null ? KeyEventRows.FirstOrDefault(r => r.Id == keep) : null;
        if (!ReferenceEquals(sel, SelectedKeyEvent)) SelectedKeyEvent = sel;
        else LoadKeyEventEditor(sel);   // the same row, fresh values
        OnPropertyChanged(nameof(HasKeyEvents)); OnPropertyChanged(nameof(KeyEventsTitle));
    }

    /// <summary>Asks the page to scroll the key events list to this row (the list may not be on screen yet).</summary>
    public event Action<KeyEventRowViewModel>? KeyEventScrollRequested;
    /// <summary>Asks the page to select all these findings in the list (the first is the current one) and scroll to it.</summary>
    public event Action<IReadOnlyList<FindingRowViewModel>>? FindingRowsSelectRequested;

    /// <summary>Selects a key event in the list (after Add to timeline or Group) and scrolls to it.</summary>
    private void SelectKeyEventRow(string? id)
    {
        if (id != null && KeyEventRows.FirstOrDefault(r => r.Id == id) is { } r)
        {
            SelectedKeyEvent = r; DetailListTab = ListTabKeyEvents;
            KeyEventScrollRequested?.Invoke(r);
        }
    }

    /// <summary>Overview card click: the Details tab, Key events list, with this key event selected.</summary>
    internal bool ShowKeyEventInDetails(string? keyEventId)
    {
        if (keyEventId == null || KeyEventRows.All(r => r.Id != keyEventId)) return false;
        SelectedTab = FindingsTab.Details;
        SelectKeyEventRow(keyEventId);
        return true;
    }

    private static DateTimeOffset? RowTime(FindingRowViewModel r) =>
        DateTimeOffset.TryParse(r.Finding.EventTime ?? r.Finding.FirstSeen, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>The findings of a key event in the list, earliest first (by event time; no time keeps the list order, after the dated ones).</summary>
    internal List<FindingRowViewModel> RowsOfKeyEvent(string keyEventId)
    {
        var ids = Analysis?.FindKeyEvent(keyEventId)?.FindingIds.ToHashSet() ?? new HashSet<string>();
        return _all.Where(r => r.Item != null && ids.Contains(r.Item.Id))
            .OrderBy(r => RowTime(r) == null).ThenBy(r => RowTime(r)).ThenBy(r => _all.IndexOf(r)).ToList();
    }

    /// <summary>Details > Findings with every finding of the key event selected; the earliest is the current one.</summary>
    internal bool ShowKeyEventFindings(string keyEventId)
    {
        var rows = RowsOfKeyEvent(keyEventId);
        if (rows.Count == 0) return false;
        DetailListTab = ListTabFindings;
        SelectedTab = FindingsTab.Details;
        if (rows.Any(r => !Rows.Contains(r))) ClearFilters();
        Selected = rows[0];
        FindingRowsSelectRequested?.Invoke(rows);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveKeyEvent))]
    private void SaveKeyEvent()
    {
        if (SelectedKeyEvent is not { } row || !TryParseKeyEventTime(KeyEventDraftTime, out var time)) return;
        var id = row.Id;
        var v = row.Values;
        var (title, sev, mitre, notes) = (KeyEventDraftTitle.Trim(), KeyEventDraftSeverity, FindingDraft.ParseMitre(KeyEventDraftMitre).ToList(), KeyEventDraftNotes.Trim());
        var saved = Mutate(a =>
        {
            if (a.FindKeyEvent(id) is not { Dismissed: false } k) return false;
            if (title != v.Title) KeyEventEdits.SetTitle(k, title);
            if (time != v.Time) KeyEventEdits.SetTime(k, time);
            if (!string.Equals(sev, v.Severity, StringComparison.OrdinalIgnoreCase)) KeyEventEdits.SetSeverity(k, sev);
            if (!mitre.SequenceEqual(v.Mitre.Select(m => m.ToUpperInvariant()))) KeyEventEdits.SetMitre(k, mitre);
            if (notes != k.Notes) KeyEventEdits.SetNotes(k, notes);
            return true;
        });
        KeyEventEditMessage = saved ? "Saved." : "Could not save.";
    }

    [RelayCommand]
    private void DiscardKeyEventEdits() => LoadKeyEventEditor(SelectedKeyEvent);

    /// <summary>The editor's Remove next to a finding: takes it out of the key event (an empty key event is removed).</summary>
    [RelayCommand]
    private void RemoveFindingFromSelectedKeyEvent(KeyEventFindingLine? line)
    {
        if (line == null) return;
        RemoveFindingFromKeyEvent(line.KeyEventId, line.ItemId);
    }

    /// <summary>Removes the key event (its findings stay in the list).</summary>
    [RelayCommand]
    private void RemoveKeyEventRow(KeyEventRowViewModel? row) => DismissKeyEvent((row ?? SelectedKeyEvent)?.Id);

    /// <summary>Removed at source: Keep anyway.</summary>
    [RelayCommand]
    private void KeepKeyEventAnyway(KeyEventRowViewModel? row)
    {
        var id = (row ?? SelectedKeyEvent)?.Id;
        if (id == null) return;
        Mutate(a =>
        {
            if (a.FindKeyEvent(id) is not { KeptByUser: false } k) return false;
            k.KeptByUser = true; k.Log("kept anyway", DateTimeOffset.UtcNow);
            return true;
        });
    }

    /// <summary>A click on a finding of a key event: Details > Findings with all its findings selected, the earliest current.</summary>
    [RelayCommand]
    private void ShowKeyEventFinding(KeyEventFindingLine? line)
    {
        if (line == null) return;
        if (ShowKeyEventFindings(line.KeyEventId)) return;
        DetailListTab = ListTabFindings;
        SelectFindingCore(line.Id);
    }
}
