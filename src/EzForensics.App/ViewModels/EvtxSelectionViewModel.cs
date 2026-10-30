using EzForensics.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.ViewModels;

/// <summary>"Use recommended event selection" block of EvtxECmd's configuration: switch, summary, Customize / Import / Export / Reset.</summary>
public sealed partial class EvtxSelectionViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action _changed;

    [ObservableProperty] private string _message = "";

    public EvtxSelectionViewModel(AppServices s, Action changed)
    {
        _s = s;
        _changed = changed;
        s.EvtxChanged += OnChanged;
    }

    /// <summary>True = Selected events mode (alias kept for the view binding).</summary>
    public bool UseSelected { get => UseRecommended; set => UseRecommended = value; }

    /// <summary>True = All events mode (the default; no --inc filter).</summary>
    public bool UseAll { get => !UseRecommended; set => UseRecommended = !value; }

    public bool UseRecommended
    {
        get => _s.Settings.EvtxFilterMode == EvtxFilterMode.Selected;
        set
        {
            if (UseRecommended == value) return;
            _s.Settings.EvtxFilterMode = value ? EvtxFilterMode.Selected : EvtxFilterMode.All;
            _s.SaveSettings();
            OnChanged();
        }
    }

    public string Summary => EvtxArgs.Summary(_s.Evtx) + (_s.Evtx.SelectedCount == 0 ? " (nothing ticked: all events are parsed)" : "");

    /// <summary>One line under the radio buttons: what the mode does to the command.</summary>
    public string PreviewLine => UseRecommended
        ? (EvtxArgs.IncludeList(_s.Evtx) is { Length: > 0 } l ? $"Adds --inc {l} to the command" : "Nothing ticked: no event filter, every event in the source is parsed.")
        : "No event filter: every event in the source is parsed.";

    /// <summary>The --inc list the command should carry, or null for no filter (All mode, or nothing ticked).</summary>
    public string? FilterList => UseRecommended && EvtxArgs.IncludeList(_s.Evtx) is { Length: > 0 } l ? l : null;

    public string IncludePreview => UseRecommended && EvtxArgs.IncludeList(_s.Evtx) is { Length: > 0 } l ? "--inc " + l : "";

    private void OnChanged()
    {
        OnPropertyChanged(nameof(UseRecommended));
        OnPropertyChanged(nameof(UseSelected));
        OnPropertyChanged(nameof(UseAll));
        OnPropertyChanged(nameof(PreviewLine));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IncludePreview));
        _changed();
    }

    [RelayCommand]
    private void Customize()
    {
        var vm = new EvtxCustomizeViewModel(_s);
        var win = new EvtxCustomizeWindow { DataContext = vm, Owner = System.Windows.Application.Current?.MainWindow };
        if (win.ShowDialog() == true) vm.Commit(); else vm.Revert();
        OnChanged();
    }

    [RelayCommand]
    private void Import()
    {
        var f = Dialogs.PickFile("Import event selection", "Event selection (JSON)|*.json|All files|*.*");
        if (string.IsNullOrEmpty(f)) return;
        try
        {
            _s.Evtx.MergeFrom(File.ReadAllText(f));
            _s.SaveEvtx();
            OnChanged();
            Message = $"Imported {Path.GetFileName(f)}: {EvtxArgs.Summary(_s.Evtx)}.";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = "Could not import: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Export()
    {
        var f = Dialogs.SaveFile("Export event selection", "Event selection (JSON)|*.json", "evtx-event-selection.json");
        if (string.IsNullOrEmpty(f)) return;
        try { File.WriteAllText(f, _s.Evtx.ToJson(selectedOnly: true)); Message = $"Exported {_s.Evtx.SelectedCount} events to {Path.GetFileName(f)}."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = "Could not export: " + ex.Message; }
    }

    [RelayCommand]
    private void Reset()
    {
        _s.Evtx.Reset();
        _s.SaveEvtx();
        OnChanged();
        Message = "Reset to the recommended selection.";
    }
}
