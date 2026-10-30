using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>Results case tree columns (Name | Tool | Earliest | Latest | Events | Size): visibility, widths and the header sort.</summary>
public sealed partial class TreeColumnsViewModel : ObservableObject
{
    public const string Name = "Name", Tool = "Tool", Earliest = "Earliest", Latest = "Latest", Events = "Events", Size = "Size";
    /// <summary>Columns the picker can hide (Name is always shown).</summary>
    public static readonly string[] Optional = { Tool, Earliest, Latest, Events, Size };

    [ObservableProperty] private double _nameWidth;
    [ObservableProperty] private double _toolWidth;
    [ObservableProperty] private bool _showTool;
    [ObservableProperty] private double _earliestWidth;
    [ObservableProperty] private double _latestWidth;
    [ObservableProperty] private double _eventsWidth;
    [ObservableProperty] private double _sizeWidth;
    [ObservableProperty] private bool _showEarliest;
    [ObservableProperty] private bool _showLatest;
    [ObservableProperty] private bool _showEvents;
    [ObservableProperty] private bool _showSize;
    /// <summary>Header sort: column name, or null for the default order.</summary>
    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;

    public TreeColumnsViewModel(ResultsTreeColumns s) => Load(s);

    public void Load(ResultsTreeColumns s)
    {
        NameWidth = Math.Max(ResultsTreeColumns.MinNameWidth, s.NameWidth);
        ToolWidth = Math.Max(ResultsTreeColumns.MinWidth, s.ToolWidth);
        ShowTool = s.ShowTool;
        EarliestWidth = Math.Max(ResultsTreeColumns.MinWidth, s.EarliestWidth);
        LatestWidth = Math.Max(ResultsTreeColumns.MinWidth, s.LatestWidth);
        EventsWidth = Math.Max(ResultsTreeColumns.MinWidth, s.EventsWidth);
        SizeWidth = Math.Max(ResultsTreeColumns.MinWidth, s.SizeWidth);
        ShowEarliest = s.ShowEarliest;
        ShowLatest = s.ShowLatest;
        ShowEvents = s.ShowEvents;
        ShowSize = s.ShowSize;
    }

    public ResultsTreeColumns ToSettings() => new()
    {
        NameWidth = NameWidth, ToolWidth = ToolWidth, ShowTool = ShowTool, EarliestWidth = EarliestWidth, LatestWidth = LatestWidth, EventsWidth = EventsWidth, SizeWidth = SizeWidth,
        ShowEarliest = ShowEarliest, ShowLatest = ShowLatest, ShowEvents = ShowEvents, ShowSize = ShowSize,
    };

    public bool IsVisible(string column) => column switch
    {
        Tool => ShowTool, Earliest => ShowEarliest, Latest => ShowLatest, Events => ShowEvents, Size => ShowSize, _ => true,
    };

    public void SetVisible(string column, bool value)
    {
        switch (column)
        {
            case Tool: ShowTool = value; break;
            case Earliest: ShowEarliest = value; break;
            case Latest: ShowLatest = value; break;
            case Events: ShowEvents = value; break;
            case Size: ShowSize = value; break;
        }
    }

    public double GetWidth(string column) => column switch
    {
        Tool => ToolWidth, Earliest => EarliestWidth, Latest => LatestWidth, Events => EventsWidth, Size => SizeWidth, _ => NameWidth,
    };

    public void SetWidth(string column, double value)
    {
        double min = column == Name ? ResultsTreeColumns.MinNameWidth : ResultsTreeColumns.MinWidth;
        value = Math.Clamp(value, min, 2000);
        switch (column)
        {
            case Tool: ToolWidth = value; break;
            case Earliest: EarliestWidth = value; break;
            case Latest: LatestWidth = value; break;
            case Events: EventsWidth = value; break;
            case Size: SizeWidth = value; break;
            default: NameWidth = value; break;
        }
    }

    /// <summary>Sort arrow shown next to a header label ("" when the tree is not sorted by it).</summary>
    public string SortGlyph(string column) => SortColumn == column ? (SortDescending ? "▼" : "▲") : "";

    public string NameSort => SortGlyph(Name);
    public string ToolSort => SortGlyph(Tool);
    public string EarliestSort => SortGlyph(Earliest);
    public string LatestSort => SortGlyph(Latest);
    public string EventsSort => SortGlyph(Events);
    public string SizeSort => SortGlyph(Size);

    partial void OnSortColumnChanged(string? value) => RaiseHeaders();
    partial void OnSortDescendingChanged(bool value) => RaiseHeaders();

    private void RaiseHeaders()
    {
        OnPropertyChanged(nameof(NameSort));
        OnPropertyChanged(nameof(ToolSort));
        OnPropertyChanged(nameof(EarliestSort));
        OnPropertyChanged(nameof(LatestSort));
        OnPropertyChanged(nameof(EventsSort));
        OnPropertyChanged(nameof(SizeSort));
    }
}
