using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Knowledge;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>One line of the side panel's connection list: the node at the other end and why they are connected.</summary>
public sealed record BrainConnection(string Key, string Label, string Reason, double Weight, long? NoteId, string Color);

/// <summary>The node shown in the side panel.</summary>
public sealed record BrainSelection(string Key, string Title, string KindOrSource, string Preview, string Color,
    IReadOnlyList<BrainConnection> Connections, IReadOnlyList<long> NoteIds,
    string? Summary = null, string? Category = null, string? Type = null)
{
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
    public bool HasType => !string.IsNullOrWhiteSpace(Type);
}

/// <summary>
/// The Brain tab: the accepted knowledge as a 3D neuron graph in a local web page (Assets\brain, served from
/// https://brain.local/). The page draws; this view model builds the graph, answers the page's clicks with the
/// WPF side panel, saves the layout the page settles on, and asks Spectre about a node.
/// Messages to the page: graph, focus, clear, theme. From the page: ready, select, layout, problem (only from https://brain.local/).
/// </summary>
public sealed partial class BrainViewModel : ObservableObject
{
    public const string Host = "brain.local";
    public const string EmptyMessage = "Nothing in the graph yet. Add documents in Intake.";
    private const int PreviewChars = 600;
    private const int MaxAskNotes = 8;
    private const int MaxLayoutNodes = 50_000;

    /// <summary>Node kind -> colour on the canvas (the same values as brain.js).</summary>
    public static readonly IReadOnlyDictionary<string, string> KindColors = new Dictionary<string, string>
    {
        ["technique"] = "#ff5c7a", ["tactic"] = "#ffb454", ["artifact"] = "#4fd1ff",
        ["eventid"] = "#a78bfa", ["tool"] = "#5eead4", ["category"] = "#7f9fc4", ["doc"] = "#94a3b8",
    };

    /// <summary>Shown instead of the graph when the page reports that WebGL is missing.</summary>
    public const string WebGlProblem = "The graph needs WebGL, which is turned off or not available on this computer (try updating the graphics driver). To review and Library still work.";

    /// <summary>Shown when the page's scripts did not start.</summary>
    public const string StartProblem = "The graph could not start. To review and Library still work.";

    private readonly KnowledgeWorkspace _ws;
    private readonly Action<string, IReadOnlyList<long>> _askAbout;
    private readonly IntakeViewModel? _intake;
    private readonly Action _showIntake;
    private bool _bannerArmed;
    private GraphData? _data;
    private Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private string? _builtRevision;
    private string? _graphJson;   // the last "graph" message, replayed when the page (re)loads
    private int _generation;
    private bool _stale = true, _pageReady;

    /// <param name="askAbout">(draft, note ids) -> Spectre. Null = the app's chat (<see cref="AssistantChatViewModel.AskAbout"/>).</param>
    /// <param name="showIntake">The empty state's button and the banner's Review now.</param>
    public BrainViewModel(KnowledgeWorkspace workspace, Action<string, IReadOnlyList<long>>? askAbout = null, IntakeViewModel? intake = null, Action? showIntake = null)
    {
        _intake = intake;
        if (intake != null) intake.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IntakeViewModel.WaitingCount)) UpdateBanner(); };
        _ws = workspace;
        _askAbout = askAbout ?? ((draft, ids) => AssistantChatViewModel.Current?.AskAbout(draft, ids));
        _showIntake = showIntake ?? (() => { });
    }

    private readonly System.Diagnostics.Stopwatch _openedAt = new();

    /// <summary>The view started (or reused) the page: times how long until the page is ready.</summary>
    public void PageStarting(bool alreadyReady)
    {
        _openedAt.Restart();
        _pageReady = alreadyReady;
        PageLoading = !alreadyReady;
    }

    /// <summary>A re-attached page missed graphs built while it was away: post the current one unless the page already has it.</summary>
    public void ResyncPage(string? pageHas)
    {
        if (_graphJson != null && !string.Equals(_graphJson, pageHas, StringComparison.Ordinal)) Post(_graphJson);
    }

    /// <summary>The page was thrown away (WebView2 disposed): it must start again.</summary>
    public void PageLost() { _pageReady = false; PageLoading = true; }

    /// <summary>The user chose Retry on the "graph could not be started" message: the view restarts the page cleanly.</summary>
    public event Action? RetryRequested;

    [RelayCommand]
    private void RetryPage() { PageProblem = null; RetryRequested?.Invoke(); }

    /// <summary>A JSON message for the page (the view posts it to the WebView2).</summary>
    public event Action<string>? PostToPage;

    /// <summary>Light or dark app theme, sent to the page with the "theme" message (the canvas itself stays dark).</summary>
    public Func<bool> IsDarkTheme { get; set; } = () => ThemeManager.Current == Core.Settings.AppTheme.Dark;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph))]
    private bool _isEmpty;

    /// <summary>True once a graph with nodes was built.</summary>
    public bool HasGraph => !IsEmpty && _data != null;

    /// <summary>Why the graph page cannot be shown (no WebView2 runtime, missing files, start failure); null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph), nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(HasPageProblem))]
    private string? _pageProblem;

    public bool HasPageProblem => PageProblem != null;

    /// <summary>The graph area shows the page: there is a graph and the page could start.</summary>
    public bool ShowGraph => HasGraph && PageProblem == null;

    public string EmptyText => EmptyMessage;

    [ObservableProperty] private bool _isBusy;

    /// <summary>The page is starting (WebView2 + scripts): "Loading graph…" shows until the page says it is ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    private bool _pageLoading = true;

    public bool ShowLoading => ShowGraph && PageLoading;

    /// <summary>"Documents are hidden..." when the graph is too big to show every document at once.</summary>
    [ObservableProperty] private string? _hint;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(AskSpectreCommand))]
    private BrainSelection? _selected;

    public bool HasSelection => Selected != null;

    /// <summary>The knowledge changed: the next <see cref="RefreshAsync"/> rebuilds.</summary>
    public void MarkStale() => _stale = true;

    // ------------------------------------------------------------------ adding (always through the Inbox, as pending)

    /// <summary>"N new documents are waiting in Inbox" after an add started here; empty when nothing is pending.</summary>
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerDetail = "";

    public bool HasBanner => BannerText.Length > 0;

    partial void OnBannerTextChanged(string value) => OnPropertyChanged(nameof(HasBanner));

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (_intake == null) return;
        _bannerArmed = true;
        await _intake.AddFilesCommand.ExecuteAsync(null);
        UpdateBanner();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_intake == null) return;
        _bannerArmed = true;
        await _intake.AddFolderCommand.ExecuteAsync(null);
        UpdateBanner();
    }

    /// <summary>Paths dropped on the graph (sent by the page). Ignored unless the message came from our own page.</summary>
    public async Task HandleDropAsync(string? sourceUri, IReadOnlyList<string> paths)
    {
        if (!IsOwnPage(sourceUri)) { AppLog.Info("Brain: ignored a drop from another origin."); return; }
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_intake == null || list.Count == 0) return;
        _bannerArmed = true;
        await _intake.AddPathsAsync(list);
        UpdateBanner();
    }

    [RelayCommand]
    private void ReviewNow() => _showIntake();

    private void UpdateBanner()
    {
        var waiting = _intake?.WaitingCount ?? 0;
        if (!_bannerArmed || waiting == 0)
        {
            if (waiting == 0) _bannerArmed = false;
            BannerText = ""; BannerDetail = "";
            return;
        }
        BannerText = $"{waiting} waiting in Intake";
        BannerDetail = "";
    }

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Builds the graph and its JSON off the UI thread when the knowledge changed (or <paramref name="force"/>), then posts the string.
    /// A refresh started later wins: the result of an older one that finishes last is dropped.
    /// </summary>
    public async Task RefreshAsync(bool force = false, bool resetLayout = false)
    {
        var generation = ++_generation;
        if (!_ws.Exists)
        {
            SetData(null, null);
            return;
        }
        var focus = Selected?.Key is { } k && k.StartsWith("c:", StringComparison.Ordinal) && _data?.DocsHidden == true ? k : null;
        bool skipIfSame = !force && !_stale && _data != null;
        var builtRevision = _builtRevision;
        IsBusy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var built = await _ws.RunAsync(s =>
            {
                var rev = Revision(s.Store);
                if (skipIfSame && rev == builtRevision) return ((GraphData?)null, (string?)null, rev);   // same data version: the layout the page already has is reused
                var g = KnowledgeGraph.Build(s.Store, new GraphOptions(FocusKey: focus));
                return (g, GraphJson(g, resetLayout), rev);
            });
            AppLog.Info($"Brain: graph {(built.Item1 == null ? "unchanged (cached)" : $"built, {built.Item1.Nodes.Count} nodes")} in {sw.ElapsedMilliseconds} ms (off the UI thread)");
            if (generation != _generation || built.Item1 == null) return;
            _stale = false;
            _builtRevision = built.rev;
            SetData(built.Item1, built.Item2);
        }
        finally { if (generation == _generation) IsBusy = false; }
    }

    /// <summary>What a rebuild depends on: the store revision (bumped by imports and status changes) and the accepted count.</summary>
    private static string Revision(KnowledgeStore s) => $"{s.GetMeta("rev")}|{s.NotesByStatus("accepted").Count}";

    /// <summary>The "graph" message, serialized once (the graph JSON is spliced in, not parsed again).</summary>
    private static string GraphJson(GraphData g, bool reset) =>
        "{\"type\":\"graph\",\"reset\":" + (reset ? "true" : "false") + ",\"data\":" + KnowledgeGraph.ToJson(g) + "}";

    private void SetData(GraphData? data, string? graphJson)
    {
        _data = data is { Nodes.Count: > 0 } ? data : null;
        _graphJson = _data != null ? graphJson : null;
        _nodes = _data?.Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        IsEmpty = _data == null;
        OnPropertyChanged(nameof(HasGraph));
        OnPropertyChanged(nameof(ShowGraph));
        OnPropertyChanged(nameof(ShowLoading));
        Hint = _data?.DocsHidden == true ? "There are many documents, so only the concepts are shown. Select a concept to see its documents." : null;
        if (Selected != null && !_nodes.ContainsKey(Selected.Key)) Selected = null;
        else if (Selected != null) Selected = Describe(Selected.Key);
        PostGraph();
    }

    /// <summary>Posts the graph (already serialized off the UI thread).</summary>
    private void PostGraph()
    {
        if (_graphJson != null) Post(_graphJson);
    }
    private void Post(string json) => PostToPage?.Invoke(json);

    public static string ThemeJson(bool dark) => new JsonObject { ["type"] = "theme", ["dark"] = dark }.ToJsonString();

    public static string FocusJson(string key) => new JsonObject { ["type"] = "focus", ["key"] = key }.ToJsonString();

    public const string ClearJson = "{\"type\":\"clear\"}";

    /// <summary>The side panel was closed (or the node left the graph): the page drops its highlight and flies back to the whole brain.</summary>
    partial void OnSelectedChanged(BrainSelection? value)
    {
        if (value == null && _pageReady) Post(ClearJson);
    }

    /// <summary>The app theme changed: tell the page.</summary>
    public void PostTheme() { if (_pageReady) Post(ThemeJson(IsDarkTheme())); }

    // ------------------------------------------------------------------ page messages

    /// <summary>True only for a document served from https://brain.local/ (default port, no user info).</summary>
    public static bool IsOwnPage(string? uri) =>
        !string.IsNullOrWhiteSpace(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo);

    /// <summary>A message from the page. <paramref name="sourceUri"/> is the sending document; anything not from our page is ignored.</summary>
    public async Task HandlePageMessageAsync(string? sourceUri, string json)
    {
        if (!IsOwnPage(sourceUri)) { AppLog.Info("Brain: ignored a message from another origin."); return; }
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
        switch (t.GetString())
        {
            case "ready":
                _pageReady = true;
                PageLoading = false;
                AppLog.Info($"Brain: page ready {(_openedAt.IsRunning ? _openedAt.ElapsedMilliseconds + " ms after the view opened" : "")}");
                Post(ThemeJson(IsDarkTheme()));
                PostGraph();
                if (Selected != null) Post(FocusJson(Selected.Key));
                break;
            case "select":
                var key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                await SelectAsync(key, focus: false);
                break;
            case "layout":
                if (root.TryGetProperty("positions", out var p) && p.ValueKind == JsonValueKind.Object) await SaveLayoutAsync(p);
                break;
            case "problem":
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                AppLog.Warn("Brain: the page reported a problem: " + (reason ?? "?"));
                PageProblem = reason == "webgl" ? WebGlProblem : StartProblem;
                break;
        }
    }

    /// <summary>
    /// Saves where the nodes settled ([x,y,z] per node; an [x,y] pair is kept without depth). It does not raise
    /// <see cref="KnowledgeWorkspace.Changed"/>: nothing else depends on it.
    /// </summary>
    private async Task SaveLayoutAsync(JsonElement positions)
    {
        var pos = new Dictionary<string, (double X, double Y, double? Z)>(StringComparer.Ordinal);
        foreach (var e in positions.EnumerateObject())
        {
            if (pos.Count >= MaxLayoutNodes) break;
            if (!_nodes.ContainsKey(e.Name) || e.Value.ValueKind != JsonValueKind.Array) continue;
            var len = e.Value.GetArrayLength();
            if (len is not (2 or 3) || e.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number)) continue;
            double dx = e.Value[0].GetDouble(), dy = e.Value[1].GetDouble();
            double? dz = len == 3 ? e.Value[2].GetDouble() : null;
            if (double.IsFinite(dx) && double.IsFinite(dy) && (dz == null || double.IsFinite(dz.Value))) pos[e.Name] = (dx, dy, dz);
        }
        if (pos.Count == 0 || !_ws.Exists) return;
        try { await _ws.RunAsync(s => { s.Store.SaveLayout(pos); return 0; }); }
        catch (Exception ex) { AppLog.Warn("Brain: could not save the layout: " + ex.Message); }
    }

    // ------------------------------------------------------------------ selection and the side panel

    /// <summary>Shows <paramref name="key"/> in the side panel; <paramref name="focus"/> also centres the page on it.</summary>
    public async Task SelectAsync(string? key, bool focus = true)
    {
        if (key == null || !_nodes.ContainsKey(key)) { Selected = null; return; }
        Selected = Describe(key);
        if (_data?.DocsHidden == true && key.StartsWith("c:", StringComparison.Ordinal))
        {
            try { await RefreshAsync(force: true); }   // a concept's documents join the graph
            catch (Exception ex) { AppLog.Warn("Brain: could not load the concept's documents: " + ex.Message); }
        }
        if (focus) Post(FocusJson(key));
    }

    private BrainSelection Describe(string key)
    {
        var n = _nodes[key];
        var isDoc = n.Kind == "doc";
        var connections = new List<BrainConnection>();
        foreach (var l in _data!.Links)
        {
            var other = l.Source == key ? l.Target : l.Target == key ? l.Source : null;
            if (other == null || !_nodes.TryGetValue(other, out var o)) continue;
            connections.Add(new BrainConnection(o.Key, o.Label, Reason(l.Kind), l.Weight, o.NoteId, KindColors.GetValueOrDefault(o.Kind, KindColors["doc"])));
        }
        var ordered = connections
            .GroupBy(c => c.Key).Select(g => g.OrderByDescending(c => c.Weight).First())
            .OrderBy(c => c.NoteId == null ? 1 : 0).ThenByDescending(c => c.Weight).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var noteIds = isDoc ? new List<long> { n.NoteId!.Value } : ordered.Where(c => c.NoteId != null).Select(c => c.NoteId!.Value).Take(MaxAskNotes).ToList();
        NoteLabels? labels = null;
        if (isDoc)
        {
            try { labels = _ws.Run(s => s.Store.Labels(n.NoteId!.Value)); }
            catch (Exception ex) { AppLog.Warn("Brain: could not read the labels: " + ex.Message); }
        }
        return new BrainSelection(key, n.Label, isDoc ? n.Group ?? "Document" : KindName(n.Kind), isDoc ? DocPreview(n.NoteId!.Value) : ConceptPreview(n),
            KindColors.GetValueOrDefault(n.Kind, KindColors["doc"]), ordered, noteIds, labels?.Summary, labels?.Category, labels?.Type);
    }

    private string DocPreview(long noteId)
    {
        try
        {
            var body = _ws.Run(s => s.Store.NoteBody(noteId)) ?? "";
            body = body.Trim();
            return body.Length <= PreviewChars ? body : body[..PreviewChars] + "…";
        }
        catch (Exception ex) { AppLog.Warn("Brain: could not read a document: " + ex.Message); return ""; }
    }

    private static string ConceptPreview(GraphNode n)
    {
        var key = n.Key.Split(':', 3) is { Length: 3 } parts ? parts[2] : n.Label;
        return n.Kind == "technique" ? TechniqueCatalog.Name(key) ?? n.Label : n.Label;
    }

    public static string KindName(string kind) => kind switch
    {
        "technique" => "ATT&CK technique",
        "tactic" => "ATT&CK tactic",
        "artifact" => "Artifact",
        "eventid" => "Event ID",
        "tool" => "Tool",
        "category" => "Category",
        _ => "Document",
    };

    /// <summary>A link kind in plain words.</summary>
    public static string Reason(string linkKind) => linkKind switch
    {
        "mention" => "mentions",
        "wikilink" or "moc" => "links to",
        "semantic" => "similar content",
        "cooccur" => "often mentioned together",
        "category" => "in category",
        "structure" => "part of",
        _ => linkKind,
    };

    // ------------------------------------------------------------------ commands

    /// <summary>Finds the first node whose label matches (whole label, then start, then anywhere), selects it and centres the page on it.</summary>
    public void Search(string text)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0 || _data == null) return;
        var visible = _data.Nodes;
        var hit = visible.FirstOrDefault(n => string.Equals(n.Label, q, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(n => n.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(n => n.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { Hint = $"Nothing called \"{q}\" in the graph."; return; }
        Hint = _data.DocsHidden ? Hint : null;
        Selected = Describe(hit.Key);
        Post(FocusJson(hit.Key));
    }

    [RelayCommand]
    private void RunSearch() => Search(SearchText);

    [RelayCommand]
    private async Task SelectConnectionAsync(BrainConnection? c)
    {
        if (c != null) await SelectAsync(c.Key);
    }

    [RelayCommand]
    private async Task ResetLayoutAsync()
    {
        if (!_ws.Exists) return;
        await _ws.RunAsync(s => { s.Store.ClearLayout(); return 0; });
        await RefreshAsync(force: true, resetLayout: true);
    }

    private bool CanAskSpectre() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanAskSpectre))]
    private void AskSpectre()
    {
        if (Selected is not { } s) return;
        _askAbout($"Tell me about {s.Title}", s.NoteIds);
    }

    [RelayCommand]
    private void ShowIntake() => _showIntake();

    [RelayCommand]
    private void ClearSelection() => Selected = null;
}
