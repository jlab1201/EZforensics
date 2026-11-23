using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels.Findings;
using EzForensics.Core.Results;
using EzForensics.Core.Spectre;

namespace EzForensics.App.ViewModels;

/// <summary>One more evidence file the banner offers when a finding's records span several CSVs.</summary>
public sealed record EvidenceFileChoice(string Name, EvidenceTarget Target);

public sealed partial class ResultsViewModel
{
    /// <summary>
    /// The rows behind the finding being shown: a transient row-set filter for the open file (never written to the flag list or the row-flag
    /// sidecar). Cleared by "Show all rows", Clear filters and when another file opens.
    /// </summary>
    private IReadOnlySet<long>? _evidenceRows;

    private void NotifyEvidenceRows() => OnPropertyChanged(nameof(HasAnyFilter));

    private IReadOnlyList<EvidenceTarget> _evidenceTargets = Array.Empty<EvidenceTarget>();
    private string _evidenceTitle = "", _evidenceCaseDir = "", _evidenceCsv = "";
    private string? _evidenceBannerText;

    /// <summary>"Showing the N records behind 'title'" while the evidence file is open; null otherwise.</summary>
    public string? EvidenceBannerText
    {
        get => string.Equals(CurrentFile, _evidenceCsv, StringComparison.OrdinalIgnoreCase) ? _evidenceBannerText : null;
        private set { _evidenceBannerText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasEvidenceBanner)); }
    }
    public bool HasEvidenceBanner => EvidenceBannerText != null;

    /// <summary>The other files with evidence for the same finding (empty when there is one file).</summary>
    public ObservableCollection<EvidenceFileChoice> EvidenceOtherFiles { get; } = new();
    public bool HasEvidenceOtherFiles => EvidenceOtherFiles.Count > 0;

    private void NotifyEvidenceBanner()
    {
        OnPropertyChanged(nameof(EvidenceBannerText));
        OnPropertyChanged(nameof(HasEvidenceBanner));
    }

    /// <summary>
    /// "Show the evidence": opens the first CSV, filters to the rows behind the finding (a transient row filter, no flags written), selects the first
    /// and shows the banner. The other files are offered in the banner.
    /// </summary>
    public async Task ShowEvidenceAsync(string caseDir, IReadOnlyList<EvidenceTarget> targets, string title)
    {
        if (targets.Count == 0) return;
        _evidenceTargets = targets; _evidenceTitle = title; _evidenceCaseDir = caseDir;
        await OpenEvidenceTargetAsync(targets[0]);
    }

    [RelayCommand]
    private Task OpenEvidenceFile(EvidenceFileChoice? choice) => choice == null ? Task.CompletedTask : OpenEvidenceTargetAsync(choice.Target);

    private async Task OpenEvidenceTargetAsync(EvidenceTarget target)
    {
        try
        {
            _evidenceCsv = target.Csv;
            EvidenceBannerText = null;
            await ShowCsvAsync(_evidenceCaseDir, target.Csv);
            var idx = _index;
            if (idx == null || !string.Equals(CurrentFile, target.Csv, StringComparison.OrdinalIgnoreCase)) return;
            try { await idx.IndexingCompleted.WaitAsync(TimeSpan.FromMinutes(2)); }
            catch (TimeoutException) { StatusText = "The file is still being indexed; the records were not looked up."; return; }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(idx, _index)) return;
            IReadOnlyList<long> rows;
            try { rows = await Task.Run(() => FindingsEvidence.ResolveRows(idx, target)); }
            catch (ObjectDisposedException) { return; }
            if (!ReferenceEquals(idx, _index)) return;

            var name = System.IO.Path.GetFileName(target.Csv);
            RebuildEvidenceOtherFiles(target);
            if (rows.Count == 0)
            {
                EvidenceBannerText = $"Could not find the records behind '{_evidenceTitle}' in {name}; the file is open.";
                StatusText = EvidenceBannerText;
                return;
            }
            var grid = (IAssistantGridTarget)this;
            _evidenceRows = new HashSet<long>(rows);
            NotifyEvidenceRows();
            ApplyNow();
            for (int i = 0; i < 1000 && IsFiltering; i++) await Task.Delay(30);
            if (!ReferenceEquals(idx, _index)) return;
            grid.GoToRow(rows[0]);
            EvidenceBannerText = rows.Count == 1
                ? $"Showing the 1 record behind '{_evidenceTitle}' in {name}."
                : $"Showing the {rows.Count:N0} records behind '{_evidenceTitle}' in {name}.";
        }
        catch (Exception ex) { ShowError("Could not open the finding's records", ex); }
    }

    private void RebuildEvidenceOtherFiles(EvidenceTarget current)
    {
        EvidenceOtherFiles.Clear();
        foreach (var t in _evidenceTargets)
            if (!string.Equals(t.Csv, current.Csv, StringComparison.OrdinalIgnoreCase))
                EvidenceOtherFiles.Add(new EvidenceFileChoice(System.IO.Path.GetFileName(t.Csv), t));
        OnPropertyChanged(nameof(HasEvidenceOtherFiles));
    }

    /// <summary>Banner: drops the evidence-row filter so every row shows.</summary>
    [RelayCommand]
    private void ShowAllEvidenceRows()
    {
        _evidenceRows = null;
        NotifyEvidenceRows();
        ApplyNow();
    }

    [RelayCommand]
    private void BackToFindings() => S.Navigate<FindingsViewModel>();
}
