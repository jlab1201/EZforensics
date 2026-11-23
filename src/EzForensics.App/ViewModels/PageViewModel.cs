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

    // ---- what Spectre (the app-wide assistant) knows about this page

    /// <summary>Which page this is for Spectre's skills index and page-scoped tools.</summary>
    public virtual Core.Assistant.SpectrePage AssistantPage => Core.Assistant.SpectrePage.Any;

    /// <summary>Whether Spectre (launcher, pop-up, dock, window) shows on this page; only Analyze and Explore do.</summary>
    public virtual bool ShowsAssistant => false;

    /// <summary>A few lines on what the page shows now (sent to Spectre in &lt;app_state&gt; after "Page: ..."); null = nothing to add.</summary>
    public virtual string? AssistantState() => null;

    /// <summary>
    /// Actions Spectre may propose on this page (Proposal-kind tools); <paramref name="host"/> shows their cards. Every page offers
    /// app_go_to_page; pages add their own and call the base.
    /// </summary>
    public virtual IEnumerable<Core.Assistant.IAssistantTool> AssistantTools(Core.Assistant.IAssistantHost host)
    {
        yield return Core.Assistant.SpectrePageTools.Create(Core.Assistant.SpectrePageTools.GoToPageSpec, host, (id, input) =>
        {
            var name = input["page"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var n) ? n.Trim() : "";
            if (PageType(name) is not { } target) return null;
            var back = GetType();
            return new Core.Assistant.PageActionProposal(id, $"Open {PageTitle(name)}", "",
                () => { if (name.Equals("jobs", StringComparison.OrdinalIgnoreCase)) S.ShowJobs(); else if ((name.Equals("inbox", StringComparison.OrdinalIgnoreCase) || name.Trim().Equals("to review", StringComparison.OrdinalIgnoreCase))) S.ShowLearnInbox(); else S.Navigate(target); return Task.FromResult<string?>(null); },
                () => { S.Navigate(back); return Task.FromResult<string?>(null); });
        }, input => PageType(input["page"]?.ToString() ?? "") == null
            ? "Unknown page. Pages: " + string.Join(", ", Core.Assistant.SpectrePageTools.Pages) + "." : null);
    }

    /// <summary>The view model type of a page named as app_go_to_page names it (null = unknown).</summary>
    private static Type? PageType(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ingest" or "triage" or "jobs" => typeof(TriageViewModel),
        "analyze" or "results" => typeof(ResultsViewModel),
        "explore" => typeof(Explore.ExploreViewModel),
        "report" or "findings" => typeof(Findings.FindingsViewModel),
        "ai" or "learn" or "intake" or "inbox" or "to review" or "brain" or "graph" or "library" => typeof(Learn.LearnViewModel),
        "tools" => typeof(ToolsViewModel),
        "options" or "settings" => typeof(SettingsViewModel),
        _ => null,
    };

    private static string PageTitle(string name) =>
        Core.Assistant.SpectrePageTools.Pages.FirstOrDefault(p => p.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? name;

    /// <summary>Runs <paramref name="read"/> on the UI thread (Spectre's tools run on a background thread).</summary>
    protected static T OnUi<T>(Func<T> read)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        return d == null || d.CheckAccess() || !d.Thread.IsAlive || d.HasShutdownStarted ? read() : d.Invoke(read);   // a dispatcher whose thread ended (test host) would block forever
    }

    public void ShowError(string context, Exception ex)
    {
        AppLog.Error(context, ex);
        Banner = new InfoBarModel($"{context}: {ErrorText(ex)}", InfoSeverity.Error);
    }

    /// <summary>The error as the banner shows it; a page may replace the raw message with friendlier words.</summary>
    protected virtual string ErrorText(Exception ex) => ex.Message;

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

    // ------------------------------------------------------------ Load demo case (Ingest's start state, Review's no-case state)

    /// <summary>The demo case is copied and its process tree built (a few seconds at most).</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(LoadDemoCaseCommand))] private bool _isLoadingDemo;

    /// <summary>The synthetic demo ships next to the exe (<c>demo-case\</c>): the button is shown.</summary>
    public bool IsDemoAvailable => Core.Case.DemoCase.IsAvailable();

    /// <summary>Copies the demo to a new case folder, opens it as the case and shows it on Review.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadDemoCase))]
    private async Task LoadDemoCaseAsync()
    {
        IsLoadingDemo = true;
        try
        {
            await GuardAsync(async () =>
            {
                await S.LoadDemoCaseAsync();
                S.Navigate<Findings.FindingsViewModel>();
                OnDemoCaseLoaded();
            }, "Could not load the demo case");
        }
        finally { IsLoadingDemo = false; }
    }

    private bool CanLoadDemoCase() => !IsLoadingDemo;

    /// <summary>After Load demo case opened the case (the shell has switched to Review unless it was already there).</summary>
    protected virtual void OnDemoCaseLoaded() { }

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
