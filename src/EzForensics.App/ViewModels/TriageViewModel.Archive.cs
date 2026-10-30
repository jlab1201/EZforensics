using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.App.Views;
using EzForensics.Core.Archives;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>One extracted archive shown on the Ingest page's "Archived evidence" card. Never holds a password.</summary>
public sealed partial class ArchiveInfoItem : ObservableObject
{
    [ObservableProperty] private bool _isDeleted;

    public ArchiveInfoItem(ArchiveOrigin origin, IReadOnlyList<string> warnings)
    {
        Origin = origin;
        Warnings = warnings;
    }

    public ArchiveOrigin Origin { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    /// <summary>Warnings other than the "Reused the extracted copy" note (re-scan of a complete extraction).</summary>
    public bool HasProblems => Warnings.Any(w => !w.StartsWith("Reused the extracted copy", StringComparison.Ordinal));
    public string Name => Path.GetFileName(Origin.ArchivePath);
    public string ArchivePath => Origin.ArchivePath;
    public string FormatText => ArchivePasswordWindow.FormatName(Origin.Format);
    public string EncryptedText => Origin.Encrypted ? $"Encrypted: yes ({Origin.EncryptionMethod ?? "unknown method"})" : "Encrypted: no";
    public string Sha256 => Origin.Sha256;
    public bool HasSha256 => Origin.Sha256.Length > 0;
    public string ExtractedTo => Origin.ExtractedTo;
    public bool HasExtractedTo => Origin.ExtractedTo.Length > 0;
    public string ExtractedText => IsDeleted ? "Extracted copy deleted" : Origin.ExtractedTo;
    /// <summary>Outer → inner chain for a nested archive ("" for a plain one).</summary>
    public string ChainText => Origin.Chain.Count > 1 ? "Inside: " + string.Join(" → ", Origin.Chain.Take(Origin.Chain.Count - 1).Select(Path.GetFileName)) : "";
    public bool HasChain => ChainText.Length > 0;

    public string CoverageText => !HasExtractedTo ? "" : Origin.TimestampCoverage switch
    {
        TimestampCoverage.All => "Timestamps: Modified, Created and Accessed times restored from the archive (the extracted files carry the original times).",
        TimestampCoverage.Mixed => "Timestamps: Modified restored for every file; Created / Accessed only where the archive stored them.",
        TimestampCoverage.DosLocal => "Timestamps: only DOS times (local time, unknown time zone, 2-second precision) were stored; Modified restored, Created / Accessed are extraction times.",
        _ => "Timestamps: only Modified times were stored and restored; Created / Accessed of the extracted files are extraction times. Rely on times inside the artifacts.",
    };

    partial void OnIsDeletedChanged(bool value) => OnPropertyChanged(nameof(ExtractedText));
}

/// <summary>Ingest page: archived evidence (zip / 7z / rar / tar / gz) is extracted into the case folder and scanned in place of the source.</summary>
public sealed partial class TriageViewModel
{
    /// <summary>Passwords the user typed on this page (memory only; never logged, persisted or shown).</summary>
    private readonly ArchivePasswordSession _archivePasswords = new();
    /// <summary>The extracted root(s) that replace <see cref="Source"/> for scanning, the queue's source path, plaso and computer details.</summary>
    private IReadOnlyList<string>? _effectiveRoots;
    /// <summary>Origins of the extracted roots (jobs without a discovered system, e.g. a memory image alone in an archive, get theirs by path).</summary>
    private IReadOnlyList<ArchiveOrigin> _archiveOrigins = Array.Empty<ArchiveOrigin>();
    /// <summary>The Source the archive state belongs to (an archive file, or a folder of archives).</summary>
    private string? _archiveSource;
    /// <summary>Case folder the archive was extracted into (kept for re-scans, so the extraction is reused).</summary>
    private string? _archiveCaseDir;
    private string? _changeCaseFolderTo;
    private int _cleanupGeneration;

    [ObservableProperty] private bool _isArchiveBusy;
    [ObservableProperty] private string _archivePhaseText = "";
    [ObservableProperty] private double _archiveProgressValue;
    [ObservableProperty] private bool _archiveProgressIndeterminate = true;
    [ObservableProperty] private string _archiveProgressDetail = "";

    public ObservableCollection<ArchiveInfoItem> Archives { get; } = new();
    public bool HasArchives => Archives.Count > 0;
    /// <summary>An archive has warnings (the compact card shows a warning icon; details in its expander).</summary>
    public bool ArchivesHaveWarnings => Archives.Any(a => a.HasProblems);
    public string ArchivesSummary => Archives.Count == 0 ? "" : Archives.Count == 1
        ? $"Archived evidence: {Archives[0].Name} (scanned from the extracted copy)"
        : $"Archived evidence: {Archives.Count} archives (scanned from the extracted copies)";

    /// <summary>Container archives in a source folder that holds no system (top 2 levels): offered as "Extract and scan".</summary>
    public ObservableCollection<string> ContainerArchives { get; } = new();
    public bool HasContainerArchives => ContainerArchives.Count > 0;
    public string ContainerArchivesText => ContainerArchives.Count == 1 ? "1 archive found in this folder" : $"{ContainerArchives.Count} archives found in this folder";

    /// <summary>Archives inside a system tree (e.g. Users\x\Downloads\a.zip): evidence, listed only, never extracted.</summary>
    public ObservableCollection<string> EvidenceArchives { get; } = new();
    public bool HasEvidenceArchives => EvidenceArchives.Count > 0;
    [ObservableProperty] private string _evidenceArchivesText = "";

    /// <summary>The path standing in for Source after extraction: the extracted root, or the folder holding several (null: no archive).</summary>
    public string? EffectiveSource => _effectiveRoots == null || _effectiveRoots.Count == 0 ? null
        : _effectiveRoots.Count == 1 ? _effectiveRoots[0] : CommonParent(_effectiveRoots);

    partial void OnIsScanningChanged(bool value) => DeleteExtractedCopyCommand.NotifyCanExecuteChanged();

    /// <summary>Single-file source that is an archive (magic bytes) or a virtual disk.</summary>
    internal static bool IsArchiveFile(string path)
    {
        if (!File.Exists(path)) return false;
        var f = ArchiveFormatDetector.Detect(path);
        return ArchiveFormatDetector.IsExtractable(f) || f is ArchiveFormat.Vhd or ArchiveFormat.Vhdx;
    }

    /// <summary>Source changed to something else: forget the archive state (the extracted copy stays on disk).</summary>
    private void OnSourceChangedForArchives(string? value)
    {
        if (_archiveSource != null && !SameDir(value, _archiveSource)) ResetArchiveState();
        if (ContainerArchives.Count > 0 && !SameDir(value, _archiveSource)) SetContainerArchives(Array.Empty<string>());
    }

    private void ResetArchiveState()
    {
        _effectiveRoots = null;
        _archiveOrigins = Array.Empty<ArchiveOrigin>();
        _archiveSource = null;
        _archiveCaseDir = null;
        Archives.Clear();
        RaiseArchives();
        SetEvidenceArchives(Array.Empty<string>(), false);
    }

    private void RaiseArchives()
    {
        OnPropertyChanged(nameof(HasArchives));
        OnPropertyChanged(nameof(ArchivesSummary));
        OnPropertyChanged(nameof(ArchivesHaveWarnings));
        OnPropertyChanged(nameof(EffectiveSource));
        DeleteExtractedCopyCommand.NotifyCanExecuteChanged();
    }

    private void SetContainerArchives(IReadOnlyList<string> list)
    {
        ContainerArchives.Clear();
        foreach (var a in list) ContainerArchives.Add(a);
        OnPropertyChanged(nameof(HasContainerArchives));
        OnPropertyChanged(nameof(ContainerArchivesText));
    }

    private void SetEvidenceArchives(IReadOnlyList<string> list, bool truncated)
    {
        EvidenceArchives.Clear();
        foreach (var a in list) EvidenceArchives.Add(a);
        _evidenceArchivesTruncated = truncated && list.Count > 0;
        EvidenceArchivesText = list.Count == 0 ? ""
            : $"{list.Count}{(truncated ? "+" : "")} archive{(list.Count == 1 ? "" : "s")} inside the system tree (evidence: listed only, never extracted)";
        OnPropertyChanged(nameof(HasEvidenceArchives));
    }

    /// <summary>Case folder to use for an archive source: the one it was extracted into before (re-scan reuse), unless the user chose another.</summary>
    private void KeepArchiveCaseFolder(string src)
    {
        if (_archiveCaseDir != null && !_caseFolderEdited && SameDir(src, _archiveSource))
        {
            _settingCaseFolder = true;
            CaseFolder = _archiveCaseDir;
            _settingCaseFolder = false;
        }
        else if (!_caseFolderEdited) UpdateCaseFolder();
    }

    /// <summary>Extraction needs a case folder (<c>&lt;case&gt;\.ezstaging\extracted</c>): asks for one when the field is empty.</summary>
    private bool EnsureCaseFolderForArchive()
    {
        if (!string.IsNullOrWhiteSpace(CaseFolder)) return true;
        var d = Dialogs.PickFolder("Choose the case folder (archived evidence is extracted into <case>\\.ezstaging\\extracted)", S.Settings.CaseRoot);
        if (d == null)
        {
            ShowMessage("Choose a case folder first: archived evidence is extracted into the case folder before it is scanned.", InfoSeverity.Warning);
            return false;
        }
        CaseFolder = d;
        return true;
    }

    /// <summary>
    /// Extracts one archive (off the UI thread, with the progress bar). Returns null when the user cancelled (status already shown) or it is a virtual disk.
    /// Throws <see cref="ArchiveIngestException"/> on failure (message never contains a password).
    /// </summary>
    private async Task<ArchiveIngestResult?> IngestArchiveAsync(string archive, CancellationTokenSource cts, bool skipOnPasswordCancel = false)
    {
        while (true)
        {
            var caseDir = CaseFolder.Trim();
            _changeCaseFolderTo = null;
            IsArchiveBusy = true;
            ArchivePhaseText = "Hashing";
            ArchiveProgressIndeterminate = true;
            ArchiveProgressDetail = Path.GetFileName(archive);
            var progress = new Progress<ArchiveProgress>(OnArchiveProgress);
            var options = new ArchiveIngestOptions { Keep = S.Settings.KeepExtractedEvidence, Confirm = ConfirmArchiveAsync, Passwords = _archivePasswords };
            try
            {
                var token = cts.Token;
                var result = await Task.Run(() => ArchiveIngest.PrepareAsync(archive, caseDir, req => AskArchivePasswordAsync(req, token), progress, token, options), token);
                if (result.ExtractedRoots.Count == 0)
                {
                    // VHD / VHDX: not extracted, the warning tells the user to mount it.
                    ShowMessage(result.Warnings.FirstOrDefault() ?? "This file is not extracted.", InfoSeverity.Warning);
                    return null;
                }
                _archiveCaseDir = caseDir;
                return result;
            }
            catch (OperationCanceledException) when (_changeCaseFolderTo != null && !cts.IsCancellationRequested)
            {
                // Low disk space: the user chose another case folder; extract there.
                _caseFolderEdited = true;
                CaseFolder = _changeCaseFolderTo;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                // Password dialog or a Continue/Cancel question was cancelled.
                if (skipOnPasswordCancel) return null;
                ShowMessage($"Scan cancelled: '{Path.GetFileName(archive)}' was not extracted.", InfoSeverity.Warning);
                return null;
            }
        }
    }

    private void OnArchiveProgress(ArchiveProgress p)
    {
        ArchivePhaseText = p.Phase switch
        {
            ArchivePhase.Hashing => "Hashing (SHA-256)",
            ArchivePhase.Inspecting => "Inspecting",
            ArchivePhase.Password => "Password",
            ArchivePhase.Extracting => "Extracting",
            ArchivePhase.RestoringTimes => "Restoring times",
            _ => "Scanning",
        };
        double? frac = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : p.EntriesTotal > 0 && p.Phase != ArchivePhase.Password ? (double)p.EntriesDone / p.EntriesTotal : null;
        ArchiveProgressIndeterminate = frac == null;
        ArchiveProgressValue = Math.Clamp((frac ?? 0) * 100, 0, 100);
        var parts = new List<string>();
        if (p.BytesTotal > 0) parts.Add($"{NetworkTriage.FormatSize(p.BytesDone)} of {NetworkTriage.FormatSize(p.BytesTotal)}");
        if (p.EntriesTotal > 0 && p.Phase is ArchivePhase.Extracting or ArchivePhase.RestoringTimes) parts.Add($"{p.EntriesDone:N0} of {p.EntriesTotal:N0} entries");
        if (p.Phase == ArchivePhase.Password) parts.Add("waiting for the password");
        if (!string.IsNullOrEmpty(p.CurrentEntry)) parts.Add(p.CurrentEntry!);
        ArchiveProgressDetail = string.Join("  ·  ", parts);
    }

    private void SetArchiveScanning()
    {
        ArchivePhaseText = "Scanning";
        ArchiveProgressIndeterminate = true;
        ArchiveProgressDetail = "looking for artifacts in the extracted copy";
    }

    /// <summary>
    /// Password provider for <see cref="ArchiveIngest.PrepareAsync"/>: the modal dialog on the UI thread. Null = cancelled (by the user, or
    /// because the scan was cancelled: the dialog closes itself when <paramref name="ct"/> fires).
    /// </summary>
    private static Task<string?> AskArchivePasswordAsync(ArchivePasswordRequest request, CancellationToken ct)
    {
        var app = Application.Current;
        if (app == null || ct.IsCancellationRequested) return Task.FromResult<string?>(null);
        return app.Dispatcher.InvokeAsync(() => ArchivePasswordWindow.Ask(app.MainWindow, request, ct)).Task;
    }

    /// <summary>Continue / Cancel questions: low disk space (with "choose another case folder") and very high compression ratio.</summary>
    private Task<bool> ConfirmArchiveAsync(ArchiveConfirmRequest req)
    {
        var app = Application.Current;
        if (app == null) return Task.FromResult(false);
        return app.Dispatcher.InvokeAsync(() =>
        {
            var owner = app.MainWindow;
            if (req.Kind == ArchiveConfirmKind.LowDiskSpace)
            {
                var r = EzForensics.App.Services.Dialogs.Show("Low disk space", $"Needs {NetworkTriage.FormatSize(req.RequiredBytes)}, {NetworkTriage.FormatSize(req.AvailableBytes)} free.", EzForensics.App.Views.EzDialogSeverity.Warning,
                    req.Message, new[] { "Continue", "Other folder", "Cancel" }, primary: 0, cancel: 2, owner: owner);
                if (r == 0) return true;
                if (r == 1)
                {
                    var d = Dialogs.PickFolder("Choose another case folder (on a drive with enough free space)", CaseFolder);
                    if (d != null) _changeCaseFolderTo = d;
                }
                return false;
            }
            return EzForensics.App.Services.Dialogs.Confirm("Very high compression ratio", $"It expands to {NetworkTriage.FormatSize(req.RequiredBytes)}. Extract anyway?", "Extract", "Cancel",
                EzForensics.App.Views.EzDialogSeverity.Warning, req.Message, defaultNo: true, owner: owner);
        }).Task;
    }

    /// <summary>Records the extraction result(s) on the page: effective roots and the "Archived evidence" card.</summary>
    private Dictionary<string, ArchiveOrigin> ApplyArchiveResults(string source, IReadOnlyList<ArchiveIngestResult> results)
    {
        var roots = new List<string>();
        var origins = new Dictionary<string, ArchiveOrigin>(StringComparer.OrdinalIgnoreCase);
        Archives.Clear();
        foreach (var r in results)
        {
            var list = r.Origins ?? new[] { r.Origin };
            for (int i = 0; i < r.ExtractedRoots.Count; i++)
            {
                var root = r.ExtractedRoots[i];
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
                origins[root] = i < list.Count ? list[i] : r.Origin;
            }
            // One card row per distinct archive (the outer one first when nested).
            var shown = list.Count > 0 ? list : new[] { r.Origin };
            for (int i = 0; i < shown.Count; i++)
                if (!Archives.Any(a => a.Origin.Sha256 == shown[i].Sha256 && SameDir(a.ExtractedTo, shown[i].ExtractedTo)))
                    Archives.Add(new ArchiveInfoItem(shown[i], i == 0 ? r.Warnings : Array.Empty<string>()));
            // A sole nested container (outer.zip holding only data.zip): the row is the inner archive, its ChainText names the outer one.
        }
        _effectiveRoots = roots;
        _archiveOrigins = origins.Values.Distinct().ToList();
        _archiveSource = source;
        RaiseArchives();
        return origins;
    }

    [RelayCommand]
    private void CopyArchiveSha(ArchiveInfoItem? item)
    {
        if (item == null || !item.HasSha256) return;
        Guard(() => Clipboard.SetText(item.Sha256), "Could not copy");
    }

    [RelayCommand]
    private void OpenExtractedFolder(ArchiveInfoItem? item)
    {
        if (item == null || !item.HasExtractedTo) return;
        if (!Directory.Exists(item.ExtractedTo)) { ShowMessage("The extracted copy no longer exists. Scan the archive again to extract it.", InfoSeverity.Warning); return; }
        Guard(() => ShellOps.OpenFolder(item.ExtractedTo), "Could not open the extracted folder");
    }

    /// <summary>The <c>&lt;case&gt;\.ezstaging\extracted\&lt;sha8&gt;</c> folders of the current archives (only folders that really sit there).</summary>
    private List<string> ExtractedCopyFolders() => ExtractedCopyFolders(Archives.Where(a => a.HasExtractedTo && !a.IsDeleted).Select(a => a.Origin));

    /// <summary>
    /// The <c>&lt;sha8&gt;</c> folders of the origins and of the archives they were extracted from (a sole nested container shows only the inner
    /// archive, but the outer archive's extracted copy holds the inner archive file and belongs to the same extraction).
    /// </summary>
    internal static List<string> ExtractedCopyFolders(IEnumerable<ArchiveOrigin> origins) => origins
        .SelectMany(o => o.SelfAndParents())
        .Where(o => o.ExtractedTo.Length > 0)
        .Select(o => Path.GetDirectoryName(Path.GetFullPath(o.ExtractedTo).TrimEnd('\\')))
        .Where(d => d != null && IsExtractedShaFolder(d))
        .Select(d => d!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Safety check before deleting: <c>…\.ezstaging\extracted\&lt;8 hex&gt;</c>.</summary>
    internal static bool IsExtractedShaFolder(string dir)
    {
        var name = Path.GetFileName(dir);
        var parent = Path.GetDirectoryName(dir);
        return name.Length == 8 && name.All(Uri.IsHexDigit) && parent != null
               && Path.GetFileName(parent).Equals(ArchiveIngest.ExtractedFolder, StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(Path.GetDirectoryName(parent) ?? "").Equals(".ezstaging", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanDeleteExtractedCopy() => HasArchives && Archives.Any(a => a.HasExtractedTo && !a.IsDeleted) && !IsScanning && !ArchiveRunActive();

    /// <summary>A run that reads the extracted copy is still queued / running (case jobs or the timeline build).</summary>
    private bool ArchiveRunActive() => CaseHasActiveTriageJobs() || (_runCaseDir != null && S.Queue.Jobs.Any(j => !j.IsFinished && SameDir(j.CaseDir, _runCaseDir)))
                                       || IsBuildingTimeline;

    [RelayCommand(CanExecute = nameof(CanDeleteExtractedCopy))]
    private async Task DeleteExtractedCopyAsync()
    {
        var folders = ExtractedCopyFolders();
        if (folders.Count == 0) return;
        if (!EzForensics.App.Services.Dialogs.Confirm("Delete the extracted copy?", "The original archive and case results are kept.", "Delete", details: string.Join("\n", folders), defaultNo: true))
            return;
        await DeleteExtractedFoldersAsync(folders, automatic: false);
    }

    private async Task DeleteExtractedFoldersAsync(List<string> folders, bool automatic)
    {
        var failed = await Task.Run(() =>
        {
            var errors = new List<string>();
            foreach (var f in folders)
            {
                try { DeleteTree(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Could not delete the extracted copy " + f, ex);
                    errors.Add(f);
                }
            }
            return errors;
        });
        foreach (var a in Archives)
            if (a.HasExtractedTo && !Directory.Exists(a.ExtractedTo)) a.IsDeleted = true;
        if (_effectiveRoots != null && _effectiveRoots.All(r => !Directory.Exists(r)))
        {
            // The plan pointed into the deleted copy: clear it (a new scan extracts again).
            ClearScanForDeletedCopy();
        }
        DeleteExtractedCopyCommand.NotifyCanExecuteChanged();
        // Only a failure is shown (the run's completion is reported next to Run selected; a deleted copy needs no notice).
        if (failed.Count > 0) ShowMessage($"Could not delete {failed.Count} extracted folder(s) (in use?): {failed[0]}", InfoSeverity.Warning);
        else AppLog.Info(automatic ? "Ingest finished; the extracted copy of the archive was deleted (keep extracted evidence is off)." : "Extracted copy deleted.");
    }

    private void ClearScanForDeletedCopy()
    {
        _effectiveRoots = null;
        _archiveOrigins = Array.Empty<ArchiveOrigin>();
        _found = null;
        _layout = null;
        _pcaps = null;
        _systems = Array.Empty<DiscoveredSystem>();
        SetSystems(Array.Empty<DiscoveredSystem>());
        SetMemoryImages(Array.Empty<Core.Memory.MemoryImage>(), null);
        _persistenceRoots = Array.Empty<string>();
        OnPropertyChanged(nameof(HasPersistence));
        SetPlan(Array.Empty<PlannedJob>());
        SetPcaps(PcapScanResult.Empty);
        PcapCount = 0;
        HasScan = false;
        SetEvidenceArchives(Array.Empty<string>(), false);
        OnPropertyChanged(nameof(EffectiveSource));
    }

    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
        var parent = Path.GetDirectoryName(dir);
        try { if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); }
        catch (IOException) { }
    }

    /// <summary>KeepExtractedEvidence = false: once the run (jobs, computer details, timeline build) is done, delete the extracted copy.</summary>
    private void ScheduleExtractedCleanup()
    {
        if (S.Settings.KeepExtractedEvidence || _effectiveRoots == null) return;
        var folders = ExtractedCopyFolders();
        if (folders.Count == 0) return;
        var gen = ++_cleanupGeneration;
        _ = CleanupWhenIdleAsync(folders, gen);
    }

    private async Task CleanupWhenIdleAsync(List<string> folders, int gen)
    {
        try
        {
            await Task.Delay(2000);
            while (gen == _cleanupGeneration && (ArchiveRunActive() || IsScanning)) await Task.Delay(2000);
            if (gen != _cleanupGeneration) return;
            await DeleteExtractedFoldersAsync(folders, automatic: true);
        }
        catch (Exception ex) { AppLog.Error("Extracted copy cleanup failed", ex); }
    }

    /// <summary>"Extract and scan": ingests each container archive found in the source folder, then scans the extracted roots together.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPlan))]
    private async Task ExtractArchivesAsync()
    {
        var src = Source?.Trim();
        var list = ContainerArchives.ToList();
        if (string.IsNullOrEmpty(src) || list.Count == 0) return;
        if (!EnsureCaseFolderForArchive()) return;

        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        IsScanning = true;
        Banner = null;
        try
        {
            var results = new List<ArchiveIngestResult>();
            var skipped = new List<string>();
            foreach (var a in list)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var r = await IngestArchiveAsync(a, cts, skipOnPasswordCancel: true);
                    if (r != null) results.Add(r); else skipped.Add(Path.GetFileName(a));
                }
                catch (ArchiveIngestException ex)
                {
                    skipped.Add($"{Path.GetFileName(a)} ({ex.Message})");
                }
            }
            if (results.Count == 0)
            {
                ShowMessage("No archive was extracted" + (skipped.Count > 0 ? ": " + string.Join("; ", skipped) : "."), InfoSeverity.Warning);
                return;
            }
            var caseDir = CaseFolder.Trim();
            var origins = ApplyArchiveResults(src, results);
            var roots = _effectiveRoots!;
            SetArchiveScanning();
            var token = cts.Token;
            var (scan, pcaps, evidence, truncated) = await Task.Run(() =>
            {
                var s = ArtifactScanner.ScanSources(roots, ct: token, origins: origins);
                // Loose captures / memory images next to the archives are still evidence of this source.
                var mem = ArtifactScanner.ScanMemoryImages(new[] { src }, token).Concat(s.MemoryImages)
                    .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                s = s with { MemoryImages = mem };
                var pc = MergePcaps(new[] { src }.Concat(roots).Select(r => PcapScanner.Scan(r, null, token)));
                var (ev, tr) = FindEvidenceArchives(s.Systems, token);
                return (s, pc, ev, tr);
            }, token);
            SetContainerArchives(Array.Empty<string>());
            await ApplyScanAsync(scan, pcaps, caseDir, src, isFile: false, pcapRoot: src);
            SetEvidenceArchives(evidence, truncated);
            if (skipped.Count > 0)
                ShowMessage($"Extracted and scanned {results.Count} archive(s). Not extracted: {string.Join("; ", skipped)}.", InfoSeverity.Warning);
        }
        catch (OperationCanceledException) { ShowMessage("Scan cancelled.", InfoSeverity.Warning); }
        catch (Exception ex) { ShowError("Extract and scan failed", ex); }
        finally
        {
            IsArchiveBusy = false;
            if (_scanCts == cts) IsScanning = false;
        }
    }

    internal static PcapScanResult MergePcaps(IEnumerable<PcapScanResult> results)
    {
        var all = results.ToList();
        var files = all.SelectMany(r => r.Files).GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var trunc = all.FirstOrDefault(r => r.Truncated);
        return new PcapScanResult(files, all.Sum(r => r.EntriesSeen), all.Sum(r => r.FilesProbed), trunc != null, trunc?.TruncatedReason);
    }

    /// <summary>Container archives of a source folder (files in the folder and its sub-folders) when no system root is found there.</summary>
    internal static List<string> FindContainerArchives(string folder, IReadOnlyList<DiscoveredSystem> systems)
    {
        var found = new List<string>();
        if (!Directory.Exists(folder)) return found;
        if (systems.Any(s => SourceLayoutDetector.LooksLikeSystemRoot(s.RootPath)) || HasSystemRootWithin(folder, 3)) return found;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder).Concat(Directory.EnumerateDirectories(folder).SelectMany(SafeFiles)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }
        foreach (var f in files)
            if (ArchiveFormatDetector.HasArchiveExtension(f) && IsArchiveFile(f)) found.Add(f);
        return found;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool HasSystemRootWithin(string dir, int depth)
    {
        if (SourceLayoutDetector.LooksLikeSystemRoot(dir)) return true;
        if (depth == 0) return false;
        try { return Directory.EnumerateDirectories(dir).Any(d => HasSystemRootWithin(d, depth - 1)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private const int EvidenceArchiveListMax = 500;
    private const int EvidenceArchiveWalkMax = 400_000;

    /// <summary>Archives inside the systems' trees (by extension, bounded walk; live volumes are skipped): evidence, listed only.</summary>
    internal static (List<string> Found, bool Truncated) FindEvidenceArchives(IReadOnlyList<DiscoveredSystem> systems, CancellationToken ct)
    {
        var found = new List<string>();
        int seen = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var s in systems.Where(s => s.AcquisitionKind != AcquisitionKind.LiveVolume && SourceLayoutDetector.LooksLikeSystemRoot(s.RootPath)))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(s.RootPath, "*", opts))
                {
                    if (++seen % 4096 == 0) ct.ThrowIfCancellationRequested();
                    if (seen > EvidenceArchiveWalkMax) return (found, true);
                    var ext = Path.GetExtension(f);
                    if (ext.Length == 0 || !ArchiveFormatDetector.HasArchiveExtension(f)) continue;
                    found.Add(f);
                    if (found.Count >= EvidenceArchiveListMax) return (found, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (found, false);
    }

    private static string CommonParent(IReadOnlyList<string> paths)
    {
        var parts = paths.Select(p => Path.GetFullPath(p).TrimEnd('\\').Split('\\')).ToList();
        int n = parts.Min(p => p.Length);
        int i = 0;
        while (i < n && parts.All(p => string.Equals(p[i], parts[0][i], StringComparison.OrdinalIgnoreCase))) i++;
        if (i == 0) return paths[0];
        var common = string.Join("\\", parts[0].Take(i));
        return common.EndsWith(':') ? common + "\\" : common;
    }
}
