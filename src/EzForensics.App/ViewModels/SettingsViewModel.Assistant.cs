using System.Text;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>
/// What Spectre sees and may change on the Options page. It reads the saved settings; it may change only the safe ones
/// (<see cref="SpectrePageTools.SafeSettings"/>), saved at once like a Save click and mirrored into the form so unsaved edits are kept.
/// </summary>
public sealed partial class SettingsViewModel
{
    public override SpectrePage AssistantPage => SpectrePage.Options;

    public override string? AssistantState() => OnUi(() =>
    {
        var s = S.Settings;
        var sb = new StringBuilder("Saved settings:");
        sb.Append("\n- AI provider: ").Append(s.AiProvider).Append(", model: ").Append(string.IsNullOrWhiteSpace(s.AiModel) ? "(auto)" : s.AiModel);
        sb.Append("\n- Auto-mode: ").Append(s.AssistantAutoMode ? "on" : "off");
        sb.Append("\n- Theme: ").Append(s.Theme);
        sb.Append("\n- Analyze hints: ").Append(s.ShowAnalyzeHints ? "on" : "off");
        sb.Append("\n- Tool logging: ").Append(s.ToolLogLevel);
        sb.Append("\n- Flags: ").Append(string.Join(", ", s.Flags.Select(f => f.Label)));
        if (IsDirty) sb.Append("\nThe Options form has unsaved changes.");
        return sb.ToString();
    });

    public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        foreach (var t in base.AssistantTools(host)) yield return t;
        yield return SpectrePageTools.Create(SpectrePageTools.OptionsSetSpec, host, (id, input) => OnUi(() =>
        {
            var setting = input["setting"]?.ToString()?.Trim().ToLowerInvariant();
            var (value, _) = SpectrePageTools.CheckSetting(setting, input["value"]?.ToString());
            if (setting == null || value == null) return null;
            var before = ReadSetting(setting);
            return new PageActionProposal(id, $"Set {Friendly(setting)} to {value}", $"Now: {before}",
                () => Task.FromResult(WriteSetting(setting, value)),
                () => Task.FromResult(WriteSetting(setting, before)));
        }), input => SpectrePageTools.CheckSetting(input["setting"]?.ToString(), input["value"]?.ToString()).Error);
    }

    private static string Friendly(string setting) => setting switch
    {
        "analyze_hints" => "Analyze hints", "tool_logging" => "tool logging", "auto_mode" => "auto-mode", _ => setting,
    };

    private string ReadSetting(string setting)
    {
        var s = S.Settings;
        return setting switch
        {
            "theme" => s.Theme.ToString(),
            "analyze_hints" => s.ShowAnalyzeHints ? "on" : "off",
            "tool_logging" => s.ToolLogLevel.ToString(),
            "auto_mode" => s.AssistantAutoMode ? "on" : "off",
            _ => "",
        };
    }

    /// <summary>Writes one safe setting to the saved settings and to the form. Returns an error text, or null when saved.</summary>
    private string? WriteSetting(string setting, string value)
    {
        void Both(Action<AppSettings> set)
        {
            set(S.Settings);
            _mirroring = true;
            try { set(Edit); }
            finally { _mirroring = false; }
        }
        switch (setting)
        {
            case "theme" when Enum.TryParse<AppTheme>(value, true, out var theme):
                Both(x => x.Theme = theme);
                ThemeManager.Apply(theme);
                break;
            case "analyze_hints":
                Both(x => x.ShowAnalyzeHints = value == "on");
                break;
            case "tool_logging" when Enum.TryParse<ToolLogLevel>(value, true, out var level):
                Both(x => x.ToolLogLevel = level);
                break;
            case "auto_mode":
                Both(x => x.AssistantAutoMode = value == "on");
                break;
            default:
                return $"Could not set {setting} to {value}.";
        }
        try { S.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "Could not save the settings: " + ex.Message; }
        AppLog.Info($"Spectre changed the setting {setting}.");
        return null;
    }
}
