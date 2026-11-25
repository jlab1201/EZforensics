using System.Text.Json;

namespace EzForensics.Core.Setup;

public sealed record GpuInfo(string Name, long VramBytes);

/// <summary>Ollama on this PC: exe found, API answering, models already pulled.</summary>
public sealed record OllamaState(bool Installed, bool Running, IReadOnlyList<string> Models)
{
    public static OllamaState None { get; } = new(false, false, Array.Empty<string>());
    public bool HasModel(string model) =>
        Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)
                     || (!model.Contains(':') && m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Reads RAM, GPUs and Ollama state for the AI recommendation. Every probe can be replaced in tests.</summary>
public static class AiHardware
{
    public const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string OllamaApiRoot = "http://localhost:11434";

    public static long TotalRamBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>One display-class registry entry: driver description plus the raw VRAM values (QWORD or DWORD).</summary>
    public sealed record RegistryGpu(string? Name, object? QwMemorySize, object? MemorySize);

    /// <summary>Turns display-class entries into GPUs (dedicated VRAM, biggest first); skips basic / virtual adapters and unknown sizes.</summary>
    public static IReadOnlyList<GpuInfo> ParseGpus(IEnumerable<RegistryGpu> entries)
    {
        var list = new List<GpuInfo>();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;
            var n = e.Name.Trim();
            if (n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || n.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            long v = ToLong(e.QwMemorySize);
            if (v <= 0) v = ToLong(e.MemorySize);
            if (v <= 0) continue;
            list.Add(new GpuInfo(n, v));
        }
        return list.OrderByDescending(g => g.VramBytes).ToList();
    }

    private static long ToLong(object? o) => o switch
    {
        long l => l,
        ulong u => (long)u,
        int i => i < 0 ? (long)(uint)i : i, // a DWORD read as a signed int
        uint u => u,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
        _ => 0,
    };

    public static IReadOnlyList<RegistryGpu> ReadRegistryGpus()
    {
        var list = new List<RegistryGpu>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls == null) return list;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                try
                {
                    using var k = cls.OpenSubKey(name);
                    if (k == null) continue;
                    list.Add(new RegistryGpu(k.GetValue("DriverDesc") as string, k.GetValue("HardwareInformation.qwMemorySize"), k.GetValue("HardwareInformation.MemorySize")));
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return list;
    }

    public static IReadOnlyList<GpuInfo> Gpus() => ParseGpus(ReadRegistryGpus());

    /// <summary>ollama.exe in the per-user install folder, %ProgramFiles%\Ollama, else on PATH. Null when not found.</summary>
    public static string? FindOllamaExe(Func<string, bool>? fileExists = null, string? localAppData = null, string? pathVar = null, string? programFiles = null)
    {
        fileExists ??= File.Exists;
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var p = Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
            if (fileExists(p)) return p;
        }
        if (!string.IsNullOrEmpty(programFiles))
        {
            var p = Path.Combine(programFiles, "Ollama", "ollama.exe");
            if (fileExists(p)) return p;
        }
        pathVar ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim().Trim('"'), "ollama.exe"); if (fileExists(p)) return p; } catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>GET /api/tags (short timeout): the pulled model names, or null when the server does not answer.</summary>
    public static async Task<IReadOnlyList<string>?> ListModelsAsync(HttpClient http, string apiRoot = OllamaApiRoot, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await http.GetAsync(apiRoot.TrimEnd('/') + "/api/tags", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { } s) names.Add(s);
            return names;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public static async Task<OllamaState> ProbeOllamaAsync(HttpClient http, string? exe = null, string apiRoot = OllamaApiRoot, CancellationToken ct = default)
    {
        exe ??= FindOllamaExe();
        var models = await ListModelsAsync(http, apiRoot, ct).ConfigureAwait(false);
        return new OllamaState(exe != null || models != null, models != null, models ?? (IReadOnlyList<string>)Array.Empty<string>());
    }
}
