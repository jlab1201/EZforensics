namespace EzForensics.Core.Advisories;

/// <summary>
/// Whether Analyze hints (reliability banner, column markers) are visible. Two switches hide them: the permanent setting
/// (Options > Display, <c>AppSettings.ShowAnalyzeHints</c> = false) and the session switch (Analyze > Actions > Hide hints).
/// The advisory computation always runs; only visibility changes.
/// </summary>
public static class AnalyzeHints
{
    /// <summary>True when hints are shown: the setting says Show and the session switch is off.</summary>
    public static bool IsVisible(bool showSetting, bool hideThisSession) => showSetting && !hideThisSession;

    /// <summary>The Actions item reads checked when hints are hidden for any reason.</summary>
    public static bool MenuChecked(bool showSetting, bool hideThisSession) => !IsVisible(showSetting, hideThisSession);

    /// <summary>The Actions item is disabled (locked) when the permanent setting already hides hints.</summary>
    public static bool MenuEnabled(bool showSetting) => showSetting;
}
