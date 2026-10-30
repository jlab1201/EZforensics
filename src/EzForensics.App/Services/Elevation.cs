using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using EzForensics.Core.Triage;

namespace EzForensics.App.Services;

public static class Elevation
{
    private static readonly Lazy<bool> Admin = new(() =>
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    });

    public static bool IsAdministrator => Admin.Value;

    /// <summary>
    /// Relaunches this executable with the runas verb (and <paramref name="args"/>) and shuts down. Returns false if the user declined UAC.
    /// The new process still finds this one's single-instance mutex while it exits: started with <see cref="IngestSessionLaunch.Switch"/> it
    /// waits for it (<see cref="IngestSessionLaunch.MutexWait"/>, App.OnStartup) instead of handing over to this one.
    /// </summary>
    public static bool RestartAsAdministrator(IReadOnlyList<string>? args = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            // When started via "dotnet EzForensics.dll" relaunch the dll through dotnet.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var dll = typeof(Elevation).Assembly.Location;
                psi.ArgumentList.Add(dll);
            }
            foreach (var a in args ?? Array.Empty<string>()) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC cancelled
        }
    }
}
