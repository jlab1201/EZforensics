using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Views;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App.Services;

/// <summary>
/// "Use local AI": finds the installed Ollama, starts it when it is stopped, downloads the model that suits this PC when it is missing and points
/// the assistant at it. EzForensics never installs Ollama: when it is missing, <see cref="OllamaMissing"/> is set and the UI shows the note with
/// a link to ollama.com and "Check again". One instance for the whole app: Options > AI, Tools > AI and the Spectre chat card share the busy
/// state, status, Cancel and log link, and a second start while a run is going attaches to it. The provider and model always go to the live app
/// settings (saved at once); screens that edit a copy reload from them when <see cref="Finished"/> is raised. Property changes are raised on the UI thread.
/// </summary>
public sealed partial class LocalAiService : ObservableObject
{
    /// <summary>What the confirmation asks: the model to download and, when Ollama already has a usable chat model, that one.</summary>
    public sealed record Ask(string Title, string Text, string DownloadModel, string? ExistingModel);

    public enum Answer { Cancel, Download, UseExisting }

    public const string ConfirmTitle = "Download the local AI model";
    public const string SpeedNote = "Also turns on two Ollama speed settings for your user: flash attention and an 8-bit cache.";

    private readonly Func<HttpClient> _http;
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Func<Ask, Answer> _confirm;
    private readonly Action<Action> _ui;
    private readonly Func<AppSettings, Action, ILocalAiOps> _opsFactory;
    private readonly Func<LocalAiRecommendation> _recommend;
    private readonly Action<string> _openUrl;
    private Task<string?>? _task;
    private CancellationTokenSource? _cts;

    /// <param name="confirm">The question before a download. Null = the app dialog: "Download X" / "Use Y" (when Ollama has a chat model) / Cancel; closing it is Cancel (it marshals to the UI thread itself).</param>
    /// <param name="ui">Runs an action on the UI thread. Null = <see cref="AppServices.OnUi"/>.</param>
    /// <param name="opsFactory">Test seam: (live settings, save) -> machine operations. Null = <see cref="OllamaOps"/>.</param>
    /// <param name="recommend">Test seam for the hardware check. Null = RAM and GPUs of this PC.</param>
    /// <param name="openUrl">Test seam. Null = the default browser.</param>
    public LocalAiService(Func<HttpClient> http, AppSettings settings, Action save, Func<Ask, Answer>? confirm = null, Action<Action>? ui = null,
        Func<AppSettings, Action, ILocalAiOps>? opsFactory = null, Func<LocalAiRecommendation>? recommend = null, Action<string>? openUrl = null)
    {
        _http = http; _settings = settings; _save = save;
        _confirm = confirm ?? AskWithDialog;
        _ui = ui ?? AppServices.OnUi;
        _opsFactory = opsFactory ?? ((s, sv) => new OllamaOps(_http, s, sv));
        _recommend = recommend ?? (() => LocalAiAdvisor.Recommend(AiHardware.TotalRamBytes(), AiHardware.Gpus()));
        _openUrl = openUrl ?? SetupLauncher.OpenUrl;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenLog))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _status = "";

    /// <summary>Ollama is not on this PC: show <see cref="OllamaNotInstalledException.Note"/>, the download link and "Check again".</summary>
    [ObservableProperty] private bool _ollamaMissing;

    /// <summary>The log of the last failure (the app log); null until one failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenLog))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogCommand))]
    private string? _lastLogPath;

    /// <summary>First line of the last failure's message; null after a success, a cancel or before any run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenLog))]
    private string? _lastError;

    /// <summary>Show the "Open log" link: the last run failed and there is a log to open.</summary>
    public bool ShowOpenLog => !IsRunning && LastError != null && LastLogPath != null;

    /// <summary>Raised on the UI thread when a started run ends (true = the model is ready). Not raised when the confirmation was declined or Ollama is missing.
    /// Each handler is called on its own: one that throws is logged and changes nothing.</summary>
    public event Action<bool>? Finished;

    private static Answer AskWithDialog(Ask a)
    {
        if (a.ExistingModel == null)
            return Dialogs.Confirm(a.Title, a.Text, "Download", "Cancel") ? Answer.Download : Answer.Cancel;
        var i = Dialogs.Show(a.Title, a.Text, EzDialogSeverity.Question, null, new[] { "Use " + a.ExistingModel, "Download " + a.DownloadModel, "Cancel" }, 0, 2);
        return i switch { 0 => Answer.UseExisting, 1 => Answer.Download, _ => Answer.Cancel };
    }

    /// <summary>What the confirmation says: the model, its size and notes. Pure, so it can be tested.</summary>
    /// <param name="existingModel">A chat model Ollama already has (offered as "Use ..." instead of the download).</param>
    /// <param name="tuningPending">The two Ollama speed settings are not set yet and this run will turn them on.</param>
    public static string BuildConfirmText(string model, double modelGb, bool recommendLocal, string? reason = null, string? existingModel = null, bool tuningPending = true)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"This downloads the model {model} (about {modelGb:0.#} GB) into Ollama for Spectre, picked for this PC.");
        sb.Append("\n\nThis can take several minutes. You can keep working.");
        if (existingModel != null)
            sb.Append($"\n\nOllama already has {existingModel}. You can use it instead, with no download.");
        if (tuningPending) sb.Append("\n\n" + SpeedNote);
        if (!recommendLocal)
            sb.Append("\n\nThis PC is on the small side for local AI" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason.Trim())
                + "\nThe small model works but is slow. A cloud AI provider (Options > AI) may suit this PC better.");
        return sb.ToString();
    }

    /// <summary>Checks Ollama (starting it when it is installed but stopped), asks before a model download, then gets everything ready. The provider and
    /// model are written to the app settings and saved at once. Returns the summary line, or null when Ollama is missing,
    /// the confirmation was declined, the run was cancelled or it failed (<see cref="Status"/> says which). A run already going is returned as is.</summary>
    public Task<string?> StartAsync()
    {
        if (_task is { IsCompleted: false }) return _task;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _task = tcs.Task;
        _cts = new CancellationTokenSource();   // before the first probe, so Cancel works during "Checking this PC…"
        var ct = _cts.Token;
        SetState(running: true, status: "Checking this PC…", error: null, missing: false);
        _ = RunAsync(tcs, ct);
        return tcs.Task;
    }

    private async Task RunAsync(TaskCompletionSource<string?> tcs, CancellationToken ct)
    {
        ILocalAiOps? ops = null;
        try
        {
            var rec = await Task.Run(_recommend, ct);
            var (model, gb) = LocalAiAdvisor.ModelToInstall(rec);
            ops = _opsFactory(_settings, _save);

            var state = await ops.ProbeAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!LocalAiSetup.IsInstalled(state, ops)) { Missing(tcs); return; }

            IReadOnlyList<string>? tuned = null;
            var started = false;
            if (!state.Running)
            {
                if (!ops.IsLocalServer)
                    throw new InvalidOperationException($"Ollama is not answering on {ops.ServerUrl}. Start it there, then click Check again.");
                SetStatus("Starting Ollama…");
                tuned = ops.ApplyTuning();
                await ops.StartServerAsync(ct);
                started = true;
                ct.ThrowIfCancellationRequested();
                state = await ops.ProbeAsync(ct);
                ct.ThrowIfCancellationRequested();
            }

            if (!state.HasModel(model))
            {
                var existing = LocalAiAdvisor.PickExistingChatModel(state.Models);
                var tuningPending = tuned is not { Count: > 0 } && !OllamaTuning.IsApplied();
                var answer = _confirm(new Ask(ConfirmTitle, BuildConfirmText(model, gb, rec.RecommendLocal, rec.Reason, existing, tuningPending), model, existing));
                if (answer == Answer.Cancel)
                {
                    SetState(running: false, status: "", error: null, missing: false);
                    tcs.SetResult(null);
                    return;
                }
                if (answer == Answer.UseExisting && existing != null) model = existing;
            }

            AppLog.Info("Local AI setup start " + model);
            if (!rec.RecommendLocal) SetStatus(rec.Reason + " Using the small model anyway…");
            var r = await LocalAiSetup.RunAsync(model, ops, p => SetStatus(p.Fraction is { } f ? $"{p.Text} {f:P0}" : p.Text), ct, tuned, started);
            AppLog.Info("Local AI setup end: " + r.Summary);
            SetState(running: false, status: r.Summary, error: null, missing: false);
            RaiseFinished(true);
            tcs.SetResult(r.Summary);
        }
        catch (OllamaNotInstalledException) { Missing(tcs); }
        catch (OperationCanceledException)
        {
            AppLog.Info("Local AI setup cancelled");
            SetState(running: false, status: "Cancelled.", error: null, missing: false);
            RaiseFinished(false);
            tcs.SetResult(null);
        }
        catch (Exception ex)
        {
            var first = FirstLine(ex.Message);
            var log = AppLog.LogPath;
            AppLog.Error($"Local AI setup FAILED: {first} (log: {log})", ex);
            SetState(running: false, status: "Local AI setup failed: " + first, error: first, missing: false, log: log);
            RaiseFinished(false);
            tcs.SetResult(null);
        }
        finally
        {
            (ops as IDisposable)?.Dispose();
            var c = _cts;
            _cts = null;
            c?.Dispose();
        }
    }

    private void Missing(TaskCompletionSource<string?> tcs)
    {
        AppLog.Info("Local AI setup: Ollama is not installed");
        SetState(running: false, status: OllamaNotInstalledException.Note, error: null, missing: true);
        tcs.SetResult(null);
    }

    /// <summary>Stops a run that is going; a model download just stops (Ollama keeps what it already has).</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Opens ollama.com/download in the default browser.</summary>
    [RelayCommand]
    private void OpenDownload()
    {
        try { _openUrl(OllamaNotInstalledException.DownloadUrl); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Status = "Could not open the browser: " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanOpenLog))]
    private void OpenLog()
    {
        try { ShellOps.OpenFile(LastLogPath); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "Could not open the log: " + ex.Message;
        }
    }

    private bool CanOpenLog() => LastLogPath != null;

    private static string FirstLine(string? message)
    {
        var line = (message ?? "").Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? "unknown error";
    }

    private void SetStatus(string text) => _ui(() => Status = text);

    private void SetState(bool running, string? status, string? error, bool missing, string? log = null)
    {
        _ui(() =>
        {
            if (log != null) LastLogPath = log;
            LastError = error;
            OllamaMissing = missing;
            if (status != null) Status = status;
            IsRunning = running;
        });
    }

    /// <summary>Calls each handler on its own: one that throws is logged and does not stop the others or change the outcome.</summary>
    private void RaiseFinished(bool success) => _ui(() =>
    {
        var handlers = Finished;
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<bool>>())
        {
            try { h(success); }
            catch (Exception ex) { AppLog.Warn("A Local AI finished handler failed: " + ex.Message); }
        }
    });
}
