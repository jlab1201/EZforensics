using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Analysis;

namespace EzForensics.App.ViewModels.Findings;

public sealed partial class FindingsViewModel
{
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _reportStatus = "";
    [ObservableProperty] private string _reportPreview = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOpenFolder))] private string? _reportMdPath;

    public bool CanOpenFolder => !string.IsNullOrEmpty(ReportMdPath);
    public bool HasReport => ReportPreview.Length > 0;

    private void ResetReport()
    {
        ReportStatus = ""; ReportPreview = "";
        ReportMdPath = null; ReportHtmlPath = null;
        OnPropertyChanged(nameof(HasReport));
    }

    partial void OnReportPreviewChanged(string value) { OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ReportButtonText)); }
    partial void OnIsGeneratingChanged(bool value) => GenerateReportCommand.NotifyCanExecuteChanged();

    partial void OnCaseIdChanged(string? value) { GenerateReportCommand.NotifyCanExecuteChanged(); StatusChanged(); }

    /// <summary>The report flyout is open.</summary>
    [ObservableProperty] private bool _isReportOpen;

    /// <summary>"Build report", "Build report (3 pending)" or "Build report (draft ready)".</summary>
    public string ReportButtonText => PendingApprovals > 0 ? $"Build report ({PendingApprovals} pending)" : HasReport ? "Build report (draft ready)" : "Build report";

    [RelayCommand]
    private void ToggleReport() => IsReportOpen = !IsReportOpen;

    [RelayCommand]
    private void CloseReport() => IsReportOpen = false;

    private bool CanGenerate() => !IsGenerating; // manual-only cases have no engine case id; the body checks the store

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateReportAsync() => GuardAsync(async () =>
    {
        IsGenerating = true; ReportStatus = "Building the report…";
        try
        {
            if (Analysis == null || CaseDir is not { } dir) { ReportStatus = "Open a case with analysis first."; return; }
            var plain = _enginePlain;
            var res = AnalysisReport.Build(Analysis, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), DateTimeOffset.UtcNow);
            var folder = Path.Combine(dir, "Reports");
            var path = Path.Combine(folder, $"key-events-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
            await Task.Run(() => { Directory.CreateDirectory(folder); File.WriteAllText(path, res.Markdown); });
            ReportMdPath = path;
            ReportPreview = res.Markdown;
            ReportStatus = $"Report ready: {res.Included} key events included, {res.Excluded} left out.";
        }
        finally { IsGenerating = false; }
    }, "Could not build the report");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanOpenInBrowser))] private string? _reportHtmlPath;
    public bool CanOpenInBrowser => !string.IsNullOrEmpty(ReportHtmlPath) && File.Exists(ReportHtmlPath);

    private string CaseName => CaseDir is { } d ? Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : "";

    /// <summary>The walk-through of the report: the key events of the Timeline slides, in story order (the key events of the analysis when the timeline is empty).</summary>
    private IReadOnlyList<WalkthroughStep> ReportSteps() => Moments.Count == 0 ? ReportHtmlRenderer.StepsFromAnalysis(Analysis!)
        : Moments.OrderBy(m => m.Moment.Time == null).ThenBy(m => m.Number)
                 .Select(m => new WalkthroughStep(m.Moment.Time, m.PlainTitle, string.Join(" ", new[] { m.Why, m.CantTell }.Where(x => !string.IsNullOrWhiteSpace(x))), m.TechniqueText, m.Severity)).ToList();

    /// <summary>Save as HTML: one self-contained file for people without the app (the default).</summary>
    [RelayCommand]
    private void SaveReportHtml()
    {
        if (Analysis == null) { ReportStatus = "Open a case with analysis first."; return; }
        var path = Dialogs.SaveFile("Save report as HTML", "HTML report (*.html)|*.html", $"key-events-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html", ReportFolder());
        if (path == null) return;
        Guard(() =>
        {
            File.WriteAllText(path, ReportHtmlRenderer.Render(Analysis!, CaseName, DateTimeOffset.UtcNow, ReportSteps()));
            ReportHtmlPath = path;
            ReportStatus = $"Saved the HTML report: {path}";
        }, "Could not save the HTML report");
    }

    /// <summary>Save as Markdown: the same report as text (Build report must have been run).</summary>
    [RelayCommand]
    private void SaveReportMarkdown()
    {
        if (!HasReport) { ReportStatus = "Build the report first."; return; }
        var path = Dialogs.SaveFile("Save report as Markdown", "Markdown (*.md)|*.md", $"key-events-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md", ReportFolder());
        if (path == null) return;
        Guard(() => { File.WriteAllText(path, ReportPreview); ReportStatus = $"Saved the Markdown report: {path}"; }, "Could not save the Markdown report");
    }

    [RelayCommand]
    private void OpenReportInBrowser() => Guard(() => ShellOps.OpenFile(ReportHtmlPath), "Could not open the report");

    private string? ReportFolder() => CaseDir is { } d ? Path.Combine(d, "Reports") : null;

    [RelayCommand]
    private void OpenReportFolder()
    {
        var path = ReportMdPath;
        Guard(() => ShellOps.OpenFolder(Path.GetDirectoryName(path)), "Could not open the report folder");
    }
}
