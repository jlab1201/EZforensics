using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EzForensics.Core.Settings;

namespace EzForensics.App.Services;

/// <summary>
/// Makes the native title bar follow the app theme (DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+/11, 19 on older 10
/// builds; dark = 1, light = 0). <see cref="Register"/> hooks every window of the app; best effort, never throws.
/// </summary>
public static class TitleBarTheme
{
    private static bool _registered;

    /// <summary>Call once at startup: every window gets the title bar of the current theme and follows later theme changes.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
        {
            if (s is Window w && ReferenceEquals(e.OriginalSource, w)) Attach(w);
        }));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Hooked = new();

    /// <summary>Applies the current theme to <paramref name="window"/> and keeps it in sync until it closes.</summary>
    public static void Attach(Window window)
    {
        if (Hooked.TryGetValue(window, out _)) return;
        Hooked.Add(window, new object());
        void OnChanged(AppTheme _) => Apply(window);
        void Hook()
        {
            Apply(window);
            ThemeManager.Changed += OnChanged;
            window.Closed += (_, _) => ThemeManager.Changed -= OnChanged;
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook();
        else window.SourceInitialized += (_, _) => Hook();
    }

    /// <summary>Sets the title bar of <paramref name="window"/> to the current theme (needs a created window handle).</summary>
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = ThemeManager.Current == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch (Exception) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
