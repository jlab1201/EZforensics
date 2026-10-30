using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Execution;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;
using EzForensics.Core.Zeek;

namespace EzForensics.App.ViewModels;

/// <summary>Tools page: simple run panel for Zeek on a chosen capture or folder of captures.</summary>
public sealed partial class ToolsViewModel
{
    private readonly List<Job> _zeekJobs = new();
    private string? _zeekCaseDir;

    [ObservableProperty] private bool _zeekIncludeLocal = true;
    private SpecialCommandTileViewModel? _zeekCommand;

    public bool ZeekReady => S.Zeek?.Selected != null;
    public string ZeekEngineText => S.Zeek?.Selected is { } z ? $"Zeek {z.Version} · {z.EngineLabel} ({z.OriginLabel})"
        : S.IsProbingZeek || S.Zeek == null ? "Checking for Zeek…" : "Zeek is not set up yet. Install it above in one click (Docker Desktop or WSL).";

    private const string ZeekInputHelp = "Pick a packet capture (pcap / pcapng) or a folder that contains captures.";

    /// <summary>The Command tile: capture or folder, case folder, the command for the (first) capture as it will run, Run.</summary>
    public SpecialCommandTileViewModel ZeekCommand => _zeekCommand ??= CreateZeekCommand();

    private SpecialCommandTileViewModel CreateZeekCommand()
    {
        var tile = new SpecialCommandTileViewModel("special:zeek", "zeek", RunZeekAsync,
            () => ZeekReady ? null : "Zeek is not set up yet: install it from the Tools table on the Overview tab.")
        {
            AutomationId = "Tools.Zeek.Command",
            SourceLabel = "Packet capture or folder",
            SourcePlaceholder = "No capture selected",
            SourceHelp = ZeekInputHelp,
            AllowFile = true, AllowFolder = true,
            FileFilter = "Packet captures|*.pcap;*.pcapng;*.cap;*.dmp;*.pcap*|All files|*.*",
            OutputHelp = "The case folder: logs and CSVs go to <case>\\Network (per host in a case made by Ingest). Auto uses a new folder under the case root for every run.",
            Caption = "One job per capture (found by its pcap / pcapng header): Zeek logs (conn, dns, http, ssl, files, x509, notice, weird, ...) and CSV copies with a DateTimeUtc column.",
            SecondaryText = "Open in Analyze",
            SecondaryCommand = OpenZeekResultsCommand,
        };
        tile.SetRows(new[] { new SpecialCommandViewModel(ExtraArgsStore.Zeek, null, ZeekPreview) });
        tile.Changed += () => tile.SourceHelp = ZeekInputHelp;
        return tile;
    }

    /// <summary>The command for the (first) capture, as it will run; before Zeek is set up the engine shows as <c>&lt;Docker or WSL&gt;</c>.</summary>
    private string ZeekPreview(IReadOnlyList<string> extra)
    {
        var tile = ZeekCommand;
        var input = tile.Source;
        var pcap = File.Exists(input) ? input : Path.Combine(string.IsNullOrEmpty(input) ? "<folder>" : input, "<capture>.pcap");
        var z = S.Zeek?.Selected;
        if (z == null)
            return "<Docker or WSL> zeek " + ExtraArgsStore.Join(ZeekCommandBuilder.ZeekArgs(pcap, ZeekIncludeLocal, extra));
        var outDir = Path.Combine(ZeekJobs.OutputRoot(tile.OutputDir), ZeekJobs.FolderNameFor(pcap));
        return ZeekCommandBuilder.Build(z, new ZeekRunOptions { Pcap = pcap, OutputDir = outDir, IncludeLocal = ZeekIncludeLocal, ExtraArgs = extra },
            "ezforensics-zeek-xxxxxxxx").Display;
    }

    private void InitZeek() => S.ZeekChanged += RaiseZeek;

    private void RaiseZeek()
    {
        OnPropertyChanged(nameof(ZeekReady));
        OnPropertyChanged(nameof(ShowZeekPanel));
        OnPropertyChanged(nameof(ZeekEngineText));
        _zeekCommand?.Refresh();
    }

    partial void OnZeekIncludeLocalChanged(bool value) => _zeekCommand?.Refresh();

    [RelayCommand]
    private void SetUpZeek() => OpenSection(ToolsSection.Zeek);

    private async Task RunZeekAsync()
    {
        var z = S.Zeek?.Selected ?? (await S.RefreshZeekAsync()).Selected;
        if (z == null) { ShowMessage("Zeek is not set up yet. Install it from the Tools table on the Overview tab, then Run.", InfoSeverity.Warning); return; }
        var tile = ZeekCommand;
        var row = tile.Rows[0];
        var input = tile.Source;
        var caseDir = tile.OutputDir.Trim();
        if (string.IsNullOrEmpty(caseDir)) { ShowMessage("Choose a case folder.", InfoSeverity.Warning); return; }
        await GuardAsync(async () =>
        {
            // a layout v2 case places each capture under the host it was collected on (evidence read here, off the UI thread)
            var (scan, evidence) = await Task.Run(() =>
            {
                var sc = PcapScanner.Scan(input);
                return (sc, ZeekJobs.IsHostLayout(caseDir) ? NetworkTriage.ReadEvidence(sc.Files, null) : null);
            });
            tile.SourceHelp = scan.Files.Count == 0 ? "No packet capture found (files are recognised by their pcap / pcapng header)."
                : $"{scan.Files.Count} capture(s), {NetworkTriage.FormatSize(scan.TotalBytes)}" + (scan.Truncated ? $" ({scan.TruncatedReason})" : "");
            if (scan.Files.Count == 0) { ShowMessage($"No packet capture (pcap / pcapng) was found in {input}.", InfoSeverity.Warning); return; }
            Directory.CreateDirectory(caseDir);
            S.Queue.SourcePath = input;
            var jobs = ZeekJobs.Enqueue(S.Queue, z, scan.Files.Select(f => f.Path), caseDir, new ZeekRunOptions { IncludeLocal = ZeekIncludeLocal, ExtraArgs = row.CurrentArgs },
                evidence: evidence);
            foreach (var j in _zeekJobs) j.PropertyChanged -= OnZeekJobChanged;
            _zeekJobs.Clear();
            _zeekJobs.AddRange(jobs);
            _zeekCaseDir = caseDir;
            foreach (var j in jobs) j.PropertyChanged += OnZeekJobChanged;
            CurrentJob = jobs[0];
            Log.Attach(jobs[0], S.Output);
            S.LastCaseDir = caseDir;
            ShowMessage(evidence != null
                ? $"Queued {jobs.Count} Zeek job(s). Logs and CSVs go to <host>\\Network in {caseDir} (_Case when the capture's host was not identified)."
                : $"Queued {jobs.Count} Zeek job(s). Logs and CSVs go to {ZeekJobs.OutputRoot(caseDir)}.", InfoSeverity.Info);
            var summary = tile.SourceHelp;
            tile.RollAutoOutputDir();
            tile.SourceHelp = summary;
        }, "Could not start Zeek");
    }

    /// <summary>Follows the next job in the live log when one finishes; reports when all are done.</summary>
    private void OnZeekJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Job.State) || sender is not Job j || !j.IsFinished) return;
        var next = _zeekJobs.FirstOrDefault(x => !x.IsFinished);
        if (next != null)
        {
            if (CurrentJob == j) { CurrentJob = next; Log.Attach(next, S.Output); }
            return;
        }
        int ok = _zeekJobs.Count(x => x.State == JobState.Succeeded);
        ShowMessage(ok == _zeekJobs.Count
                ? $"Zeek finished: {ok} capture(s) processed. Open it on the Analyze page ({_zeekCaseDir}\\Network\\Zeek) to review the CSVs."
                : $"Zeek finished: {_zeekJobs.Count - ok} of {_zeekJobs.Count} job(s) did not succeed. See the Jobs page for logs.",
            ok == _zeekJobs.Count ? InfoSeverity.Success : InfoSeverity.Warning);
    }

    [RelayCommand]
    private void OpenZeekResults()
    {
        if (_zeekCaseDir == null) return;
        var csv = Directory.Exists(ZeekJobs.OutputRoot(_zeekCaseDir))
            ? Directory.EnumerateFiles(ZeekJobs.OutputRoot(_zeekCaseDir), "conn.csv", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (csv != null) S.OpenInResults(_zeekCaseDir, csv);
        else { S.LastCaseDir = _zeekCaseDir; S.Navigate<ResultsViewModel>(); }
    }
}
