using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EzForensics.App.Views.Learn;

/// <summary>
/// The Brain tab: the graph page (Assets\brain) in a locked-down WebView2 plus the WPF side panel. Only https://brain.local/ (mapped
/// to the local folder) is reachable; downloads, new windows, permissions and context menus are off. The view only carries messages:
/// <see cref="BrainViewModel"/> decides what they mean and ignores anything that does not come from https://brain.local/.
/// The WebView2 is created when the view is loaded and disposed when it is unloaded (page change).
/// </summary>
public partial class BrainView : UserControl
{
    // The WebView2 (and the page with its scripts) outlives the view: leaving the page only detaches it, coming back re-attaches it, so
    // the graph is there at once instead of starting WebView2 and loading three.js again (3-5 s). Only one Brain view exists at a time.
    private static WebView2? s_web;
    private static readonly SharedInit<CoreWebView2Environment> s_env = new(CreateEnvironmentAsync);
    private static Task? s_start;   // the one start of the shared WebView2 (EnsureCoreWebView2 + lockdown + navigate), shared by every caller
    private static BrainView? s_owner;
    private static BrainViewModel? s_webVm;   // the view model the page's graph belongs to
    private static bool s_pageLoaded;
    private static string? s_lastGraph;   // the last graph message the live page was sent

    private WebView2? _web;
    private BrainViewModel? _vm;
    private bool _starting;
    private bool _pageLoaded { get => s_pageLoaded; set => s_pageLoaded = value; }

    public BrainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as BrainViewModel);
        Loaded += async (_, _) => await StartAsync();
        Unloaded += (_, _) => Teardown();
    }

    /// <summary>Folder of the page next to the exe.</summary>
    public static string PageFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "brain");

    private void Attach(BrainViewModel? vm)
    {
        if (_vm != null) { _vm.PostToPage -= Post; _vm.RetryRequested -= OnRetry; }
        _vm = vm;
        if (_vm != null) { _vm.PostToPage += Post; _vm.RetryRequested += OnRetry; }
    }

    private async Task StartAsync()
    {
        if (_starting || _web != null) return;
        _starting = true;
        WebView2? web = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_vm != null) _vm.PageProblem = null;   // a new try: an earlier failure no longer hides the graph
            if (s_start is { IsCompleted: false } running)
            {
                try { await running; } catch { /* the start's own caller reports it; we look again below */ }
                if (!IsLoaded) return;
            }
            if (s_web is { CoreWebView2: not null } && (s_webVm == _vm || s_webVm == null))
            {
                // reuse the live page: nothing to start, the graph is already drawn
                s_owner = this;
                s_webVm = _vm;
                _web = s_web;
                DetachFromParent(s_web);
                WebHost.Children.Add(s_web);
                ThemeManager.Changed += OnThemeChanged;
                _vm?.PageStarting(alreadyReady: s_pageLoaded);
                if (s_pageLoaded) { _vm?.PostTheme(); _vm?.ResyncPage(s_lastGraph); }
                AppLog.Info($"Brain: page re-attached in {sw.ElapsedMilliseconds} ms");
                return;
            }
            DisposeShared();   // a page for another view model (or none): start fresh
            _vm?.PageStarting(alreadyReady: false);
            var folder = PageFolder;
            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                ShowPlaceholder("The graph page is missing from the install folder (Assets\\brain). Reinstall EzForensics.");
                return;
            }
            try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex)
            {
                AppLog.Warn("WebView2 runtime not available for the Brain: " + ex.Message);
                ShowPlaceholder("The Microsoft WebView2 Runtime is not installed, so the graph cannot be drawn. To review and Library still work.");
                return;
            }
            var env = await s_env.GetAsync();
            AppLog.Info($"Brain: WebView2 environment ready in {sw.ElapsedMilliseconds} ms");
            if (!IsLoaded) return;
            web = new WebView2 { DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(ThemeManager.Current == Core.Settings.AppTheme.Dark ? "#0b0f1a" : "#eef1f7") };
            WebHost.Children.Add(web);
            _web = web;
            s_web = web; s_owner = this; s_webVm = _vm;
            var start = s_start = StartPageAsync(web, env, folder);
            await start;
            AppLog.Info($"Brain: WebView2 started in {sw.ElapsedMilliseconds} ms");
            if (_web != web) return;   // unloaded meanwhile (the control stays alive for the next visit)
            ThemeManager.Changed += OnThemeChanged;
        }
        catch (Exception ex)
        {
            // leaving the page while WebView2 was still starting disposes it: that is not a failure to show
            if (!IsLoaded || (web != null && _web != web)) { AppLog.Info("Brain: start abandoned (view unloaded): " + ex.Message); return; }
            AppLog.Error("Could not start the Brain", ex);
            Teardown(dispose: true);
            s_env.Reset();   // Retry builds a fresh environment too
            ShowPlaceholder("The graph could not be started (" + ex.GetType().Name + ": " + ex.Message + "). Choose Retry to try again.");
        }
        finally { _starting = false; }
    }

    private static bool IsAllowed(string? uri) => BrainViewModel.IsOwnPage(uri);

    private static void Lockdown(CoreWebView2 core, CoreWebView2Environment env, string folder)
    {
        core.SetVirtualHostNameToFolderMapping(BrainViewModel.Host, folder, CoreWebView2HostResourceAccessKind.Deny);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (IsAllowed(e.Request.Uri)) return;
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        core.NavigationStarting += (_, e) => { if (!IsAllowed(e.Uri)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        var s = core.Settings;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
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

    private static void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // the page outlives the view: messages go to the view that has it right now (none = ignored; the page asks again when re-attached)
        if (s_owner is { } owner) owner.HandleWebMessage(e);
        else if (s_web != null && IsAllowed(e.Source) && e.WebMessageAsJson.Contains("\"ready\"")) s_pageLoaded = true;   // prewarmed: the view takes it from here
    }

    // ------------------------------------------------------------ start helpers

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userData = Path.Combine(AppPaths.DataRoot, "webview2", "brain");
        Directory.CreateDirectory(userData);
        return await CoreWebView2Environment.CreateAsync(null, userData);
    }

    private static async Task StartPageAsync(WebView2 web, CoreWebView2Environment env, string folder)
    {
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        Lockdown(core, env, folder);
        core.WebMessageReceived += OnWebMessage;
        // the page's own files come from the app folder; a disk cache from an older app version could serve a stale brain.js
        try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); }
        catch (Exception ex) { AppLog.Info("Brain: could not clear the page cache: " + ex.Message); }
        core.Navigate($"https://{BrainViewModel.Host}/index.html");
    }

    private static void DetachFromParent(WebView2 web)
    {
        if (web.Parent is Panel p) p.Children.Remove(web);
        else if (web.Parent is ContentControl cc) cc.Content = null;
    }

    /// <summary>Starts the WebView2 environment (the slow part) in the background after startup, so opening Learn only has to create
    /// the control. The control itself is never created or hosted outside the Learn page.</summary>
    public static async Task PrewarmAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(PageFolder, "index.html"))) return;
            try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return; }
            await s_env.GetAsync();
            AppLog.Info("Brain: WebView2 environment prewarmed");
        }
        catch (Exception ex) { AppLog.Warn("Brain: prewarm failed: " + ex.Message); }
    }

    private async void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (_vm == null) return;
            if (IsAllowed(e.Source)) _pageLoaded = true;   // our page is up: messages to it can flow
            var json = e.WebMessageAsJson;
            if (IsDropMessage(json))
            {
                await _vm.HandleDropAsync(e.Source, DroppedPaths(e));
                return;
            }
            await _vm.HandlePageMessageAsync(e.Source, json);
        }
        catch (Exception ex) { AppLog.Error("Brain: a page message failed", ex); }
    }

    private static bool IsDropMessage(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String && t.GetString() == "drop";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>The files and folders the page sent with the drop message (WebView2 turns them into paths).</summary>
    private static List<string> DroppedPaths(CoreWebView2WebMessageReceivedEventArgs e)
    {
        var paths = new List<string>();
        var objects = e.AdditionalObjects;
        if (objects == null) return paths;
        foreach (var o in objects)
        {
            var p = o switch { CoreWebView2File f => f.Path, CoreWebView2FileSystemHandle h => h.Path, _ => null };
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
        }
        return paths;
    }

    private void Post(string json)
    {
        if (!_pageLoaded || _web?.CoreWebView2 is not { } core) return;   // the page asks for everything again when it is ready
        if (json.StartsWith("{\"type\":\"graph\"", StringComparison.Ordinal)) s_lastGraph = json;
        try { core.PostWebMessageAsJson(json); }
        catch (Exception ex) { AppLog.Warn("Brain: could not post a message: " + ex.Message); }
    }

    /// <summary>Retry after a failure: throw the old WebView2 away and start cleanly.</summary>
    private async void OnRetry()
    {
        try
        {
            Teardown(dispose: true);
            await StartAsync();
        }
        catch (Exception ex) { AppLog.Error("Brain: retry failed", ex); }
    }

    private void OnThemeChanged(AppTheme _) => _vm?.PostTheme();

    private void ShowPlaceholder(string reason)
    {
        if (_vm != null) _vm.PageProblem = reason;
    }

    /// <summary>Leaving the page detaches the WebView2 and keeps it; <paramref name="dispose"/> (a failure) throws it away.</summary>
    private void Teardown(bool dispose = false)
    {
        ThemeManager.Changed -= OnThemeChanged;
        var web = _web;
        _web = null;
        if (web == null) return;
        WebHost.Children.Remove(web);
        if (ReferenceEquals(s_owner, this)) s_owner = null;
        if (dispose) { DisposeShared(); _vm?.PageLost(); }
    }

    private static void DisposeShared()
    {
        var web = s_web;
        s_start = null; s_web = null; s_webVm = null; s_pageLoaded = false; s_lastGraph = null; s_owner = null;
        if (web == null) return;
        try
        {
            if (web.CoreWebView2 is { } core) core.WebMessageReceived -= OnWebMessage;
            DetachFromParent(web);
            web.Dispose();
        }
        catch (Exception ex) { AppLog.Warn("Brain: could not dispose the page: " + ex.Message); }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        _vm.Search(SearchBox.Text);
        e.Handled = true;
    }
}
