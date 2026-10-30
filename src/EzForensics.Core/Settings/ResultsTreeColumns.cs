namespace EzForensics.Core.Settings;

/// <summary>Results case tree columns: which optional columns are shown and every column's width (Name is always shown).</summary>
public sealed class ResultsTreeColumns
{
    public const double MinWidth = 36;
    public const double MinNameWidth = 120;

    public bool ShowEarliest { get; set; } = true;
    public bool ShowLatest { get; set; } = true;
    public bool ShowEvents { get; set; } = true;
    public bool ShowSize { get; set; } = false;
    public double NameWidth { get; set; } = 280;
    public double EarliestWidth { get; set; } = 140;
    public double LatestWidth { get; set; } = 140;
    public double EventsWidth { get; set; } = 80;
    public double SizeWidth { get; set; } = 72;
    // appended: Tool column (from CaseIndex.Describe(path).ToolId), shown next to Name
    public bool ShowTool { get; set; } = false;
    public double ToolWidth { get; set; } = 110;
}
