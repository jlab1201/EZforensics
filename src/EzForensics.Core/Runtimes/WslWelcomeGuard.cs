using System.Diagnostics;

namespace EzForensics.Core.Runtimes;

/// <summary>The per-user flag WSL's service checks before it opens the "Welcome to WSL" (wslsettings) first-run window.</summary>
public interface IWslOobeStore
{
    /// <summary>The current DWORD value, or null when it is missing / unreadable.</summary>
    int? Read();
    /// <summary>Sets the value to 1. False when it could not be written.</summary>
    bool MarkComplete();
}

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c>, DWORD <c>OOBEComplete</c>: wslservice.exe reads it and opens the
/// "Welcome to WSL" window (wslsettings.exe) once while it is not 1. Per user, no admin rights.
/// </summary>
public sealed class RegistryWslOobeStore : IWslOobeStore
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    public const string ValueName = "OOBEComplete";
    public const string Display = @"HKCU\" + KeyPath + " " + ValueName;

    public static readonly RegistryWslOobeStore Instance = new();

    public int? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) is int v ? v : null;
        }
        catch { return null; }
    }

    public bool MarkComplete()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            k.SetValue(ValueName, 1, Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>A running process that might be the "Welcome to WSL" window.</summary>
public sealed record WslSettingsProcess(int Id, string Name, DateTime? StartTime, bool HasMainWindow);

public interface IWslSettingsProcesses
{
    IReadOnlyList<WslSettingsProcess> List();
    /// <summary>Asks the process to close its main window (WM_CLOSE; never killed). False when it is gone or has no window.</summary>
    bool CloseMainWindow(int id);
}

public sealed class SystemWslSettingsProcesses : IWslSettingsProcesses
{
    public const string ProcessName = "wslsettings";
    public static readonly SystemWslSettingsProcesses Instance = new();

    public IReadOnlyList<WslSettingsProcess> List()
    {
        var list = new List<WslSettingsProcess>();
        Process[] ps;
        try { ps = Process.GetProcessesByName(ProcessName); } catch { return list; }
        foreach (var p in ps)
            using (p)
            {
                DateTime? start = null;
                bool window = false;
                try { start = p.StartTime; } catch { }
                try { window = p.MainWindowHandle != IntPtr.Zero; } catch { }
                list.Add(new WslSettingsProcess(p.Id, p.ProcessName, start, window));
            }
        return list;
    }

    public bool CloseMainWindow(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return p.CloseMainWindow();
        }
        catch { return false; }
    }
}

/// <summary>
/// Keeps WSL's "Welcome to WSL" first-run window (wslsettings OOBE) from popping up, and staying open, during setup:
/// <see cref="MarkOobeComplete"/> before the first wsl.exe call sets <see cref="RegistryWslOobeStore"/>'s flag; as a fallback,
/// <see cref="CloseWelcomeWindows"/> after the distro / Zeek / RITA steps closes any wslsettings main window that started after
/// setup began (CloseMainWindow, never Kill). Both return the lines for the step log.
/// </summary>
public sealed class WslWelcomeGuard
{
    private readonly IWslOobeStore _store;
    private readonly IWslSettingsProcesses _processes;
    private bool _marked;

    public WslWelcomeGuard(IWslOobeStore store, IWslSettingsProcesses processes, DateTime? since = null)
    {
        _store = store;
        _processes = processes;
        Since = since ?? DateTime.Now;
    }

    /// <summary>The real registry and process list; <see cref="Since"/> = now (when setup begins).</summary>
    public static WslWelcomeGuard CreateDefault() => new(RegistryWslOobeStore.Instance, SystemWslSettingsProcesses.Instance);

    /// <summary>Local time setup began: only wslsettings windows started at or after it are closed.</summary>
    public DateTime Since { get; }

    private static int _processMarked;

    /// <summary>
    /// Once per process, on the real registry: sets the flag before EZ Forensics' own wsl.exe calls outside setup (EzDistro.RunAsync).
    /// </summary>
    public static void MarkOobeCompleteOnce()
    {
        if (Interlocked.Exchange(ref _processMarked, 1) != 0) return;
        try { if (NeedsOobeFlag(RegistryWslOobeStore.Instance.Read())) RegistryWslOobeStore.Instance.MarkComplete(); } catch { }
    }

    /// <summary>The flag must be written unless it is already 1.</summary>
    public static bool NeedsOobeFlag(int? current) => current != 1;

    /// <summary>The wslsettings windows to close: started at or after <paramref name="since"/> and showing a main window.</summary>
    public static IReadOnlyList<WslSettingsProcess> ToClose(IEnumerable<WslSettingsProcess> processes, DateTime since) =>
        processes.Where(p => p.Name.Equals(SystemWslSettingsProcesses.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                             p.HasMainWindow && p.StartTime is { } t && t >= since).ToList();

    /// <summary>Sets OOBEComplete = 1 (once per guard). Returns the log line, or null when there was nothing to do.</summary>
    public string? MarkOobeComplete()
    {
        if (_marked) return null;
        _marked = true;
        if (!NeedsOobeFlag(_store.Read())) return null;
        return _store.MarkComplete()
            ? $"WSL: marked the 'Welcome to WSL' first-run window as already seen ({RegistryWslOobeStore.Display} = 1), so it does not open."
            : $"WSL: could not set {RegistryWslOobeStore.Display} (the 'Welcome to WSL' window may open; it is closed after this step).";
    }

    /// <summary>Closes the wslsettings windows that opened during setup. Returns one log line per window.</summary>
    public IReadOnlyList<string> CloseWelcomeWindows()
    {
        var lines = new List<string>();
        foreach (var p in ToClose(_processes.List(), Since))
            lines.Add(_processes.CloseMainWindow(p.Id)
                ? $"WSL: closed the 'Welcome to WSL' window (wslsettings, process {p.Id}) that opened during setup."
                : $"WSL: the 'Welcome to WSL' window (wslsettings, process {p.Id}) did not close; close it yourself.");
        return lines;
    }
}
