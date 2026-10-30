using System.Diagnostics;

namespace EzForensics.Core.Tools;

public static class GuiLauncher
{
    public static Process? Launch(string exePath, IEnumerable<string>? args = null, bool runAsAdmin = false)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        if (args != null) foreach (var a in args) psi.ArgumentList.Add(a);
        if (runAsAdmin) psi.Verb = "runas";
        return Process.Start(psi);
    }
}
