using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Catalog;
using EzForensics.Core.Commands;

namespace EzForensics.App.ViewModels;

/// <summary>One generated form field, bound to a <see cref="ParameterDef"/>.</summary>
public sealed partial class ParameterViewModel : ObservableObject
{
    private readonly ToolFormViewModel _owner;

    [ObservableProperty] private string? _value;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private DateTime? _datePart;
    [ObservableProperty] private string _timePart = "00:00:00";

    public ParameterViewModel(ToolFormViewModel owner, ParameterDef def)
    {
        _owner = owner;
        Def = def;
        if (def.Type == ParamType.Enum)
        {
            var list = new List<string>();
            if (!def.Required) list.Add("");
            list.AddRange(def.Values ?? new List<string>());
            EnumValues = list;
        }
    }

    public ParameterDef Def { get; }
    public string Id => Def.Id;
    public ParamType Type => Def.Type;
    public string Label => string.IsNullOrWhiteSpace(Def.Label) ? Def.Flag : Def.Label;
    public string Flag => Def.Flag;
    public string Description => Def.Description;
    public bool Required => Def.Required;
    public bool Advanced => Def.Advanced;
    public IReadOnlyList<string> EnumValues { get; } = Array.Empty<string>();

    public string? Placeholder => Def.Placeholder
        ?? (Def.Type == ParamType.Multi ? "Comma separated values" : null)
        ?? (Def.Default != null && Def.Type != ParamType.Bool ? "default: " + Def.Default : null);

    public string ToolTipText => string.IsNullOrWhiteSpace(Description) ? Flag : $"{Flag}\n{Description}";

    public bool CanBrowseFile => Type is ParamType.File or ParamType.FileOrFolder;
    public bool CanBrowseFolder => Type is ParamType.Folder or ParamType.FileOrFolder or ParamType.OutputDir;

    /// <summary>CheckBox binding; null Value means "tool default".</summary>
    public bool BoolValue
    {
        get => ParseBool(Value) ?? Def.DefaultIsTrue;
        set { Value = value ? "true" : "false"; OnPropertyChanged(); }
    }

    partial void OnValueChanged(string? value)
    {
        if (Type == ParamType.Bool) OnPropertyChanged(nameof(BoolValue));
        _owner.Recompute();
    }

    partial void OnDatePartChanged(DateTime? value) => SyncDateValue();
    partial void OnTimePartChanged(string value) => SyncDateValue();

    private void SyncDateValue()
    {
        if (DatePart is not DateTime d) { Value = null; return; }
        var t = string.IsNullOrWhiteSpace(TimePart) ? "00:00:00" : TimePart.Trim();
        Value = $"{d:yyyy-MM-dd} {t}";
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var f = Dialogs.PickFile($"Select {Label}", Def.Filter, Value);
        if (f != null) Value = f;
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var f = Dialogs.PickFolder($"Select {Label}", Value);
        if (f != null) Value = f;
    }

    private static bool? ParseBool(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null,
    };
}

/// <summary>One file a run produced (Output tile).</summary>
public sealed record ProducedFile(string Path, string Name, string SizeText);

/// <summary>The dynamically generated form for one CLI tool.</summary>
public sealed partial class ToolFormViewModel : ObservableObject
{
    private bool _suspend;
    private string _autoOutputDir = "";
    private string? _customOutputDir;

    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _powerShellPreview = "";
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private string _exePath = "";
    [ObservableProperty] private bool _isInstalled;

    public ToolFormViewModel(ToolDefinition def, ToolManagerViewModel? manager = null)
    {
        Def = def;
        if (manager != null) Release = new ToolReleaseViewModel(def, manager);
        _suspend = true;
        Parameters = def.Parameters.Select(p => new ParameterViewModel(this, p)).ToList();

        // Default output folder: <CaseRoot>\<timestamp>_<ToolId> for the first output dir (and any required one).
        _autoOutputDir = NewAutoOutputDir();
        bool first = true;
        foreach (var p in Parameters.Where(p => p.Type == ParamType.OutputDir))
        {
            if (first || p.Required) p.Value = _autoOutputDir;
            first = false;
        }
        // Settings.DateFormat pre-fills the shared --dt parameter.
        var fmt = AppServices.Current.Settings.DateFormat;
        if (!string.IsNullOrWhiteSpace(fmt))
        {
            var dt = Parameters.FirstOrDefault(p => p.Id.Equals("dt", StringComparison.OrdinalIgnoreCase));
            if (dt != null) dt.Value = fmt;
        }
        if (def.Id.Equals("RECmd", StringComparison.OrdinalIgnoreCase))
            RecmdBatch = new RecmdBatchViewModel(AppServices.Current, () => File.Exists(ExePath) ? Path.GetDirectoryName(ExePath) : null);
        if (def.Id.Equals("EvtxECmd", StringComparison.OrdinalIgnoreCase))
            Evtx = new EvtxSelectionViewModel(AppServices.Current, () => { SyncEvtxCommand(); Recompute(); });
        var rules = EzForensics.Core.Triage.TriageRules.LoadEmbedded()
            .Where(r => r.ToolId.Equals(def.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        // rla / iisGeolocate have no Ingest rule: a pseudo rule ("tool:<Id>", not in rules.json, so Ingest ignores it) keeps their command editable here.
        if (rules.Count == 0 && PageOnlyRule(def) is { } pseudo) { rules.Add(pseudo); IsPageOnly = true; }
        IngestCommands = rules.Select(r => new IngestCommandViewModel(AppServices.Current, r) { ShowLabel = rules.Count > 1 }).ToList();
        if (Evtx != null)
        {
            foreach (var c in IngestCommands) c.AfterReset = SyncEvtxCommand;
            if (Evtx.UseSelected) SyncEvtxCommand();
        }
        _suspend = false;
        RefreshExe();
        Recompute();
    }

    private static EzForensics.Core.Triage.TriageRule? PageOnlyRule(ToolDefinition def)
    {
        var args = def.Id.ToLowerInvariant() switch
        {
            "rla" => new List<string> { "-d", "{dir}", "--out", "{out}" },
            "iisgeolocate" => new List<string> { "-d", "{dir}", "--csv", "{out}" },
            _ => null,
        };
        return args == null ? null : new EzForensics.Core.Triage.TriageRule
        {
            Id = "tool:" + def.Id, Label = def.DisplayName, ToolId = def.Id, ExeName = def.Exe, Category = def.Category,
            Mode = EzForensics.Core.Triage.RuleMode.Dir, Args = args, DefaultSelected = false,
        };
    }

    /// <summary>The command only runs from this page (no Ingest rule).</summary>
    public bool IsPageOnly { get; private set; }

    /// <summary>Last job started from this panel and its log; the Output section shows once something was run.</summary>
    [ObservableProperty] private EzForensics.Core.Execution.Job? _currentJob;
    [ObservableProperty] private bool _hasRun;
    /// <summary>Source (folder or file) the last run read.</summary>
    [ObservableProperty] private string _lastSource = "";

    /// <summary>Files the last run produced in its output folder (job log and manifests excluded).</summary>
    public ObservableCollection<ProducedFile> OutputFiles { get; } = new();
    public bool HasOutputFiles => OutputFiles.Count > 0;
    public string OutputFolderShown => CurrentJob?.Spec.OutputDir ?? "";

    /// <summary>One line under the state chip: the job's StatusReason (or what it is doing).</summary>
    public string StatusText => CurrentJob?.StatusReason is { Length: > 0 } r ? r
        : CurrentJob is { IsFinished: false } ? (CurrentJob.State == EzForensics.Core.Execution.JobState.Queued ? "Queued..." : "Running...") : "";

    /// <summary>No input found, no rows or no output files: shown in the warning colour.</summary>
    public bool ReasonIsWarning => CurrentJob?.StatusReason is { } r
        && (EzForensics.Core.Execution.JobOutcomeClassifier.IsNoInput(r) || r.Contains(", 0 rows", StringComparison.Ordinal)
            || r.StartsWith("Completed but produced no output", StringComparison.Ordinal));

    /// <summary>The file Open output folder selects in Explorer: the largest CSV, else the largest file.</summary>
    public string? MainOutputFile
    {
        get
        {
            var files = OutputFiles.Where(f => File.Exists(f.Path)).Select(f => new FileInfo(f.Path)).ToList();
            return (files.Where(f => f.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Length).FirstOrDefault()
                    ?? files.OrderByDescending(f => f.Length).FirstOrDefault())?.FullName;
        }
    }

    partial void OnCurrentJobChanged(EzForensics.Core.Execution.Job? oldValue, EzForensics.Core.Execution.Job? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnJobChanged;
        if (newValue != null) newValue.PropertyChanged += OnJobChanged;
        OutputFiles.Clear();
        RaiseJob();
    }

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) RaiseJob(); else d.BeginInvoke(new Action(RaiseJob));
    }

    private void RaiseJob()
    {
        if (CurrentJob is { IsFinished: true } job) RefreshOutputFiles(job);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ReasonIsWarning));
        OnPropertyChanged(nameof(OutputFolderShown));
    }

    private void RefreshOutputFiles(EzForensics.Core.Execution.Job job)
    {
        try
        {
            OutputFiles.Clear();
            var dir = job.Spec.OutputDir;
            if (Directory.Exists(dir))
            {
                var since = (job.StartUtc ?? DateTime.UtcNow).AddSeconds(-5);
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                             .Where(f => !EzForensics.Core.Execution.OutputNaming.IsEzForensicsSidecar(f)
                                         && !EzForensics.Core.Execution.JobQueue.IsJobBookkeepingFile(f, job.LogPath)
                                         && File.GetLastWriteTimeUtc(f) >= since)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(200))
                    OutputFiles.Add(new ProducedFile(f, Path.GetRelativePath(dir, f), FormatSize(new FileInfo(f).Length)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        OnPropertyChanged(nameof(HasOutputFiles));
    }

    private static string FormatSize(long b) => b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";
    public LogBuffer Log { get; } = new();

    /// <summary>Release tab data: installed version, updates, maps sync.</summary>
    public ToolReleaseViewModel? Release { get; }

    public string Limitation => Def.Limitation;
    public bool HasLimitation => !string.IsNullOrWhiteSpace(Def.Limitation);
    public string CategoryLabel => ToolCategoryBadge.Label(Def.Category);
    public int CategoryIndex => ToolCategoryBadge.Index(Def.Category);

    /// <summary>EvtxECmd only: the "use recommended event selection" block (its --inc list is added to the built command).</summary>
    public EvtxSelectionViewModel? Evtx { get; }

    /// <summary>RECmd only: the batch file used for {batchFile}.</summary>
    public RecmdBatchViewModel? RecmdBatch { get; }
    public bool HasBatchFile => RecmdBatch != null;

    /// <summary>The editable Ingest commands of the rules that run this tool.</summary>
    public IReadOnlyList<IngestCommandViewModel> IngestCommands { get; } = Array.Empty<IngestCommandViewModel>();
    public bool HasIngestCommands => IngestCommands.Count > 0;
    public bool HasEventIds => Evtx != null;

    public ToolDefinition Def { get; }
    public string DisplayName => Def.DisplayName;
    public string Description => Def.Description;
    public string Usage => Def.Usage;
    public string Category => Def.Category;
    public IReadOnlyList<ParameterViewModel> Parameters { get; }
    public ObservableCollection<string> Errors { get; } = new();
    public BuiltCommand? Built { get; private set; }

    /// <summary>Folder used as the job's OutputDir (and case dir): the first output-dir value set, else the auto folder.</summary>
    public string OutputDir =>
        !string.IsNullOrWhiteSpace(_customOutputDir) ? _customOutputDir! :
        Parameters.Where(p => p.Type == ParamType.OutputDir && p.IsVisible && !string.IsNullOrWhiteSpace(p.Value))
                  .Select(p => p.Value!.Trim()).FirstOrDefault() ?? _autoOutputDir;

    /// <summary>True while the output folder is the generated <c>&lt;CaseRoot&gt;\&lt;stamp&gt;_&lt;Tool&gt;</c> one (no user pick).</summary>
    public bool OutputDirIsAuto => string.IsNullOrWhiteSpace(_customOutputDir);
    public bool OutputDirIsCustom => !OutputDirIsAuto;

    [RelayCommand]
    private void ChangeOutputDir()
    {
        var f = Dialogs.PickFolder("Select the output folder", OutputDir);
        if (string.IsNullOrEmpty(f)) return;
        _customOutputDir = f;
        OnPropertyChanged(nameof(OutputDir));
        OnPropertyChanged(nameof(OutputDirIsAuto));
        OnPropertyChanged(nameof(OutputDirIsCustom));
    }

    [RelayCommand]
    private void ResetOutputDir()
    {
        _customOutputDir = null;
        OnPropertyChanged(nameof(OutputDir));
        OnPropertyChanged(nameof(OutputDirIsAuto));
        OnPropertyChanged(nameof(OutputDirIsCustom));
    }

    public void RefreshExe()
    {
        var t = AppServices.Current.ResolveNonBlocking(Def.Exe);
        IsInstalled = t != null;
        ExePath = t?.Path ?? Def.Exe;
        Release?.Refresh();
        Recompute();
    }

    /// <summary>After a run: move auto-filled output folders to a fresh timestamp so the next run does not collide.</summary>
    public void RollAutoOutputDir()
    {
        var old = _autoOutputDir;
        _autoOutputDir = NewAutoOutputDir();
        _suspend = true;
        foreach (var p in Parameters.Where(p => p.Type == ParamType.OutputDir && p.Value == old)) p.Value = _autoOutputDir;
        _suspend = false;
        Recompute();
    }

    /// <summary>Writes the Event selection into EvtxECmd's command: Selected adds <c>--inc &lt;ids&gt;</c>, All removes any filter.</summary>
    private void SyncEvtxCommand()
    {
        if (Evtx == null) return;
        foreach (var c in IngestCommands) c.SetEventFilter(Evtx.FilterList);
    }

    public void Recompute()
    {
        if (_suspend) return;
        var values = Parameters.ToDictionary(p => p.Id, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parameters) p.IsVisible = CommandBuilder.IsVisible(Def, p.Def, values);
        try
        {
            Built = CommandBuilder.Build(Def, values);
            if (Built.IsValid) Built = Built with { Args = AppServices.Current.ExpandAppVariables(Built.Args, quiet: true, toolId: Def.Id) };
            Preview = Built.ToDisplayString(ExePath);
            PowerShellPreview = Built.ToPowerShell(ExePath);
            Errors.Clear();
            foreach (var e in Built.Errors) Errors.Add(e);
            IsValid = Built.IsValid;
        }
        catch (Exception ex)
        {
            Built = null;
            Errors.Clear();
            Errors.Add("Could not build the command: " + ex.Message);
            IsValid = false;
        }
        OnPropertyChanged(nameof(OutputDir));
    }

    private string NewAutoOutputDir() =>
        Path.Combine(AppServices.Current.Settings.CaseRoot, $"{AppServices.Stamp()}_{Def.Id}");
}
