using EzForensics.Core.Setup;

namespace EzForensics.Core.Assistant;

public enum LocalAiChoice { Local, LocalSlow, Cloud }

/// <param name="Model">Ollama model to pull (null for Cloud).</param>
/// <param name="DownloadGb">Approximate model download size.</param>
public sealed record LocalAiRecommendation(LocalAiChoice Choice, string? Model, double DownloadGb, string Reason)
{
    /// <summary>Local AI that runs well: worth pre-checking in the wizard.</summary>
    public bool RecommendLocal => Choice == LocalAiChoice.Local;
}

/// <summary>Picks a local model (or advises a cloud provider) from RAM, GPU memory and what Ollama already has.</summary>
public static class LocalAiAdvisor
{
    private const long Gb = 1024L * 1024 * 1024;

    public const string FallbackModel = "qwen3:4b";

    public static LocalAiRecommendation Recommend(SystemCheckResult r) => Recommend(r.TotalRamBytes, r.Gpus);

    /// <summary>The model to install: the recommended one, or the small one when the user wants local AI on a PC that suits a cloud AI better.</summary>
    public static (string Model, double Gb) ModelToInstall(LocalAiRecommendation rec) => rec.Model != null ? (rec.Model, rec.DownloadGb) : (FallbackModel, 2.6);

    public static LocalAiRecommendation Recommend(long ramBytes, IReadOnlyList<GpuInfo> gpus)
    {
        var best = gpus.OrderByDescending(g => g.VramBytes).FirstOrDefault();
        // GPUs report just under their nominal size (8 GB cards read ~7.9 GB), so allow a small margin.
        double vram = best == null ? 0 : best.VramBytes / (double)Gb;
        if (best != null && vram >= 7.5)
            return new(LocalAiChoice.Local, "qwen3:8b", 5.2, $"{best.Name}, {Round(vram)} GB: qwen3:8b runs on the GPU.");
        if (best != null && vram >= 5.5)
            return new(LocalAiChoice.Local, "qwen3:4b", 2.6, $"{best.Name}, {Round(vram)} GB: qwen3:4b runs on the GPU.");
        double ram = ramBytes / (double)Gb;
        string gpuNote = best == null ? "No dedicated GPU found" : $"{best.Name} has only {Round(vram)} GB";
        if (ram >= 15.5)
            return new(LocalAiChoice.LocalSlow, "qwen3:4b", 2.6, $"{gpuNote}: qwen3:4b would run on the CPU and be slow. A cloud AI is recommended.");
        return new(LocalAiChoice.Cloud, null, 0, $"{gpuNote} and {Round(ram)} GB of RAM: a cloud AI is the better fit.");
    }

    /// <summary>A model that can chat: embedding models (nomic-embed-text, bge-m3, all-minilm, ...) are not.</summary>
    public static bool IsChatModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim().ToLowerInvariant();
        var bare = n.Contains('/') ? n[(n.LastIndexOf('/') + 1)..] : n;
        string[] markers = { "embed", "minilm", "bge-", "bge:", "granite-embedding" };
        if (markers.Any(n.Contains)) return false;
        return !bare.StartsWith("bge");
    }

    /// <summary>The chat model to offer when the recommended one is not downloaded: the one with the most parameters in its name ("14b"), else the first.</summary>
    public static string? PickExistingChatModel(IEnumerable<string> installed)
    {
        string? best = null;
        double bestSize = -1;
        foreach (var m in installed.Where(IsChatModel))
        {
            var mt = System.Text.RegularExpressions.Regex.Match(m, @"(?<![\d.])(\d+(?:\.\d+)?)b(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var size = mt.Success && double.TryParse(mt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            if (size > bestSize) { best = m; bestSize = size; }
        }
        return best;
    }

    /// <summary>Text for the chat card: what this PC suits. The small-PC advice comes first, then what Ollama can still do.</summary>
    public static string ChatCardText(LocalAiChoice choice, string baseText) => choice == LocalAiChoice.Local ? baseText
        : "This PC is better suited to a cloud AI. You can still use a local model if Ollama is installed.";

    private static string Round(double gb) => Math.Round(gb).ToString("0");
}
