using System.Windows;

namespace EzForensics.App.Controls;

/// <summary>Keeps big fixed-size windows inside the screen's work area on small or scaled displays.</summary>
public static class WindowSizing
{
    private const double Margin = 24;

    /// <summary>
    /// Call before Show (e.g. right after InitializeComponent). If the window's Width or Height is larger than the primary work area minus
    /// a 24px margin, shrinks it to fit and centres it on the work area. Minimum sizes are lowered if they would block the shrink.
    /// </summary>
    public static void FitToWorkArea(Window w)
    {
        var wa = SystemParameters.WorkArea;
        var maxW = Math.Max(320, wa.Width - 2 * Margin);
        var maxH = Math.Max(240, wa.Height - 2 * Margin);
        var shrunk = false;
        if (!double.IsNaN(w.Width) && w.Width > maxW) { w.Width = maxW; shrunk = true; }
        if (!double.IsNaN(w.Height) && w.Height > maxH) { w.Height = maxH; shrunk = true; }
        if (w.MinWidth > w.Width) w.MinWidth = w.Width;
        if (w.MinHeight > w.Height) w.MinHeight = w.Height;
        if (!shrunk) return;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = wa.Left + (wa.Width - w.Width) / 2;
        w.Top = wa.Top + (wa.Height - w.Height) / 2;
    }
}
