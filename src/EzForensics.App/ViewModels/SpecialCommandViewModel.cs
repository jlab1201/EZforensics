using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Commands;

namespace EzForensics.App.ViewModels;

/// <summary>
/// One app-built command on the Tools page (plaso, Zeek, RITA, Volatility): a read-only live preview with a Copy icon and the user's
/// "Additional arguments" (<see cref="ExtraArgsStore"/>), with parse errors and Save / Reset. Ingest reads the saved arguments.
/// </summary>
public sealed partial class SpecialCommandViewModel : ObservableObject
{
    private static AppServices S => AppServices.Current;
    private readonly Func<IReadOnlyList<string>, string> _preview;
    private string _saved;

    [ObservableProperty] private string _extraArgs;
    [ObservableProperty] private IReadOnlyList<string> _errors = Array.Empty<string>();
    [ObservableProperty] private bool _isDirty;

    /// <param name="id">Command id (<see cref="ExtraArgsStore"/>).</param>
    /// <param name="preview">The command as it will run, given the additional arguments (exceptions are shown as the preview).</param>
    public SpecialCommandViewModel(string id, string? label, Func<IReadOnlyList<string>, string> preview)
    {
        Id = id;
        Label = label;
        _preview = preview;
        _saved = ExtraArgsStore.Get(S.Settings, id);
        _extraArgs = _saved;
        Reparse();
    }

    public string Id { get; }
    /// <summary>Shown above the preview when the tile has several commands (log2timeline / psort, one per plugin).</summary>
    public string? Label { get; }
    public bool ShowLabel => !string.IsNullOrEmpty(Label);
    public string ArgsHelp { get; init; } = "Added to the command above; saved for the Tools page and Ingest.";
    public bool HasErrors => Errors.Count > 0;

    /// <summary>The typed arguments when they parse, else the saved ones (what Run and the preview use).</summary>
    public IReadOnlyList<string> CurrentArgs
    {
        get
        {
            var p = ExtraArgsStore.Parse(Id, ExtraArgs);
            return p.IsValid ? p.Args : ExtraArgsStore.Args(S.Settings, Id);
        }
    }

    /// <summary><see cref="CurrentArgs"/> as text (for builders that take the typed text, e.g. Volatility's).</summary>
    public string? CurrentText => ExtraArgsStore.Parse(Id, ExtraArgs).IsValid ? ExtraArgs : ExtraArgsStore.ValidText(S.Settings, Id);

    public string Preview
    {
        get
        {
            try { return _preview(CurrentArgs); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException) { return ex.Message; }
        }
    }

    /// <summary>Raised after Save / Reset (e.g. the Volatility plan is re-made).</summary>
    public event Action<SpecialCommandViewModel>? Saved;

    public void Refresh() => OnPropertyChanged(nameof(Preview));

    partial void OnExtraArgsChanged(string value)
    {
        IsDirty = !string.Equals(value.Trim(), _saved, StringComparison.Ordinal);
        Reparse();
        Refresh();
        SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    private void Reparse()
    {
        Errors = ExtraArgsStore.Parse(Id, ExtraArgs).Errors;
        OnPropertyChanged(nameof(HasErrors));
    }

    private bool CanSave() => IsDirty && Errors.Count == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Commit(ExtraArgs);

    private bool CanReset() => !string.IsNullOrWhiteSpace(ExtraArgs) || _saved.Length > 0;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset()
    {
        ExtraArgs = "";
        Commit("");
    }

    private void Commit(string text)
    {
        ExtraArgsStore.Set(S.Settings, Id, text);
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        _saved = ExtraArgsStore.Get(S.Settings, Id);
        IsDirty = false;
        SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        Saved?.Invoke(this);
    }

    [RelayCommand]
    private void Copy()
    {
        var text = Preview;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetDataObject(text, true); }
        catch (System.Runtime.InteropServices.COMException) { AppLog.Warn("Could not copy to the clipboard"); }
    }
}
