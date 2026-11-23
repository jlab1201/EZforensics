using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>One accepted document of a source, in the list under the source (select several to label or distill them).</summary>
public sealed class LibraryDocRow(long id, string title, string category, int children)
{
    public long Id { get; } = id;
    public string Title { get; } = title;
    public string Category { get; } = category;
    public bool HasCategory => Category.Length > 0;
    /// <summary>Notes already distilled from this document (waiting or accepted).</summary>
    public int Children { get; } = children;
    public bool HasChildren => Children > 0;
    public string NotesText => EzForensics.Core.Common.Plural.Of(Children, "note");
}

/// <summary>One source in the Library with its notes counted by review status.</summary>
public sealed partial class LibrarySourceRow : ObservableObject
{
    public LibrarySourceRow(SourceInfo info, int accepted, int pending, int rejected, int chunks, string status,
        IAsyncRelayCommand<LibrarySourceRow> reindex, IAsyncRelayCommand<LibrarySourceRow> remove, IAsyncRelayCommand<LibrarySourceRow> export,
        IAsyncRelayCommand<LibrarySourceRow> labelAi, IAsyncRelayCommand<LibrarySourceRow> distill)
    {
        Info = info; Accepted = accepted; Pending = pending; Rejected = rejected; Chunks = chunks;
        Status = status.Length == 0 ? "Ready" : status;
        HasProblem = status.Length > 0;
        ReindexCommand = reindex; RemoveCommand = remove; ExportCommand = export; LabelAiCommand = labelAi; DistillCommand = distill;
    }

    /// <summary>The source's accepted documents (loaded when it is expanded).</summary>
    public ObservableCollection<LibraryDocRow> Documents { get; } = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _loadingDocs;
    private IReadOnlyList<LibraryDocRow> _selectedDocs = Array.Empty<LibraryDocRow>();
    public IReadOnlyList<LibraryDocRow> SelectedDocs => _selectedDocs;
    public int SelectedCount => _selectedDocs.Count;
    public bool HasDocSelection => _selectedDocs.Count > 0;
    public string SelectedText => _selectedDocs.Count == 0 ? "Select documents to distill or label them." : $"{_selectedDocs.Count} selected";
    /// <summary>Called when the selection changed (the Library refreshes the buttons).</summary>
    public Action? SelectionChanged { get; set; }

    public void SetSelectedDocs(IEnumerable<LibraryDocRow> docs)
    {
        _selectedDocs = docs.ToList();
        OnPropertyChanged(nameof(SelectedDocs)); OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasDocSelection)); OnPropertyChanged(nameof(SelectedText));
        SelectionChanged?.Invoke();
    }

    public SourceInfo Info { get; }
    public string Name => Info.Name;
    public string Kind => Info.Kind switch { "vault" => "Vault folder", "pack" => "Knowledge pack", "files" => "Documents", _ => Info.Kind };
    public int Accepted { get; }
    public int Pending { get; }
    public int Rejected { get; }
    public int Chunks { get; }
    public string Counts => $"{Accepted} accepted, {Pending} waiting, {Rejected} rejected, {EzForensics.Core.Common.Plural.Of(Chunks, "chunk")}";
    public string Added => DateTime.TryParse(Info.ImportedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Info.ImportedAt;
    public string Status { get; }
    public bool HasProblem { get; }
    public IAsyncRelayCommand<LibrarySourceRow> ReindexCommand { get; }
    public IAsyncRelayCommand<LibrarySourceRow> RemoveCommand { get; }
    public IAsyncRelayCommand<LibrarySourceRow> ExportCommand { get; }
    public IAsyncRelayCommand<LibrarySourceRow> LabelAiCommand { get; }
    public IAsyncRelayCommand<LibrarySourceRow> DistillCommand { get; }
}

/// <summary>
/// The Library tab: the sources of the knowledge base with counts by status, Re-index, Remove, Export as a .ezkb pack, Empty rejected,
/// and the one-time concept backfill for notes that came from before the Brain existed.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly KnowledgeWorkspace _ws;
    private readonly Func<IEnumerable<string>, Task> _addPaths;
    private readonly Action<string, InfoSeverity>? _notify;
    private CancellationTokenSource? _cts;
    private bool _backfilling;

    private readonly Func<(KnowledgeCompletion? Complete, string Model)> _ai;
    private readonly KnowledgeAiGate _gate;

    public LibraryViewModel(KnowledgeWorkspace ws, Func<IEnumerable<string>, Task> addPaths, Action<string, InfoSeverity>? notify = null,
        Func<(KnowledgeCompletion? Complete, string Model)>? ai = null, Func<Task<bool>>? probe = null)
    {
        _ws = ws;
        _ws.BusyChanged += NotifyBusyCommands;
        _addPaths = addPaths;
        _notify = notify;
        _gate = new KnowledgeAiGate(ai, probe);
        _ai = _gate.Resolve;
        HasAi = _ai().Complete != null;
    }

    /// <summary>Asked before a source's documents are distilled (true = go ahead).</summary>
    public Func<string, bool> ConfirmDistill { get; set; } =
        m => Dialogs.Confirm("Distil into notes", m, "Distil", "Cancel", defaultNo: true);

    /// <summary>Asked before documents are labelled ("Label N unlabelled documents (about K AI calls)?").</summary>
    public Func<string, bool> ConfirmLabel { get; set; } =
        m => Dialogs.Confirm("Label with AI", m, "Label", "Cancel", defaultNo: true);

    /// <summary>Asked when a document to distil already has pending notes: "Replace its N pending notes?".</summary>
    public Func<string, bool> ConfirmReplaceChildren { get; set; } =
        m => Dialogs.Confirm("Distil again", m, "Replace", "Cancel", defaultNo: true);

    public Func<string, bool> ConfirmRemove { get; set; } =
        m => Dialogs.Confirm("Remove source", m, "Remove", "Cancel", defaultNo: true);

    public Func<string, bool> ConfirmEmptyRejected { get; set; } =
        m => Dialogs.Confirm("Empty rejected", m, "Delete for good", "Cancel", defaultNo: true);

    public ObservableCollection<LibrarySourceRow> Sources { get; } = new();
    public bool HasSources => Sources.Count > 0;
    public bool NoSources => Sources.Count == 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRejected))] private int _rejectedCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint), nameof(LabelTip), nameof(DistillTip))] private bool _hasAi;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint), nameof(LabelTip), nameof(DistillTip))] private bool _aiUnreachable;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isBackfilling;
    [ObservableProperty] private string _backfillText = "";

    public bool HasRejected => RejectedCount > 0;
    public string AiHint => HasAi ? "" : AiUnreachable ? KnowledgeAiGate.UnreachableHint : KnowledgeAiRun.NoAiHint;
    public string LabelTip => HasAi ? "Label the selected documents, or the unlabelled ones of this source when none are selected" : AiHint;
    public string DistillTip => HasAi ? "Split the selected documents into single-idea notes that wait in To review (expand the source and select documents)" : AiHint;

    // ---- loading

    /// <summary>Called when the Learn page is shown: reloads the list and starts the concept backfill once when it is due.</summary>
    public async Task OnShownAsync()
    {
        await RefreshAiAsync();
        await RefreshAsync();
        await BackfillIfDueAsync();
    }

    /// <summary>Settings plus a reachability probe of the local AI server; false when the AI cannot be used right now.</summary>
    public async Task<bool> RefreshAiAsync()
    {
        var (ok, down) = await _gate.CheckAsync();
        AiUnreachable = down;
        HasAi = ok;
        return ok;
    }

    public async Task RefreshAsync()
    {
        List<LibrarySourceRow> rows; int rejected;
        try
        {
            // Opening the store creates the file, so check first: an empty Library must not create an empty knowledge base.
            (rows, rejected) = !_ws.Exists ? (new List<LibrarySourceRow>(), 0) : await _ws.RunAsync(s =>
            {
                var list = s.Store.Sources().Select(i => new LibrarySourceRow(i,
                    s.Store.NotesByStatus(KnowledgeIntake.Accepted, i.Id).Count, s.Store.NotesByStatus(KnowledgeIntake.Pending, i.Id).Count,
                    s.Store.NotesByStatus(KnowledgeIntake.Rejected, i.Id).Count, s.Store.ChunkCount(i.Id), s.Store.GetMeta("src_status:" + i.Id) ?? "",
                    ReindexCommand, RemoveCommand, ExportCommand, LabelAiCommand, DistillCommand) { SelectionChanged = NotifyAiCommands }).ToList();
                return (list, list.Sum(r => r.Rejected));
            });
        }
        catch (Exception ex) { Message = "Could not read the knowledge base: " + ex.Message; AppLog.Error("Library list failed", ex); return; }
        Sources.Clear();
        foreach (var r in rows) { r.IsExpanded = _expanded.Contains(r.Info.Id); Sources.Add(r); }
        RejectedCount = rejected;
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(NoSources));
    }

    private async Task BackfillIfDueAsync()
    {
        if (_backfilling || !_ws.Exists) return;
        try
        {
            if (!await _ws.RunAsync(s => s.Intake.NeedsConceptBackfill)) return;
            _backfilling = true;
            IsBackfilling = true;
            BackfillText = "Reading your documents for the graph…";
            var progress = new Progress<(int done, int total, string item)>(p => BackfillText = $"Reading your documents for the graph: {p.done}/{p.total} {p.item}");
            await _ws.UseAsync(async s => { await s.Intake.BackfillConceptsAsync(progress, CancellationToken.None); return 0; }, changes: true, embed: false);
        }
        catch (Exception ex) { Message = "The graph could not read some documents: " + ex.Message; AppLog.Error("Concept backfill failed", ex); }
        finally { _backfilling = false; IsBackfilling = false; BackfillText = ""; }
    }

    // ---- add (through the Inbox)

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFilesAsync()
    {
        var files = Dialogs.PickFiles("Add documents", InboxViewModel.FileFilter);
        return files is { Count: > 0 } ? _addPaths(files) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFolderAsync()
    {
        var dir = Dialogs.PickFolder("Add a folder (an Obsidian vault or a folder of documents)");
        return dir != null ? _addPaths(new[] { dir }) : Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => !Busy;

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
        EmptyRejectedCommand.NotifyCanExecuteChanged();
        ReindexCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        NotifyAiCommands();
    }

    partial void OnHasAiChanged(bool value)
    {
        NotifyAiCommands();
        OnPropertyChanged(nameof(LabelTip));
        OnPropertyChanged(nameof(DistillTip));
    }

    private void NotifyAiCommands()
    {
        LabelAiCommand.NotifyCanExecuteChanged();
        DistillCommand.NotifyCanExecuteChanged();
        ReindexCommand.NotifyCanExecuteChanged();
    }

    // ---- per source

    public IAsyncRelayCommand<LibrarySourceRow> ReindexCommand => _reindex ??= new AsyncRelayCommand<LibrarySourceRow>(r => r == null ? Task.CompletedTask : ReindexAsync(r), CanReindex);
    public IAsyncRelayCommand<LibrarySourceRow> RemoveCommand => _remove ??= new AsyncRelayCommand<LibrarySourceRow>(r => r == null ? Task.CompletedTask : RemoveAsync(r), _ => !Busy);
    public IAsyncRelayCommand<LibrarySourceRow> ExportCommand => _export ??= new AsyncRelayCommand<LibrarySourceRow>(r => r == null ? Task.CompletedTask : ExportDialogAsync(r), _ => !Busy);
    public IAsyncRelayCommand<LibrarySourceRow> LabelAiCommand => _labelAi ??= new AsyncRelayCommand<LibrarySourceRow>(r => r == null ? Task.CompletedTask : LabelSourceAsync(r), _ => HasAi && !Busy);
    public IAsyncRelayCommand<LibrarySourceRow> DistillCommand => _distill ??= new AsyncRelayCommand<LibrarySourceRow>(r => r == null ? Task.CompletedTask : DistillDocsAsync(r), r => HasAi && !Busy && r is { HasDocSelection: true });
    /// <summary>Re-index of a folder or pack adds changed documents as pending notes, which must be labelled: it needs the AI. Loose documents update in place.</summary>
    private bool CanReindex(LibrarySourceRow? row) => !Busy && (row == null || row.Info.Kind == KnowledgeImporter.FilesKind || HasAi);

    private IAsyncRelayCommand<LibrarySourceRow>? _reindex, _remove, _export, _labelAi, _distill;

    // ---- AI: label and distil the accepted documents of a source (the same code as the Inbox)

    private readonly HashSet<long> _expanded = new();

    /// <summary>A source was expanded or collapsed: remembers it (a refresh keeps it open) and loads its accepted documents.</summary>
    public async Task SetExpandedAsync(LibrarySourceRow row, bool expanded)
    {
        if (!expanded) { _expanded.Remove(row.Info.Id); return; }
        _expanded.Add(row.Info.Id);
        await LoadDocumentsAsync(row);
    }

    /// <summary>The accepted documents of the source (distilled notes are counted under their document, not listed).</summary>
    public async Task LoadDocumentsAsync(LibrarySourceRow row)
    {
        row.LoadingDocs = true;
        try
        {
            var docs = await _ws.RunAsync(s =>
            {
                var all = s.Store.NotesByStatus(KnowledgeIntake.Accepted, row.Info.Id).Concat(s.Store.NotesByStatus(KnowledgeIntake.Pending, row.Info.Id)).ToList();
                var kids = all.Where(n => n.ParentId != null).GroupBy(n => n.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
                return all.Where(n => n.Status == KnowledgeIntake.Accepted && n.ParentId == null)
                    .Select(n => new LibraryDocRow(n.Id, n.Title, n.Category ?? "", kids.GetValueOrDefault(n.Id))).ToList();
            });
            row.Documents.Clear();
            foreach (var d in docs) row.Documents.Add(d);
        }
        catch (Exception ex) { Message = "Could not read the documents: " + ex.Message; AppLog.Error("Library documents failed", ex); }
        finally { row.LoadingDocs = false; }
    }

    /// <summary>Labels the selected documents, or when none are selected the unlabelled ones of the source. Asks first: one AI call per 6.</summary>
    private Task LabelSourceAsync(LibrarySourceRow row) => RunAiAsync("Labelling " + row.Name, async (c, m, p, ct) =>
    {
        var ids = row.HasDocSelection ? row.SelectedDocs.Select(d => d.Id).ToList()
            : await _ws.RunAsync(s => s.Store.NotesByStatus(KnowledgeIntake.Accepted, row.Info.Id).Where(n => n.AiState != KnowledgeLabels.Labelled).Select(n => n.Id).ToList());
        if (ids.Count == 0) return ("Every document of " + row.Name + " is labelled already.", false);
        if (!ConfirmLabel(KnowledgeAiRun.LabelConfirmText(ids.Count, row.HasDocSelection ? "selected" : "unlabelled"))) return ("Nothing labelled.", false);
        return await KnowledgeAiRun.LabelAsync(_ws, c, m, ids, p, ct);
    });

    /// <summary>Distils the selected documents only: one start confirm (with the number of AI calls), one replace question.</summary>
    private Task DistillDocsAsync(LibrarySourceRow row) => RunAiAsync("Distilling " + row.Name, (c, m, p, ct) =>
        KnowledgeAiRun.DistillAsync(_ws, c, m, row.SelectedDocs.Select(d => d.Id).ToList(), ConfirmDistill, ConfirmReplaceChildren, true, p, ct));

    private async Task RunAiAsync(string title, Func<KnowledgeCompletion, string, IProgress<(int, int)>, CancellationToken, Task<(string Message, bool Problem)>> work)
    {
        if (Busy) return;
        IsBusy = true;
        Message = "";
        try
        {
            if (!await RefreshAiAsync()) { Message = AiHint; return; }
            var (msg, severity) = await KnowledgeAiRun.RunAsync(_ws, title, _ai, t => ProgressText = t, c => _cts = c, work);
            Message = msg;
            if (severity is { } sev) _notify?.Invoke(msg, sev);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
        await RefreshAsync();
    }

    private async Task ReindexAsync(LibrarySourceRow row)
    {
        if (row.Info.Kind != KnowledgeImporter.FilesKind && !await RefreshAiAsync()) { Message = AiHint; return; }
        await ReindexCoreAsync(row);
    }

    private Task ReindexCoreAsync(LibrarySourceRow row) => RunAsync("Re-indexing " + row.Name, async (s, progress, ct) =>
    {
        switch (row.Info.Kind)
        {
            case "files":
                var r = await s.Importer.ReindexFilesAsync(row.Name, progress, ct);
                return r.Errors.Count == 0 ? $"Re-indexed {row.Name}." : "Re-indexed with problems: " + string.Join("; ", r.Errors.Select(e => $"{Path.GetFileName(e.File)}: {e.Error}"));
            case "vault":
                if (!Directory.Exists(row.Info.Path)) return $"The folder is gone: {row.Info.Path}";
                return await AddAndLabelAsync(s, row, progress, ct);
            default:
                if (!File.Exists(row.Info.Path)) return $"The pack file is gone: {row.Info.Path}";
                return await AddAndLabelAsync(s, row, progress, ct);
        }
    }, embed: true);

    /// <summary>Re-adds the folder or pack (changed documents land pending) and labels the new pending notes, like Send to review does.</summary>
    private async Task<string> AddAndLabelAsync(KnowledgeSession s, LibrarySourceRow row, IProgress<(int, int, string)> progress, CancellationToken ct)
    {
        var r = await s.Intake.AddAsync(new[] { row.Info.Path }, row.Name, progress, ct);
        var msg = AfterAdd(row.Name, r);
        var (complete, model) = _ai();
        if (r.NewIds.Count == 0 || complete == null) return msg;
        var lr = await new KnowledgeLabeler(complete, model).LabelAsync(s.Store, r.NewIds, null, ct);
        return msg + (lr.Failed == 0 ? $" Labelled {lr.Labelled}." : $" Labelled {lr.Labelled}, {lr.Failed} failed (use Retry AI in To review).");
    }

    private static string AfterAdd(string name, IntakeResult r) =>
        r.Errors.Count > 0 ? $"Re-indexed {name} with problems: " + string.Join("; ", r.Errors.Select(e => $"{Path.GetFileName(e.File)}: {e.Error}"))
        : r.Added > 0 ? $"Re-indexed {name}: {r.Added} changed document(s) wait in To review." : $"Re-indexed {name}: nothing changed.";

    private Task RemoveAsync(LibrarySourceRow row)
    {
        if (!ConfirmRemove($"Remove \"{row.Name}\" from the knowledge base? Its notes, chunks and links are deleted (the original files are not touched)."))
            return Task.CompletedTask;
        return RunAsync("Removing " + row.Name, async (s, _, _) => { await s.Importer.RemoveSourceAsync(row.Name); return $"Removed {row.Name}."; }, embed: false);
    }

    private Task ExportDialogAsync(LibrarySourceRow row)
    {
        var path = Dialogs.SaveFile("Export knowledge pack", "Knowledge pack (*.ezkb)|*.ezkb", row.Name + ".ezkb");
        return path == null ? Task.CompletedTask : ExportAsync(row, path);
    }

    /// <summary>Writes the accepted notes of the source as a .ezkb pack.</summary>
    public Task ExportAsync(LibrarySourceRow row, string path) => RunAsync("Exporting " + row.Name, (s, _, _) => Task.Run(() =>
    {
        var m = EzkbPack.Write(s.Store, row.Name, path);
        return $"Exported {m.Notes} note(s) to {path}.";
    }), embed: false, changes: false);

    // ---- empty rejected

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task EmptyRejectedAsync()
    {
        if (RejectedCount == 0) return;
        if (!ConfirmEmptyRejected($"Delete the {RejectedCount} rejected document(s) for good? This cannot be undone. The original files are not touched."))
            return;
        await RunAsync("Emptying rejected", (s, _, _) => Task.Run(() => $"Deleted {s.Intake.EmptyRejected()} rejected document(s)."), embed: false);
    }

    private async Task RunAsync(string title, Func<KnowledgeSession, IProgress<(int, int, string)>, CancellationToken, Task<string>> work, bool embed, bool changes = true)
    {
        if (Busy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        ProgressText = title + "…";
        Message = "";
        var progress = new Progress<(int done, int total, string item)>(p => ProgressText = $"{title}: {p.done}/{p.total} {p.item}");
        try
        {
            Message = await _ws.UseAsync(s => work(s, progress, _cts.Token), changes, embed);
            _notify?.Invoke(Message, Message.Contains("problem", StringComparison.OrdinalIgnoreCase) ? InfoSeverity.Warning : InfoSeverity.Success);
        }
        catch (OperationCanceledException) { Message = "Cancelled."; _ws.NotifyChanged(); }
        catch (Exception ex)
        {
            Message = title + " failed: " + ex.Message;
            AppLog.Error(title + " failed", ex);
            _notify?.Invoke(Message, InfoSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            _cts?.Dispose();
            _cts = null;
        }
        await RefreshAsync();
    }
}
