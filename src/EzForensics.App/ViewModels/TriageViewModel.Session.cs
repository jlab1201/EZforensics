using EzForensics.App.Services;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Ingest page: "Restart as Administrator" keeps the user's work. The page (source, case, scan result, card ticks, row ticks) is saved as an
/// <see cref="IngestSessionSnapshot"/>, the app relaunches elevated with <see cref="IngestSessionLaunch.Switch"/> and the new instance shows
/// the same page without scanning again.
/// </summary>
public sealed partial class TriageViewModel
{
    /// <summary>Row ticks of a restored session, for rows planned after the restore (memory plugins, persistence); null otherwise.</summary>
    private Dictionary<string, bool>? _restoredTicks;
    private bool _evidenceArchivesTruncated;

    /// <summary>The page as it is now (never a password: archive passwords stay in memory).</summary>
    internal IngestSessionSnapshot CaptureSession() => new()
    {
        SavedUtc = DateTime.UtcNow,
        Source = Source,
        CaseName = CaseName,
        CaseFolder = CaseFolder,
        CaseFolderEdited = _caseFolderEdited,
        Scan = HasScan && _layout != null
            ? new TriageScanResult(_summarySource, _layout, _systems, _found ?? new List<DetectedArtifact>()) { MemoryImages = _memoryImages }
            : null,
        Pcaps = _pcaps,
        PcapRoot = _pcapRoot,
        SummarySource = _summarySource,
        SummaryIsFile = _summaryIsFile,
        EffectiveRoots = _effectiveRoots,
        ArchiveOrigins = _archiveOrigins,
        ArchiveSource = _archiveSource,
        ArchiveCaseDir = _archiveCaseDir,
        Archives = Archives.Where(a => !a.IsDeleted).Select(a => new IngestArchiveCard(a.Origin, a.Warnings)).ToList(),
        EvidenceArchives = EvidenceArchives.ToList(),
        EvidenceArchivesTruncated = _evidenceArchivesTruncated,
        ContainerArchives = ContainerArchives.ToList(),
        Systems = Systems.Select(s => new IngestSystemState(s.RootPath, s.IsIncluded, s.HostName)).ToList(),
        MemoryImages = MemoryImages.Select(m => new IngestPathState(m.Path, m.IsIncluded)).ToList(),
        PcapFiles = Pcaps.Select(p => new IngestPathState(p.Path, p.IsIncluded)).ToList(),
        IncludePagefile = IncludePagefile,
        Rows = WantedTicks().Select(kv => new IngestRowTick(kv.Key, kv.Value)).ToList(),
    };

    /// <summary>Shows a saved page again: the scan result is applied as it was (no new scan), then the card and row ticks.</summary>
    public async Task RestoreSessionAsync(IngestSessionSnapshot s)
    {
        if (RunActive()) { AppLog.Warn("Ingest: the saved session was not restored, a run is going on."); return; }
        AppLog.Info($"Ingest: restoring the session saved at {s.SavedUtc:u} (source {s.Source}, case {s.CaseFolder}, scanned={s.Scan != null}).");
        try
        {
            _restoredTicks = s.Rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Selected);
            Source = s.Source;
            CaseName = s.CaseName;
            _settingCaseFolder = true;
            CaseFolder = s.CaseFolder;
            _settingCaseFolder = false;
            _caseFolderEdited = s.CaseFolderEdited;
            if (s.Scan == null) return;

            if (s.EffectiveRoots is { Count: > 0 } roots)
            {
                _effectiveRoots = roots;
                _archiveOrigins = s.ArchiveOrigins;
                _archiveSource = s.ArchiveSource;
                _archiveCaseDir = s.ArchiveCaseDir;
                Archives.Clear();
                foreach (var a in s.Archives) Archives.Add(new ArchiveInfoItem(a.Origin, a.Warnings));
                RaiseArchives();
            }

            IsScanning = true;
            try
            {
                if (S.Zeek == null) { try { await S.RefreshZeekAsync(); } catch (Exception ex) { AppLog.Error("Zeek discovery failed", ex); } }
                if (!S.VolatilityProbed) { try { await S.RefreshVolatilityAsync(); } catch (Exception ex) { AppLog.Error("Volatility discovery failed", ex); } }
                var pcaps = s.Pcaps ?? new PcapScanResult(Array.Empty<PcapFile>(), 0, 0, false, null);
                if (pcaps.Files.Count > 0) await EnsureRitaProbedAsync();
                await ApplyScanAsync(s.Scan, pcaps, CaseFolder, s.SummarySource, s.SummaryIsFile, s.PcapRoot);
            }
            finally { IsScanning = false; }
            SetEvidenceArchives(s.EvidenceArchives, s.EvidenceArchivesTruncated);
            SetContainerArchives(s.ContainerArchives);

            // card ticks and host labels (a label change re-plans, keeping the restored ticks)
            foreach (var st in s.Systems)
                if (Systems.FirstOrDefault(x => SameDir(x.RootPath, st.RootPath)) is { } item)
                {
                    if (item.HostName != st.HostName) item.HostName = st.HostName;
                    item.IsIncluded = st.Included;
                }
            foreach (var st in s.MemoryImages)
                if (MemoryImages.FirstOrDefault(x => string.Equals(x.Path, st.Path, StringComparison.OrdinalIgnoreCase)) is { } m && m.CanToggle) m.IsIncluded = st.Included;
            foreach (var st in s.PcapFiles)
                if (Pcaps.FirstOrDefault(x => string.Equals(x.Path, st.Path, StringComparison.OrdinalIgnoreCase)) is { } p) p.IsIncluded = st.Included;
            IncludePagefile = s.IncludePagefile;
            ApplyRestoredTicks();
            ReportRun("Restarted as Administrator: the Ingest page was restored as you left it (no new scan). Run selected when ready.", InfoSeverity.Info);
        }
        catch (Exception ex) { ShowError("Could not restore the Ingest page", ex); }
    }

    /// <summary>Gives the rows that are planned now their restored tick.</summary>
    private void ApplyRestoredTicks()
    {
        if (_restoredTicks == null) return;
        foreach (var p in Planned)
            if (_restoredTicks.TryGetValue(p.Key, out var on)) p.SetWanted(on);
    }

    /// <summary>Saves the page and relaunches elevated (the elevated instance restores it). False when nothing was relaunched (UAC declined).</summary>
    private bool SaveSessionAndRestartElevated()
    {
        string? path = null;
        try
        {
            path = IngestSessionStore.DefaultPath;
            IngestSessionStore.Save(CaptureSession(), path);
            AppLog.Info("Ingest: session saved for the restart as Administrator: " + path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save the Ingest session before the restart", ex);
            path = null;
        }
        if (Elevation.RestartAsAdministrator(path == null ? Array.Empty<string>() : IngestSessionLaunch.Args(path))) return true;
        if (path != null) IngestSessionStore.Delete(path);
        return false;
    }
}
