using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>Options page: one saved Ingest selection in the list.</summary>
public sealed class IngestSelectionRow
{
    public IngestSelectionRow(IngestSelection s) { Name = s.Name; Count = s.RuleIds.Count; IsDefault = IngestSelections.IsDefault(s.Name); }
    public string Name { get; }
    public int Count { get; }
    public bool IsDefault { get; }
    public string CountText => Count == 1 ? "1 job" : $"{Count} jobs";
    public string Summary => IsDefault ? $"{CountText}  ·  built in (cannot be renamed or deleted)" : CountText;
}

/// <summary>Selection editor: one ingest rule checkbox.</summary>
public sealed partial class IngestRuleCheck : ObservableObject
{
    public IngestRuleCheck(IngestRuleInfo info, bool on) { Id = info.Id; Label = info.Label; _isChecked = on; }
    public string Id { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Selection editor: the rules of one group (Disk / Memory / Network / Timeline).</summary>
public sealed class IngestRuleGroup
{
    public IngestRuleGroup(string name, IEnumerable<IngestRuleCheck> rules) { Name = name; Rules = rules.ToList(); }
    public string Name { get; }
    public List<IngestRuleCheck> Rules { get; }
}

// Options > Ingest selection. A list of the saved selections; the selected one shows its jobs as checkboxes. Every change is saved
// straight away through the normal settings save path (S.SaveSettings), so the Ingest "Custom selection" menu sees it at once;
// other pending edits on the page stay unsaved.
public sealed partial class SettingsViewModel
{
    public ObservableCollection<IngestSelectionRow> SelectionRows { get; } = new();
    public ObservableCollection<IngestRuleGroup> EditorGroups { get; } = new();
    [ObservableProperty] private IngestSelectionRow? _selectedRow;
    /// <summary>The name box (New / Rename) is open.</summary>
    [ObservableProperty] private bool _isEditingSelection;
    [ObservableProperty] private string _editorName = "";
    [ObservableProperty] private string? _editorError;
    private bool _namingNew;
    private bool _loadingChecks;
    private bool _keepChecks;
    private bool _refreshing;
    private List<string> _editorUnknownIds = new();

    public bool CanRenameSelected => SelectedRow is { IsDefault: false };
    public bool CanDeleteSelected => SelectedRow is { IsDefault: false };
    public bool IsDefaultSelected => SelectedRow is { IsDefault: true };

    /// <summary>The Options view scrolls to the Ingest selection section.</summary>
    public event Action? IngestSelectionsFocusRequested;

    public void FocusIngestSelections() { RefreshSelectionRows(); IngestSelectionsFocusRequested?.Invoke(); }

    partial void OnSelectedRowChanged(IngestSelectionRow? value)
    {
        if (_refreshing) return;
        OnPropertyChanged(nameof(CanRenameSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(IsDefaultSelected));
        RenameSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        if (_keepChecks) return;
        IsEditingSelection = false;
        EditorError = null;
        LoadChecks(value?.Name);
    }

    private void RefreshSelectionRows(string? select = null, bool reloadChecks = false)
    {
        var name = select ?? SelectedRow?.Name;
        var prev = SelectedRow?.Name;
        _refreshing = true;
        try
        {
            SelectionRows.Clear();
            foreach (var s in IngestSelections.Normalize(S.Settings.IngestSelections)) SelectionRows.Add(new IngestSelectionRow(s));
        }
        finally { _refreshing = false; }
        var row = SelectionRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) ?? SelectionRows[0];
        _keepChecks = !reloadChecks && EditorGroups.Count > 0 && string.Equals(prev, row.Name, StringComparison.OrdinalIgnoreCase);
        try { SelectedRow = row; } finally { _keepChecks = false; }
    }

    private void LoadChecks(string? name)
    {
        _loadingChecks = true;
        try
        {
            EditorGroups.Clear();
            if (name == null) return;
            var existing = IngestSelections.Find(S.Settings.IngestSelections, name);
            var ticked = new HashSet<string>(existing.RuleIds, StringComparer.OrdinalIgnoreCase);
            var all = IngestSelections.AllRules();
            foreach (var g in IngestSelections.Groups)
            {
                var checks = all.Where(r => r.Group == g).Select(r => new IngestRuleCheck(r, ticked.Contains(r.Id))).ToList();
                foreach (var c in checks) c.PropertyChanged += OnRuleCheckChanged;
                EditorGroups.Add(new IngestRuleGroup(g, checks));
            }
            _editorUnknownIds = existing.RuleIds.Where(i => !all.Any(r => string.Equals(r.Id, i, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        finally { _loadingChecks = false; }
    }

    private async void OnRuleCheckChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loadingChecks || e.PropertyName != nameof(IngestRuleCheck.IsChecked) || SelectedRow == null) return;
        try { await SaveCheckedAsync(); }
        catch (Exception ex) { AppLog.Warn("Settings: could not save the ingest selection: " + ex.Message); }
    }

    private async Task SaveCheckedAsync()
    {
        if (SelectedRow == null) return;
        var stored = IngestSelections.Normalize(S.Settings.IngestSelections);
        var idx = stored.FindIndex(s => string.Equals(s.Name, SelectedRow.Name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var ids = EditorGroups.SelectMany(g => g.Rules).Where(r => r.IsChecked).Select(r => r.Id).Concat(_editorUnknownIds).ToList();
        stored[idx] = new IngestSelection(stored[idx].Name, ids);
        await PersistSelectionsAsync(stored, SelectedRow.Name);
    }

    [RelayCommand]
    private void NewSelection()
    {
        _namingNew = true;
        EditorName = "";
        EditorError = null;
        IsEditingSelection = true;
    }

    [RelayCommand(CanExecute = nameof(CanRenameSelected))]
    private void RenameSelection()
    {
        if (SelectedRow is not { IsDefault: false } row) return;
        _namingNew = false;
        EditorName = row.Name;
        EditorError = null;
        IsEditingSelection = true;
    }

    [RelayCommand]
    private void CancelSelection() { IsEditingSelection = false; EditorError = null; }

    /// <summary>Confirms the name box: creates the new (empty) selection or renames the selected one.</summary>
    [RelayCommand]
    private async Task SaveSelectionAsync()
    {
        var stored = IngestSelections.Normalize(S.Settings.IngestSelections);
        var oldName = _namingNew ? null : SelectedRow?.Name;
        var name = EditorName.Trim();
        var others = stored.Where(s => !string.Equals(s.Name, oldName, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name);
        var err = IngestSelections.ValidateName(name, others);
        if (err != null) { EditorError = err; return; }
        if (oldName == null) stored.Add(new IngestSelection(name, new List<string>()));
        else
        {
            var idx = stored.FindIndex(s => string.Equals(s.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;
            stored[idx] = new IngestSelection(name, stored[idx].RuleIds);
        }
        IsEditingSelection = false;
        EditorError = null;
        await PersistSelectionsAsync(stored, name, reloadChecks: oldName == null);
    }

    /// <summary>Default only: puts the built-in set back.</summary>
    [RelayCommand]
    private async Task ResetSelectionToBuiltInAsync()
    {
        if (SelectedRow is not { IsDefault: true }) return;
        var stored = IngestSelections.Normalize(S.Settings.IngestSelections);
        stored[0] = IngestSelections.BuiltInDefault();
        await PersistSelectionsAsync(stored, IngestSelections.DefaultName, reloadChecks: true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectionAsync()
    {
        var row = SelectedRow;
        if (row == null || row.IsDefault) return;
        if (!Dialogs.Confirm("Delete selection", $"Delete the selection \"{row.Name}\"?", "Delete", "Cancel", defaultNo: true)) return;
        var stored = IngestSelections.Normalize(S.Settings.IngestSelections);
        stored.RemoveAll(s => string.Equals(s.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        await PersistSelectionsAsync(stored, IngestSelections.DefaultName, reloadChecks: true);
    }

    private async Task PersistSelectionsAsync(List<IngestSelection> list, string? select = null, bool reloadChecks = false)
    {
        await GuardAsync(() =>
        {
            S.Settings.IngestSelections = list;
            S.SaveSettings();
            Edit.IngestSelections = list.Select(i => i.Clone()).ToList();
            RefreshSelectionRows(select, reloadChecks);
            ShowMessage("Ingest selections saved.", InfoSeverity.Success);
            return Task.CompletedTask;
        }, "Could not save the Ingest selections");
    }
}
