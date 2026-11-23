namespace EzForensics.App.Services;

/// <summary>User-visible page names. The code names (TriageViewModel, Core/Triage, AutomationIds) are unchanged.</summary>
public static class PageNames
{
    public const string Ingest = "Ingest";
    public const string Analyze = "Analyze";
    public const string Explore = "Explore";
    public const string Findings = "Review";
    public const string Learn = "AI";
}

/// <summary>A special tool entry on the Tools page (<see cref="AppServices.OpenToolsConfiguration"/>).</summary>
public enum ToolsSection { Plaso, Zeek, Rita, Volatility, Explore, Spectre, Documents }
