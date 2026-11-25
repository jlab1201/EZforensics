using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App.ViewModels;

public sealed record AiProviderOption(string Id, string Name);

/// <summary>Options > AI: provider, model list, API key (saved with DPAPI, never shown) and a connection test.</summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    public static IReadOnlyList<AiProviderOption> Providers { get; } = AiProviders.All.Select(p => new AiProviderOption(p.Id, p.Name)).ToList();

    private readonly Func<AppSettings> _edit;
    private readonly ApiKeyStore _keys;
    private readonly Func<HttpClient> _http;

    /// <summary>The app-wide "Use local AI" run (shared with Tools > AI and the chat card).</summary>
    public LocalAiService LocalAi { get; }

    public AiSettingsViewModel(Func<AppSettings> edit, ApiKeyStore keys, Func<HttpClient> http, LocalAiService? localAi = null)
    {
        _edit = edit; _keys = keys; _http = http;
        LocalAi = localAi ?? new LocalAiService(http, new AppSettings(), () => { });
        LocalAi.PropertyChanged += OnLocalAiChanged;
        LocalAi.Finished += OnLocalAiFinished;
        RefreshKeyState();
    }

    public ObservableCollection<string> Models { get; } = new();

    /// <summary>The provider takes an API key (the row also shows when the key is optional, as for Other).</summary>
    [ObservableProperty] private bool _needsKey;
    /// <summary>Show the Base URL row (Other, Ollama, OpenAI).</summary>
    [ObservableProperty] private bool _showBaseUrl;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    /// <summary>Local provider and Ollama's speed settings (<see cref="OllamaTuning"/>) not set yet: Options shows "Speed up Ollama".</summary>
    [ObservableProperty] private bool _canTuneOllama;

    private string Provider => _edit().AiProvider;
    private string _lastProvider = "";

    /// <summary>Call when the provider changed: remember the old provider's model, restore the new one's, reset the key state and load the model list in the background.
    /// <paramref name="loading"/> = the form was just (re)loaded, so the model already belongs to the current provider.</summary>
    public void OnProviderChanged(bool loading = false)
    {
        var settings = _edit();
        if (!loading && _lastProvider != Provider)
            settings.AiModel = AiModelLists.Switch(settings.AiModelByProvider, _lastProvider, settings.AiModel, Provider);
        _lastProvider = Provider;
        Models.Clear();
        Status = "";
        RefreshKeyState();
        _ = RefreshModelsAsync();
    }

    private void RefreshKeyState()
    {
        NeedsKey = AiProviders.ShowsKey(Provider);
        ShowBaseUrl = AiProviders.Find(Provider)?.BaseUrlEditable == true;
        CanTuneOllama = Provider == "ollama" && !OllamaTuning.IsApplied();
        KeyStatus = _keys.Has(Provider) ? "Key saved" : "No key";
    }

    public void SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _keys.Save(Provider, key);
        RefreshKeyState();
        _ = RefreshModelsAsync();
    }

    /// <summary>Sets Ollama's speed settings (user environment variables, no admin); Ollama reads them when it next starts.</summary>
    [RelayCommand]
    private void TuneOllama()
    {
        try
        {
            OllamaTuning.Apply();
            CanTuneOllama = false;
            Status = "Done. Quit Ollama from its tray icon and start it again to use the faster settings.";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Status = "Could not save the Ollama settings for this Windows user.";
        }
    }

    /// <summary>Raised after a "Use local AI" run changed the live settings (provider, model): Options reloads its form fields from them.</summary>
    public event Action? LiveSettingsChanged;

    /// <summary>Options > AI > "Use local AI": Ollama if installed, plus the model that suits this PC. The provider and model are
    /// applied to the app settings and saved at once (like Tools > AI and the chat card); the form then shows them.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLocalAi))]
    private async Task UseLocalAiAsync()
    {
        var summary = await LocalAi.StartAsync();
        Status = summary ?? LocalAi.Status;
    }

    private void OnLocalAiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalAiService.Status)) Status = LocalAi.Status;
        else if (e.PropertyName == nameof(LocalAiService.IsRunning)) UseLocalAiCommand.NotifyCanExecuteChanged();
    }

    private void OnLocalAiFinished(bool success)
    {
        if (!success) return;
        LiveSettingsChanged?.Invoke();
        RefreshKeyState();
        _ = RefreshModelsAsync();
    }

    private bool CanUseLocalAi() => !LocalAi.IsRunning;

    [RelayCommand]
    private void OpenPromptFiles()
    {
        try
        {
            var dir = PromptStore.OverrideDir;
            Directory.CreateDirectory(dir);
            if (!Directory.EnumerateFiles(dir, "README*").Any())
                File.WriteAllText(Path.Combine(dir, "README.txt"), "To change what Spectre is told, copy a file from the install folder's prompts folder (next to EzForensics.exe) into this folder and edit the copy. Delete it to go back to the built-in text. Restart the app after editing.\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = "Could not open the prompt files folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveLastRequest()
    {
        var json = AssistantChatViewModel.Current?.LastRequestJson;
        if (string.IsNullOrEmpty(json)) { Status = "No request yet"; return; }
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var path = Path.Combine(AppPaths.LogsDir, "spectre-last-request.md");
            File.WriteAllText(path, "# Last request sent to the model\r\n\r\nSaved " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                ". The API key is sent as a header and is not part of this file.\r\n\r\n```json\r\n" + json + "\r\n```\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = "Could not save the request: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearKey()
    {
        _keys.Delete(Provider);
        RefreshKeyState();
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        var provider = Provider;
        if (AiProviders.NeedsKey(provider) && !_keys.Has(provider))
        {
            Models.Clear();
            Status = "Enter an API key to list models";
            return;
        }
        var ids = await ListAsync(showCount: false);
        if (Provider != provider) return;
        Models.Clear();
        if (ids == null) return; // Status holds the error
        foreach (var id in ChatModels(provider, ids).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Models.Add(id);
        var settings = _edit();
        if (!Models.Contains(settings.AiModel, StringComparer.OrdinalIgnoreCase) && PickDefault(provider, Models) is { } pick)
            settings.AiModel = pick;
    }

    /// <summary>The models to offer for chatting: Ollama also lists its embedding models, which cannot chat.</summary>
    public static IEnumerable<string> ChatModels(string provider, IEnumerable<string> ids)
        => provider == "ollama" ? ids.Where(LocalAiAdvisor.IsChatModel) : ids;

    /// <summary>The model chosen when none is set (or the saved one is gone): Anthropic's default if offered, else the first.</summary>
    public static string? PickDefault(string provider, IEnumerable<string> ids)
    {
        var list = ChatModels(provider, ids).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (provider == "anthropic" && list.Contains(AnthropicDefaults.Model)) return AnthropicDefaults.Model;
        return list.FirstOrDefault();
    }

    /// <summary>Lists the provider's models (see <see cref="AiProviders.ListModelsAsync"/>).</summary>
    public static Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, string provider, string baseUrl, string? key, CancellationToken ct)
        => AiProviders.ListModelsAsync(http, provider, baseUrl, key, ct);

    [RelayCommand]
    private async Task TestAsync() => await ListAsync(showCount: true);

    private async Task<IReadOnlyList<string>?> ListAsync(bool showCount)
    {
        var provider = Provider;
        var settings = _edit();
        var key = _keys.TryLoad(provider);
        if (AiProviders.NeedsKey(provider) && key == null) { Status = "Save an API key first."; return null; }
        IsBusy = true;
        Status = "Working…";
        try
        {
            using var http = _http();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ids = await ListModelsAsync(http, provider, settings.AiBaseUrl, key, cts.Token);
            Status = showCount ? $"OK — {ids.Count} model{(ids.Count == 1 ? "" : "s")}" : ids.Count == 0 ? "No models found." : "";
            return ids;
        }
        catch (Exception ex) when (ex is AssistantModelException or UriFormatException or HttpRequestException or OperationCanceledException)
        {
            Status = Friendly(provider, Or(settings.AiBaseUrl, OllamaDefaults.BaseUrl), ex, key);
            return null;
        }
        finally { IsBusy = false; }
    }

    private static string Or(string v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;

    /// <summary>One short line for the user; the key (if the server echoed it) is removed.</summary>
    public static string Friendly(string provider, string ollamaUrl, Exception ex, string? key)
    {
        if (ex is OperationCanceledException) return "The request timed out.";
        if (ex is UriFormatException) return "The base URL is not valid.";
        var msg = ex.Message;
        if (provider == "ollama" && (msg.Contains("could not reach", StringComparison.OrdinalIgnoreCase) || ex is HttpRequestException))
        {
            var host = Uri.TryCreate(ollamaUrl, UriKind.Absolute, out var u) ? u.Authority : "localhost:11434";
            return $"Ollama isn't running on {host}";
        }
        if (!string.IsNullOrEmpty(key)) msg = msg.Replace(key, "…");
        msg = msg.ReplaceLineEndings(" ");
        return msg.Length > 160 ? msg[..160] : msg;
    }
}
