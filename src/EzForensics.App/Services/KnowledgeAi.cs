using EzForensics.Core.Assistant;
using EzForensics.Core.Knowledge;
using EzForensics.Core.Setup;

namespace EzForensics.App.Services;

/// <summary>
/// The AI the knowledge feed labels and distils with: the model chosen in Tools → AI, one fresh request per call (no chat history).
/// Nothing is pinged here: a server that is down shows up as failed labels that can be retried.
/// </summary>
public static class KnowledgeAi
{
    /// <summary>The short task prompt for labelling and distilling; the real instructions are in each request. (Spectre's chat prompt would contradict them.)</summary>
    public const string SystemPrompt = "You label and distill DFIR knowledge notes. Reply with JSON only.";

    /// <summary>The completion, or null when the provider or model is blank or a cloud provider has no key.</summary>
    public static KnowledgeCompletion? Create(out string modelName)
    {
        modelName = "";
        var app = AppServices.Current;
        if (app?.Settings is not { } s) return null;
        var provider = s.AiProvider;
        var model = s.AiModel;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) return null;
        string? key = null;
        if (provider != "ollama")
        {
            key = new ApiKeyStore().TryLoad(provider);
            if (AiProviders.NeedsKey(provider) && string.IsNullOrEmpty(key)) return null;
        }
        var baseUrl = s.AiBaseUrl;
        modelName = model;
        return async (prompt, ct) =>
        {
            using var http = app.CreateHttpClient();
            var client = AssistantModelFactory.Create(provider, model, baseUrl, key, http, SystemPrompt, disableThinking: true);
            var step = await client.SendUserAsync(prompt, Array.Empty<ToolSpec>(), ct).ConfigureAwait(false);
            return string.Join("\n", step.Texts);
        };
    }

    /// <summary>The Ollama server root for the configured base URL ("http://host:11434/v1/" gives "http://host:11434").</summary>
    internal static string OllamaRoot(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return AiHardware.OllamaApiRoot;
        var u = baseUrl.Trim().TrimEnd('/');
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^3];
        return u;
    }

    /// <summary>Is the AI server answering? Only Ollama is probed (GET /api/tags); a cloud provider needs no probe (the settings are enough).</summary>
    public static async Task<bool> ProbeAsync()
    {
        var app = AppServices.Current;
        if (app?.Settings is not { } s || s.AiProvider != "ollama") return true;
        using var http = app.CreateHttpClient();
        return await AiHardware.ListModelsAsync(http, OllamaRoot(s.AiBaseUrl)).ConfigureAwait(false) != null;
    }

    /// <summary>For the view models: the completion and model name now (re-read from the settings each time).</summary>
    public static (KnowledgeCompletion? Complete, string Model) Resolve()
    {
        try { var c = Create(out var m); return (c, m); }
        catch (Exception ex) { AppLog.Warn("Knowledge AI settings: " + ex.Message); return (null, ""); }
    }
}
