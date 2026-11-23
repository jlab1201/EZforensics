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
    /// <summary>Downloads the official installer and runs it per-user and silently.</summary>
    Task InstallOllamaAsync(Action<LocalAiProgress> progress, CancellationToken ct);
    /// <summary>Sets the recommended user variables (flash attention, 8-bit KV cache); returns the names that were set.</summary>
    IReadOnlyList<string> ApplyTuning();
    /// <summary>Starts <c>ollama serve</c> (restarting a running Ollama first when asked) and waits until the API answers.</summary>
    Task StartServerAsync(bool restart, CancellationToken ct);
    Task PullModelAsync(string model, Action<LocalAiProgress> progress, CancellationToken ct);
    /// <summary>Makes <paramref name="model"/> the assistant's model on the Ollama provider and saves the settings.</summary>
    void UseModel(string model);
}

public sealed record LocalAiSetupResult(string Summary, IReadOnlyList<string> Skipped);

/// <summary>Installs Ollama and a model, then points the assistant at it. Steps that are already satisfied are skipped.</summary>
public static class LocalAiSetup
{
    public static async Task<LocalAiSetupResult> RunAsync(string model, ILocalAiOps ops, Action<LocalAiProgress>? progress = null, CancellationToken ct = default)
    {
        void Say(string t, double? f = null) => progress?.Invoke(new LocalAiProgress(t, f));
        var skipped = new List<string>();

        Say("Checking for Ollama…");
        var state = await ops.ProbeAsync(ct).ConfigureAwait(false);
        bool installedNow = false;
        if (!state.Installed)
        {
            Say("Downloading Ollama…", 0);
            await ops.InstallOllamaAsync(p => progress?.Invoke(p), ct).ConfigureAwait(false);
            installedNow = true;
        }
        else skipped.Add("Ollama is already installed.");

        ct.ThrowIfCancellationRequested();
        var tuned = ops.ApplyTuning();

        if (!state.Running || installedNow || tuned.Count > 0)
        {
            Say(state.Running ? "Restarting Ollama…" : "Starting Ollama…");
            // A server that was already up (or the installer started) does not have the new variables yet.
            await ops.StartServerAsync(restart: installedNow || (state.Running && tuned.Count > 0), ct).ConfigureAwait(false);
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
        return new LocalAiSetupResult($"Local AI is ready: Spectre will use {model} through Ollama.", skipped);
    }
}

/// <summary>The real machine operations behind <see cref="LocalAiSetup"/>.</summary>
public sealed class OllamaOps : ILocalAiOps
{
    public const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";
    /// <summary>Authenticode checker for the downloaded installer. Replaceable for tests.</summary>
    public IScriptSignatureVerifier SignatureVerifier { get; init; } = PowerShellSignatureVerifier.Instance;
    private readonly Func<HttpClient> _http;
    private readonly AppSettings _settings;
    private readonly Action _save;

    public OllamaOps(Func<HttpClient> http, AppSettings settings, Action save) { _http = http; _settings = settings; _save = save; }

    public Task<OllamaState> ProbeAsync(CancellationToken ct) => AiHardware.ProbeOllamaAsync(_http(), ct: ct);

    public async Task InstallOllamaAsync(Action<LocalAiProgress> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-ollama-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "OllamaSetup.exe");
        try
        {
            using (var resp = await _http().GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var finalUri = resp.RequestMessage?.RequestUri;
                if (!OllamaInstallerPolicy.IsAllowedDownloadHost(finalUri))
                    throw new InvalidOperationException($"The Ollama download came from an unexpected address ({finalUri?.Host ?? "unknown"}); it was not run.");
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(exe);
                var buf = new byte[81920];
                long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    progress(new LocalAiProgress($"Downloading Ollama ({done / 1048576} MB)…", total is > 0 ? Math.Min(0.99, done / (double)total.Value) : null));
                }
            }
            progress(new LocalAiProgress("Checking the installer's signature…", null));
            var sig = SignatureVerifier.Verify(exe);
            var problem = OllamaInstallerPolicy.SignatureProblem(sig);
            if (problem != null) throw new InvalidOperationException($"The Ollama installer was not run: {problem}.");
            progress(new LocalAiProgress($"Installing Ollama (signed by {sig.SignerSubject})…", null));
            var psi = new ProcessStartInfo(exe, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES") { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("The Ollama installer did not start.");
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0) throw new InvalidOperationException($"The Ollama installer exited with code {p.ExitCode}.");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    public IReadOnlyList<string> ApplyTuning() => OllamaTuning.Apply();

    public async Task StartServerAsync(bool restart, CancellationToken ct)
    {
        if (restart)
        {
            foreach (var name in new[] { "ollama app", "ollama" })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(true); p.WaitForExit(5000); } catch (Exception) { } finally { p.Dispose(); }
        }
        if (await AiHardware.ListModelsAsync(_http(), ct: ct).ConfigureAwait(false) == null)
        {
            var exe = AiHardware.FindOllamaExe() ?? throw new InvalidOperationException("ollama.exe was not found after the install.");
            var psi = new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true };
            // The tuning variables were just set for the user; this process may predate them, so pass them along.
            foreach (var kv in OllamaTuning.Recommended)
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(kv.Key))) psi.Environment[kv.Key] = kv.Value;
            Process.Start(psi)?.Dispose();
        }
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await AiHardware.ListModelsAsync(_http(), ct: ct).ConfigureAwait(false) == null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Ollama did not start within a minute.");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public async Task PullModelAsync(string model, Action<LocalAiProgress> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AiHardware.OllamaApiRoot + "/api/pull")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
