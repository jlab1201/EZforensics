using System.Windows;

namespace EzForensics.App.Views.Explore;

/// <summary>
/// Owns the CyberChef panel and its popup <see cref="CyberChefWindow"/>. CyberChef is never docked: the window is created on first use,
/// the user closing it only hides it, and the panel (recipe and input) stays alive until the app exits.
/// </summary>
public sealed class CyberChefHost
{
    private CyberChefPanel? _panel;
    private CyberChefWindow? _window;
    private readonly CyberChefLayout _layout;
    private readonly string? _layoutPath;

    public CyberChefHost(string? folderPath = null, string? layoutPath = null)
    {
        FolderPath = folderPath;
        _layoutPath = layoutPath;
        _layout = CyberChefLayout.Load(layoutPath);
    }

    /// <summary>CyberChef install folder; null falls back to EZFORENSICS_CYBERCHEF_DIR.</summary>
    public string? FolderPath { get; set; }

    /// <summary>True while the CyberChef window is on screen.</summary>
    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>Raised when input had to be cut to the 10 MB cap (original length).</summary>
    public event Action<long>? InputTruncated;

    private bool? _knownInstalled;

    /// <summary>Call on each page visit: when CyberChef got installed (or removed) meanwhile, points the host at the folder and rebuilds the panel.</summary>
    public void Refresh(string? folderPath, bool installed)
    {
        var changed = _knownInstalled is bool k && k != installed || !string.Equals(FolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
        _knownInstalled = installed;
        FolderPath = folderPath;
        if (changed && _panel != null) RebuildPanel();
    }

    /// <summary>Shows the CyberChef window (created on first use), or brings it to the front when it is already open.</summary>
    public void Open()
    {
        EnsureWindow();
        var w = _window!;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (!w.IsVisible) w.Show();
        w.Activate();
    }

    /// <summary>Closes the window for good and disposes the panel (app exit).</summary>
    public void Detach()
    {
        if (_window is { } w)
        {
            CaptureWindowBounds();
            _window = null;
            w.ClosingByHost = true;
            w.Panel = null;
            try { w.Close(); } catch (InvalidOperationException) { }
        }
        _panel?.Dispose();
        _panel = null;
    }

    /// <summary>Sends bytes to CyberChef's input, opening the window first if needed.</summary>
    public async Task SendInputAsync(byte[] data)
    {
        Open();
        if (_panel == null) return;
        await _panel.SendInputAsync(data);
    }

    public Task<string> GetStateFragmentAsync() => _panel?.GetStateFragmentAsync() ?? Task.FromResult("");

    private void EnsureWindow()
    {
        if (_window != null) return;
        _panel ??= NewPanel(null);
        var w = _window = new CyberChefWindow { Panel = _panel };
        if (Application.Current?.MainWindow is { IsVisible: true } mw) w.Owner = mw;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyBounds(w);
        w.HiddenByUser += () => { CaptureWindowBounds(); _layout.Save(_layoutPath); };
    }

    // A new panel (CyberChef got installed or moved): the recipe is carried over through the URL fragment where it can be read.
    private async void RebuildPanel()
    {
        try
        {
            var old = _panel;
            var fragment = old != null ? await old.GetStateFragmentAsync() : "";
            if (_panel != old) return;
            var panel = NewPanel(fragment);
            if (_window != null) _window.Panel = panel;
            _panel = panel;
            old?.Dispose();
        }
        catch (Exception) { }
    }

    private CyberChefPanel NewPanel(string? fragment)
    {
        var p = new CyberChefPanel { FolderPath = FolderPath, InitialFragment = fragment };
        p.InputTruncated += n => InputTruncated?.Invoke(n);
        return p;
    }

    private void ApplyBounds(Window w)
    {
        w.Width = Math.Max(w.MinWidth, _layout.Width);
        w.Height = Math.Max(w.MinHeight, _layout.Height);
        if (!double.IsNaN(_layout.Left) && !double.IsNaN(_layout.Top)
            && _layout.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && _layout.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40
            && _layout.Left + _layout.Width > SystemParameters.VirtualScreenLeft + 40
            && _layout.Top >= SystemParameters.VirtualScreenTop)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = _layout.Left;
            w.Top = _layout.Top;
        }
    }

    private void CaptureWindowBounds()
    {
        if (_window is not { } w) return;
        var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
        _layout.Left = b.Left; _layout.Top = b.Top; _layout.Width = b.Width; _layout.Height = b.Height;
    }
}
