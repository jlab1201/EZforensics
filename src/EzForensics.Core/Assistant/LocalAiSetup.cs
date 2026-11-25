using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.Core.Assistant;

/// <summary>A status line for the user and, when known, how far the current download is (0..1).</summary>
public readonly record struct LocalAiProgress(string Text, double? Fraction);

/// <summary>What <see cref="LocalAiSetup"/> needs from the machine. The real one is <see cref="OllamaOps"/>; tests use a fake.</summary>
public interface ILocalAiOps
{
    Task<OllamaState> ProbeAsync(CancellationToken ct);
    /// <summary>ollama.exe when it is on disk (per-user install folder or PATH), else null.</summary>
    string? FindExe() => null;
    /// <summary>Sets the recommended user variables (flash attention, 8-bit KV cache); returns the names that were set.</summary>
    IReadOnlyList<string> ApplyTuning();
    /// <summary>The Ollama server address in use (no trailing slash).</summary>
    string ServerUrl => AiHardware.OllamaApiRoot;
    /// <summary>The server is on this PC (loopback): only then can the app start Ollama.</summary>
    bool IsLocalServer => true;
    /// <summary>Starts the installed Ollama (never kills a running one) and waits until the API answers.</summary>
    Task StartServerAsync(CancellationToken ct);
    Task PullModelAsync(string model, Action<LocalAiProgress> progress, CancellationToken ct);
    /// <summary>Makes <paramref name="model"/> the assistant's model on the Ollama provider and saves the settings.</summary>
    void UseModel(string model);
}

/// <summary>Ollama is not on this PC. EzForensics does not install it; the UI tells the user to get it from ollama.com.</summary>
public sealed class OllamaNotInstalledException : Exception
{
    public const string DownloadUrl = "https://ollama.com/download";
    public const string Note = "Local AI needs Ollama (a separate, free app). Install it from ollama.com, then click Check again.";
    public OllamaNotInstalledException() : base(Note) { }
}

public sealed record LocalAiSetupResult(string Summary, IReadOnlyList<string> Skipped);

/// <summary>Uses an installed Ollama: starts it if it is stopped, downloads the model if missing, then points the assistant at it.</summary>
public static class LocalAiSetup
{
    /// <summary>Whether Ollama is on this PC (the probe says so, the API answers, or ollama.exe is on disk).</summary>
    public static bool IsInstalled(OllamaState state, ILocalAiOps ops) => state.Installed || state.Running || ops.FindExe() != null;

    /// <summary>The line the summary gets when Ollama's speed settings were just turned on.</summary>
    public const string TuningNote = "Also turned on two Ollama speed settings for your user: flash attention and an 8-bit cache.";

    /// <param name="tunedBefore">Variables the caller already set while starting Ollama itself.</param>
    /// <param name="startedHere">The caller just started Ollama, so it already has the speed settings.</param>
    public static async Task<LocalAiSetupResult> RunAsync(string model, ILocalAiOps ops, Action<LocalAiProgress>? progress = null, CancellationToken ct = default,
        IReadOnlyList<string>? tunedBefore = null, bool startedHere = false)
    {
        void Say(string t, double? f = null) => progress?.Invoke(new LocalAiProgress(t, f));
        var skipped = new List<string>();

        Say("Checking for Ollama…");
        var state = await ops.ProbeAsync(ct).ConfigureAwait(false);
        if (!IsInstalled(state, ops)) throw new OllamaNotInstalledException();
        if (!state.Running && !ops.IsLocalServer)
            throw new InvalidOperationException($"Ollama is not answering on {ops.ServerUrl}. Start it there, then click Check again.");

        ct.ThrowIfCancellationRequested();
        var tuned = (tunedBefore ?? Array.Empty<string>()).Concat(ops.ApplyTuning()).ToList();

        if (!state.Running)
        {
            Say("Starting Ollama…");
            await ops.StartServerAsync(ct).ConfigureAwait(false);
            startedHere = true;
        }
        else skipped.Add("Ollama is already running.");

        ct.ThrowIfCancellationRequested();
        var after = await ops.ProbeAsync(ct).ConfigureAwait(false);
        if (!after.HasModel(model))
        {
            Say($"Downloading {model}…", 0);
            await ops.PullModelAsync(model, p => progress?.Invoke(p), ct).ConfigureAwait(false);
        }
        else skipped.Add($"{model} is already downloaded.");

        ops.UseModel(model);
        Say("Done.", 1);
        var summary = $"Local AI is ready: Spectre will use {model} through Ollama.";
        if (tuned.Count > 0)
        {
            summary += " " + TuningNote;
            if (!startedHere) summary += " They apply the next time Ollama starts.";
        }
        return new LocalAiSetupResult(summary, skipped);
    }
}

/// <summary>The real machine operations behind <see cref="LocalAiSetup"/>. One HTTP client for the whole run; dispose the instance when the run is over.</summary>
public sealed class OllamaOps : ILocalAiOps, IDisposable
{
    private readonly Func<HttpClient> _http;
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly string _root;
    private HttpClient? _client;

    /// <param name="http">Creates a client; called once, the instance owns and disposes it.</param>
    /// <param name="serverUrl">Ollama's server root. Null = <see cref="OllamaEndpoint.Resolve"/> from the settings and OLLAMA_HOST.</param>
    public OllamaOps(Func<HttpClient> http, AppSettings settings, Action save, string? serverUrl = null)
    {
        _http = http; _settings = settings; _save = save;
        _root = (serverUrl ?? OllamaEndpoint.Resolve(settings.AiProvider, settings.AiBaseUrl, Environment.GetEnvironmentVariable("OLLAMA_HOST"))).TrimEnd('/');
    }

    private HttpClient Http => _client ??= _http();

    public void Dispose() { _client?.Dispose(); _client = null; }

    public string ServerUrl => _root;
    public bool IsLocalServer => OllamaEndpoint.IsLoopback(_root);

    public Task<OllamaState> ProbeAsync(CancellationToken ct) => AiHardware.ProbeOllamaAsync(Http, apiRoot: _root, ct: ct);

    public string? FindExe() => AiHardware.FindOllamaExe();

    /// <summary>How long to wait for the API after Ollama was started.</summary>
    public static TimeSpan ServerStartTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The tray app of the install (it starts and supervises <c>ollama serve</c>): per-user folder, then %ProgramFiles%\Ollama.</summary>
    public static string? FindOllamaAppExe(string? localAppData = null, string? programFiles = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { localAppData is { Length: > 0 } l ? Path.Combine(l, "Programs") : null, programFiles })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Ollama", "ollama app.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public IReadOnlyList<string> ApplyTuning() => OllamaTuning.Apply();

    public async Task StartServerAsync(CancellationToken ct)
    {
        if (!IsLocalServer) throw new InvalidOperationException($"Ollama is not answering on {_root}. Start it there, then click Check again.");
        var sw = Stopwatch.StartNew();
        async Task<bool> Answers() => await AiHardware.ListModelsAsync(Http, _root, ct).ConfigureAwait(false) != null;

        if (!await Answers().ConfigureAwait(false))
        {
            // Ollama already starting (its tray app was just opened): give it a moment before starting a second one.
            if (IsOllamaProcessRunning())
            {
                var grace = TimeSpan.FromSeconds(Math.Min(30, ServerStartTimeout.TotalSeconds / 2));
                while (sw.Elapsed < grace && !await Answers().ConfigureAwait(false)) await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (!await Answers().ConfigureAwait(false))
            {
                ProcessStartInfo psi;
                if (FindOllamaAppExe() is { } app) psi = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
                else
                {
                    var exe = AiHardware.FindOllamaExe() ?? throw new InvalidOperationException("ollama.exe was not found.");
                    psi = new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true };
                }
                // The tuning variables were just set for the user; this process may predate them, so pass them along.
                foreach (var kv in OllamaTuning.Recommended)
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(kv.Key))) psi.Environment[kv.Key] = kv.Value;
                Process.Start(psi)?.Dispose();
            }
        }
        while (!await Answers().ConfigureAwait(false))
        {
            if (sw.Elapsed > ServerStartTimeout)
                throw new TimeoutException($"Ollama did not answer on {_root} within {(int)ServerStartTimeout.TotalSeconds} seconds. Start Ollama from the Start menu, then click Check again.");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static bool IsOllamaProcessRunning()
    {
        foreach (var name in new[] { "ollama app", "ollama" })
        {
            var ps = Process.GetProcessesByName(name);
            try { if (ps.Length > 0) return true; }
            finally { foreach (var p in ps) p.Dispose(); }
        }
        return false;
    }

    public async Task PullModelAsync(string model, Action<LocalAiProgress> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _root + "/api/pull")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var layers = new Dictionary<string, (long total, long done)>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;
            var update = ParsePullLine(line, layers);
            if (update.Error != null) throw new InvalidOperationException($"Ollama could not download {model}: {update.Error}");
            progress(new LocalAiProgress(update.Status ?? $"Downloading {model}…", update.Fraction));
        }
    }

    /// <summary>One NDJSON line of /api/pull. Fraction is completed / total over every layer seen so far.</summary>
    public static (string? Status, string? Error, double? Fraction) ParsePullLine(string line, Dictionary<string, (long total, long done)> layers)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err)) return (null, err.GetString() ?? "unknown error", null);
            string? status = r.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (r.TryGetProperty("digest", out var d) && d.GetString() is { } digest && r.TryGetProperty("total", out var t) && t.TryGetInt64(out var total))
            {
                long done = r.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                layers[digest] = (total, done);
            }
            long sumT = layers.Values.Sum(v => v.total), sumD = layers.Values.Sum(v => v.done);
            double? frac = sumT > 0 ? Math.Min(0.99, sumD / (double)sumT) : null;
            return (status, null, frac);
        }
        catch (JsonException) { return (null, null, null); }
    }

    public void UseModel(string model)
    {
        _settings.AiProvider = "ollama";
        _settings.AiModel = model;
        _settings.AiModelByProvider["ollama"] = model;
        _save();
    }
}
