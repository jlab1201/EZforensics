namespace EzForensics.Core.Assistant;

/// <summary>
/// Ollama server settings that keep Spectre's 16k context in GPU memory: flash attention and an 8-bit KV cache. Without them, on an
/// 8 GB GPU, part of qwen3:8b runs on the CPU and a reply with thinking took 72.6 s; with them 27.0 s (all in VRAM). They are user
/// environment variables (no admin) read by Ollama when it starts, so Ollama must be restarted afterwards.
/// </summary>
public static class OllamaTuning
{
    public static IReadOnlyDictionary<string, string> Recommended { get; } = new Dictionary<string, string>
    {
        ["OLLAMA_FLASH_ATTENTION"] = "1",
        ["OLLAMA_KV_CACHE_TYPE"] = "q8_0",
    };

    /// <summary>True when every recommended variable is set (user scope by default) to its value. Any value the user chose is kept.</summary>
    public static bool IsApplied(Func<string, string?>? getUser = null)
    {
        getUser ??= name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return Recommended.All(kv => !string.IsNullOrEmpty(getUser(kv.Key)));
    }

    /// <summary>Sets the recommended variables that are not set yet (user scope by default); a variable the user already set is left alone.</summary>
    /// <returns>The names that were set.</returns>
    public static IReadOnlyList<string> Apply(Func<string, string?>? getUser = null, Action<string, string>? setUser = null)
    {
        getUser ??= name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        setUser ??= (name, value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        var set = new List<string>();
        foreach (var (name, value) in Recommended)
        {
            if (!string.IsNullOrEmpty(getUser(name))) continue;
            setUser(name, value);
            set.Add(name);
        }
        return set;
    }
}
