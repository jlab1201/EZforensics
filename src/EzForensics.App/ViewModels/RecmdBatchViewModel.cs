using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;

namespace EzForensics.App.ViewModels;

/// <summary>RECmd's "Batch file" block: the file inserted where <c>{batchFile}</c> appears (Settings.RecmdBatchFile; empty = built-in).</summary>
public sealed partial class RecmdBatchViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Func<string?> _toolDir;

    public RecmdBatchViewModel(AppServices s, Func<string?> toolDir)
    {
        _s = s;
        _toolDir = toolDir;
    }

    public string Current => string.IsNullOrWhiteSpace(_s.Settings.RecmdBatchFile)
        ? @"Built-in: BatchExamples\DFIRBatch.reb"
        : _s.Settings.RecmdBatchFile;

    public bool IsCustom => !string.IsNullOrWhiteSpace(_s.Settings.RecmdBatchFile);

    private void Set(string value)
    {
        _s.Settings.RecmdBatchFile = value;
        _s.SaveSettings();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsCustom));
        ResetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Browse()
    {
        var dir = _toolDir();
        var initial = !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "BatchExamples") : null;
        var picked = Dialogs.PickFile("Select a RECmd batch file", "RECmd batch files|*.reb|All files|*.*", initial);
        if (!string.IsNullOrWhiteSpace(picked)) Set(picked);
    }

    [RelayCommand(CanExecute = nameof(IsCustom))]
    private void Reset() => Set("");
}
