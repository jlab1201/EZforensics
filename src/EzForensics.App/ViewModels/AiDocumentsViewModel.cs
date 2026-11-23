using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Learn;
using EzForensics.Core.Assistant;
using EzForensics.Core.Setup;

namespace EzForensics.App.ViewModels;

/// <summary>
/// The "AI" entry of the Tools page: AI status and "Set up local AI…". The documents the assistant searches (adding, review, sources)
/// live on the Learn page; the "Manage knowledge in Learn" button goes there.
/// </summary>
public sealed partial class AiDocumentsViewModel : ObservableObject
{
    private readonly Func<bool> _spectreConnected;
    private readonly Action<string, InfoSeverity> _onMessage;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private string _message = "";
    /// <summary>Ollama is installed (the Tools list shows the entry as set up).</summary>
    [ObservableProperty] private bool _localAiInstalled;

    public AiDocumentsViewModel(Func<bool> spectreConnected, Action<string, InfoSeverity> onMessage)
    {
        _spectreConnected = spectreConnected;
        _onMessage = onMessage;
    }

    private static AppServices S => AppServices.Current;

    /// <summary>Re-reads the status line (the first time the page is shown, and after the setup ran).</summary>
    public async Task RefreshAsync()
    {
        string local;
        try
        {
            var st = await AiHardware.ProbeOllamaAsync(S.CreateHttpClient());
            LocalAiInstalled = st.Installed;
            local = st.Installed ? (st.Running ? "installed, running" : "installed, not running") : "not installed";
        }
        catch (Exception) { local = "unknown"; }
        var s = S.Settings;
        AiStatus = $"Provider: {s.AiProvider}   Model: {(s.AiModel.Length == 0 ? "not chosen" : s.AiModel)}   Local AI (Ollama): {local}   Spectre engine: {(_spectreConnected() ? "connected" : "not connected")}";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SetUpLocalAiAsync()
    {
        IsBusy = true;
        var ctx = SynchronizationContext.Current;
        try
        {
            var summary = await LocalAiRunner.RunAsync(S.CreateHttpClient, S.Settings, S.SaveSettings, t => ctx?.Post(_ => ProgressText = t, null));
            Message = summary;
            _onMessage(summary, InfoSeverity.Success);
        }
        catch (OperationCanceledException) { Message = "Cancelled."; }
        catch (Exception ex) { Message = "Local AI setup failed: " + ex.Message; AppLog.Error("Local AI setup failed", ex); }
        finally { IsBusy = false; ProgressText = ""; }
        await RefreshAsync();
    }

    [RelayCommand]
    private void ManageInLearn() => S.Navigate<LearnViewModel>();

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => SetUpLocalAiCommand.NotifyCanExecuteChanged();
}
