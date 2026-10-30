using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EzForensics.App.Services;

/// <summary>Native title-bar tweaks for secondary windows.</summary>
public static class WindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_DLGMODALFRAME = 0x1;
    private const int WM_SETICON = 0x80;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Removes the application icon from the window's title bar (no icon, no system-menu icon). Call from the constructor.</summary>
    public static void HideIcon(Window w)
    {
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(w);
        else w.SourceInitialized += (_, _) => Apply(w);
    }

    private static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)(GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() | WS_EX_DLGMODALFRAME));
        SendMessage(h, WM_SETICON, IntPtr.Zero, IntPtr.Zero);
        SendMessage(h, WM_SETICON, (IntPtr)1, IntPtr.Zero);
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }
}
