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

    private static string Round(double gb) => Math.Round(gb).ToString("0");
}
