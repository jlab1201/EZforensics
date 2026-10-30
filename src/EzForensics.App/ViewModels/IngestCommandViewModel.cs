using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>One rule's editable Ingest command on the Tools page: text, parse feedback, example and Save / Reset / Copy.</summary>
public sealed partial class IngestCommandViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly TriageRule _rule;
    private string _saved;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private IReadOnlyList<string> _errors = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _warnings = Array.Empty<string>();
    [ObservableProperty] private string _example = "";
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private bool _isDirty;

    public IngestCommandViewModel(AppServices s, TriageRule rule)
    {
        _s = s;
        _rule = rule;
        _saved = s.IngestCommands.Get(rule.Id) ?? CustomCommand.DefaultText(rule);
        _text = _saved;
        _isCustom = CustomCommand.IsCustom(s.IngestCommands, rule.Id);
        _sourcePath = s.Settings.ToolSourcePaths.TryGetValue(rule.Id, out var sp) ? sp : "";
        Reparse();
    }

    /// <summary>The source the Run button reads: a folder (Dir rules) or a file. Remembered per rule in the settings.</summary>
    [ObservableProperty] private string _sourcePath = "";

    partial void OnSourcePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _s.Settings.ToolSourcePaths.Remove(_rule.Id);
        else _s.Settings.ToolSourcePaths[_rule.Id] = value;
        _s.SaveSettings();
        OnPropertyChanged(nameof(HasSource));
    }

    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);
    public string SourceLabel => _rule.Mode == RuleMode.Dir ? "Source folder" : "Source file";
    public string SourceHelp
    {
        get
        {
            var label = Label.Trim();
            var mask = string.IsNullOrWhiteSpace(_rule.Mask) ? "" : $" ({_rule.Mask})";
            return _rule.Mode == RuleMode.Dir
                ? $"Folder that holds the {char.ToLowerInvariant(label[0])}{label[1..]}{mask}"
                : $"File to parse: {label}";
        }
    }
    public string SourcePlaceholder => _rule.Mode == RuleMode.Dir ? "No folder selected" : "No file selected";

    /// <summary>Picks the source (the dialog starts at the remembered one); returns the new path or null when cancelled.</summary>
    public string? PickSource()
    {
        var start = HasSource ? SourcePath : null;
        var picked = _rule.Mode == RuleMode.Dir
            ? Dialogs.PickFolder($"Select the {SourceLabel.ToLowerInvariant()} ({Label})", start)
            : Dialogs.PickFile($"Select the {SourceLabel.ToLowerInvariant()} ({Label})", "All files|*.*", start);
        if (!string.IsNullOrEmpty(picked)) SourcePath = picked;
        return string.IsNullOrEmpty(picked) ? null : picked;
    }

    [RelayCommand]
    private void BrowseSource() => PickSource();

    /// <summary>Show the rule label above the box (only when the tool has more than one rule).</summary>
    public bool ShowLabel { get; init; }
    public bool IsPageOnly => _rule.Id.StartsWith("tool:", StringComparison.Ordinal);
    /// <summary>Caption under the box for page-only commands.</summary>
    public string? Caption => IsPageOnly ? "Run on this page only; Ingest does not use this command." : null;

    private EzForensics.Core.Execution.Job? _job;
    /// <summary>True while this row's job is queued or running (the Run button becomes Stop).</summary>
    public bool IsActive => _job is { IsFinished: false };

    /// <summary>Tracks the job started from this row.</summary>
    public void Attach(EzForensics.Core.Execution.Job? job)
    {
        if (_job != null) _job.PropertyChanged -= OnJobChanged;
        _job = job;
        if (_job != null) _job.PropertyChanged += OnJobChanged;
        RaiseActive();
    }

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EzForensics.Core.Execution.Job.State) or nameof(EzForensics.Core.Execution.Job.IsFinished)) RaiseActive();
    }

    private void RaiseActive()
    {
        void Do() { OnPropertyChanged(nameof(IsActive)); }
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Do(); else d.BeginInvoke(Do);
    }

    [RelayCommand]
    private void Stop() => _job?.Cancel();

    public string RuleId => _rule.Id;
    public string Label => string.IsNullOrWhiteSpace(_rule.Label) ? _rule.Id : _rule.Label;
    public RuleMode Mode => _rule.Mode;
    public TriageRule Rule => _rule;
    /// <summary>The typed command's arguments (exe removed), placeholders unexpanded.</summary>
    public IReadOnlyList<string> ParsedArgs => CustomCommand.Parse(Text, _rule).Args;
    /// <summary>Raised when the row's Run button is pressed; the Tools page picks the source and queues the job.</summary>
    public event Action<IngestCommandViewModel>? RunRequested;
    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;

    partial void OnTextChanged(string value)
    {
        IsDirty = !string.Equals(value, _saved, StringComparison.Ordinal);
        Reparse();
        SaveCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void Reparse()
    {
        var r = CustomCommand.Parse(Text, _rule);
        Errors = r.Errors;
        Warnings = r.Warnings;
        if (r.Args.Any(a => a.Contains("{dateFormat}", StringComparison.Ordinal)) && _s.DateFormatWarning is { } dw) Warnings = Warnings.Append(dw).ToList();
        Example = r.IsValid ? BuildExample(r.Args) : "";
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
    }

    private const char Bs = (char)92;

    private string BuildExample(IReadOnlyList<string> args)
    {
        const string evidence = @"<evidence>", tools = @"<tools>", cases = @"<case>";
        var first = _rule.Paths.FirstOrDefault()?.Replace('/', Bs) ?? "";
        var filePath = evidence + (first.Length > 0 ? Bs + first : "");
        var dirPath = _rule.Mode == RuleMode.Dir ? filePath : (filePath.Contains(Bs) ? filePath[..filePath.LastIndexOf(Bs)] : filePath);
        var map = new Dictionary<string, string>
        {
            ["{file}"] = filePath,
            ["{dir}"] = dirPath,
            ["{out}"] = cases + @"\.ezstaging\" + _rule.Id.Replace(':', '_'),
            ["{root}"] = evidence,
            ["{softwareHive}"] = evidence + @"\Windows\System32\config\SOFTWARE",
            ["{toolDir}"] = tools,
        };
        var expanded = _s.ExpandAppVariables(args, quiet: true, toolId: _rule.ToolId).Select(a => map.Aggregate(a, (acc, kv) => acc.Replace(kv.Key, kv.Value)));
        return string.Join(" ", new[] { _rule.ExeName }.Concat(expanded).Select(BuiltCommand.QuoteWindows));
    }

    private bool CanSave() => IsDirty && Errors.Count == 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var isDefault = string.Equals(Text.Trim(), CustomCommand.DefaultText(_rule), StringComparison.Ordinal);
        _s.IngestCommands.Set(_rule.Id, isDefault ? null : Text.Trim());
        _s.SaveIngestCommands();
        Committed();
    }

    private bool CanRunRow() => Errors.Count == 0;

    [RelayCommand(CanExecute = nameof(CanRunRow))]
    private void Run() => RunRequested?.Invoke(this);

    [RelayCommand]
    private void Reset()
    {
        Text = CustomCommand.DefaultText(_rule);
        _s.IngestCommands.Set(_rule.Id, null);
        _s.SaveIngestCommands();
        Committed();
        AfterReset?.Invoke();
    }

    /// <summary>Called after Reset so the page can re-apply app-managed parts (EvtxECmd's event filter).</summary>
    public Action? AfterReset { get; set; }

    private static readonly System.Text.RegularExpressions.Regex EventFilterRx =
        new(@"\s+--(?:inc|exc)\b(?:\s+(?!-)(?:""[^""]*""|[^\s""]+))?|\s*\{eventIds\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Replaces the command's --inc/--exc filter with <c>--inc <paramref name="ids"/></c>, or removes it when null; saves when the text changes.</summary>
    public void SetEventFilter(string? ids)
    {
        var text = EventFilterRx.Replace(Text, "").TrimEnd();
        if (!string.IsNullOrEmpty(ids)) text += " --inc " + ids;
        if (string.Equals(text, Text, StringComparison.Ordinal)) return;
        Text = text;
        var isDefault = string.Equals(text.Trim(), CustomCommand.DefaultText(_rule), StringComparison.Ordinal);
        _s.IngestCommands.Set(_rule.Id, isDefault ? null : text.Trim());
        _s.SaveIngestCommands();
        Committed();
    }

    [RelayCommand]
    private void Copy()
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        try { Clipboard.SetDataObject(Text, true); }
        catch (System.Runtime.InteropServices.COMException) { AppLog.Warn("Could not copy to the clipboard"); }
    }

    private void Committed()
    {
        _saved = Text;
        IsCustom = CustomCommand.IsCustom(_s.IngestCommands, _rule.Id);
        IsDirty = false;
        SaveCommand.NotifyCanExecuteChanged();
    }
}
