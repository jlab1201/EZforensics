using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;

namespace EzForensics.App.ViewModels;

public enum InfoSeverity { Info, Success, Warning, Error }

/// <summary>Content of the InfoBar-style banner shown at the top of a page.</summary>
public sealed record InfoBarModel(string Message, InfoSeverity Severity);

/// <summary>Base for every navigable page: banner handling, first-run banner, guarded async helpers.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private InfoBarModel? _banner;
    [ObservableProperty] private bool _showFirstRun;

    protected static AppServices S => AppServices.Current;

    public abstract string Title { get; }

    public virtual void OnNavigatedTo() { }

    public void ShowError(string context, Exception ex)
    {
        AppLog.Error(context, ex);
        Banner = new InfoBarModel($"{context}: {ex.Message}", InfoSeverity.Error);
    }

    public void ShowMessage(string message, InfoSeverity severity = InfoSeverity.Info) =>
        Banner = new InfoBarModel(message, severity);

    [RelayCommand]
    private void DismissBanner() => Banner = null;

    /// <summary>Runs an async action, routing any exception to the banner (cancellation is silent).</summary>
    protected async Task GuardAsync(Func<Task> action, string context)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(context, ex); }
    }

    protected void Guard(Action action, string context)
    {
        try { action(); }
        catch (Exception ex) { ShowError(context, ex); }
    }

    // ------------------------------------------------------------ first-run experience

    protected void UpdateFirstRun() => ShowFirstRun = !S.HasAnyEzTools;

    [RelayCommand]
    private async Task PointToExistingToolsAsync()
    {
        var dir = Dialogs.PickFolder("Select your existing EZ Tools folder", S.Settings.ToolsRoot);
        if (dir == null) return;
        await GuardAsync(async () =>
        {
            S.Settings.ToolsRoot = dir;
            S.SaveSettings();
            await S.RescanAsync();
            if (S.HasAnyEzTools)
                ShowMessage($"Found {S.Locator.All.Count} executables under {dir}.", InfoSeverity.Success);
            else
                ShowMessage($"No EZ tools were found under {dir}. Pick the folder Get-ZimmermanTools downloaded into, or download the tools.", InfoSeverity.Warning);
        }, "Could not use that folder");
    }

    [RelayCommand]
    private void GoDownloadTools()
    {
        // The setup wizard downloads the tools and what they need in one go (Tools stays available for single tools).
        SetupLauncher.Open(System.Windows.Application.Current?.MainWindow);
        OnNavigatedTo();
    }
}
