using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EzForensics.App.Views.Explore;

/// <summary>
/// CyberChef in a locked-down WebView2: only https://cyberchef.local/ (mapped to the install folder) is reachable,
/// downloads and new windows are blocked. Never reparent this control between windows; dispose it and create a new one
/// (see <see cref="CyberChefHost"/>).
/// </summary>
public partial class CyberChefPanel : UserControl, IDisposable
{
    public static readonly DependencyProperty FolderPathProperty =
        DependencyProperty.Register(nameof(FolderPath), typeof(string), typeof(CyberChefPanel), new PropertyMetadata(null, OnFolderChanged));

    private WebView2? _web;
    private string? _html;
    private bool _started, _disposed;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CyberChefPanel()
    {
        InitializeComponent();
        RuntimeUrlBox.Text = CyberChefLogic.RuntimeDownloadUrl;
        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>CyberChef install folder; defaults to EZFORENSICS_CYBERCHEF_DIR when unset.</summary>
    public string? FolderPath { get => (string?)GetValue(FolderPathProperty); set => SetValue(FolderPathProperty, value); }

    /// <summary>Recipe/input fragment loaded with the first navigation (set before the panel is loaded).</summary>
    public string? InitialFragment { get; set; }

    /// <summary>True once CyberChef is navigated and usable.</summary>
    public bool IsReady => _ready.Task is { IsCompletedSuccessfully: true } t && t.Result;

    /// <summary>Why the panel shows a placeholder, or null.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Raised with the original byte count when <see cref="SendInputAsync"/> had to truncate to the cap.</summary>
    public event Action<long>? InputTruncated;

    private static void OnFolderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { }

    public async Task StartAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        var folder = FolderPath ?? CyberChefLogic.FolderFromEnvironment();
        _html = CyberChefLogic.FindHtml(folder);
        if (folder == null || _html == null)
        {
            ShowPlaceholder(folder == null
                ? "No CyberChef folder is configured. Install CyberChef from the Tools page, or set EZFORENSICS_CYBERCHEF_DIR."
                : $"No CyberChef html file (CyberChef_vX.Y.Z.html) was found in {folder}.", false);
            return;
        }
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex)
        {
            AppLog.Error("WebView2 runtime not available", ex);
            ShowPlaceholder("The Microsoft WebView2 Runtime is not installed, so CyberChef cannot be shown.", true);
            return;
        }

        try
        {
            var userData = Path.Combine(AppPaths.DataRoot, "webview2", "cyberchef");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            WebHost.Children.Add(_web);
            await _web.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            var core = _web.CoreWebView2;
            ConfigureLockdown(core, env, folder);
            await InstallThemeAsync(core);
            ThemeManager.Changed += OnAppThemeChanged;
            var done = new TaskCompletionSource();
            void Nav(object? s, CoreWebView2NavigationCompletedEventArgs a) { core.NavigationCompleted -= Nav; done.TrySetResult(); }
            core.NavigationCompleted += Nav;
            core.Navigate(CyberChefLogic.PageUrl(_html, InitialFragment));
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            _ready.TrySetResult(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start CyberChef", ex);
            ShowPlaceholder("CyberChef could not be started: " + ex.Message, false);
        }
    }

    private string? _themeScriptId;

    /// <summary>The app's current brushes and fonts as CyberChef theme input.</summary>
    internal static CyberChefLogic.ThemeColors CurrentThemeColors()
    {
        var res = Application.Current?.Resources;
        string Hex(string key, string fallback)
        {
            if (res?[key] is System.Windows.Media.SolidColorBrush b)
                return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            return fallback;
        }
        string Font(string key, string fallback) => (res?[key] as System.Windows.Media.FontFamily)?.Source ?? fallback;
        var dark = ThemeManager.Current == AppTheme.Dark;
        return new CyberChefLogic.ThemeColors(dark,
            Hex("Brush.Window", "#1E2128"), Hex("Brush.Surface", "#272B34"), Hex("Brush.SurfaceAlt", "#313642"),
            Hex("Brush.Text", "#E6E8EE"), Hex("Brush.TextSecondary", "#9AA1B0"),
            Hex("Brush.Accent", "#3B82F6"), Hex("Brush.AccentText", "#FFFFFF"), Hex("Brush.Border", "#3E4452"),
            Hex("Brush.Selection", "#2C4A7A"), Hex("Brush.Input", "#1A1D24"), Hex("Brush.Code", "#111318"),
            Font("Font.UI", "Segoe UI Variable Text, Segoe UI"), 13, Font("Font.Mono", "Cascadia Mono, Consolas, Courier New"));
    }

    // Applied to every document before its scripts run (so there is no flash of the default theme) and again to the loaded page.
    private async Task InstallThemeAsync(CoreWebView2 core)
    {
        var script = CyberChefLogic.ApplyThemeScript(CurrentThemeColors());
        if (_themeScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_themeScriptId);
        _themeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        try { await core.ExecuteScriptAsync(script); } catch (Exception) { }
    }

    private async void OnAppThemeChanged(AppTheme _)
    {
        try
        {
            if (_disposed || _web?.CoreWebView2 is not { } core) return;
            await InstallThemeAsync(core);
        }
        catch (Exception ex) { AppLog.Error("Could not re-theme CyberChef", ex); }
    }

    private static void ConfigureLockdown(CoreWebView2 core, CoreWebView2Environment env, string folder)
    {
        core.SetVirtualHostNameToFolderMapping(CyberChefLogic.Host, folder, CoreWebView2HostResourceAccessKind.Deny);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (CyberChefLogic.IsAllowedUri(e.Request.Uri)) return;
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        core.NavigationStarting += (_, e) => { if (!CyberChefLogic.IsAllowedUri(e.Uri)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ContextMenuRequested += (_, e) =>
        {
            var items = e.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var n = items[i].Name ?? "";
                if (n.Contains("link", StringComparison.OrdinalIgnoreCase) || n.Contains("frame", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("inspect", StringComparison.OrdinalIgnoreCase) || n.Contains("webCapture", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("share", StringComparison.OrdinalIgnoreCase) || n.Contains("search", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("saveAs", StringComparison.OrdinalIgnoreCase) || n.Contains("print", StringComparison.OrdinalIgnoreCase))
                    items.RemoveAt(i);
            }
        };
        var s = core.Settings;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsWebMessageEnabled = false;
    }

    private void ShowPlaceholder(string reason, bool showRuntimeLink)
    {
        UnavailableReason = reason;
        ReasonText.Text = reason;
        RuntimeLinkPanel.Visibility = showRuntimeLink ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        _ready.TrySetResult(false);
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e)
    {
        // Fixed, trusted URL (not from evidence or CyberChef content).
        try { Process.Start(new ProcessStartInfo(CyberChefLogic.RuntimeDownloadUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { AppLog.Error("Could not open the WebView2 download page", ex); }
    }

    /// <summary>Puts <paramref name="data"/> into CyberChef's input (recipe is kept). Data above 10 MB is cut and <see cref="InputTruncated"/> raised.</summary>
    public async Task SendInputAsync(byte[] data)
    {
        if (!await _ready.Task || _web?.CoreWebView2 is not { } core) return;
        var capped = CyberChefLogic.ApplyCap(data, out var truncated);
        if (truncated) InputTruncated?.Invoke(data.LongLength);
        var current = CyberChefLogic.UnwrapJsonString(await core.ExecuteScriptAsync(CyberChefLogic.GetFragmentScript));
        await core.ExecuteScriptAsync(CyberChefLogic.SetFragmentScript(CyberChefLogic.WithInput(current, capped)));
    }

    /// <summary>Current recipe + input as a CyberChef URL fragment (no leading '#'); empty if not ready.</summary>
    public async Task<string> GetStateFragmentAsync()
    {
        if (!await _ready.Task || _web?.CoreWebView2 is not { } core) return InitialFragment ?? "";
        return CyberChefLogic.NormalizeFragment(CyberChefLogic.UnwrapJsonString(await core.ExecuteScriptAsync(CyberChefLogic.GetFragmentScript)));
    }

    /// <summary>Loads a fragment from <see cref="GetStateFragmentAsync"/>.</summary>
    public async Task RestoreStateAsync(string fragment)
    {
        if (!_started) { InitialFragment = fragment; return; }
        if (!await _ready.Task || _web?.CoreWebView2 is not { } core) return;
        await core.ExecuteScriptAsync(CyberChefLogic.SetFragmentScript(fragment));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeManager.Changed -= OnAppThemeChanged;
        _ready.TrySetResult(false);
        if (_web != null) { WebHost.Children.Remove(_web); _web.Dispose(); _web = null; }
    }
}
