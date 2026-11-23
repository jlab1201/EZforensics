using System.Security.Cryptography;
using System.Text;

namespace EzForensics.Core.Assistant;

/// <summary>
/// The environment that gives the Spectre engine the model chosen in Options → AI (its explanations and report narrative).
/// The engine knows four direct kinds: deepseek, anthropic, openai (any OpenAI-compatible URL) and local (no key).
/// Passed to wsl.exe through WSLENV, never on the command line, so the key does not show in process lists.
/// </summary>
public static class SpectreLlmEnv
{
    /// <summary>Variables for <paramref name="provider"/>; empty when there is nothing usable (unknown provider, or a key is needed and missing).</summary>
    public static IReadOnlyDictionary<string, string> For(string provider, string? model, string? baseUrl, string? key)
    {
        var env = new Dictionary<string, string>();
        if (AiProviders.Find(provider) is not { } p) return env;
        bool hasKey = !string.IsNullOrWhiteSpace(key);
        string chat = ChatUrl(AiProviders.ResolveBaseUrl(p, baseUrl));
        if (p.Kind == AiProviderKind.Anthropic)
        {
            if (!hasKey) return env;
            env["SPECTRE_LLM_PROVIDER"] = "anthropic";
            env["ANTHROPIC_API_KEY"] = key!.Trim();
        }
        else if (p.Kind == AiProviderKind.Ollama || (!hasKey && p.Id == AiProviders.Custom))
        {
            if (chat.Length == 0) return env;
            env["SPECTRE_LLM_PROVIDER"] = "local";
            env["SPECTRE_LOCAL_LLM_URL"] = chat;
        }
        else if (p.Id == "deepseek")
        {
            if (!hasKey) return env;
            env["SPECTRE_LLM_PROVIDER"] = "deepseek";
            env["DEEPSEEK_API_KEY"] = key!.Trim();
            env["SPECTRE_DEEPSEEK_URL"] = chat;
        }
        else
        {
            // OpenAI itself and every OpenAI-compatible vendor (Gemini, Mistral, OpenRouter, Groq, …, Other)
            if (!hasKey || chat.Length == 0) return env;
            env["SPECTRE_LLM_PROVIDER"] = "openai";
            env["OPENAI_API_KEY"] = key!.Trim();
            env["SPECTRE_OPENAI_URL"] = chat;
        }
        if (!string.IsNullOrWhiteSpace(model)) env["SPECTRE_LLM_MODEL"] = model.Trim();
        return env;
    }

    /// <summary><paramref name="env"/> plus WSLENV naming each variable, so wsl.exe hands them to the distro (an existing WSLENV is kept).</summary>
    public static IReadOnlyDictionary<string, string> WithWslEnv(IReadOnlyDictionary<string, string> env, string? existingWslEnv)
    {
        if (env.Count == 0) return env;
        var all = new Dictionary<string, string>(env);
        var names = string.Join(":", env.Keys);
        all["WSLENV"] = string.IsNullOrEmpty(existingWslEnv) ? names : existingWslEnv.TrimEnd(':') + ":" + names;
        return all;
    }

    /// <summary>A short fingerprint of the settings (the key only as a hash), so a change starts a new engine.</summary>
    public static string Signature(IReadOnlyDictionary<string, string> env)
    {
        if (env.Count == 0) return "";
        var text = string.Join("\n", env.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static string ChatUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "";
        var b = baseUrl.Trim();
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
        return b.TrimEnd('/') + "/chat/completions";
    }
}
