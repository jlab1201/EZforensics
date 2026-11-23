using EzForensics.App.Services;
using EzForensics.Core.Knowledge;

namespace EzForensics.App.ViewModels.Learn;

/// <summary>
/// Decides whether the AI actions of the Learn tabs are available: the settings must name a model and, for a local server (Ollama),
/// the server must answer. The probe is injectable; when a test injects the AI but no probe, the server counts as reachable.
/// </summary>
public sealed class KnowledgeAiGate
{
    public const string UnreachableHint = "Local AI isn't running. Start Ollama or check Tools → AI.";

    private readonly Func<(KnowledgeCompletion? Complete, string Model)> _ai;
    private readonly Func<Task<bool>> _probe;

    public KnowledgeAiGate(Func<(KnowledgeCompletion? Complete, string Model)>? ai = null, Func<Task<bool>>? probe = null)
    {
        _ai = ai ?? KnowledgeAi.Resolve;
        _probe = probe ?? (ai == null ? KnowledgeAi.ProbeAsync : () => Task.FromResult(true));
    }

    /// <summary>The settings-based AI (re-read each time).</summary>
    public Func<(KnowledgeCompletion? Complete, string Model)> Resolve => _ai;

    /// <summary>True when the settings name a usable model (no server contact).</summary>
    public bool SettingsOk() => _ai().Complete != null;

    /// <summary>Settings plus reachability. Unreachable is true only when the settings are fine but the server does not answer.</summary>
    public async Task<(bool Ok, bool Unreachable)> CheckAsync()
    {
        if (!SettingsOk()) return (false, false);
        try { return await _probe().ConfigureAwait(false) ? (true, false) : (false, true); }
        catch (Exception ex) { AppLog.Warn("AI probe failed: " + ex.Message); return (false, true); }
    }
}
