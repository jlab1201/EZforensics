using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "Command" tile of plaso, Zeek, RITA and Volatility on the Tools page, in the disk-tool layout: Source (remembered in
/// <c>AppSettings.ToolSourcePaths</c>), Output folder (auto = a new folder under the case root, or Change…), the app-built command rows
/// (<see cref="SpecialCommandViewModel"/>) and Run, which the owning card supplies.
/// </summary>
public sealed partial class SpecialCommandTileViewModel : ObservableObject
{
    private static AppServices S => AppServices.Current;
    private readonly string _sourceKey;
    private readonly string _outputSuffix;
    private readonly Func<Task> _run;
    private readonly Func<string?> _blocked;
    private string _autoOutputDir;
    private string? _customOutputDir;

    /// <param name="sourceKey">Key of the remembered source in <c>ToolSourcePaths</c> (e.g. <c>special:plaso</c>).</param>
    /// <param name="outputSuffix">Suffix of the auto output folder (<c>&lt;CaseRoot&gt;\&lt;stamp&gt;_&lt;suffix&gt;</c>).</param>
    /// <param name="run">Queues the run (source and output folder read from this tile).</param>
    /// <param name="blocked">Why Run cannot start now (tool not set up...), null when it can.</param>
    public SpecialCommandTileViewModel(string sourceKey, string outputSuffix, Func<Task> run, Func<string?> blocked)
    {
        _sourceKey = sourceKey;
        _outputSuffix = outputSuffix;
        _run = run;
        _blocked = blocked;
        _autoOutputDir = NewAutoOutputDir();
        _sourcePath = S.Settings.ToolSourcePaths.TryGetValue(sourceKey, out var sp) ? sp : "";
        Rows.CollectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
    }

    public string AutomationId { get; init; } = "Tools.Special.Command";
    public string SourceLabel { get; init; } = "Source";
    public string SourcePlaceholder { get; init; } = "Nothing selected";
    public bool AllowFile { get; init; }
    public bool AllowFolder { get; init; }
    public string FileFilter { get; init; } = "All files|*.*";
    public string FileButtonText => AllowFolder ? "File…" : "Browse…";
    public string FolderButtonText => AllowFile ? "Folder…" : "Browse…";
    public string OutputHelp { get; init; } = "Auto uses a new folder under the case root for every run.";
    public string RunText { get; init; } = "Run";
    /// <summary>Optional second button next to Run (Zeek: Open in Analyze).</summary>
    public string? SecondaryText { get; init; }
    public ICommand? SecondaryCommand { get; init; }
    public bool HasSecondary => SecondaryCommand != null;
    /// <summary>Optional caption above the rows (e.g. which commands Run starts).</summary>
    public string? Caption { get; init; }
    public bool HasCaption => !string.IsNullOrEmpty(Caption);

    /// <summary>Caption under the source (the input summary, or what the source should be).</summary>
    [ObservableProperty] private string _sourceHelp = "";
    [ObservableProperty] private string _sourcePath;

    public ObservableCollection<SpecialCommandViewModel> Rows { get; } = new();

    /// <summary>Raised when the source or the output folder changes.</summary>
    public event Action? Changed;

    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);
    /// <summary>The source path without surrounding quotes / spaces.</summary>
    public string Source => SourcePath.Trim().Trim('"');

    partial void OnSourcePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) S.Settings.ToolSourcePaths.Remove(_sourceKey);
        else S.Settings.ToolSourcePaths[_sourceKey] = value;
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(Source));
        RaiseChanged();
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var f = Dialogs.PickFile($"Select the {SourceLabel.ToLowerInvariant()}", FileFilter, HasSource ? Source : null);
        if (!string.IsNullOrEmpty(f)) SourcePath = f;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var d = Dialogs.PickFolder($"Select the {SourceLabel.ToLowerInvariant()}", HasSource ? Source : null);
        if (!string.IsNullOrEmpty(d)) SourcePath = d;
    }

    // ------------------------------------------------------------------ output folder

    public string OutputDir => _customOutputDir ?? _autoOutputDir;
    public bool OutputDirIsCustom => _customOutputDir != null;

    private string NewAutoOutputDir() => Path.Combine(S.Settings.CaseRoot, $"{AppServices.Stamp()}_{_outputSuffix}");

    [RelayCommand]
    private void ChangeOutputDir()
    {
        var f = Dialogs.PickFolder("Select the output folder", OutputDir);
        if (string.IsNullOrEmpty(f)) return;
        _customOutputDir = f;
        RaiseOutput();
    }

    [RelayCommand]
    private void ResetOutputDir()
    {
        _customOutputDir = null;
        _autoOutputDir = NewAutoOutputDir();
        RaiseOutput();
    }

    /// <summary>After a run: the auto folder moves to a fresh timestamp so the next run does not collide.</summary>
    public void RollAutoOutputDir()
    {
        if (_customOutputDir != null) return;
        _autoOutputDir = NewAutoOutputDir();
        RaiseOutput();
    }

    private void RaiseOutput()
    {
        OnPropertyChanged(nameof(OutputDir));
        OnPropertyChanged(nameof(OutputDirIsCustom));
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
        Refresh();
    }

    // ------------------------------------------------------------------ run

    /// <summary>Why Run is disabled (tool not set up, no source, invalid arguments); empty when it can run.</summary>
    public string RunHint => _blocked() ?? (!HasSource ? $"Choose the {SourceLabel.ToLowerInvariant()} first."
        : Rows.Any(r => r.HasErrors) ? "Fix the additional arguments first." : "");
    public bool HasRunHint => RunHint.Length > 0;

    private bool _running;
    private bool CanRun() => !_running && !HasRunHint;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        _running = true;
        RunCommand.NotifyCanExecuteChanged();
        try { await _run(); }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    /// <summary>Re-reads the previews and the Run state (call when the install, the options or the rows change).</summary>
    public void Refresh()
    {
        foreach (var r in Rows) r.Refresh();
        OnPropertyChanged(nameof(RunHint));
        OnPropertyChanged(nameof(HasRunHint));
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Replaces the rows (Volatility: one per ticked plugin), keeping each row's Refresh hooked to the Run state.</summary>
    public void SetRows(IEnumerable<SpecialCommandViewModel> rows)
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var r in rows)
        {
            r.PropertyChanged += OnRowChanged;
            Rows.Add(r);
        }
        Refresh();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SpecialCommandViewModel.HasErrors)) return;
        OnPropertyChanged(nameof(RunHint));
        OnPropertyChanged(nameof(HasRunHint));
        RunCommand.NotifyCanExecuteChanged();
    }
}
