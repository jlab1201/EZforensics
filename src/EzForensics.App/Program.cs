using EzForensics.App.Services;
using Velopack;

namespace EzForensics.App;

/// <summary>
/// Entry point. Velopack's hook runner must be the very first thing that runs, before WPF starts: when the installer launches
/// the exe with <c>--veloapp-install</c> / <c>--veloapp-uninstall</c> / <c>--veloapp-updated</c> / <c>--veloapp-obsolete</c>, it runs
/// the matching callback and exits the process (no window). For any other arguments (normal start, or the headless job switches
/// <c>--computer-details</c> / <c>--persistence</c>) it returns at once and the app starts normally.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            VelopackApp.Build()
                // Fast callbacks: no UI, at most 15-30 s. Shortcuts ("EZ Forensics" on the desktop and in the Start menu) are
                // created and removed by Velopack itself (vpk pack --shortcuts, named after --packTitle).
                .OnAfterInstallFastCallback(v => AppLog.Info($"Installed EZ Forensics {v} (per-user) into {AppContext.BaseDirectory}"))
                .OnAfterUpdateFastCallback(v => AppLog.Info($"Updated EZ Forensics to {v}"))
                // Uninstall removes only the app folder (%LOCALAPPDATA%\EzForensics). User data lives in %LOCALAPPDATA%\EzForensicsData
                // (settings, downloaded tools, plaso, logs) and is kept: the user deletes it themselves if they want to (docs\SETUP.md).
                .OnBeforeUninstallFastCallback(v => AppLog.Info($"Uninstalling EZ Forensics {v}; user data in {EzForensics.Core.Settings.AppPaths.DataRoot} is kept"))
                .OnFirstRun(v => App.IsFirstRunAfterInstall = true)
                .Run();
        }
        catch (Exception ex)
        {
            // Never let the updater runtime stop the app from starting (portable copy, dev build, odd locator state).
            AppLog.Error("Velopack startup hook failed", ex);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
