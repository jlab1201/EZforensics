using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EzForensics.App.Views.Assistant;

/// <summary>
/// The Spectre conversation as one HTML page in a locked-down WebView2. The chat view model stays the source of truth: this class
/// pushes its messages to the page as JSON (never as script), pushes card state changes, and hands the page's Apply / Dismiss / Undo
/// clicks back to the view model. Evidence text cannot run or navigate: Markdig escapes raw HTML, the page has a strict CSP, every
/// navigation after the first load is cancelled, and devtools, context menus, script dialogs, downloads and new windows are off.
/// If the WebView2 runtime is missing or fails to start, <paramref name="onUnavailable"/> runs and the WPF list stays in use.
/// </summary>
internal sealed class SpectreTranscript : IDisposable
{
    private readonly Grid _host;
    private readonly AssistantChatViewModel _vm;
    private readonly Action<bool> _setActive;
    private readonly Dictionary<ChatMessage, string> _ids = new();
    private readonly HashSet<ProposalCardViewModel> _watched = new();
    private WebView2? _web;
    private bool _ready, _disposed, _allowNavigation;
    private int _seq;

    /// <param name="setActive">true = the page is showing (hide the fallback list), false = use the fallback list.</param>
    public SpectreTranscript(Grid host, AssistantChatViewModel vm, Action<bool> setActive)
    {
        _host = host; _vm = vm; _setActive = setActive;
    }

    public async Task StartAsync()
    {
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex)
        {
            AppLog.Warn("WebView2 runtime not available for the Spectre transcript: " + ex.Message);
            return;
        }
        try
        {
            var userData = Path.Combine(AppPaths.DataRoot, "webview2", "spectre");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            if (_disposed) return;
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            _host.Children.Add(_web);
            await _web.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            var core = _web.CoreWebView2;
            Lockdown(core);
            ApplyColorScheme(core);
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, _) => _allowNavigation = false;
            _vm.Messages.CollectionChanged += OnMessagesChanged;
            _vm.PropertyChanged += OnVmChanged;
            ThemeManager.Changed += OnThemeChanged;
            _allowNavigation = true;
            core.NavigateToString(ChatHtml.BuildPage(CurrentTheme(), ChatHtml.NewNonce()));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start the Spectre transcript", ex);
            Teardown();
            _setActive(false);
        }
    }

    private void Lockdown(CoreWebView2 core)
    {
        core.NavigationStarting += (_, e) => { if (!_allowNavigation) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsWebMessageEnabled = true;
    }

    /// <summary>The app's brushes and fonts as page theme values (same source as the CyberChef panel).</summary>
    private static ChatHtml.Theme CurrentTheme()
    {
        var c = Explore.CyberChefPanel.CurrentThemeColors();
        return new ChatHtml.Theme(c.Dark, c.Background, c.Surface, c.SurfaceAlt, c.Text, c.TextSecondary, c.Accent, c.AccentText, c.Border,
            c.Selection, c.Input, c.Code, c.UiFont, c.MonoFont);
    }

    private void OnThemeChanged(AppTheme _)
    {
        if (_web?.CoreWebView2 is { } core) ApplyColorScheme(core);
        Post(ChatHtml.ThemeJson(CurrentTheme()));
    }

    /// <summary>Browser-level light/dark preference, so scrollbars and form controls match the app theme.</summary>
    private static void ApplyColorScheme(CoreWebView2 core)
    {
        try { core.Profile.PreferredColorScheme = CurrentTheme().Dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; }
        catch (Exception ex) { AppLog.Warn("Spectre transcript: could not set the colour scheme: " + ex.Message); }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantChatViewModel.IsBusy) or nameof(AssistantChatViewModel.StatusText)) PostTyping();
    }

    private void PostTyping() => Post(ChatHtml.TypingJson(_vm.IsBusy, _vm.StatusText));

    private void Post(string json)
    {
        if (_disposed || !_ready || _web?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(json); }
        catch (Exception ex) { AppLog.Warn("Spectre transcript: could not post a message: " + ex.Message); }
    }

    // ------------------------------------------------------------------ page to host

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only our own page (the data: document from NavigateToString) may talk to the view model.
        var action = ChatHtml.ParseAction(e.WebMessageAsJson);
        if (action is not { } a) { AppLog.Info("Spectre transcript: ignored a page message that is not a card action."); return; }
        AppLog.Info($"Spectre transcript: page message '{a.Type}'.");
        if (a.Type == "ready")
        {
            _ready = true;
            _setActive(true);
            Replay();
            return;
        }
        _vm.HandleCardAction(a.Type, a.Id);
    }


    // ------------------------------------------------------------------ host to page

    private void Replay()
    {
        Post(ChatHtml.ClearJson());
        foreach (var m in _vm.Messages.ToList()) Add(m);
        if (_vm.IsBusy) PostTyping();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { ForgetAll(); Post(ChatHtml.ClearJson()); return; }
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            foreach (ChatMessage m in e.NewItems) Add(m);
    }

    private void ForgetAll()
    {
        foreach (var c in _watched) c.PropertyChanged -= OnCardChanged;
        _watched.Clear();
        _ids.Clear();
    }

    private void Add(ChatMessage m)
    {
        if (m.Card is { } card)
        {
            if (_watched.Add(card)) card.PropertyChanged += OnCardChanged;
            Post(CardJson("add", card));
            return;
        }
        var id = _ids[m] = (++_seq).ToString();
        Post(m.Role switch
        {
            ChatRole.You => ChatHtml.AddMessageJson(id, "you", text: m.Text),
            ChatRole.Status => ChatHtml.AddMessageJson(id, "status", text: m.Text),
            ChatRole.Step => ChatHtml.AddMessageJson(id, "step", text: m.Text),
            _ => ChatHtml.AddMessageJson(id, "spectre", html: ChatHtml.RenderMarkdown(m.Text)),
        });
    }

    private static string CardJson(string op, ProposalCardViewModel c) =>
        ChatHtml.CardJson(op, c.Key, c.Title, c.Detail, c.Reason, c.State.ToString().ToLowerInvariant(), c.Note, c.CanUndo);

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ProposalCardViewModel c && e.PropertyName is nameof(ProposalCardViewModel.State) or nameof(ProposalCardViewModel.Note) or nameof(ProposalCardViewModel.CanUndo))
            Post(CardJson("card", c));
    }

    private void Teardown()
    {
        _vm.Messages.CollectionChanged -= OnMessagesChanged;
        _vm.PropertyChanged -= OnVmChanged;
        ThemeManager.Changed -= OnThemeChanged;
        ForgetAll();
        if (_web != null) { _host.Children.Remove(_web); _web.Dispose(); _web = null; }
        _ready = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
        _setActive(false);
    }
}
