using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

/// <summary>
/// The Explore page: browse the case folder and added sources, inspect one file (overview, hex, strings, metadata, PE) and hand bytes to
/// CyberChef. Evidence is only ever read; nothing is executed or opened with its default app. Split like <c>ResultsViewModel</c>:
/// .Tree (sources), .List (folder listing), .Viewer (the tabs).
/// </summary>
public sealed partial class ExploreViewModel : PageViewModel
{
    public const long RowFactsMaxBytes = 64L * 1024 * 1024;

    public override string Title => PageNames.Explore;

    public ExploreToolLocator Tools { get; } = new();

    private string? _caseDir;
    private ExploreSources? _sources;
    private ResultCache? _cache;

    [ObservableProperty] private bool _hasCase;
    [ObservableProperty] private string _caseLabel = "";

    // CyberChef: the view owns the CyberChefHost (a popup window, never docked) and plugs these in.
    public Func<byte[], Task>? CyberChefSender { get; set; }
    public Action? CyberChefOpener { get; set; }

    public string? CaseDir => _caseDir;

    public ExploreViewModel()
    {
        InitList();
        Search = new ExploreSearchViewModel(this);
    }

    public override void OnNavigatedTo()
    {
        var dir = ResolveCaseDir();
        if (dir == null)
        {
            if (_caseDir == null) HasCase = false;
            return;
        }
        if (!SamePath(dir, _caseDir)) Guard(() => LoadCase(dir), "Could not open the case in Explore");
    }

    private string? ResolveCaseDir()
    {
        var last = S.LastCaseDir;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
        if (_caseDir != null && Directory.Exists(_caseDir)) return _caseDir;
        try
        {
            var root = S.Settings.CaseRoot;
            if (!Directory.Exists(root)) return null;
            return new DirectoryInfo(root).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith("_"))
                .OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal void LoadCase(string dir)
    {
        Search.Reset();
        ResetList();
        ClearViewer();
        _caseDir = Path.GetFullPath(dir);
        _sources = ExploreSources.Load(_caseDir);
        _cache = new ResultCache(_caseDir);
        _factsCache.Clear();
        HasCase = true;
        CaseLabel = _sources.CaseSource.Label;
        BuildTree();
        OnPropertyChanged(nameof(IsCaseHidden));
        if (RootNodes.FirstOrDefault() is { } first) first.IsSelected = true;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------------------ CyberChef

    /// <summary>Opens the CyberChef window, or brings it to the front when it is already open.</summary>
    [RelayCommand]
    private void OpenCyberChef() => CyberChefOpener?.Invoke();

    public async Task SendToCyberChefAsync(byte[] data)
    {
        if (CyberChefSender == null) { ShowMessage("CyberChef is not available on this page yet.", InfoSeverity.Warning); return; }
        try { await CyberChefSender(data); }
        catch (Exception ex) { ShowError("Could not send to CyberChef", ex); }
    }

    // ------------------------------------------------------------------------------ misc

    [RelayCommand]
    private void OpenTools() => S.OpenToolsConfiguration(ToolsSection.Explore);

    private static void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException) { }
    }
}
