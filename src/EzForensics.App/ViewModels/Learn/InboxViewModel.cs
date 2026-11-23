using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>A removable filter badge: a facet (Category, Type, Source, Tag, Flag) and one value.</summary>
public sealed record FilterBadge(string Facet, string Value)
{
    public string Text => $"{Facet}: {Value}";
}

/// <summary>One tag chip of an expanded row; Remove drops it from the row (and saves).</summary>
public sealed class InboxTag(InboxRow row, string name)
{
    public string Name { get; } = name;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => row.RemoveTag(name));
}

public sealed record PageSizeOption(string Label, int Size);

/// <summary>One document waiting in the Inbox (or rejected). Title, tags, category and type edit in place and are saved as they change.</summary>
public sealed partial class InboxRow : ObservableObject
{
    private bool _loading = true;

    public InboxRow(KnowledgeStore.NoteMeta meta, string sourceName, string title, string tags, IReadOnlyList<string> flagTexts,
        long? otherId, string otherTitle, string otherStatus, NoteLabels? labels = null, string parentTitle = "", IReadOnlyList<string>? flagKinds = null)
    {
        Id = meta.Id; SourceId = meta.SourceId; SourceName = sourceName;
        _title = title; _tags = tags;
        FlagTexts = flagTexts;
        FlagKinds = flagKinds ?? flagTexts;
        OtherId = otherId; OtherTitle = otherTitle; OtherStatus = otherStatus;
        Replaces = meta.Replaces != null;
        AddedText = FormatAdded(meta.AddedAt, meta.AddedBy);
        _category = labels?.Category ?? meta.Category ?? "";
        _noteType = labels?.Type ?? meta.NoteType ?? "";
        _summary = labels?.Summary ?? "";
        _aiState = labels?.AiState ?? meta.AiState;
        AiModel = labels?.AiModel ?? "";
        AiAt = FormatWhen(labels?.AiAt);
        ParentTitle = parentTitle;
        _loading = false;
    }

    public long Id { get; }
    public long SourceId { get; }
    public string SourceName { get; }
    public IReadOnlyList<string> FlagTexts { get; }
    /// <summary>Short flag names for the chips and the Flag filter ("Same text", "Little text", …).</summary>
    public IReadOnlyList<string> FlagKinds { get; }
    public bool HasFlags => FlagTexts.Count > 0;
    /// <summary>The note this one duplicates (same text or a near match), when there is one.</summary>
    public long? OtherId { get; }
    public string OtherTitle { get; }
    public string OtherStatus { get; }
    public bool IsDuplicate => OtherId != null;
    /// <summary>A newer version of an accepted document: accepting it replaces the old one.</summary>
    public bool Replaces { get; }
    public string AddedText { get; }
    public string AiModel { get; }
    public string AiAt { get; }
    /// <summary>The document this note was distilled from (shown as a "from X" chip); empty for ordinary notes.</summary>
    public string ParentTitle { get; }
    public bool HasParent => ParentTitle.Length > 0;
    public string FromText => "from " + ParentTitle;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _otherPreview = "";
    [ObservableProperty] private string _category;
    [ObservableProperty] private string _noteType;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAiFailed), nameof(NotLabelled), nameof(AiStateText), nameof(SummaryLine))] private string _aiState;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSummary), nameof(SummaryLine))] private string _summary;

    public bool HasAiFailed => AiState == KnowledgeLabels.Failed;
    /// <summary>No labels yet (a failed run has its own chip).</summary>
    public bool NotLabelled => AiState != KnowledgeLabels.Labelled && AiState != KnowledgeLabels.Failed;
    /// <summary>"Labelled", "Not labelled" or "Failed": the value of the AI filter.</summary>
    public string AiStateText => AiState == KnowledgeLabels.Labelled ? "Labelled" : AiState == KnowledgeLabels.Failed ? "Failed" : "Not labelled";
    /// <summary>"Distilled notes" (made from a document) or "Whole documents": the value of the Distilled filter.</summary>
    public string DistilledText => HasParent ? InboxViewModel.DistilledNotes : InboxViewModel.WholeDocuments;
    public bool HasSummary => SummaryLine.Length > 0;
    public string CategoryText => Category.Length > 0 ? Category : "Category";
    public string TypeText => NoteType.Length > 0 ? NoteType : "Type";
    /// <summary>The summary with the model and date, or what happened when the AI could not label the document.</summary>
    public string SummaryLine => HasAiFailed ? "The AI could not label this document." :
        Summary.Length == 0 ? "" : AiModel.Length > 0 ? $"{Summary}  ({AiModel}{(AiAt.Length > 0 ? ", " + AiAt : "")})" : Summary;

    public IReadOnlyList<string> TagNames => Tags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>The first three tags (collapsed row); the rest count in <see cref="MoreTags"/>.</summary>
    public IReadOnlyList<string> TagList => TagNames.Take(3).ToList();
    public string MoreTags => TagNames.Count > 3 ? $"+{TagNames.Count - 3}" : "";
    public bool HasMoreTags => TagNames.Count > 3;
    public IReadOnlyList<InboxTag> TagChips => TagNames.Select(t => new InboxTag(this, t)).ToList();

    /// <summary>Called after the title or tags changed (set by the Inbox once the row is on screen).</summary>
    public Action<InboxRow>? Saver { get; set; }
    /// <summary>Called after a category or type was picked from a chip menu (category, type; null = unchanged).</summary>
    public Action<InboxRow, string?, string?>? LabelSaver { get; set; }

    partial void OnTitleChanged(string value) { if (!_loading) Saver?.Invoke(this); }
    partial void OnTagsChanged(string value)
    {
        OnPropertyChanged(nameof(TagList)); OnPropertyChanged(nameof(MoreTags)); OnPropertyChanged(nameof(HasMoreTags));
        OnPropertyChanged(nameof(TagChips)); OnPropertyChanged(nameof(TagNames));
        if (!_loading) Saver?.Invoke(this);
    }
    partial void OnCategoryChanged(string value) => OnPropertyChanged(nameof(CategoryText));
    partial void OnNoteTypeChanged(string value) => OnPropertyChanged(nameof(TypeText));

    [RelayCommand]
    private void SetCategory(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Category = KnowledgeLabels.Category(value);
        LabelSaver?.Invoke(this, Category, null);
    }

    [RelayCommand]
    private void SetType(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        NoteType = KnowledgeLabels.Type(value);
        LabelSaver?.Invoke(this, null, NoteType);
    }

    public void AddTag(string tag) => Tags = string.Join(", ", KnowledgeLabels.Tags(TagNames.Append(tag)));

    public void RemoveTag(string tag) =>
        Tags = string.Join(", ", TagNames.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));

    private static string FormatWhen(string? at) =>
        DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    private static string FormatAdded(string? at, string? by)
    {
        var when = FormatWhen(at);
        return (when, by) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"Added {when} by {by}",
            ({ Length: > 0 }, _) => $"Added {when}",
            _ => "",
        };
    }
}

/// <summary>
/// The Inbox tab: documents added to the knowledge base wait here until the examiner accepts or rejects them (an AIOS-style review list:
/// find bar with filters, one-line rows that expand, an action bar for the selection, a pager). Every add goes through
/// <see cref="KnowledgeIntake.AddAsync"/>; nothing becomes searchable before Accept.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject
{
    public const string FileFilter = "Documents (PDF, Markdown, TXT, HTML, EZKB)|*.pdf;*.md;*.markdown;*.txt;*.html;*.htm;*.ezkb|All files|*.*";
    /// <summary>Source of loose files. Folders get a source named after the folder, packs their manifest name.</summary>
    public const string DefaultSourceName = KnowledgeIntake.DocumentsSource;
    public const string NoAiHint = KnowledgeAiRun.NoAiHint;
    public static readonly IReadOnlyList<string> Facets = new[] { "Category", "Type", "Source", "Tag", "Flag", "AI", "Distilled" };
    public const string DistilledNotes = "Distilled notes", WholeDocuments = "Whole documents";
    private const int PreviewChars = 2500;
    private const int SearchBodyChars = 2000;

    private readonly KnowledgeWorkspace _ws;
    private readonly Action<string, InfoSeverity>? _notify;
    private readonly Func<(KnowledgeCompletion? Complete, string Model)> _ai;
    private readonly KnowledgeAiGate _gate;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<InboxRow> _selected = Array.Empty<InboxRow>();
    private List<InboxRow> _filtered = new();
    private readonly Dictionary<long, string> _bodies = new();
    private bool _applying, _rebuilding;

    public InboxViewModel(KnowledgeWorkspace ws, Action<string, InfoSeverity>? notify = null, Func<(KnowledgeCompletion? Complete, string Model)>? ai = null, Func<Task<bool>>? probe = null)
    {
        _ws = ws;
        _ws.BusyChanged += NotifyBusyCommands;
        _notify = notify;
        _gate = new KnowledgeAiGate(ai, probe);
        _ai = _gate.Resolve;
        HasAi = _ai().Complete != null;
        Filters.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasFilters)); ResetView(); };
    }

    /// <summary>Asked before "Keep this" rejects a document that is already accepted and searchable.</summary>
    public Func<string, bool> ConfirmRejectAccepted { get; set; } =
        m => Dialogs.Confirm("Reject accepted document", m, "Reject it", "Cancel", defaultNo: true);

    /// <summary>Asked before several documents are distilled: "Distill M documents (about S AI calls)?" (true = go ahead).</summary>
    public Func<string, bool> ConfirmDistill { get; set; } =
        m => Dialogs.Confirm("Distil into notes", m, "Distil", "Cancel", defaultNo: true);

    /// <summary>Asked once when some of the documents to distil already have pending notes: "Replace X pending notes from Y documents?" (true = replace them).</summary>
    public Func<string, bool> ConfirmReplaceChildren { get; set; } =
        m => Dialogs.Confirm("Distil again", m, "Replace", "Cancel", defaultNo: true);

    /// <summary>Test seam: runs before each row is accepted or rejected; throwing marks that row as failed.</summary>
    internal Action<long>? ReviewProbe { get; set; }

    /// <summary>Test seam: sees the ids of the one batched call that accepts or rejects the rows that passed their checks.</summary>
    internal Action<IReadOnlyList<long>>? BulkProbe { get; set; }

    /// <summary>Every waiting document; the list shows <see cref="PageRows"/>, the filtered page of it.</summary>
    public ObservableCollection<InboxRow> Rows { get; } = new();
    public ObservableCollection<InboxRow> PageRows { get; } = new();
    public ObservableCollection<InboxRow> Rejected { get; } = new();
    public ObservableCollection<FilterBadge> Filters { get; } = new();
    /// <summary>Every tag of the waiting documents (the tag editor's suggestions).</summary>
    public ObservableCollection<string> KnownTags { get; } = new();
    public IReadOnlyList<string> Categories => KnowledgeLabels.Categories;
    public IReadOnlyList<string> Types => KnowledgeLabels.Types;
    public IReadOnlyList<PageSizeOption> PageSizes { get; } = new[] { new PageSizeOption("25", 25), new PageSizeOption("50", 50), new PageSizeOption("All", 0) };

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _bulkTags = "";
    [ObservableProperty] private bool _showRejected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _page = 1;
    /// <summary>Rows per page: 25 or 50, or 0 for all.</summary>
    [ObservableProperty] private int _pageSize = 25;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint))] private bool _hasAi;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AiHint))] private bool _aiUnreachable;
    [ObservableProperty] private bool _isConfirmingReject;

    public string AiHint => HasAi ? "" : AiUnreachable ? KnowledgeAiGate.UnreachableHint : NoAiHint;
    public bool HasFilters => Filters.Count > 0;
    public bool IsFiltered => Search.Trim().Length > 0 || Filters.Count > 0;
    public int PendingCount => Rows.Count;
    public bool IsEmpty => Rows.Count == 0;
    public bool NoMatches => Rows.Count > 0 && _filtered.Count == 0;
    public bool HasRejected => Rejected.Count > 0;
    public string RejectedHeader => $"Rejected ({Rejected.Count})";
    public bool HasSelection => _selected.Count > 0;
    public int SelectedCount => _selected.Count;
    public IReadOnlyList<InboxRow> SelectedRows => _selected;
    public string SelectedText => $"{SelectedCount} selected";
    public string AcceptText => $"✓ Accept {SelectedCount}";
    public string RejectText => $"× Reject {SelectedCount}";
    public string RejectAsk => $"Reject {SelectedCount}?";

    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
    public string RangeText
    {
        get
        {
            if (_filtered.Count == 0) return Rows.Count == 0 ? "" : $"0 of 0 (filtered from {Rows.Count})";
            var from = PageSize <= 0 ? 1 : (Page - 1) * PageSize + 1;
            var to = PageSize <= 0 ? _filtered.Count : Math.Min(_filtered.Count, Page * PageSize);
            var text = $"{from}–{to} of {_filtered.Count}";
            return _filtered.Count != Rows.Count ? text + $" (filtered from {Rows.Count})" : text;
        }
    }

    /// <summary>Rows to select in the list (the view applies them); raised when the view model changes the selection itself.</summary>
    public event Action<IReadOnlyList<InboxRow>>? SelectionPushed;

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

    partial void OnHasAiChanged(bool value)
    {
        LabelWithAiCommand.NotifyCanExecuteChanged();
        DistillCommand.NotifyCanExecuteChanged();
        RetryAiCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The rows the list has selected (the view calls this on every selection change).</summary>
    public void SetSelected(IEnumerable<InboxRow> rows)
    {
        if (_rebuilding) return;
        _selected = rows.ToList();
        if (_selected.Count == 0) IsConfirmingReject = false;
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(AcceptText));
        OnPropertyChanged(nameof(RejectText));
        OnPropertyChanged(nameof(RejectAsk));
        NotifySelectionCommands();
    }

    private void NotifySelectionCommands()
    {
        AcceptCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        LabelWithAiCommand.NotifyCanExecuteChanged();
        DistillCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        SetSelected(Array.Empty<InboxRow>());
        SelectionPushed?.Invoke(Array.Empty<InboxRow>());
    }

    // ---- find bar: search, filters, paging

    partial void OnSearchChanged(string value) { OnPropertyChanged(nameof(IsFiltered)); ResetView(); }
    partial void OnPageSizeChanged(int value) => ResetView();
    partial void OnPageChanged(int value) { if (!_applying) { ClearSelection(); Apply(); } }

    /// <summary>Values the facet can filter by, taken from the waiting documents only.</summary>
    public IReadOnlyList<string> FacetValues(string facet)
    {
        IEnumerable<string> all = facet switch
        {
            "Category" => Rows.Select(r => r.Category),
            "Type" => Rows.Select(r => r.NoteType),
            "Source" => Rows.Select(r => r.SourceName),
            "Tag" => Rows.SelectMany(r => r.TagNames),
            "Flag" => Rows.SelectMany(r => r.FlagKinds),
            "AI" => Rows.Select(r => r.AiStateText),
            "Distilled" => Rows.Select(r => r.DistilledText),
            _ => Array.Empty<string>(),
        };
        return all.Where(v => v.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void AddFilter(string facet, string value)
    {
        if (Filters.Any(f => f.Facet == facet && string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase))) return;
        Filters.Add(new FilterBadge(facet, value));
    }

    [RelayCommand]
    public void RemoveFilter(FilterBadge? badge) { if (badge != null) Filters.Remove(badge); }

    [RelayCommand]
    public void ClearFilters()
    {
        if (Filters.Count > 0) Filters.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanPrev))]
    private void PrevPage() => Page = Math.Max(1, Page - 1);
    private bool CanPrev() => Page > 1;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() => Page = Math.Min(PageCount, Page + 1);
    private bool CanNext() => Page < PageCount;

    /// <summary>A change of Search or Filters: back to page 1 and nothing selected, so a bulk action never reaches hidden rows.</summary>
    private void ResetView()
    {
        _applying = true;
        Page = 1;
        _applying = false;
        ClearSelection();
        Apply();
    }

    private bool Matches(InboxRow r, string[] words)
    {
        foreach (var group in Filters.GroupBy(f => f.Facet))
        {
            var ok = group.Any(f => f.Facet switch
            {
                "Category" => string.Equals(r.Category, f.Value, StringComparison.OrdinalIgnoreCase),
                "Type" => string.Equals(r.NoteType, f.Value, StringComparison.OrdinalIgnoreCase),
                "Source" => string.Equals(r.SourceName, f.Value, StringComparison.OrdinalIgnoreCase),
                "Tag" => r.TagNames.Any(t => string.Equals(t, f.Value, StringComparison.OrdinalIgnoreCase)),
                "Flag" => r.FlagKinds.Any(t => string.Equals(t, f.Value, StringComparison.OrdinalIgnoreCase)),
                "AI" => string.Equals(r.AiStateText, f.Value, StringComparison.OrdinalIgnoreCase),
                "Distilled" => string.Equals(r.DistilledText, f.Value, StringComparison.OrdinalIgnoreCase),
                _ => true,
            });
            if (!ok) return false;
        }
        foreach (var w in words)
        {
            var hit = Has(r.Title, w) || Has(r.Summary, w) || (_bodies.TryGetValue(r.Id, out var b) && Has(b, w));
            if (!hit) return false;
        }
        return true;
    }

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The latest background read of body text for the search (tests await it).</summary>
    public Task PendingBodies { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the body text (first 2,000 characters) of the rows the search needs, once per id, off the UI thread; the list is filtered again when it arrives.</summary>
    private void EnsureBodies(string[] words)
    {
        if (words.Length == 0 || !_ws.Exists) return;
        var need = Rows.Where(r => !_bodies.ContainsKey(r.Id)).Select(r => r.Id).ToList();
        if (need.Count == 0) return;
        var asked = Search;
        PendingBodies = LoadBodiesAsync(need, asked);
    }

    private async Task LoadBodiesAsync(List<long> need, string asked)
    {
        try
        {
            var got = await _ws.RunAsync(s => need.ToDictionary(id => id, id =>
            {
                var b = s.Store.NoteBody(id) ?? "";
                return b.Length > SearchBodyChars ? b[..SearchBodyChars] : b;
            }));
            foreach (var (id, b) in got) _bodies[id] = b;
            if (Search == asked) ApplyKeepingSelection();
        }
        catch (Exception ex) { AppLog.Warn("Inbox search could not read the text: " + ex.Message); }
    }

    /// <summary>Filters again (a label changed, or search text arrived) without losing the selection of rows that are still shown.</summary>
    private void ApplyKeepingSelection()
    {
        var keep = _selected.Select(r => r.Id).ToHashSet();
        Apply();
        var again = PageRows.Where(r => keep.Contains(r.Id)).ToList();
        SetSelected(again);
        SelectionPushed?.Invoke(again);
    }

    private void Apply()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        EnsureBodies(words);
        _filtered = Rows.Where(r => Matches(r, words)).ToList();
        _applying = true;
        var pages = PageCount;
        if (Page > pages) Page = pages;
        if (Page < 1) Page = 1;
        _applying = false;
        var visible = PageSize <= 0 ? _filtered : _filtered.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
        _rebuilding = true;
        try
        {
            PageRows.Clear();
            foreach (var r in visible) PageRows.Add(r);
        }
        finally { _rebuilding = false; }
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(IsFiltered));
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    // ---- loading

    public async Task RefreshAsync()
    {
        await RefreshAiAsync();
        if (!_ws.Exists) { Fill(new(), new()); return; }
        try
        {
            var (pending, rejected) = await _ws.RunAsync(sess => (Load(sess.Store, KnowledgeIntake.Pending), Load(sess.Store, KnowledgeIntake.Rejected)));
            Fill(pending, rejected);
        }
        catch (Exception ex) { Message = "Could not read the knowledge base: " + ex.Message; AppLog.Error("Inbox refresh failed", ex); }
    }

    private void Fill(List<InboxRow> pending, List<InboxRow> rejected)
    {
        var keepSelected = _selected.Select(r => r.Id).ToHashSet();
        _rebuilding = true;
        try
        {
            Rows.Clear();
            foreach (var r in pending) { r.Saver = SaveEdit; r.LabelSaver = SaveLabels; Rows.Add(r); }
            Rejected.Clear();
            foreach (var r in rejected) Rejected.Add(r);
        }
        finally { _rebuilding = false; }
        var live = Rows.Select(r => r.Id).ToHashSet();
        foreach (var id in _bodies.Keys.Where(id => !live.Contains(id)).ToList()) _bodies.Remove(id);
        KnownTags.Clear();
        foreach (var t in Rows.SelectMany(r => r.TagNames).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)) KnownTags.Add(t);
        Apply();
        var again = PageRows.Where(r => keepSelected.Contains(r.Id)).ToList();
        SetSelected(again);
        SelectionPushed?.Invoke(again);
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRejected));
        OnPropertyChanged(nameof(RejectedHeader));
    }

    private static List<InboxRow> Load(KnowledgeStore store, string status)
    {
        var names = store.Sources().ToDictionary(s => s.Id, s => s.Name);
        return store.NotesByStatus(status)
            .OrderBy(m => m.Id)
            .Select(m => ToRow(store, m, names.GetValueOrDefault(m.SourceId, ""))).ToList();
    }

    private static InboxRow ToRow(KnowledgeStore store, KnowledgeStore.NoteMeta m, string source)
    {
        var texts = new List<string>();
        var kinds = new List<string>();
        long? otherId = null; var otherTitle = ""; var otherStatus = "";
        foreach (var f in m.Flags)
        {
            var parts = f.Split(':');
            switch (parts[0])
            {
                case "same" or "near" when parts.Length >= 2 && long.TryParse(parts[1], out var id):
                    var other = store.Meta(id);
                    var title = other?.Title ?? "a removed document";
                    if (otherId == null && other != null) { otherId = id; otherTitle = title; otherStatus = other.Status; }
                    texts.Add(parts[0] == "same" ? $"Same as {title}" : $"Near-duplicate of {title} ({(parts.Length > 2 ? parts[2] : "?")}%)");
                    kinds.Add(parts[0] == "same" ? "Same text" : "Near-duplicate");
                    break;
                case "little-text": texts.Add("Little text"); kinds.Add("Little text"); break;
                case "noise": texts.Add("Mostly noise"); kinds.Add("Mostly noise"); break;
                default: texts.Add(f); kinds.Add(f); break;
            }
        }
        if (m.Replaces != null) kinds.Add("Replaces earlier version");
        var labels = store.Labels(m.Id);
        var parent = labels.ParentId is { } p ? store.Meta(p)?.Title ?? "" : "";
        return new InboxRow(m, source, m.Title, string.Join(", ", labels.Tags), texts, otherId, otherTitle, otherStatus, labels, parent, kinds);
    }

    // ---- adding

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFilesAsync()
    {
        var files = PickFiles("Add documents", FileFilter);
        return files is { Count: > 0 } ? ToIntake(files) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddFolderAsync()
    {
        var dir = PickFolder("Add a folder (an Obsidian vault or a folder of documents)");
        return dir != null ? ToIntake(new[] { dir }) : Task.CompletedTask;
    }

    /// <summary>Where the Add buttons send their files: Intake (set by the Learn page). Without it they are added to the Inbox directly.</summary>
    public Func<IEnumerable<string>, Task>? AddToIntake { get; set; }

    private Task ToIntake(IReadOnlyList<string> paths) => AddToIntake != null ? AddToIntake(paths) : AddPathsAsync(paths);

    /// <summary>The file and folder dialogs (tests replace them).</summary>
    public Func<string, string, IReadOnlyList<string>?> PickFiles { get; set; } = (t, f) => Dialogs.PickFiles(t, f);
    public Func<string, string?> PickFolder { get; set; } = t => Dialogs.PickFolder(t);

    /// <summary>The result of the latest add (null before the first one).</summary>
    public IntakeResult? LastResult { get; private set; }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => !Busy;

    /// <summary>Files, folders (an Obsidian vault or documents) and .ezkb packs, from the dialogs or a drop. They land in the Inbox, not in search.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (Busy) return;
        var list = paths.ToList();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        ProgressText = "Adding documents…";
        Message = "";
        var progress = new Progress<(int done, int total, string item)>(p => ProgressText = $"Adding documents: {p.done}/{p.total} {p.item}");
        try
        {
            var r = await _ws.UseAsync(s => s.Intake.AddAsync(list, "", progress, _cts.Token));
            LastResult = r;
            Message = Summarize(r);
            Report(Message, r.Errors.Count > 0 ? InfoSeverity.Warning : InfoSeverity.Success);
        }
        catch (OperationCanceledException) { Message = "Cancelled. Documents already added are waiting in To review."; _ws.NotifyChanged(); }
        catch (Exception ex)
        {
            Message = "Adding documents failed: " + ex.Message;
            AppLog.Error("Adding documents failed", ex);
            Report(Message, InfoSeverity.Error);
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

    private static string Summarize(IntakeResult r)
    {
        var parts = new List<string> { $"Added {r.Added} for review" };
        if (r.AlreadyWaiting > 0) parts.Add($"{r.AlreadyWaiting} already waiting");
        if (r.Unchanged > 0) parts.Add($"{r.Unchanged} already in the library");
        if (r.RejectedBefore > 0) parts.Add($"Rejected before: {r.RejectedBefore} (not added again, see Rejected)");
        if (r.Errors.Count > 0) parts.Add("Problems: " + string.Join("; ", r.Errors.Select(e => $"{Path.GetFileName(e.File)}: {e.Error}")));
        return string.Join(". ", parts) + ".";
    }


    private void Report(string text, InfoSeverity severity) => _notify?.Invoke(text, severity);

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
        AcceptCommand.NotifyCanExecuteChanged();
        AcceptRowCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
        RejectRowCommand.NotifyCanExecuteChanged();
        ConfirmRejectCommand.NotifyCanExecuteChanged();
        LabelWithAiCommand.NotifyCanExecuteChanged();
        DistillCommand.NotifyCanExecuteChanged();
        RetryAiCommand.NotifyCanExecuteChanged();
    }

    // ---- review

    private bool CanReview() => !Busy;
    private bool CanReviewSelection() => !Busy && HasSelection;

    [RelayCommand(CanExecute = nameof(CanReviewSelection))]
    private Task AcceptAsync() => ReviewRowsAsync(_selected.ToList(), (s, ids) => s.Intake.Accept(ids), "Accepted");

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task AcceptRowAsync(InboxRow? row) => row == null ? Task.CompletedTask : ReviewRowsAsync(new[] { row }, (s, ids) => s.Intake.Accept(ids), "Accepted");

    /// <summary>Asks "Reject N? yes / no" inline (the view shows it); <see cref="ConfirmRejectCommand"/> goes ahead.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewSelection))]
    private void Reject() => IsConfirmingReject = true;

    [RelayCommand]
    private void CancelReject() => IsConfirmingReject = false;

    [RelayCommand(CanExecute = nameof(CanReviewSelection))]
    private Task ConfirmRejectAsync()
    {
        IsConfirmingReject = false;
        return ReviewRowsAsync(_selected.ToList(), (s, ids) => s.Intake.Reject(ids), "Rejected");
    }

    /// <summary>The × on a row: rejects that document at once (Undo is in Rejected).</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task RejectRowAsync(InboxRow? row) => row == null ? Task.CompletedTask : ReviewRowsAsync(new[] { row }, (s, ids) => s.Intake.Reject(ids), "Rejected");

    /// <summary>
    /// Accepts or rejects the rows. Each row is checked on its own (one that fails does not stop the others and stays selected), then
    /// the rows that passed go through in ONE call, so the links of a source are rebuilt once, not once per row.
    /// </summary>
    private async Task ReviewRowsAsync(IReadOnlyList<InboxRow> rows, Action<KnowledgeSession, IReadOnlyList<long>> act, string past)
    {
        if (rows.Count == 0 || Busy) return;
        var ids = rows.Select(r => r.Id).ToList();
        var failed = new List<long>();
        var last = "";
        IsBusy = true;
        try
        {
            await _ws.RunAsync(s =>
            {
                var valid = new List<long>();
                foreach (var id in ids)
                {
                    try
                    {
                        ReviewProbe?.Invoke(id);
                        if (s.Store.Meta(id)?.Status != KnowledgeIntake.Pending) throw new InvalidOperationException("it is no longer waiting");
                        valid.Add(id);
                    }
                    catch (Exception ex) { failed.Add(id); last = ex.Message; AppLog.Error("Inbox review failed for note " + id, ex); }
                }
                if (valid.Count == 0) return 0;
                BulkProbe?.Invoke(valid);
                try { act(s, valid); }
                catch (Exception ex)
                {
                    // the batch stopped half way: redo it row by row so the culprit is the only one reported
                    AppLog.Error("Inbox bulk review failed, retrying row by row", ex);
                    foreach (var id in valid)
                    {
                        if (s.Store.Meta(id)?.Status != KnowledgeIntake.Pending) continue;
                        try { act(s, new[] { id }); }
                        catch (Exception ex2) { failed.Add(id); last = ex2.Message; AppLog.Error("Inbox review failed for note " + id, ex2); }
                    }
                }
                return 0;
            }, changes: true);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Message = "That did not work: " + ex.Message;
            AppLog.Error("Inbox action failed", ex);
            Report(Message, InfoSeverity.Error);
            await RefreshAsync();
            return;
        }
        IsBusy = false;
        Message = failed.Count > 0 ? $"{failed.Count} of {ids.Count} failed — {last}" : $"{past} {ids.Count}.";
        if (failed.Count > 0) Report(Message, InfoSeverity.Warning);
        SetSelected(rows.Where(r => failed.Contains(r.Id)));
        await RefreshAsync();
    }

    /// <summary>Keeps this document and rejects the one it duplicates (asks first when that one is already accepted).</summary>
    [RelayCommand]
    private Task KeepThisAsync(InboxRow? row) => row?.OtherId is { } other ? Keep(row.Id, other, row.OtherStatus, row.OtherTitle) : Task.CompletedTask;

    /// <summary>The other side of a duplicate: keeps the older document and rejects this one.</summary>
    [RelayCommand]
    private Task KeepOtherAsync(InboxRow? row) => row?.OtherId is { } other ? Keep(other, row.Id, KnowledgeIntake.Pending, row.Title) : Task.CompletedTask;

    private async Task Keep(long keep, long drop, string dropStatus, string dropTitle)
    {
        if (dropStatus == KnowledgeIntake.Accepted && !ConfirmRejectAccepted($"\"{dropTitle}\" is already accepted and searchable. Reject it and keep the other one?"))
            return;
        await Review(s =>
        {
            s.Intake.KeepOneOf(keep, drop);
            if (s.Store.Meta(keep)?.Status == KnowledgeIntake.Pending) s.Intake.Accept(new[] { keep });
        }, "Kept one of the two.");
    }

    [RelayCommand]
    private async Task UndoAsync(InboxRow? row)
    {
        if (row == null) return;
        var back = false;
        await Review(s => { back = s.Store.Meta(row.Id)?.Status == KnowledgeIntake.Rejected; if (back) s.Intake.Undo(row.Id); }, "");
        Message = back ? "Put back in To review." : "That document is gone, nothing to undo.";
    }

    private async Task Review(Action<KnowledgeSession> action, string done)
    {
        try
        {
            await _ws.RunAsync(s => { action(s); return 0; }, changes: true);
            Message = done;
        }
        catch (Exception ex)
        {
            Message = "That did not work: " + ex.Message;
            AppLog.Error("Inbox action failed", ex);
            Report(Message, InfoSeverity.Error);
        }
        await RefreshAsync();
    }

    // ---- labels and tags

    /// <summary>Sets the category of every selected document (saved at once).</summary>
    [RelayCommand]
    private void SetCategory(string? value)
    {
        if (string.IsNullOrEmpty(value) || _selected.Count == 0) return;
        var cat = KnowledgeLabels.Category(value);
        if (SaveBulk(_selected.Select(r => r.Id).ToList(), cat, null))
        {
            foreach (var r in _selected) r.Category = cat;
            Message = $"Set category on {_selected.Count}.";
            ReapplyIfLabelFiltered();
        }
    }

    /// <summary>Sets the type of every selected document (saved at once).</summary>
    [RelayCommand]
    private void SetType(string? value)
    {
        if (string.IsNullOrEmpty(value) || _selected.Count == 0) return;
        var type = KnowledgeLabels.Type(value);
        if (SaveBulk(_selected.Select(r => r.Id).ToList(), null, type))
        {
            foreach (var r in _selected) r.NoteType = type;
            Message = $"Set type on {_selected.Count}.";
            ReapplyIfLabelFiltered();
        }
    }

    private bool SaveBulk(IReadOnlyList<long> ids, string? category, string? type)
    {
        try { _ws.Run(s => { s.Store.SetCategoryType(ids, category, type); return 0; }, changes: true); return true; }
        catch (Exception ex) { Message = "Could not save the change: " + ex.Message; AppLog.Error("Inbox label save failed", ex); return false; }
    }

    private void SaveLabels(InboxRow row, string? category, string? type)
    {
        if (SaveBulk(new[] { row.Id }, category, type)) ReapplyIfLabelFiltered();
    }

    /// <summary>A row that no longer matches an active Category or Type filter leaves the list.</summary>
    private void ReapplyIfLabelFiltered()
    {
        if (Filters.Any(f => f.Facet is "Category" or "Type")) ApplyKeepingSelection();
    }

    [RelayCommand]
    private void SetTags()
    {
        if (_selected.Count == 0) return;
        var tags = ParseTags(BulkTags);
        try
        {
            _ws.Run(s => { foreach (var r in _selected) s.Store.SetTitleTags(r.Id, r.Title, tags); return 0; }, changes: true);
            foreach (var r in _selected) SetSilently(r, string.Join(", ", tags));
            Message = $"Set tags on {_selected.Count}.";
        }
        catch (Exception ex) { Message = "Could not set tags: " + ex.Message; AppLog.Error("Set tags failed", ex); }
    }

    private static void SetSilently(InboxRow r, string tags)
    {
        var saver = r.Saver;
        r.Saver = null;
        r.Tags = tags;
        r.Saver = saver;
    }

    private static List<string> ParseTags(string text) =>
        KnowledgeLabels.Tags(text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    // ---- AI: label and distil

    private bool CanAiSelection() => HasAi && HasSelection && !Busy;
    private bool CanAi() => HasAi && !Busy;

    [RelayCommand(CanExecute = nameof(CanAiSelection))]
    private Task LabelWithAiAsync() => RunAiAsync("Labelling", (c, m, p, ct) => KnowledgeAiRun.LabelAsync(_ws, c, m, _selected.Select(r => r.Id).ToList(), p, ct));

    /// <summary>Labels one document again after the AI failed on it.</summary>
    [RelayCommand(CanExecute = nameof(CanAi))]
    private Task RetryAiAsync(InboxRow? row) =>
        row == null ? Task.CompletedTask : RunAiAsync("Labelling", (c, m, p, ct) => KnowledgeAiRun.LabelAsync(_ws, c, m, new[] { row.Id }, p, ct));

    [RelayCommand(CanExecute = nameof(CanAiSelection))]
    private Task DistillAsync()
    {
        var ids = _selected.Select(r => r.Id).ToList();
        return RunAiAsync("Distilling", (c, m, p, ct) => KnowledgeAiRun.DistillAsync(_ws, c, m, ids, ConfirmDistill, ConfirmReplaceChildren, false, p, ct));
    }

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
            if (severity is { } sev) Report(msg, sev);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
        await RefreshAsync();
    }

    // ---- edits and previews

    private void SaveEdit(InboxRow row)
    {
        try
        {
            var title = row.Title.Trim();
            if (title.Length == 0) return;
            _ws.Run(s => { s.Store.SetTitleTags(row.Id, title, ParseTags(row.Tags)); return 0; }, changes: true);
        }
        catch (Exception ex) { Message = "Could not save the change: " + ex.Message; AppLog.Error("Inbox edit failed", ex); }
    }

    /// <summary>Opens or closes a row's text preview; for a duplicate the other document's text loads beside it.</summary>
    [RelayCommand]
    private void ToggleExpand(InboxRow? row)
    {
        if (row == null) return;
        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded || row.Preview.Length > 0) return;
        try
        {
            var (mine, theirs) = _ws.Run(s => (Clip(s.Store.NoteBody(row.Id)), row.OtherId is { } o ? Clip(s.Store.NoteBody(o)) : ""));
            row.Preview = mine.Length == 0 ? "(no text)" : mine;
            row.OtherPreview = theirs;
        }
        catch (Exception ex) { row.Preview = "Could not read the text: " + ex.Message; }
    }

    private static string Clip(string? body) =>
        body == null ? "" : body.Length <= PreviewChars ? body : body[..PreviewChars] + "…";
}
