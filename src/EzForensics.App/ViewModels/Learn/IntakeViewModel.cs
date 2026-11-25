using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>Where an Intake row's file goes: a saved or new source (its name and folder), or loose files (both null).</summary>
public sealed record IntakeOrigin(string? SourceName, string? SourceFolder, long? SavedSourceId);

/// <summary>One file waiting in Intake. Only new and changed files can be ticked; the rest are shown greyed.</summary>
public sealed partial class IntakeRowVm : ObservableObject
{
    public IntakeRowVm(IntakeFile file, IntakeOrigin origin)
    {
        File = file; Origin = origin;
        _isSelected = IsSelectable;
    }

    public IntakeFile File { get; }
    public IntakeOrigin Origin { get; }
    public string FullPath => File.FullPath;
    public string Title => File.Title;
    public string RelPath => File.RelPath;
    public long Size => File.Size;
    public string SizeText => Size < 1024 ? $"{Size} B" : Size < 1024 * 1024 ? $"{Size / 1024} KB" : $"{Size / 1024 / 1024.0:0.#} MB";
    public bool IsSelectable => File.State is IntakeFileState.New or IntakeFileState.Changed;
    public string StateText => File.State switch
    {
        IntakeFileState.New => "New",
        IntakeFileState.Changed => "Changed",
        IntakeFileState.AlreadyIn => "Already in",
        IntakeFileState.RejectedBefore => "Rejected before",
        _ => "Unsupported",
    };

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _problem = "";
    public bool HasProblem => Problem.Length > 0;
    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));
}

/// <summary>
/// The Intake tab: pick or drop files, or check a saved folder for new notes, tick what to read and send it to the Inbox with AI labels.
/// Everything lands as pending; the AI never accepts anything.
/// </summary>
public sealed partial class IntakeViewModel : ObservableObject
{
    public const string QueueKey = "intake_queue";
    /// <summary>Test seam: runs before each file is read in Send to review; throwing marks that file as failed.</summary>
    internal Action<string>? AddProbe { get; set; }

    public const string NoAiHint = "Use local AI (Tools → AI)";

    private readonly KnowledgeWorkspace _ws;
    private readonly Func<(KnowledgeCompletion? Complete, string Model)> _ai;
    private readonly KnowledgeAiGate _gate;
    private readonly Action<string, InfoSeverity>? _notify;
    private CancellationTokenSource? _cts;
    private bool _restored;

    public IntakeViewModel(KnowledgeWorkspace ws, Func<(KnowledgeCompletion? Complete, string Model)>? ai = null, Action<string, InfoSeverity>? notify = null, Func<Task<bool>>? probe = null)
    {
        _ws = ws;
        _ws.BusyChanged += NotifyBusyCommands;
        _gate = new KnowledgeAiGate(ai, probe);
        _ai = _gate.Resolve;
        _notify = notify;
        RefreshAi();
        Rows.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (IntakeRowVm r in e.NewItems) r.PropertyChanged += OnRowChanged;
            if (e.OldItems != null) foreach (IntakeRowVm r in e.OldItems) r.PropertyChanged -= OnRowChanged;
            OnPropertyChanged(nameof(WaitingCount));
            OnPropertyChanged(nameof(IsEmpty));
            SendToReviewCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<SourceInfo> Sources { get; } = new();
    public ObservableCollection<IntakeRowVm> Rows { get; } = new();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckForNewCommand))] private SourceInfo? _selectedSource;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint))] private bool _hasAi;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint))] private bool _aiUnreachable;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _message = "";

    public int WaitingCount => Rows.Count;
    public bool IsEmpty => Rows.Count == 0;
    public string AiHint => HasAi ? "" : AiUnreachable ? KnowledgeAiGate.UnreachableHint : NoAiHint;

    /// <summary>Asked for a dropped or picked folder that is not a saved source (true = save it as one).</summary>
    public Func<string, bool> ConfirmSaveSource { get; set; } =
        m => Dialogs.Confirm("Save as a source?", m, "Save as source", "Add as loose files");

    public Func<string, string, IReadOnlyList<string>?> PickFiles { get; set; } = (t, f) => Dialogs.PickFiles(t, f);
    public Func<string, string?> PickFolder { get; set; } = t => Dialogs.PickFolder(t);

    /// <summary>Reads the AI settings again (they may have changed in Tools → AI).</summary>
    public void RefreshAi() => HasAi = _ai().Complete != null;

    /// <summary>Settings plus a reachability probe of the local AI server; false when the AI cannot be used right now.</summary>
    public async Task<bool> RefreshAiAsync()
    {
        var (ok, down) = await _gate.CheckAsync();
        AiUnreachable = down;
        HasAi = ok;
        return ok;
    }

    // ---- showing

    /// <summary>The tab was shown: re-read the AI settings and the saved sources, and put back rows that were dropped but not sent.</summary>
    public async Task OnShownAsync()
    {
        await RefreshAiAsync();
        if (!_ws.Exists) return;
        try
        {
            var (sources, queue) = await _ws.RunAsync(s => (s.Intake.SavedSources(), s.Store.GetMeta(QueueKey)));
            var keep = SelectedSource?.Id;
            Sources.Clear();
            foreach (var s in sources) Sources.Add(s);
            SelectedSource = Sources.FirstOrDefault(s => s.Id == keep) ?? Sources.FirstOrDefault();
            if (!_restored)
            {
                _restored = true;
                if (Rows.Count == 0 && !string.IsNullOrEmpty(queue))
                {
                    await RestoreQueueAsync(queue, sources);
                }
            }
        }
        catch (Exception ex) { Message = "Could not read the knowledge base: " + ex.Message; AppLog.Error("Intake open failed", ex); }
    }

    // ---- adding rows

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFilesAsync()
    {
        var files = PickFiles("Add documents", InboxViewModel.FileFilter);
        return files is { Count: > 0 } ? AddPathsAsync(files) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFolderAsync()
    {
        var dir = PickFolder("Add a folder (an Obsidian vault or a folder of documents)");
        return dir != null ? AddPathsAsync(new[] { dir }) : Task.CompletedTask;
    }

    /// <summary>Picks a folder and adds it as a source; its files are listed to send. The source is saved when the first files are sent.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task AddSourceAsync()
    {
        var dir = PickFolder("Add a source folder (an Obsidian vault or a folder of documents)");
        if (dir == null || Busy) return;
        IsBusy = true;
        try
        {
            await AddFolderCoreAsync(dir, true);
            Message = Waiting();
        }
        catch (Exception ex) { Message = "Could not check the folder: " + ex.Message; AppLog.Error("Intake check failed", ex); Report(Message, InfoSeverity.Error); }
        finally { IsBusy = false; ProgressText = ""; }
        PersistQueue();
    }

    /// <summary>Files and folders from the dialogs or a drop (also used by the page and the Graph). A folder can be saved as a source.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (Busy) return;
        IsBusy = true;
        Message = "";
        ProgressText = "Checking files…";
        try
        {
            var loose = new List<string>();
            foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(p))
                {
                    var saved = await _ws.RunAsync(s => s.Intake.SavedSources().FirstOrDefault(x => SamePath(x.Path, p)));
                    var asSource = saved != null || ConfirmSaveSource($"Save this folder as a source?\n\n{p}\n\nA source remembers the folder, so Check for new can list what is new in it later.");
                    await AddFolderCoreAsync(p, asSource);
                }
                else if (File.Exists(p)) loose.Add(p);
            }
            if (loose.Count > 0)
            {
                var files = await _ws.UseAsync(s => s.Intake.CheckFilesAsync(loose, CancellationToken.None), changes: false, embed: false);
                foreach (var f in files) AddRow(new IntakeRowVm(f, new IntakeOrigin(null, null, null)));
            }
            Message = Waiting();
        }
        catch (Exception ex) { Message = "Could not check the files: " + ex.Message; AppLog.Error("Intake check failed", ex); Report(Message, InfoSeverity.Error); }
        finally { IsBusy = false; ProgressText = ""; }
        PersistQueue();
    }

    private async Task AddFolderCoreAsync(string dir, bool asSource)
    {
        var saved = await _ws.RunAsync(s => s.Intake.SavedSources().FirstOrDefault(x => SamePath(x.Path, dir)));
        // "Save as source" saves it now (not when the first files are sent), so it is still there after a restart
        if (saved == null && asSource) saved = await _ws.RunAsync(s => s.Intake.SaveSource(dir), changes: true);
        IReadOnlyList<IntakeFile> files;
        IntakeOrigin origin;
        if (saved != null)
        {
            files = await _ws.UseAsync(s => s.Intake.CheckForNewAsync(saved.Id, CancellationToken.None), changes: false, embed: false);
            origin = new IntakeOrigin(saved.Name, saved.Path, saved.Id);
            RemoveRowsOf(saved.Id);
        }
        else
        {
            files = await _ws.UseAsync(s => s.Intake.CheckFilesAsync(new[] { dir }, CancellationToken.None), changes: false, embed: false);
            origin = new IntakeOrigin(null, null, null);
        }
        foreach (var f in files.Where(f => f.State != IntakeFileState.Unsupported)) AddRow(new IntakeRowVm(f, origin));
        if (saved != null && Sources.All(x => x.Id != saved.Id)) { Sources.Add(saved); SelectedSource ??= saved; }
    }

    private static bool SamePath(string a, string b) =>
        a.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private void AddRow(IntakeRowVm row)
    {
        if (Rows.Any(r => string.Equals(r.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase))) return;
        Rows.Add(row);
    }

    private void RemoveRowsOf(long sourceId)
    {
        foreach (var r in Rows.Where(r => r.Origin.SavedSourceId == sourceId).ToList()) Rows.Remove(r);
    }

    private string Waiting() => Rows.Count == 0 ? "Nothing new to add."
        : $"{Rows.Count(r => r.IsSelectable)} ready to send, {Rows.Count(r => !r.IsSelectable)} already in or rejected before.";

    /// <summary>Compares the selected saved source's folder with the knowledge base and lists what is new or changed.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckForNewAsync()
    {
        if (SelectedSource is not { } src) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Message = "";
        ProgressText = $"Checking {src.Name}…";
        var progress = new Progress<(int done, int total)>(p => { if (IsBusy) ProgressText = $"Checking {src.Name} {p.done}/{p.total}"; });
        try
        {
            var files = await _ws.UseAsync(s => s.Intake.CheckForNewAsync(src.Id, ct, progress), changes: false, embed: false);
            RemoveRowsOf(src.Id);
            var origin = new IntakeOrigin(src.Name, src.Path, src.Id);
            foreach (var f in files.Where(f => f.State != IntakeFileState.Unsupported)) AddRow(new IntakeRowVm(f, origin));
            var fresh = Rows.Count(r => r.Origin.SavedSourceId == src.Id && r.IsSelectable);
            Message = fresh == 0 ? $"Nothing new in {src.Name}." : $"{fresh} new or changed in {src.Name}.";
        }
        catch (OperationCanceledException) { Message = "Cancelled. The list is unchanged."; }
        catch (Exception ex) { Message = ex.Message; AppLog.Error("Check for new failed", ex); Report(Message, InfoSeverity.Warning); }
        finally { IsBusy = false; ProgressText = ""; _cts?.Dispose(); _cts = null; }
        PersistQueue();
    }

    private bool CanCheck() => !Busy && SelectedSource != null;
    private bool CanRun() => !Busy;

    [RelayCommand]
    private void SelectAllNew()
    {
        foreach (var r in Rows.Where(r => r.IsSelectable)) r.IsSelected = true;
    }

    [RelayCommand]
    private void Clear()
    {
        if (Busy) return;
        Rows.Clear();
        PersistQueue();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void OnRowChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IntakeRowVm.IsSelected)) SendToReviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        _ws.SetBusy(value);
        NotifyBusyCommands();
    }

    /// <summary>True while this tab or any other Learn tab runs a job (one job at a time on the knowledge base).</summary>
    private bool Busy => IsBusy || _ws.IsBusy;

    private void NotifyBusyCommands()
    {
        AddFilesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        AddSourceCommand.NotifyCanExecuteChanged();
        CheckForNewCommand.NotifyCanExecuteChanged();
        SendToReviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasAiChanged(bool value) => SendToReviewCommand.NotifyCanExecuteChanged();

    // ---- send to review

    private bool CanSend() => HasAi && !Busy && Rows.Any(r => r.IsSelected && r.IsSelectable);

    /// <summary>Reads the ticked files into the Inbox as pending notes and labels them with the AI. Sent rows leave the list; failed ones stay with the problem.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendToReviewAsync()
    {
        if (!await RefreshAiAsync()) { Message = AiHint; return; }
        var (complete, model) = _ai();
        if (complete == null) return;
        var ticked = Rows.Where(r => r.IsSelected && r.IsSelectable).ToList();
        var groups = ticked.GroupBy(r => (r.Origin.SourceName, r.Origin.SourceFolder)).ToList();
        foreach (var r in ticked) r.Problem = "";
        IsBusy = true;
        Message = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sent = new HashSet<IntakeRowVm>();
        var labelFailed = 0;
        var skipped = 0;
        string? cancelled = null;
        string? failed = null;
        // created here, on the UI thread, so their callbacks are marshalled to it
        var reading = new Progress<(int done, int total)>(p => ProgressText = $"Reading {p.done}/{p.total}");
        var labelling = new Progress<(int done, int total)>(p => ProgressText = $"Labelling {p.done}/{p.total}");
        try
        {
            await _ws.UseAsync(async sess =>
            {
                var labeler = new KnowledgeLabeler(complete, model);
                foreach (var g in groups)
                {
                    var files = g.ToList();
                    var ids = new List<long>();
                    var n = 0;
                    ((IProgress<(int, int)>)reading).Report((0, files.Count));
                    // labels the notes added so far (up to one batch of 6 at a time), so a cancel or a failure later on
                    // never leaves the earlier notes unlabelled
                    async Task LabelPendingAsync()
                    {
                        if (ids.Count == 0) return;
                        var batch = ids.ToList();
                        ids.Clear();
                        ((IProgress<(int, int)>)labelling).Report((0, batch.Count));
                        var lr = await labeler.LabelAsync(sess.Store, batch, labelling, ct);
                        labelFailed += lr.Failed;
                    }
                    // one file at a time, so each row knows what happened to its own file
                    foreach (var r in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            AddProbe?.Invoke(r.FullPath);
                            var (result, noteIds) = await sess.Intake.AddSelectedAsync(g.Key.SourceName, g.Key.SourceFolder, new[] { r.FullPath }, null, ct);
                            if (result.Errors.Count > 0) r.Problem = result.Errors[0].Error;
                            else if (result.Added > 0 || noteIds.Count > 0) { sent.Add(r); ids.AddRange(noteIds); }
                            else if (result.AlreadyWaiting > 0) { r.Problem = "Already waiting in To review"; skipped++; }
                            else if (result.RejectedBefore > 0) { r.Problem = "Rejected before (see Rejected in To review)"; skipped++; }
                            else { r.Problem = "Already in the library"; skipped++; }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // any other error (access denied, a parser bug) belongs to this file only
                            r.Problem = ex.Message;
                            AppLog.Warn($"Intake could not read {r.FullPath}: {ex.Message}");
                        }
                        ((IProgress<(int, int)>)reading).Report((++n, files.Count));
                        if (ids.Count >= KnowledgeLabeler.BatchSize) await LabelPendingAsync();
                    }
                    await LabelPendingAsync();
                }
                return 0;
            });
        }
        catch (OperationCanceledException) { cancelled = "Cancelled. Notes already sent are waiting in To review; the rest stay here."; _ws.NotifyChanged(); }
        catch (Exception ex)
        {
            failed = "Sending failed: " + ex.Message;
            AppLog.Error("Intake send failed", ex);
            _ws.NotifyChanged();
        }
        finally
        {
            foreach (var r in sent) Rows.Remove(r);
            IsBusy = false;
            ProgressText = "";
            _cts?.Dispose();
            _cts = null;
        }
        if (cancelled != null) Message = cancelled;
        else if (failed != null) { Message = failed; Report(Message, InfoSeverity.Error); }
        else
        {
            Message = $"Sent {sent.Count} for review" + (labelFailed > 0 ? $" ({labelFailed} couldn't be labelled, retry in To review)." : ".");
            if (skipped > 0) Message += $" {skipped} skipped (already waiting, already in or rejected before), see the list.";
            var problems = ticked.Count(r => r.HasProblem) - skipped;
            if (problems > 0) Message += $" {problems} could not be read, see the list.";
            Report(Message, labelFailed > 0 || problems > 0 || skipped > 0 ? InfoSeverity.Warning : InfoSeverity.Success);
        }
        PersistQueue();
    }

    private void Report(string text, InfoSeverity severity) => _notify?.Invoke(text, severity);

    // ---- the queue survives a restart

    /// <summary>One queued file with where it goes (a saved source's id, or nothing for a loose file).</summary>
    private sealed record QueuedFile(string Path, long? SourceId);

    private void PersistQueue()
    {
        try
        {
            var queue = Rows.Select(r => new QueuedFile(r.FullPath, r.Origin.SavedSourceId)).ToList();
            if (queue.Count == 0 && !_ws.Exists) return;
            var json = JsonSerializer.Serialize(queue);
            _ws.Run(s => { s.Store.SetMeta(QueueKey, json); return 0; });
        }
        catch (Exception ex) { AppLog.Warn("Could not save the Intake list: " + ex.Message); }
    }

    /// <summary>Reads the saved list (the old format was just paths) and puts the rows back with their source.</summary>
    private async Task RestoreQueueAsync(string queue, IReadOnlyList<SourceInfo> sources)
    {
        var items = new List<QueuedFile>();
        try
        {
            using var doc = JsonDocument.Parse(queue);
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) items.Add(new QueuedFile(e.GetString() ?? "", null));
                else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Path", out var p))
                    items.Add(new QueuedFile(p.GetString() ?? "", e.TryGetProperty("SourceId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null));
        }
        catch (JsonException) { return; }
        items = items.Where(i => File.Exists(i.Path)).ToList();

        var loose = items.Where(i => i.SourceId == null || sources.All(s => s.Id != i.SourceId)).Select(i => i.Path).ToList();
        if (loose.Count > 0)
        {
            var files = await _ws.UseAsync(s => s.Intake.CheckFilesAsync(loose, CancellationToken.None), changes: false, embed: false);
            foreach (var f in files) AddRow(new IntakeRowVm(f, new IntakeOrigin(null, null, null)));
        }
        foreach (var g in items.Where(i => i.SourceId != null && sources.Any(s => s.Id == i.SourceId)).GroupBy(i => i.SourceId!.Value))
        {
            var src = sources.First(s => s.Id == g.Key);
            var want = g.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = await _ws.UseAsync(s => s.Intake.CheckForNewAsync(src.Id, CancellationToken.None), changes: false, embed: false);
            var origin = new IntakeOrigin(src.Name, src.Path, src.Id);
            foreach (var f in files.Where(f => want.Contains(f.FullPath))) AddRow(new IntakeRowVm(f, origin));
        }
    }
}
