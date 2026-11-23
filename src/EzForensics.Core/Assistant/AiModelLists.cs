namespace EzForensics.Core.Assistant;

/// <summary>Pure helpers for the Options → AI model picker: chat-model filtering and the per-provider model memory.</summary>
public static class AiModelLists
{
    private static readonly string[] OpenAiChatPrefixes = { "gpt-", "chatgpt", "o1", "o3", "o4" };
    private static readonly string[] OpenAiNotChat = { "audio", "realtime", "transcribe", "tts", "image", "embedding", "moderation", "whisper", "search-preview", "instruct" };

    /// <summary>OpenAI's /models also lists embeddings, speech and image models; keep the chat ones, sorted.</summary>
    public static IReadOnlyList<string> FilterOpenAiChat(IEnumerable<string> ids) => ids
        .Where(id => OpenAiChatPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                     && !OpenAiNotChat.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The provider's saved model if the list offers it (case-insensitive), else the first item, else "".</summary>
    public static string Choose(string saved, IReadOnlyList<string> models)
    {
        if (models.Count == 0) return "";
        return models.FirstOrDefault(m => string.Equals(m, saved, StringComparison.OrdinalIgnoreCase)) ?? models[0];
    }

    /// <summary>Switch from one provider to another: remember <paramref name="currentModel"/> for <paramref name="from"/>, return the model remembered for <paramref name="to"/> ("" if none).</summary>
    public static string Switch(IDictionary<string, string> memory, string from, string currentModel, string to)
    {
        if (!string.IsNullOrEmpty(from) && !string.IsNullOrWhiteSpace(currentModel)) memory[from] = currentModel.Trim();
        return memory.TryGetValue(to, out var m) ? m : "";
    }
}
