using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Spectre, the app-wide AI chat (one conversation across pages, owned by the shell). Builds the model client from Options > AI, asks once per case before sending
/// case data to a cloud provider, and shows the assistant's filter / flag suggestions as cards the examiner applies or dismisses.
/// It is the <see cref="IAssistantHost"/> of its session, so callbacks arrive on background threads and are marshalled with <c>ui</c>.
/// </summary>
public sealed partial class AssistantChatViewModel : ObservableObject, IAssistantHost, IAsyncDisposable
{
    private static readonly List<AssistantChatViewModel> Live = new();

    private readonly AppSettings _settings;
    private readonly ApiKeyStore _keys;
    private readonly Func<HttpClient> _http;
    private readonly Func<string?> _caseDir;
    private readonly Func<PageViewModel?> _currentPage;
    private readonly Func<IEnumerable<PageViewModel>> _allPages;
    private string? _question;
    private readonly Func<IModelClient>? _createModel;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<Action> _ui;
    private readonly Func<string?, CancellationToken, Task<AssistantExtraTools?>> _connectSpectre;
    private AssistantSession? _session;
    private AssistantExtraTools? _extra;
    private readonly AssistantContext _context = new(EzForensics.Core.Knowledge.KnowledgeService.Default);
    private AssistantCaseTools? _caseTools;
    private Task? _extraTask;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _cts;
    private string? _lastKey;
    private readonly Func<IReadOnlyList<long>, string?> _pinnedKnowledge;
    private IReadOnlyList<long>? _attachedNotes;

    /// <param name="caseDir">The app's current case folder (null = none open).</param>
    /// <param name="currentPage">The page on screen, read on the UI thread at each message. It supplies the page name, its state text, its
    /// tools and, when it implements <see cref="IAssistantGridTarget"/>, the grid. Null = no page.</param>
    /// <param name="createModel">Test seam; null = <see cref="AssistantModelFactory"/> from the current settings and stored key.</param>
    /// <param name="confirm">(title, message) -> true for Yes. Null = the app dialog (No is the default button).</param>
    /// <param name="ui">Runs an action on the UI thread. Null = <see cref="AppServices.OnUi"/>.</param>
    /// <param name="connectSpectre">Null = <see cref="AssistantSpectre.TryConnectAsync"/>.</param>
    /// <param name="allPages">Every page of the shell: their cross-page actions (<see cref="SpectrePageTools.CrossPage"/>) are offered on any page.</param>
    /// <param name="pinnedKnowledge">Note ids -> &lt;knowledge&gt; block for <see cref="AskAbout"/>. Null = the app knowledge base.</param>
    public AssistantChatViewModel(AppSettings settings, ApiKeyStore keys, Func<HttpClient> http, Func<string?> caseDir, Func<PageViewModel?>? currentPage = null,
        Func<IModelClient>? createModel = null, Func<string, string, bool>? confirm = null, Action<Action>? ui = null,
        Func<string?, CancellationToken, Task<AssistantExtraTools?>>? connectSpectre = null, Func<IEnumerable<PageViewModel>>? allPages = null,
        Func<IReadOnlyList<long>, string?>? pinnedKnowledge = null)
    {
        _pinnedKnowledge = pinnedKnowledge ?? (ids => EzForensics.Core.Knowledge.KnowledgeService.Default.NotesBlock(ids, AssistantContext.MaxKnowledgeChars));
        Current = this;
        _currentPage = currentPage ?? (() => null);
        _allPages = allPages ?? (() => Array.Empty<PageViewModel>());
        _settings = settings; _keys = keys; _http = http; _caseDir = caseDir; _createModel = createModel;
        _confirm = confirm ?? ((title, message) => Dialogs.Confirm(title, message, "Yes", "No", defaultNo: true));
        _ui = ui ?? AppServices.OnUi;
        _connectSpectre = connectSpectre ?? AssistantSpectre.TryConnectAsync;
        _settings.PropertyChanged += OnSettingsChanged;
        lock (Live) Live.Add(this);
        _autoProbe = createModel == null && connectSpectre == null;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStarters));
        if (IsExpanded && connectSpectre == null) StartSpectreConnect();
        if (IsExpanded) StartProviderCheck();   // opened last run: connect at start (tests pass their own connector)
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Transient line under the list ("Running grid_get_context..."); empty when idle.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>"You switched to Explore. Start a new conversation?" after a page change mid-conversation; null when not shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageSwitchNotice))]
    private string? _pageSwitchNotice;

    public bool HasPageSwitchNotice => PageSwitchNotice != null;

    /// <summary>The page the conversation belongs to (where it started, or where the examiner chose to keep it).</summary>
    private PageViewModel? _conversationPage;

    public bool IsIdle => !IsBusy;

    // ------------------------------------------------------------------ first run: local AI card and starter prompts

    public const string LocalAiCardText = "Spectre needs a local AI model. Set it up once; it stays on this PC.";

    /// <summary>The three prompts shown as chips while the chat is empty.</summary>
    public static IReadOnlyList<string> StarterPrompts { get; } = new[] { "Any encoded PowerShell?", "Summarize this case", "What ran at logon?" };

    private readonly bool _autoProbe;

    /// <summary>Local provider chosen but Ollama is not running or has no models: the chat shows a card with "Set up local AI".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalAiCard))]
    private bool _needsLocalAi;

    /// <summary>Progress or result of the setup started from the card.</summary>
    [ObservableProperty] private string _localAiStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpLocalAiCommand))]
    private bool _isSettingUpLocalAi;

    public bool ShowLocalAiCard => NeedsLocalAi;

    /// <summary>The starter chips show while nothing has been said yet.</summary>
    public bool ShowStarters => !Messages.Any(m => m.Role == ChatRole.You);

    private void StartProviderCheck()
    {
        if (!_autoProbe) return;
        _ = CheckProviderAsync();
    }

    /// <summary>
    /// Looks at the local provider: not running or no models installed shows the setup card, a ready provider hides it.
    /// Other providers never show it (their problems are a missing key, which Options > AI explains).
    /// </summary>
    public async Task CheckProviderAsync(CancellationToken ct = default)
    {
        var provider = _settings.AiProvider;
        if (provider != "ollama") { _ui(() => NeedsLocalAi = false); return; }
        bool missing;
        try
        {
            using var http = _http();
            http.Timeout = TimeSpan.FromSeconds(5);
            var ids = await AiSettingsViewModel.ListModelsAsync(http, provider, _settings.AiBaseUrl, null, ct);
            missing = ids.Count == 0;
        }
        catch (Exception ex) when (ex is AssistantModelException or HttpRequestException or UriFormatException or OperationCanceledException) { missing = true; }
        _ui(() => NeedsLocalAi = missing && _settings.AiProvider == "ollama");
    }

    /// <summary>The card's button: the same local AI setup as Options > AI > "Set up local AI…", saved at once.</summary>
    [RelayCommand(CanExecute = nameof(CanSetUpLocalAi))]
    private async Task SetUpLocalAiAsync()
    {
        IsSettingUpLocalAi = true;
        try
        {
            var summary = await (SetUpLocalAiOverride?.Invoke() ?? LocalAiRunner.RunAsync(_http, _settings, SaveSettings, t => _ui(() => LocalAiStatus = t)));
            LocalAiStatus = summary;
            OnPropertyChanged(nameof(ModelLabel));
            await CheckProviderAsync();
        }
        catch (OperationCanceledException) { LocalAiStatus = "Cancelled."; }
        catch (Exception ex) { LocalAiStatus = "Local AI setup failed: " + Scrub(ex.Message); }
        finally { IsSettingUpLocalAi = false; }
    }

    private bool CanSetUpLocalAi() => !IsSettingUpLocalAi;

    /// <summary>Test seam for the setup run (returns its summary line).</summary>
    internal Func<Task<string>>? SetUpLocalAiOverride { get; set; }

    /// <summary>A starter chip: sends its prompt.</summary>
    [RelayCommand]
    private async Task SendStarterAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || IsBusy) return;
        InputText = prompt;
        await SendAsync();
    }

    /// <summary>Header text: "Local · qwen3:8b".</summary>
    public string ModelLabel
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(_settings.AiModel) ? "no model chosen" : _settings.AiModel;
            return $"{ProviderName(_settings.AiProvider)} · {model}";
        }
    }

    public static string ProviderName(string provider) => AiProviders.DisplayName(provider);

    public const string ModePopup = "popup", ModeDocked = "docked", ModeWindow = "window";

    /// <summary>Where Spectre shows when open: <see cref="ModePopup"/> (default), <see cref="ModeDocked"/> or <see cref="ModeWindow"/>.</summary>
    public string Mode
    {
        get => _settings.AssistantMode;
        set
        {
            if (_settings.AssistantMode == value) return;
            _settings.AssistantMode = value;
            SaveSettings();
        }
    }

    public bool IsPopupMode => Mode == ModePopup;
    public bool IsDockedMode => Mode == ModeDocked;
    public bool IsWindowMode => Mode == ModeWindow;
    /// <summary>The header's minimize button: popup and docked only (the window has its own title-bar close).</summary>
    public bool CanMinimize => Mode != ModeWindow;

    /// <summary>Open (true) or collapsed to the launcher icon (false); remembered across runs.</summary>
    public bool IsExpanded
    {
        get => _settings.AssistantExpanded;
        set
        {
            if (_settings.AssistantExpanded == value) return;
            _settings.AssistantExpanded = value;
            SaveSettings();
            if (value) { StartSpectreConnect(); StartProviderCheck(); }
        }
    }

    [RelayCommand]
    private void SetMode(string? mode)
    {
        if (mode is not (ModePopup or ModeDocked or ModeWindow)) return;
        Mode = mode;
        IsExpanded = true;
    }

    [RelayCommand]
    private void Minimize() => IsExpanded = false;

    /// <summary>The launcher: opens Spectre, or (pop-up) collapses it again.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Auto-mode (same setting as Options > AI): proposals are applied at once, with Undo.</summary>
    public bool AutoMode
    {
        get => _settings.AssistantAutoMode;
        set
        {
            if (_settings.AssistantAutoMode == value) return;
            _settings.AssistantAutoMode = value;
            SaveSettings();
        }
    }

    private static void SaveSettings()
    {
        try { AppServices.Current?.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not save the Spectre settings: " + ex.Message); }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.AssistantFloating) or nameof(AppSettings.AssistantMode))
            _ui(() =>
            {
                OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(IsPopupMode)); OnPropertyChanged(nameof(IsDockedMode));
                OnPropertyChanged(nameof(IsWindowMode)); OnPropertyChanged(nameof(CanMinimize));
            });
        if (e.PropertyName == nameof(AppSettings.AssistantExpanded)) _ui(() => OnPropertyChanged(nameof(IsExpanded)));
        if (e.PropertyName == nameof(AppSettings.AssistantAutoMode)) _ui(() => OnPropertyChanged(nameof(AutoMode)));
        if (e.PropertyName is nameof(AppSettings.AiProvider) or nameof(AppSettings.AiModel) or nameof(AppSettings.AiBaseUrl))
            _ui(() => { _session = null; OnPropertyChanged(nameof(ModelLabel)); StartProviderCheck(); }); // the history belongs to the old model
    }

    // ------------------------------------------------------------------ IAssistantHost (background threads)

    public void Status(string text) => _ui(() => StatusText = text);

    /// <summary>The examiner's own words of the message being answered.</summary>
    public string? Question => _question;

    /// <summary>One muted step line per tool call, so the examiner sees what Spectre did (never raw arguments: see <see cref="SpectrePageTools.Describe"/>).</summary>
    public void ToolStep(ToolCall call, ToolOutcome outcome)
    {
        var text = SpectrePageTools.Describe(call, outcome);
        _ui(() => Messages.Add(new ChatMessage(ChatRole.Step, Scrub(text))));
    }

    /// <summary>The grid of the page on screen (Analyze), or null on every other page.</summary>
    private IAssistantGridTarget? Grid => _currentPage() as IAssistantGridTarget;

    public void ShowProposal(AssistantProposal proposal) =>
        _ui(() => Messages.Add(new ChatMessage(ChatRole.Assistant, "", new ProposalCardViewModel(proposal, Grid))));

    /// <summary>
    /// Shows the card. With auto-mode on it is applied at once (same guards as the Apply button) and the model is told so;
    /// otherwise the model is told it was only proposed.
    /// </summary>
    public Task<string> HandleProposalAsync(AssistantProposal proposal)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui(async () =>
        {
            try
            {
                var card = new ProposalCardViewModel(proposal, Grid);
                Messages.Add(new ChatMessage(ChatRole.Assistant, "", card));
                var kind = proposal.GetType().Name;
                if (!AutoMode)
                {
                    AppLog.Info($"Spectre proposal {kind}: pending card (auto-mode off).");
                    done.TrySetResult(AssistantGridTools.ProposedText);
                    return;
                }
                var err = await card.AutoApplyAsync();
                AppLog.Info(err == null ? $"Spectre proposal {kind}: applied by auto-mode." : $"Spectre proposal {kind}: NOT applied by auto-mode: {err}");
                done.TrySetResult(err == null ? AppliedText(proposal)
                    : $"Auto-mode could not apply it ({err}). NOT applied; it is still shown to the examiner. Do not assume it was applied." + AssistantGridTools.ReplyRule);
            }
            catch (Exception ex)
            {
                AppLog.Error("Auto-mode failed", ex);
                done.TrySetResult(AssistantGridTools.ProposedText);
            }
        });
        return done.Task;
    }

    public Task<bool> ConfirmWriteAsync(ToolCall call) =>
        Task.FromResult(_confirm("Allow Spectre?", $"Spectre wants to run '{call.Name}', which changes data. Allow it?"));

    /// <summary>A button of a card in the transcript page. <paramref name="type"/> is apply, dismiss or undo; unknown keys and busy commands are ignored.</summary>
    public void HandleCardAction(string type, string key)
    {
        var card = Messages.Select(m => m.Card).FirstOrDefault(c => c != null && c.Key == key);
        if (card == null) { AppLog.Info($"Spectre card {type}: no card with that key."); return; }
        switch (type)
        {
            case "apply" when card.ApplyCommand.CanExecute(null): card.ApplyCommand.Execute(null); break;
            case "dismiss" when card.DismissCommand.CanExecute(null): card.DismissCommand.Execute(null); break;
            case "undo" when card.UndoCommand.CanExecute(null): card.UndoCommand.Execute(null); break;
            case "applyAll": _ = ApplyAllAsync(key); break;
            default: AppLog.Info($"Spectre card {type}: ignored (card is {card.State})."); break;
        }
    }

    /// <summary>
    /// "Apply all": applies, in order, every card of the same reply that is still pending. A reply's cards are the unbroken run of
    /// card messages around the card with <paramref name="key"/>. Cards that cannot be applied are skipped (their own note says why).
    /// </summary>
    public async Task ApplyAllAsync(string key)
    {
        var list = Messages.Where(m => m.Role != ChatRole.Step).ToList();   // step lines sit between a reply's cards
        var at = list.FindIndex(m => m.Card != null && m.Card.Key == key);
        if (at < 0) { AppLog.Info("Spectre apply all: no card with that key."); return; }
        int from = at, to = at;
        while (from > 0 && list[from - 1].Card != null) from--;
        while (to < list.Count - 1 && list[to + 1].Card != null) to++;
        for (int i = from; i <= to; i++)
        {
            var card = list[i].Card!;
            if (!card.IsPending) continue;
            try { await card.ApplyCommand.ExecuteAsync(null); }
            catch (Exception ex) { AppLog.Error("Spectre apply all failed on a card", ex); }
        }
    }

    // ------------------------------------------------------------------ commands

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (!HasConsent()) return;
        InputText = "";
        if (!Messages.Any(m => m.Role == ChatRole.You) || HasPageSwitchNotice) KeepConversation(); // sending here keeps the conversation
        Messages.Add(new ChatMessage(ChatRole.You, text));
        _question = text;
        IsBusy = true;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var session = await EnsureSessionAsync(cts.Token);
            await OfferToolsAsync(session, cts.Token);
            var notes = _attachedNotes; _attachedNotes = null;
            var said = await session.AskAsync(WithCurrentView(text, notes), cts.Token, question: text);
            if (said.Count == 0) AddStatus("Spectre had nothing to say.");
            foreach (var s in said) Messages.Add(new ChatMessage(ChatRole.Assistant, SpectreSkills.ReplaceIds(s)));
        }
        catch (OperationCanceledException) { AddStatus("Stopped."); }
        catch (AssistantModelException ex) { AddStatus(Scrub(ex.Message)); if (_settings.AiProvider == "ollama") StartProviderCheck(); }
        catch (Exception ex) { AppLog.Error("Assistant failed", ex); AddStatus($"Spectre failed ({ex.GetType().Name})."); }
        finally
        {
            IsBusy = false;
            StatusText = "";
            if (_cts == cts) _cts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void Stop() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }

    [RelayCommand]
    private void NewChat()
    {
        Stop();
        _session = null;
        _context.Reset();
        _attachedNotes = null;
        Messages.Clear();
        StatusText = "";
        KeepConversation();
    }

    /// <summary>
    /// Called by the shell when the page on screen changed. Mid-conversation it asks whether to start a new one; going back to the
    /// conversation's page before answering takes the question away. An empty conversation simply moves with the examiner.
    /// </summary>
    public void OnPageChanged()
    {
        var page = _currentPage();
        // an empty conversation moves with the examiner; a page Spectre opened itself (a tool runs while busy) is not a switch to ask about
        if (!Messages.Any(m => m.Role == ChatRole.You) || IsBusy) { _conversationPage = page; PageSwitchNotice = null; return; }
        if (IsReviewPage(page) && IsReviewPage(_conversationPage)) { _conversationPage = page; PageSwitchNotice = null; return; }   // Analyze, Explore and Findings share one conversation
        PageSwitchNotice = page == _conversationPage ? null : $"You switched to {FriendlyPageName(page)}. Start a new conversation?";
    }

    private static bool IsReviewPage(PageViewModel? p) => p?.AssistantPage is SpectrePage.Analyze or SpectrePage.Explore or SpectrePage.Findings;

    /// <summary>
    /// "Ask Spectre about this ..." buttons: opens the chat (in the examiner's mode) and sends <paramref name="text"/>; when Spectre is busy
    /// the text is put in the box instead.
    /// </summary>
    public void Ask(string text)
    {
        _ui(() =>
        {
            IsExpanded = true;
            InputText = text;
            if (SendCommand.CanExecute(null)) _ = SendCommand.ExecuteAsync(null);
        });
    }

    /// <summary>
    /// The Brain's "Ask Spectre": opens Spectre (in the examiner's mode) with <paramref name="draft"/> in the box, not sent, and attaches the
    /// notes <paramref name="noteIds"/> as the &lt;knowledge&gt; block of the next request (instead of a search).
    /// </summary>
    public void AskAbout(string draft, IReadOnlyList<long> noteIds)
    {
        var ids = noteIds.ToList();
        _ui(() =>
        {
            IsExpanded = true;
            InputText = draft;
            _attachedNotes = ids.Count > 0 ? ids : null;
        });
    }

    /// <summary>Keeps the conversation going on the page now on screen.</summary>
    [RelayCommand]
    private void KeepConversation()
    {
        _conversationPage = _currentPage();
        PageSwitchNotice = null;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Cloud providers get case data only after the examiner agreed once for this case.</summary>
    private bool HasConsent()
    {
        var provider = _settings.AiProvider;
        var dir = _caseDir();
        if (provider == "ollama" || AssistantConsent.Has(dir, provider, _settings.AiBaseUrl)) return true; // no case open still counts: the Analyze grid can be sent
        if (!_confirm("Send case data?", $"Case data will be sent to {(provider == AiProviders.Custom ? "the server at " + CustomHost(_settings.AiBaseUrl) : ProviderName(provider))}. Continue?"))
        {
            AddStatus("Nothing was sent. Choose Local in Options > AI to keep case data on this PC.");
            return false;
        }
        try { AssistantConsent.Grant(dir, provider, _settings.AiModel, _settings.AiBaseUrl); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddStatus("Could not save your consent for this case; you will be asked again."); }
        return true;
    }

    private static string CustomHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : "the address in Options > AI";

    private async Task<AssistantSession> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session != null) return _session;
        _context.Reset(); // a new session has not seen the state or the view yet
        await EnsureModelChosenAsync(ct);
        var model = CreateModel();
        _model = model;
        return _session = new AssistantSession(model, Array.Empty<IAssistantTool>(), this, log: AppLog.Info);
    }

    /// <summary>
    /// Sets the tools for this request from the page on screen: grid tools only where there is a grid, case tools when a case is open,
    /// the page's own proposal tools, Spectre's findings tools and skill_help; then drops those that belong to other pages.
    /// </summary>
    private async Task OfferToolsAsync(AssistantSession session, CancellationToken ct)
    {
        var page = _currentPage();
        var grid = page as IAssistantGridTarget;
        var spectrePage = page?.AssistantPage ?? SpectrePage.Any;
        var tools = new List<IAssistantTool>();
        _caseTools ??= new AssistantCaseTools(_caseDir, this, canOpenFile: true, openFile: () => (_currentPage() as IAssistantGridTarget)?.CurrentCsv);
        if (grid != null) tools.AddRange(AssistantGridTools.Create(grid.GetContext, () => grid.FlagIds, this, () => GridFlags(grid), _caseTools));
        if (!string.IsNullOrEmpty(_caseDir()))
            tools.AddRange(_caseTools.Tools.Where(t => grid != null || t.Spec.Name != "analyze_open_file")); // opening a file needs the Analyze grid
        if (page != null) tools.AddRange(page.AssistantTools(this));
        // the other pages' actions ("scan D:\evidence" from Analyze): each opens its page first, so the examiner sees it happen
        foreach (var other in _allPages().Where(p => !ReferenceEquals(p, page)))
            foreach (var t in other.AssistantTools(this).Where(t => SpectrePageTools.CrossPage.Contains(t.Spec.Name) && tools.All(x => x.Spec.Name != t.Spec.Name)))
                tools.Add(new OpensPageFirst(t, other));
        var extra = await EnsureSpectreAsync(ct);
        if (extra != null)
        {
            tools.AddRange(extra.Tools);
            if (!string.IsNullOrEmpty(_caseDir()) && SpectreCaseHandoff.CreateTool(extra.Tools, _caseDir) is { } handoff) tools.Add(handoff);
        }
        tools.Add(SpectreSkills.CreateHelpTool());
        if (!EzForensics.Core.Knowledge.KnowledgeService.Default.IsEmpty) tools.AddRange(KnowledgeTools.Create(EzForensics.Core.Knowledge.KnowledgeService.Default));
        var offered = SpectreSkills.ForPage(spectrePage, tools);
        session.SetTools(offered);
        AppLog.Info($"Spectre on {spectrePage}: offering {offered.Count} tools ({string.Join(", ", offered.Select(t => t.Spec.Name))}).");
    }

    /// <summary>The page on screen as Spectre's skills know it.</summary>
    private SpectrePage Page => _currentPage()?.AssistantPage ?? SpectrePage.Any;

    /// <summary>The examiner's flags as (id, label), listed in the view and accepted by label in grid_flag_rows.</summary>
    private static IReadOnlyList<(string Id, string Label)> GridFlags(IAssistantGridTarget? grid) =>
        grid == null ? Array.Empty<(string, string)>() : grid.FlagIds.Select(id => (id, grid.FlagLabel(id))).ToList();

    /// <summary>The page's name in the state text ("Page: Process tree").</summary>
    public static string FriendlyPageName(PageViewModel? page) => page?.AssistantPage switch
    {
        SpectrePage.Analyze => "Analyze",
        SpectrePage.Explore => "Explore",
        SpectrePage.Findings => "Review",
        SpectrePage.Ingest => "Ingest",
        SpectrePage.ProcessTree => "Process tree",
        SpectrePage.Tools => "Tools",
        SpectrePage.Options => "Options",
        _ => page?.Title ?? "unknown",
    };

    /// <summary>
    /// Puts where the examiner is (app state) and what they are looking at (grid view) in front of their message, so the model
    /// answers about the screen without asking. Unchanged parts are not repeated: the view says "same", the state is left out.
    /// </summary>
    public string WithCurrentView(string text, IReadOnlyList<long>? notes = null)
    {
        var page = _currentPage();
        string? state = null, view = null;
        if (page != null)
        {
            try { state = ("Page: " + FriendlyPageName(page) + "\n" + (page.AssistantState() ?? "")).TrimEnd(); }
            catch (Exception ex) { AppLog.Warn("Assistant could not read the app state: " + ex.Message); }
        }
        if (page is IAssistantGridTarget grid)
        {
            try { view = grid.GetContext().Describe(flags: GridFlags(grid)); }
            catch (Exception ex) { AppLog.Warn("Assistant could not read the grid: " + ex.Message); }
        }
        string? pinned = null;
        if (notes is { Count: > 0 })
        {
            try { pinned = _pinnedKnowledge(notes); }
            catch (Exception ex) { AppLog.Warn("Assistant could not read the notes asked about: " + ex.Message); }
        }
        return _context.Build(Page, state, view, text, AppLog.Info, pinned);
    }

    /// <summary>No model set yet: pick one from the provider's list and save it, so a fresh install chats without visiting Options.</summary>
    private async Task EnsureModelChosenAsync(CancellationToken ct)
    {
        if (_createModel != null || !string.IsNullOrWhiteSpace(_settings.AiModel) || _settings.AiProvider == "anthropic") return;
        var provider = _settings.AiProvider;
        var key = _keys.TryLoad(provider);
        if (AiProviders.NeedsKey(provider) && key == null) return; // the factory reports the missing key
        StatusText = "Finding a model…";
        IReadOnlyList<string> ids;
        try
        {
            using var http = _http();
            ids = await AiSettingsViewModel.ListModelsAsync(http, provider, _settings.AiBaseUrl, key, ct);
        }
        catch (Exception ex) when (ex is AssistantModelException or HttpRequestException or UriFormatException)
        {
            throw new AssistantModelException(AiSettingsViewModel.Friendly(provider, _settings.AiBaseUrl is { Length: > 0 } u ? u : OllamaDefaults.BaseUrl, ex, key));
        }
        if (AiSettingsViewModel.PickDefault(provider, ids) is not { } pick)
            throw new AssistantModelException(provider == "ollama" ? "Ollama has no models installed." : "No models found.");
        _settings.AiModel = pick;
        try { AppServices.Current?.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not save the chosen AI model: " + ex.Message); }
    }

    private IModelClient CreateModel()
    {
        if (_createModel != null) return _createModel();
        var provider = _settings.AiProvider;
        _lastKey = _keys.TryLoad(provider);
        return AssistantModelFactory.Create(provider, _settings.AiModel, _settings.AiBaseUrl, _lastKey, _http());
    }

    /// <summary>Connects Spectre once (lazily, only when the dev override is set); a failure leaves the chat without Spectre tools.</summary>
    private async Task<AssistantExtraTools?> EnsureSpectreAsync(CancellationToken ct)
    {
        StartSpectreConnect();
        // Never hold the first message for a slow engine start: wait a moment, then go on; the tools join the next request.
        if (_extraTask != null && !_extraTask.IsCompleted)
            await Task.WhenAny(_extraTask, Task.Delay(300, ct)).ConfigureAwait(true);
        return _extra;
    }

    /// <summary>Connects the Spectre engine in the background (once per chat). A failure is logged and leaves the chat working.</summary>
    public void StartSpectreConnect()
    {
        if (_extraTask != null) return;
        var cts = _connectCts = new CancellationTokenSource();
        var dir = _caseDir();
        _extraTask = Task.Run(async () =>
        {
            try
            {
                var extra = await _connectSpectre(dir, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) { if (extra != null) await extra.Owner.DisposeAsync(); return; }
                _extra = extra;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Warn("Spectre tools unavailable: " + ex.Message); }
        });
    }

    private void AddStatus(string text) => Messages.Add(new ChatMessage(ChatRole.Status, text));

    /// <summary>What the model hears after auto-mode applied a proposal: "applied", plus what the action found (a scan's summary).</summary>
    private static string AppliedText(AssistantProposal proposal)
    {
        string? more = null;
        try { more = (proposal as PageActionProposal)?.AfterApply?.Invoke(); }
        catch (Exception ex) { AppLog.Error("Spectre: reading what an action found failed", ex); }
        return string.IsNullOrWhiteSpace(more) ? AssistantGridTools.AppliedText : AssistantGridTools.AppliedText + "\n" + more;
    }

    /// <summary>A page's action used from another page: opens that page, then runs the action there.</summary>
    private sealed class OpensPageFirst : IAssistantTool
    {
        private readonly IAssistantTool _inner;
        private readonly PageViewModel _page;
        public OpensPageFirst(IAssistantTool inner, PageViewModel page) { _inner = inner; _page = page; }
        public ToolSpec Spec => _inner.Spec;

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            AppServices.OnUi(() => AppServices.Current.Navigate(_page.GetType()));
            return _inner.RunAsync(call, ct);
        }
    }

    /// <summary>The key must never reach the screen, even if a server echoed it back.</summary>
    private string Scrub(string text) => string.IsNullOrEmpty(_lastKey) ? text : text.Replace(_lastKey, "…");

    public async ValueTask DisposeAsync()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        lock (Live) Live.Remove(this);
        Stop();
        _caseTools?.Dispose(); _caseTools = null;
        try { _connectCts?.Cancel(); } catch (ObjectDisposedException) { }
        var extra = _extra; _extra = null;
        if (extra != null) { try { await extra.Owner.DisposeAsync(); } catch (Exception ex) { AppLog.Warn("Closing Spectre failed: " + ex.Message); } }
    }

    private IModelClient? _model;

    /// <summary>The newest chat view model (the app has one), so Options > AI can show what was last sent to the model.</summary>
    public static AssistantChatViewModel? Current { get; private set; }

    /// <summary>JSON of the last request sent to the model, or null when nothing was sent yet or the model keeps no snapshot.</summary>
    public string? LastRequestJson => (_model as IRequestSnapshot)?.LastRequestJson;

    /// <summary>App exit: stops every chat and the Spectre process behind it.</summary>
    public static void Shutdown()
    {
        AssistantChatViewModel[] all;
        lock (Live) all = Live.ToArray();
        foreach (var vm in all) { try { vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { } }
        try { AssistantSpectre.ShutdownAsync().Wait(TimeSpan.FromSeconds(15)); } catch (Exception) { }
    }
}
