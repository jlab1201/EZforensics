using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Assistant;
using EzForensics.Core.Setup;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "AI" entry of the Tools page: AI status and "Use local AI". The documents the assistant searches (adding, review, sources)
/// live on the Learn page; the "Manage knowledge in Learn" button goes there.
/// </summary>
public sealed partial class AiDocumentsViewModel : ObservableObject
{
    private readonly Func<bool> _spectreConnected;
    private readonly Action<string, InfoSeverity> _onMessage;
    private readonly Func<Task<OllamaState>>? _probe;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private string _message = "";
    /// <summary>Ollama is installed (the Tools list shows the entry as set up).</summary>
    [ObservableProperty] private bool _localAiInstalled;

    /// <summary>The app-wide "Use local AI" run (shared with Options > AI and the chat card).</summary>
    public LocalAiService LocalAi { get; }

    public AiDocumentsViewModel(Func<bool> spectreConnected, Action<string, InfoSeverity> onMessage, LocalAiService? localAi = null, Func<Task<OllamaState>>? probe = null)
    {
        _spectreConnected = spectreConnected;
        _onMessage = onMessage;
        _probe = probe;
        LocalAi = localAi ?? AppServices.Current?.LocalAi ?? new LocalAiService(() => new System.Net.Http.HttpClient(), new EzForensics.Core.Settings.AppSettings(), () => { });
        LocalAi.PropertyChanged += OnLocalAiChanged;
        LocalAi.Finished += OnLocalAiFinished;
    }

    private static AppServices S => AppServices.Current;

    /// <summary>Re-reads the status line (the first time the page is shown, and after the setup ran).</summary>
    public async Task RefreshAsync()
    {
        string local;
        try
        {
            var st = _probe != null ? await _probe() : await ProbeAsync();
            LocalAiInstalled = st.Installed;
            local = st.Installed ? (st.Running ? "installed, running" : "installed, not running") : "not installed";
        }
        catch (Exception) { local = "unknown"; }
        var s = S?.Settings ?? new EzForensics.Core.Settings.AppSettings();
        AiStatus = $"Provider: {s.AiProvider}   Model: {(s.AiModel.Length == 0 ? "not chosen" : s.AiModel)}   Local AI (Ollama): {local}   Spectre engine: {(_spectreConnected() ? "connected" : "not connected")}";
    }

    /// <summary>Ollama on the configured address (Options > AI base URL, OLLAMA_HOST, else localhost).</summary>
    private static async Task<OllamaState> ProbeAsync()
    {
        var s = S.Settings;
        var root = OllamaEndpoint.Resolve(s.AiProvider, s.AiBaseUrl, Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        using var http = S.CreateHttpClient();
        return await AiHardware.ProbeOllamaAsync(http, apiRoot: root);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UseLocalAiAsync()
    {
        var summary = await LocalAi.StartAsync();
        if (summary != null) { Message = summary; _onMessage(summary, InfoSeverity.Success); }
        else Message = LocalAi.Status;
    }

    private void OnLocalAiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalAiService.Status)) ProgressText = LocalAi.IsRunning ? LocalAi.Status : "";
        else if (e.PropertyName == nameof(LocalAiService.IsRunning))
        {
            IsBusy = LocalAi.IsRunning;
            if (!LocalAi.IsRunning) { ProgressText = ""; Message = LocalAi.Status; }
        }
    }

    private void OnLocalAiFinished(bool success)
    {
        if (success) _ = RefreshAsync();
    }

    [RelayCommand]
    private void ManageInLearn() => S.Navigate<LearnViewModel>();

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => UseLocalAiCommand.NotifyCanExecuteChanged();
}
