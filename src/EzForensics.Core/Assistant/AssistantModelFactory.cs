namespace EzForensics.Core.Assistant;

/// <summary>Builds the <see cref="IModelClient"/> for the provider chosen in Options → AI.</summary>
public static class AssistantModelFactory
{
    public const string OpenAiBaseUrl = "https://api.openai.com/v1/";
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1/";
    /// <summary>Output room for OpenAI-compatible chat (Ollama, OpenAI). Generous on purpose: thinking models (qwen3) spend tokens before answering, and answer length is set by the prompt, not by cutting replies off.</summary>
    public const int ChatMaxTokens = 8192;
    /// <summary>
    /// Context window asked of Ollama. Its own default is 4096 on an 8 GB GPU, too small for the system prompt, ~15 tools and the case
    /// context, so the rules were cut off. 16k fits qwen3:8b on 8 GB (KV cache ~2.3 GB); Ollama spills to CPU if it must.
    /// </summary>
    public const int OllamaContextTokens = 16384;

    /// <param name="provider">An id from <see cref="AiProviders"/>.</param>
    /// <param name="model">Empty means the provider default (Anthropic only; the others need a chosen model).</param>
    /// <param name="baseUrl">Optional override for Ollama/OpenAI; required for "custom".</param>
    public static IModelClient Create(string provider, string? model, string? baseUrl, string? apiKey, HttpClient http,
        string? systemPrompt = null, bool disableThinking = false)
    {
        systemPrompt ??= AssistantPrompt.System;
        var p = AiProviders.Find(provider) ?? throw new AssistantModelException($"Unknown AI provider '{provider}'. Pick one in Options → AI");
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);
        if (AiProviders.NeedsKey(provider) && !hasKey) throw new AssistantModelException("Add an API key in Options → AI");
        switch (p.Kind)
        {
            case AiProviderKind.Anthropic:
                return new AnthropicModelClient(apiKey!, model, systemPrompt, http: http);
            case AiProviderKind.Ollama:
                return OpenAi(http, AiProviders.ResolveBaseUrl(p, baseUrl), model, null, systemPrompt, OllamaContextTokens, disableThinking);
            default:
                var url = AiProviders.ResolveBaseUrl(p, baseUrl);
                if (string.IsNullOrWhiteSpace(url)) throw new AssistantModelException("Enter the server's Base URL in Options → AI");
                return OpenAi(http, url, model, hasKey ? apiKey!.Trim() : null, systemPrompt);
        }
    }

    private static OpenAiCompatibleModelClient OpenAi(HttpClient http, string url, string? model, string? key, string systemPrompt, int? contextTokens = null, bool disableThinking = false)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new AssistantModelException("Pick a model in Options → AI");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new AssistantModelException("The AI server address is not a valid URL");
        return new OpenAiCompatibleModelClient(http, uri, model.Trim(), key, systemPrompt, ChatMaxTokens, contextTokens, disableThinking);
    }

}
