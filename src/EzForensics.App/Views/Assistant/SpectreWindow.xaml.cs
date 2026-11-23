using System.ComponentModel;
using System.Windows;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views.Assistant;

/// <summary>
/// The Spectre window ("Open in window" mode): a normal resizable window owned by the main window (never topmost) that hosts one chat
/// panel. Its own title-bar close collapses Spectre to the icon; the mode stays "window". Size and position are remembered.
/// </summary>
public partial class SpectreWindow : Window
{
    private readonly AppSettings _settings;
    internal bool ClosedByProgram;

    internal SpectreWindow(AssistantChatViewModel chat, AppSettings settings, Window? owner)
    {
        _settings = settings;
        InitializeComponent();
        Owner = owner;
        DataContext = chat;
        Placement(owner);
    }

    /// <summary>Saved bounds if they are still on a screen, otherwise beside the main window's right edge.</summary>
    private void Placement(Window? owner)
    {
        var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
        double w = _settings.AssistantWindowWidth, h = _settings.AssistantWindowHeight;
        if (w >= MinWidth && h >= MinHeight)
        {
            w = Math.Min(w, vw); h = Math.Min(h, vh);
            double l = _settings.AssistantWindowLeft, t = _settings.AssistantWindowTop;
            // keep at least a 120x60 corner of it inside the virtual screen
            bool visible = l + w - 120 > vl && l + 120 < vl + vw && t >= vt && t + 60 < vt + vh;
            if (visible) { Left = l; Top = t; Width = w; Height = h; return; }
            Width = w; Height = h;
        }
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height);
        if (owner is { WindowState: WindowState.Normal } && owner.IsVisible)
        {
            Left = Math.Min(Math.Max(area.Left, owner.Left + owner.ActualWidth - Width - 24), area.Right - Width);
            Top = Math.Min(Math.Max(area.Top, owner.Top + 80), area.Bottom - Height);
        }
        else
        {
            Left = area.Right - Width - 24;
            Top = area.Top + 60;
        }
    }

    /// <summary>Remembers the normal (not maximized) bounds; the caller saves the settings file.</summary>
    internal void RememberBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.Width < MinWidth || b.Height < MinHeight || double.IsNaN(b.Left) || double.IsNaN(b.Top)) return;
        _settings.AssistantWindowLeft = b.Left; _settings.AssistantWindowTop = b.Top;
        _settings.AssistantWindowWidth = b.Width; _settings.AssistantWindowHeight = b.Height;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        RememberBounds();
        base.OnClosing(e);
    }
}

/// <summary>The one Spectre window of the app: shown while Spectre is open in window mode, closed when it collapses or the mode changes.</summary>
internal static class SpectreWindowHost
{
    private static SpectreWindow? s_win;
    private static bool s_ownerClosing;

    public static bool IsShowing => s_win != null;

    /// <summary>Shows the window for <paramref name="chat"/> (once; an open window is left where it is).</summary>
    public static void Show(AssistantChatViewModel chat, AppSettings settings)
    {
        if (s_win != null) return;
        var owner = Application.Current?.MainWindow;
        if (owner == null) return;
        if (!owner.IsLoaded)
        {
            // Spectre was open in a window at the last exit and the main window is not ready yet: try again when idle
            owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (s_win == null && owner.IsLoaded && chat.IsExpanded && chat.IsWindowMode) Show(chat, settings);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        owner.Closing -= OnOwnerClosing;
        owner.Closing += OnOwnerClosing;
        var win = s_win = new SpectreWindow(chat, settings, owner);
        win.Closed += (_, _) =>
        {
            var byProgram = win.ClosedByProgram;
            if (s_win == win) s_win = null;
            Save();
            // the examiner closed it with the title-bar X (not a mode change, not the app exiting): collapse to the icon, mode stays "window"
            if (!byProgram && !s_ownerClosing) chat.IsExpanded = false;
        };
        win.Show();
    }

    /// <summary>Closes the window if it is open.</summary>
    public static void Hide()
    {
        if (s_win is not { } w) return;
        w.ClosedByProgram = true;
        w.Close();
    }

    private static void OnOwnerClosing(object? sender, CancelEventArgs e) => s_ownerClosing = !e.Cancel;

    private static void Save()
    {
        try { AppServices.Current?.SaveSettings(); }
        catch (Exception ex) { AppLog.Warn("Could not save the Spectre window position: " + ex.Message); }
    }
}
