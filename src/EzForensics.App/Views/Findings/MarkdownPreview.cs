using System.Windows;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EzForensics.App.Views.Findings;

/// <summary>
/// Rendered Markdown (the report preview) in a locked-down WebView2: a static page with no script, every navigation after the page is
/// set is cancelled. The WebView2 starts only when the control is shown with some Markdown; without the runtime the Markdown text is shown.
/// </summary>
public sealed class MarkdownPreview : Grid
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownPreview),
        new PropertyMetadata("", (d, _) => ((MarkdownPreview)d).Refresh()));

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    private readonly TextBox _fallback;
    private WebView2? _web;
    private bool _starting, _failed, _allowNavigation;

    public MarkdownPreview()
    {
        _fallback = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed,
        };
        _fallback.SetResourceReference(StyleProperty, "MonoBox");
        System.Windows.Automation.AutomationProperties.SetName(_fallback, "Report preview (Markdown)");
        Children.Add(_fallback);
        IsVisibleChanged += (_, _) => Refresh();
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        Loaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; ThemeManager.Changed += OnThemeChanged; };
    }

    private void OnThemeChanged(AppTheme _) { _shown = null; Refresh(); }

    private string? _shown;

    private void Refresh()
    {
        _fallback.Text = Markdown ?? "";
        if (!IsVisible || string.IsNullOrEmpty(Markdown)) return;
        if (_failed) { _fallback.Visibility = Visibility.Visible; return; }
        if (_web?.CoreWebView2 is { } core)
        {
            if (_shown == Markdown) return;
            _shown = Markdown;
            _allowNavigation = true;
            core.NavigateToString(ChatHtml.BuildDocument(Markdown, Theme()));
            return;
        }
        if (!_starting) _ = StartAsync();
    }

    private async Task StartAsync()
    {
        _starting = true;
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var userData = Path.Combine(AppPaths.DataRoot, "webview2", "report");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Children.Add(web);
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.NavigationStarting += (_, e) => { if (!_allowNavigation) e.Cancel = true; };
            core.NavigationCompleted += (_, _) => _allowNavigation = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            var s = core.Settings;
            s.AreDevToolsEnabled = false; s.AreDefaultScriptDialogsEnabled = false; s.AreHostObjectsAllowed = false; s.IsScriptEnabled = false;
            s.IsWebMessageEnabled = false; s.IsStatusBarEnabled = false; s.IsGeneralAutofillEnabled = false; s.IsPasswordAutosaveEnabled = false;
            _web = web;
            _fallback.Visibility = Visibility.Collapsed;
            _shown = null;
            Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Report preview: WebView2 is not available, showing the Markdown text: " + ex.Message);
            _failed = true;
            if (_web == null) foreach (var w in Children.OfType<WebView2>().ToList()) { Children.Remove(w); w.Dispose(); }
            _fallback.Visibility = Visibility.Visible;
        }
        finally { _starting = false; }
    }

    private static ChatHtml.Theme Theme()
    {
        var c = Explore.CyberChefPanel.CurrentThemeColors();
        return new ChatHtml.Theme(c.Dark, c.Background, c.Surface, c.SurfaceAlt, c.Text, c.TextSecondary, c.Accent, c.AccentText, c.Border,
            c.Selection, c.Input, c.Code, c.UiFont, c.MonoFont);
    }
}
