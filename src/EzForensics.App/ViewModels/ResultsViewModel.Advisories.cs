using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Advisories;
using EzForensics.Core.Case;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>One advisory row in the banner's details list.</summary>
public sealed class AdvisoryItemViewModel
{
    public AdvisoryItemViewModel(Advisory a)
    {
        Advisory = a;
        SeverityText = a.Severity.ToString().ToUpperInvariant();
        Kind = SeverityKind(a.Severity);
    }

    public Advisory Advisory { get; }
    public string Title => Advisory.Title;
    public string Message => Advisory.Message;
    public string SeverityText { get; }
    public string Kind { get; }
    public string Id => Advisory.Id;
    public string? AffectedText => Advisory.AffectedColumns.Count == 0 ? null : "Affected columns: " + string.Join(", ", Advisory.AffectedColumns);
    public string? ReliableText => Advisory.ReliableColumns.Count == 0 ? null : "Still reliable: " + string.Join(", ", Advisory.ReliableColumns);
    public string? ReferenceUrl => Advisory.ReferenceUrl;
    public bool HasReference => !string.IsNullOrEmpty(Advisory.ReferenceUrl);
    public bool HasAffected => AffectedText != null;
    public bool HasReliable => ReliableText != null;

    public static string SeverityKind(AdvisorySeverity s) => s switch
    {
        AdvisorySeverity.Critical => "crit",
        AdvisorySeverity.Warning => "warn",
        _ => "info",
    };
}

/// <summary>Banner above the events grid (EventsPane.AdvisoryBannerHost): headline, copy evidence, expandable details, dismiss.</summary>
public sealed partial class AdvisoryBannerViewModel : ObservableObject
{
    private readonly Action<AdvisoryBannerViewModel> _dismiss;
    private readonly Action<AdvisoryBannerViewModel>? _hidePermanently;

    public AdvisoryBannerViewModel(string csvPath, AdvisoryReport report, BannerShape shape, Action<AdvisoryBannerViewModel> dismiss,
        Action<AdvisoryBannerViewModel>? hidePermanently = null)
    {
        CsvPath = csvPath;
        Report = report;
        Shape = shape;
        _dismiss = dismiss;
        _hidePermanently = hidePermanently;
        Items = report.Advisories.Select(a => new AdvisoryItemViewModel(a)).ToList();
        Kind = AdvisoryItemViewModel.SeverityKind(shape.Severity);
        Headline = shape.Headline;
        Summary = shape.Summary;
        IsSubtle = shape.IsSubtle;
        Extra = !shape.IsSubtle && shape.Count > 1 ? $"+{shape.Count - 1} more" : "";
    }

    public string CsvPath { get; }
    public AdvisoryReport Report { get; }
    public BannerShape Shape { get; }
    public IReadOnlyList<AdvisoryItemViewModel> Items { get; }
    public string Kind { get; }
    public string Headline { get; }
    public string Summary { get; }
    public string Extra { get; }
    public bool IsSubtle { get; }
    public bool HasSummary => Summary.Length > 0;
    [ObservableProperty] private bool _isExpanded;

    [RelayCommand] private void ToggleDetails() => IsExpanded = !IsExpanded;
    [RelayCommand] private void Dismiss() => _dismiss(this);
    [RelayCommand] private void HidePermanently() => _hidePermanently?.Invoke(this);

    /// <summary>"Hide hints" on the banner: after <paramref name="confirm"/> says yes, turns <see cref="AppSettings.ShowAnalyzeHints"/> off and saves it. Returns whether hints were hidden.</summary>
    public static bool HideHintsPermanently(AppSettings settings, Func<bool> confirm, Action save)
    {
        if (!confirm()) return false;
        settings.ShowAnalyzeHints = false;
        try { save(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        return true;
    }

    [RelayCommand]
    private void OpenReference(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { AppLog.Error("Could not open reference link", ex); }
    }
}

public sealed partial class ResultsViewModel
{
    private static readonly string AdvisoryOverridePath = Path.Combine(AppPaths.DataRoot, "advisories.json");
    private ReliabilityAdvisor? _advisor;
    private DateTime _advisorOverrideStamp;
    private readonly object _advisoryLock = new();
    private (string Dir, string Stamp, CaseIndex Index)? _caseIndexCache;
    private readonly HashSet<string> _dismissedAdvisories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _advisoryCts;

    /// <summary>Clears banner and header markers (a file closed / another one is opening).</summary>
    private void ClearAdvisories()
    {
        _advisoryCts?.Cancel();
        AdvisoryBanner = null;
        foreach (var c in Cells) { c.AdvisoryKind = ""; c.AdvisoryTip = null; }
    }

    /// <summary>Computes the reliability report off the UI thread and shows banner + column markers (no-op if the file changed meanwhile).</summary>
    private void StartAdvisories(string csvPath, IReadOnlyList<string> headers)
    {
        _advisoryCts?.Cancel();
        var cts = _advisoryCts = new CancellationTokenSource();
        var caseDir = CaseFolder;
        _ = Task.Run(() =>
        {
            try
            {
                var report = BuildAdvisoryReport(csvPath, headers, caseDir);
                var shape = AdvisoryPresentation.ShapeBanner(report);
                var markers = AdvisoryPresentation.ColumnMarkers(report, headers);
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested || !string.Equals(CurrentFile, csvPath, StringComparison.OrdinalIgnoreCase)) return;
                    var byCol = markers.ToDictionary(m => m.Column, StringComparer.OrdinalIgnoreCase);
                    foreach (var c in Cells)
                    {
                        if (!byCol.TryGetValue(c.Column, out var m)) continue;
                        c.AdvisoryKind = m.Severity is { } s ? AdvisoryItemViewModel.SeverityKind(s) : "ok";
                        c.AdvisoryTip = m.Tip;
                    }
                    AdvisoryBanner = shape.Show && !_dismissedAdvisories.Contains(csvPath)
                        ? new AdvisoryBannerViewModel(csvPath, report, shape, DismissAdvisory, ConfirmHideHintsPermanently)
                        : null;
                }));
            }
            catch (Exception ex) { AppLog.Error("Could not evaluate reliability advisories", ex); }
        });
    }

    private void DismissAdvisory(AdvisoryBannerViewModel b)
    {
        _dismissedAdvisories.Add(b.CsvPath);
        if (ReferenceEquals(AdvisoryBanner, b)) AdvisoryBanner = null;
    }

    // HintsVisible follows the setting (ResultsViewModel ctor), so the banner and markers vanish at once
    private void ConfirmHideHintsPermanently(AdvisoryBannerViewModel _) =>
        AdvisoryBannerViewModel.HideHintsPermanently(S.Settings,
            () => Dialogs.Confirm("Hide hints permanently?",
                "Reliability banners and column markers on Analyze will stay hidden, also after a restart. You can turn them back on in Options › Display › Analyze hints.",
                "Hide hints", "Cancel"),
            S.SaveSettings);

    private AdvisoryReport BuildAdvisoryReport(string csvPath, IReadOnlyList<string> headers, string? caseDir)
    {
        ReliabilityAdvisor advisor;
        lock (_advisoryLock)
        {
            var stamp = File.Exists(AdvisoryOverridePath) ? File.GetLastWriteTimeUtc(AdvisoryOverridePath) : DateTime.MinValue;
            if (_advisor == null || stamp != _advisorOverrideStamp)
            {
                _advisor = new ReliabilityAdvisor(AdvisoryRuleSet.LoadWithOverride(AdvisoryOverridePath));
                _advisorOverrideStamp = stamp;
            }
            advisor = _advisor;
        }
        CsvProvenance? prov = null;
        var idx = GetCaseIndex(caseDir);
        if (idx != null)
        {
            try { prov = idx.Describe(csvPath); }
            catch (Exception ex) { AppLog.Error("Could not read case provenance", ex); }
        }
        NoteArtifactId(csvPath, prov?.AdvisoryArtifactId);
        return advisor.Evaluate(new AdvisoryContext
        {
            ArtifactId = prov?.AdvisoryArtifactId,
            Headers = headers,
            CsvFileName = Path.GetFileName(csvPath),
            Acquisition = prov?.EvidenceAcquisition ?? EvidenceAcquisition.Unknown,
            InputTimestampFacts = prov?.InputTimestampFacts,
            ArchiveTimestampCoverage = prov?.ArchiveTimestampCoverage,
            MemoryFormat = prov?.MemoryFormat,
            MemoryDumpKind = prov?.MemoryDumpKind,
            MemoryLiveDump = prov?.MemoryLiveDump,
            RitaDataSpanHours = prov?.RitaDataSpanHours,
        });
    }

    /// <summary>Case index cached per case folder; rebuilt when the folder changes or a run manifest was added/updated (e.g. after a tree refresh).</summary>
    private CaseIndex? GetCaseIndex(string? caseDir)
    {
        if (string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return null;
        try
        {
            var files = Directory.GetFiles(caseDir, CaseIndex.ManifestPattern, SearchOption.TopDirectoryOnly);
            var stamp = files.Length + ":" + (files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks));
            lock (_advisoryLock)
            {
                if (_caseIndexCache is { } c && string.Equals(c.Dir, caseDir, StringComparison.OrdinalIgnoreCase) && c.Stamp == stamp) return c.Index;
                var idx = CaseIndex.Load(caseDir);
                _caseIndexCache = (caseDir, stamp, idx);
                return idx;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load case index", ex);
            return null;
        }
    }
}
