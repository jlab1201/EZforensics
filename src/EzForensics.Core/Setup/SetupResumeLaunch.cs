namespace EzForensics.Core.Setup;

/// <summary>
/// Reopens EZ Forensics by itself after the Windows restart that enabling WSL needs: a per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce</c> value starts <c>EzForensics.exe --resume-setup</c> once at the next
/// sign-in (no admin; Windows deletes the value before running it). The setup wizard opens on its Install page and finishes the
/// pending steps (<see cref="StartupMode.ResumeWizard"/>); the main window opens when it closes. The pending items live in
/// <c>AppSettings.SetupResume</c>, so opening EZ Forensics by hand resumes the same way.
/// </summary>
public static class SetupResumeLaunch
{
    public const string Switch = "--resume-setup";
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string ValueName = "EzForensicsResumeSetup";

    /// <summary>
    /// The command line: <c>"exe" --resume-setup</c>, or <c>"dotnet.exe" "app.dll" --resume-setup</c> for a dev build started through
    /// dotnet. Null when there is no usable exe.
    /// </summary>
    public static string? BuildCommand(string? exe, string? dll = null)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        bool viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (viaDotnet && string.IsNullOrWhiteSpace(dll)) return null;
        return viaDotnet ? $"\"{exe}\" \"{dll}\" {Switch}" : $"\"{exe}\" {Switch}";
    }

    public static bool HasSwitch(IEnumerable<string> args) => args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the RunOnce value (HKCU). <paramref name="keyPath"/> is for tests. Returns false on failure / non-Windows.</summary>
    public static bool Register(string command, string keyPath = RunOnceKey)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            k.SetValue(ValueName, command, Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    public static string? Read(string keyPath = RunOnceKey)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
            return k?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    public static void Unregister(string keyPath = RunOnceKey)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
