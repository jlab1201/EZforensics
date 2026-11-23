namespace EzForensics.Core.Assistant;

/// <summary>How a provider is spoken to.</summary>
public enum AiProviderKind { Ollama, Anthropic, OpenAiCompatible }

/// <summary>One provider in Options → AI. <see cref="Id"/> is lowercase and doubles as the ApiKeyStore key.</summary>
public sealed record AiProvider(
    string Id,
    string Name,
    string BaseUrl,
    bool NeedsKey,
    AiProviderKind Kind = AiProviderKind.OpenAiCompatible,
    Func<IEnumerable<string>, IReadOnlyList<string>>? Filter = null,
    IReadOnlyList<string>? Fallback = null,
    bool BaseUrlEditable = false,
    bool KeyOptional = false);

/// <summary>Single source of truth for the AI providers: Options combo, model listing, client factory, display names.</summary>
public static class AiProviders
{
    public const string Custom = "custom";

    private static Func<IEnumerable<string>, IReadOnlyList<string>> Drop(params string[] bad) =>
        ids => Clean(ids.Where(id => !bad.Any(b => id.Contains(b, StringComparison.OrdinalIgnoreCase))));

    private static IReadOnlyList<string> Clean(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<string> GeminiFilter(IEnumerable<string> ids) => Clean(ids
        .Select(id => id.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? id[7..] : id)
        .Where(id => id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                     && !new[] { "embedding", "imagen", "tts", "aqa", "veo" }.Any(b => id.Contains(b, StringComparison.OrdinalIgnoreCase))));

    /// <summary>In combo order.</summary>
    public static IReadOnlyList<AiProvider> All { get; } = new AiProvider[]
    {
        new("ollama", "Local (Ollama)", OllamaDefaults.BaseUrl, false, AiProviderKind.Ollama, BaseUrlEditable: true),
        new("anthropic", "Anthropic", "https://api.anthropic.com/", true, AiProviderKind.Anthropic),
        new("openai", "OpenAI", AssistantModelFactory.OpenAiBaseUrl, true, Filter: AiModelLists.FilterOpenAiChat, BaseUrlEditable: true),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", true, Filter: GeminiFilter, Fallback: new[] { "gemini-2.5-pro", "gemini-2.5-flash" }),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1/", true, Fallback: new[] { "deepseek-chat", "deepseek-reasoner" }),
        new("mistral", "Mistral", "https://api.mistral.ai/v1/", true, Filter: Drop("embed", "moderation", "ocr"), Fallback: new[] { "mistral-large-latest", "mistral-medium-latest", "codestral-latest" }),
        new("xai", "xAI (Grok)", "https://api.x.ai/v1/", true, Filter: Drop("image"), Fallback: new[] { "grok-4", "grok-3-mini" }),
        new("openrouter", "OpenRouter", AssistantModelFactory.OpenRouterBaseUrl, true),
        new("groq", "Groq", "https://api.groq.com/openai/v1/", true, Filter: Drop("whisper", "tts", "guard")),
        new("together", "Together AI", "https://api.together.xyz/v1/", true),
        new("fireworks", "Fireworks AI", "https://api.fireworks.ai/inference/v1/", true),
        new("cerebras", "Cerebras", "https://api.cerebras.ai/v1/", true),
        new("moonshot", "Moonshot (Kimi)", "https://api.moonshot.ai/v1/", true),
        new("qwen", "Alibaba Qwen (Model Studio)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/", true, Fallback: new[] { "qwen-max", "qwen-plus", "qwen-turbo" }),
        new("zai", "Z.ai (GLM)", "https://api.z.ai/api/paas/v4/", true, Fallback: new[] { "glm-4.6", "glm-4.5" }),
        new(Custom, "Other (OpenAI-compatible)", "", true, BaseUrlEditable: true, KeyOptional: true),
    };

    public static AiProvider? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Short name for chat headers and prompts ("Local" for Ollama).</summary>
    public static string DisplayName(string id) => id == "ollama" ? "Local" : Find(id)?.Name ?? id;

    /// <summary>True when a key must be saved before use (unknown ids too). Other takes a key but does not need one.</summary>
    public static bool NeedsKey(string id) => Find(id) is not { } p || (p.NeedsKey && !p.KeyOptional);

    /// <summary>True when the provider shows a key row (needs one or accepts one).</summary>
    public static bool ShowsKey(string id) => Find(id) is { NeedsKey: true };

    /// <summary>The user's override where the provider honours one, else the provider default. Other has no default (returns "").</summary>
    public static string ResolveBaseUrl(AiProvider p, string? userUrl) =>
        p.BaseUrlEditable && !string.IsNullOrWhiteSpace(userUrl) ? userUrl.Trim() : p.BaseUrl;

    /// <summary>Lists chat models. When /models is missing (404) or yields nothing, the provider's fallback list is used; auth and network errors still throw.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, string provider, string? baseUrl, string? key, CancellationToken ct)
    {
        var p = Find(provider) ?? throw new AssistantModelException($"Unknown AI provider '{provider}'. Pick one in Options → AI");
        if (p.Kind == AiProviderKind.Anthropic) return await AnthropicModels.ListAsync(http, key!, ct).ConfigureAwait(false);
        var url = ResolveBaseUrl(p, baseUrl);
        if (string.IsNullOrWhiteSpace(url)) throw new AssistantModelException("Enter the server's Base URL in Options → AI");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new UriFormatException("The base URL is not valid.");
        IReadOnlyList<string> ids;
        try
        {
            ids = await OpenAiCompatibleModelClient.ListModelsAsync(http, uri, string.IsNullOrWhiteSpace(key) ? null : key, ct).ConfigureAwait(false);
            if (p.Filter != null) ids = p.Filter(ids);
        }
        catch (AssistantModelException ex) when (ex.StatusCode == 404 && p.Fallback is { Count: > 0 }) { return p.Fallback; }
        return ids.Count == 0 && p.Fallback is { Count: > 0 } ? p.Fallback : ids;
    }
}
