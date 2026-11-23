using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>Optional columns of the Ingest jobs list: Matched path, Duration, Result, Output.</summary>
public sealed partial class IngestColumnsViewModel : ObservableObject
{
    public const string MatchedPath = "Matched path", Duration = "Duration", Result = "Result", Output = "Output";
    public static readonly string[] Optional = { MatchedPath, Duration, Result, Output };

    /// <summary>The <c>Show…</c> property that holds the column's visibility ("Matched path" -> ShowMatchedPath).</summary>
    public static string PropertyOf(string column)
    {
        var words = column.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return "Show" + string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    [ObservableProperty] private bool _showMatchedPath;
    [ObservableProperty] private bool _showDuration;
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private bool _showOutput;

    public IngestColumnsViewModel(IngestJobColumns s) => Load(s);

    public void Load(IngestJobColumns s)
    {
        ShowMatchedPath = s.ShowMatchedPath; ShowDuration = s.ShowDuration; ShowResult = s.ShowResult; ShowOutput = s.ShowOutput;
    }

    public IngestJobColumns ToSettings() => new()
    {
        ShowMatchedPath = ShowMatchedPath, ShowDuration = ShowDuration, ShowResult = ShowResult, ShowOutput = ShowOutput,
    };

    public bool IsVisible(string column) => column switch
    {
        MatchedPath => ShowMatchedPath, Duration => ShowDuration, Result => ShowResult, Output => ShowOutput, _ => true,
    };

    public void SetVisible(string column, bool value)
    {
        switch (column)
        {
            case MatchedPath: ShowMatchedPath = value; break;
            case Duration: ShowDuration = value; break;
            case Result: ShowResult = value; break;
            case Output: ShowOutput = value; break;
        }
    }
}
