using System.Net.Http;
using EzForensics.Core.Assistant;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;

namespace EzForensics.App.Services;

/// <summary>"Set up local AI": picks the model that suits this PC and runs the wizard's local AI step. Shared by Options > AI and Tools > AI.</summary>
public static class LocalAiRunner
{
    /// <summary>Returns the summary line. <paramref name="status"/> may be called from any thread; <paramref name="save"/> persists the provider and model.</summary>
    public static async Task<string> RunAsync(Func<HttpClient> http, AppSettings settings, Action save, Action<string> status)
    {
        status("Checking this PC…");
        var rec = await Task.Run(() => LocalAiAdvisor.Recommend(AiHardware.TotalRamBytes(), AiHardware.Gpus()));
        var (model, _) = LocalAiAdvisor.ModelToInstall(rec);
        if (!rec.RecommendLocal) status(rec.Reason + " Installing the small model anyway…");
        var ops = new OllamaOps(http, settings, save);
        var r = await LocalAiSetup.RunAsync(model, ops, p => status(p.Fraction is { } f ? $"{p.Text} {f:P0}" : p.Text));
        return r.Summary;
    }
}
