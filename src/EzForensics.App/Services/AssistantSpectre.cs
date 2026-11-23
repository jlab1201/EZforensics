using System.Text.Json.Nodes;
using EzForensics.Core.Assistant;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Spectre;

namespace EzForensics.App.Services;

/// <summary>Spectre tools connected over MCP, plus what to dispose when the chat ends.</summary>
public sealed record AssistantExtraTools(IReadOnlyList<IAssistantTool> Tools, IAsyncDisposable Owner);

/// <summary>
/// Starts the Spectre MCP server for the assistant and the Findings page. They share ONE connection (started lazily, restarted when the
/// server process died). Setting the environment variable <see cref="EnvVar"/> to a local server executable replaces the private-distro
/// server (<see cref="SpectreMcp.Command"/>) for development.
/// </summary>
public static class AssistantSpectre
{
    public const string EnvVar = "EZFORENSICS_SPECTRE_MCP";
    private static readonly System.Text.RegularExpressions.Regex Judging = new("approve|reject|finding_edit", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool _engineKnown;

    /// <summary>The engine is installed in the ready EZ distro. Only a "yes" is cached for the app run, so installing it later is noticed.</summary>
    private static async Task<string?> FindWslIfEngineReadyAsync(CancellationToken ct)
    {
        var info = await Task.Run(() => EzDistro.Inspect(), ct).ConfigureAwait(false);
        if (!info.Ready || info.WslExe == null) return null;
        if (_engineKnown) return info.WslExe;
        var status = await SpectreEngineTools.ProbeAsync(ct).ConfigureAwait(false);
        if (!status.TryGetValue("spectre", out var s) || !s.Installed) return null;
        _engineKnown = true;
        return info.WslExe;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static McpClient? _shared;
    private static string? _sharedKey;
    private static bool _sharedDev;

    /// <summary>The running server, or a new one. Null when Spectre is not available (no override set, distro not ready or engine not installed).</summary>
    private static int _inFlight;

    /// <summary>
    /// The engine's AI model from Options → AI. A cloud provider is passed only when the examiner agreed to send this case's data to it
    /// (<see cref="AssistantConsent"/>, the same consent as the chat); without it the engine runs without AI, as before.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> LlmEnvFor(string? caseDir)
    {
        try
        {
            if (AppServices.Current?.Settings is not { } s) return new Dictionary<string, string>();
            var provider = s.AiProvider;
            if (provider != "ollama" && (string.IsNullOrEmpty(caseDir) || !AssistantConsent.Has(caseDir, provider, s.AiBaseUrl))) return new Dictionary<string, string>();
            var keys = new ApiKeyStore();
            return SpectreLlmEnv.For(provider, s.AiModel, s.AiBaseUrl, provider == "ollama" ? null : keys.TryLoad(provider));
        }
        catch (Exception ex) { AppLog.Warn("Spectre AI settings: " + ex.Message); return new Dictionary<string, string>(); }
    }

    private static async Task<McpClient?> GetClientAsync(string? caseDir, CancellationToken ct)
    {
        var exe = Environment.GetEnvironmentVariable(EnvVar);
        var dev = !string.IsNullOrWhiteSpace(exe);
        // The dev server keeps its cases under the open case folder, so another case needs another server; the distro engine has one cases dir.
        var llm = LlmEnvFor(caseDir);
        var key = (dev ? caseDir ?? "" : "") + "|" + SpectreLlmEnv.Signature(llm);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shared is { IsClosed: false } && _sharedKey == key && _sharedDev == dev) return _shared;
            // the AI settings or consent changed: never kill a running tool (an analysis run); the new settings apply once it is idle
            if (_shared is { IsClosed: false } && _sharedDev == dev && Volatile.Read(ref _inFlight) > 0) return _shared;
            await CloseSharedAsync().ConfigureAwait(false);
            (string File, IReadOnlyList<string> Args, IReadOnlyList<string> Cancel) cmd;
            if (dev)
            {
                var cases = Path.Combine(string.IsNullOrEmpty(caseDir) ? Path.GetTempPath() : caseDir, ".ezassistant", "spectre-cases");
                Directory.CreateDirectory(cases);
                cmd = SpectreMcp.DevCommand(exe!.Trim(), new[] { "--cases-dir", cases });
            }
            else
            {
                var wsl = await FindWslIfEngineReadyAsync(ct).ConfigureAwait(false);
                if (wsl == null) { AppLog.Info("Spectre engine not connected: the EZ distro is not ready or the engine is not installed."); return null; }
                var c = SpectreMcp.Command(wsl);
                cmd = (c.Exe, c.Args, c.Cancel);
            }
            var (file, args, cancel) = cmd;
            var env = dev ? llm : SpectreLlmEnv.WithWslEnv(llm, Environment.GetEnvironmentVariable("WSLENV"));
            var host = StdioProcessHost.Start(file, args, env.Count == 0 ? null : env, cancel, line => AppLog.Info("spectre-mcp: " + line));
            _shared = await McpClient.ConnectAsync(host, TimeSpan.FromSeconds(60), ct);
            _sharedKey = key; _sharedDev = dev;
            AppLog.Info("Spectre engine connected" + (llm.TryGetValue("SPECTRE_LLM_PROVIDER", out var kind) ? $" (AI: {kind})" : " (no AI model for the engine)"));
            return _shared;
        }
        finally { Gate.Release(); }
    }

    private static async Task CloseSharedAsync()
    {
        var old = _shared; _shared = null;
        if (old != null) { try { await old.DisposeAsync(); } catch (Exception ex) { AppLog.Warn("Closing Spectre failed: " + ex.Message); } }
    }

    /// <summary>Stops the Spectre process (app exit).</summary>
    public static async Task ShutdownAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await CloseSharedAsync(); } finally { Gate.Release(); }
    }

    /// <summary>Calls through the shared connection; starts a new server once when the old one had stopped.</summary>
    private static async Task<McpToolResult> CallAsync(string? caseDir, string name, JsonObject args, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = await GetClientAsync(caseDir, ct).ConfigureAwait(false) ?? throw new IOException("Spectre is not available.");
            Interlocked.Increment(ref _inFlight);
            try { return await client.CallToolAsync(name, args, timeout, ct).ConfigureAwait(false); }
            catch (IOException) when (attempt == 0 && client.IsClosed) { /* stopped between calls: the next loop starts a new server */ }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    /// <summary>The typed findings client on the shared connection (for the Findings page).</summary>
    public static SpectreFindingsClient CreateFindingsClient(Func<string?> caseDir)
    {
        SpectreErrors.Log ??= AppLog.Warn;
        return CreateFindingsClientCore(caseDir);
    }

    private static SpectreFindingsClient CreateFindingsClientCore(Func<string?> caseDir) =>
        new(new SharedCaller(caseDir), caseDir, string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)) ? EzDistro.Name : null);

    /// <summary>
    /// argv that stops the engine's running tool by ending the distro's MCP server (<see cref="SpectreMcp.Command"/>'s Cancel), for the
    /// Spectre analysis job's Cancel. Null in dev mode (<see cref="EnvVar"/>) or when the distro or engine is not ready.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> CancelCommandAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar))) return null;
        try
        {
            var wsl = await FindWslIfEngineReadyAsync(ct).ConfigureAwait(false);
            return wsl == null ? null : SpectreMcp.Command(wsl).Cancel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("Spectre cancel command: " + ex.Message); return null; }
    }

    /// <summary>True when a Spectre server is running or could be started.</summary>
    public static async Task<bool> IsAvailableAsync(string? caseDir, CancellationToken ct)
    {
        try { return await GetClientAsync(caseDir, ct).ConfigureAwait(false) != null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("Spectre unavailable: " + ex.Message); return false; }
    }

    private sealed class SharedCaller : ISpectreToolCaller
    {
        private readonly Func<string?> _caseDir;
        public SharedCaller(Func<string?> caseDir) { _caseDir = caseDir; }
        public Task<McpToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct) =>
            AssistantSpectre.CallAsync(_caseDir(), tool, args, McpAssistantTools.TimeoutFor(tool), ct);
    }

    /// <summary>Nothing to close per chat: the connection is shared and lives until <see cref="ShutdownAsync"/>.</summary>
    private sealed class SharedOwner : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <returns>Null when Spectre is not available (no override set, distro not ready or engine not installed).</returns>
    public static async Task<AssistantExtraTools?> TryConnectAsync(string? caseDir, CancellationToken ct)
    {
        var client = await GetClientAsync(caseDir, ct).ConfigureAwait(false);
        if (client == null) return null;
        // The examiner approves, rejects and edits findings; the assistant never gets those tools.
        var tools = (await client.ListToolsAsync(ct)).Where(t => !Judging.IsMatch(t.Name)).ToList();
        var list = McpAssistantTools.Create((n, a, t, c) => CallAsync(caseDir, n, a, t, c), tools).ToList();
        AppLog.Info($"Spectre engine connected: {list.Count} tools");
        return new AssistantExtraTools(list, new SharedOwner());
    }
}
