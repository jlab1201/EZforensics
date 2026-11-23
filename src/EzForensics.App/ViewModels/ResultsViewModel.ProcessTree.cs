using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Execution;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;

namespace EzForensics.App.ViewModels;

/// <summary>Analyze side of the process tree: hosts the tree in the split pane above the events grid, the entry points and the callbacks the tree uses (rows in grid, Add to Evidence).</summary>
public sealed partial class ResultsViewModel
{
    /// <summary>Open process tree (the top half of the right pane), or null when only the events grid is shown. Opening another CSV keeps it open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessTreeOpen))]
    private ProcessTreeViewModel? _processTree;

    public bool IsProcessTreeOpen => ProcessTree != null;

    /// <summary>The host has Volatility process lists or an event log CSV (case tree "Process tree" menu item; only file names are checked).</summary>
    public static bool CanShowProcessTree(string? hostFolder)
    {
        if (string.IsNullOrEmpty(hostFolder)) return false;
        try { return ProcessGraphBuilder.FindFiles(hostFolder).CanBuildTree; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Entry points (hint strip, host menu): opens the split with the tree of this host (the same tree is reused for the same host, else the old one is
    /// disposed). Returns the tree once it has loaded (null when it was closed meanwhile), for callers that select a process afterwards.
    /// </summary>
    public async Task<ProcessTreeViewModel?> OpenProcessTreeAsync(string hostFolder)
    {
        if (ProcessTree is { } open && SamePath(open.HostFolder, hostFolder))
        {
            await open.LoadTask;
            return ReferenceEquals(ProcessTree, open) ? open : null;
        }
        return await OpenNewProcessTreeAsync(hostFolder);
    }

    /// <summary>Fire-and-forget form of <see cref="OpenProcessTreeAsync"/> for the menus.</summary>
    public void OpenProcessTree(string hostFolder) => _ = OpenProcessTreeAsync(hostFolder);

    private async Task<ProcessTreeViewModel?> OpenNewProcessTreeAsync(string hostFolder)
    {
        CloseProcessTree();
        var name = Path.GetFileName(hostFolder.TrimEnd('\\', '/'));
        var vm = new ProcessTreeViewModel(hostFolder, string.IsNullOrEmpty(name) ? hostFolder : name, OpenProcessRowsInGrid, CloseProcessTree, CorrelateProcessesAsync)
        {
            FlagRowsSink = FlagProcessRows,
        };
        ProcessTree = vm;
        vm.LoadTask = vm.LoadAsync();
        await vm.LoadTask;
        return ReferenceEquals(ProcessTree, vm) ? vm : null;
    }

    /// <summary>Closes the split (Close button); releases the graph and the facet CSVs.</summary>
    public void CloseProcessTree()
    {
        var t = ProcessTree;
        if (t == null) return;
        ProcessTree = null;
        t.Dispose();
    }

    /// <summary>Raised on the UI thread when a forced process correlation of a host finished.</summary>
    public event Action<string>? ProcessCorrelationRebuilt;

    /// <summary>Called when a CSV opened: sets the PID link host and closes the graph when the file has no PID links.</summary>
    private void UpdatePidLinks(string path, IReadOnlyList<string> headers)
    {
        PidLinkHost = PidLinkHostOf(path);
        // a file without PID links gets the full height; a file with links never opens the graph and leaves an open one alone
        if (!HasPidLinks(PidLinkHost, headers)) CloseProcessTree();
    }

    /// <summary>The grid renders PID links for this file: its host has process data and the headers have a PID column or text columns to scan (<see cref="PidLinks.HasPidSource"/>).</summary>
    public static bool HasPidLinks(string? pidLinkHost, IReadOnlyList<string> headers) => pidLinkHost != null && PidLinks.HasPidSource(headers);

    /// <summary>
    /// Host whose process tree the open CSV's PIDs open (PID columns and "NewProcessId: 0x…" tokens are links); null = no links (super timeline, <c>_Case</c>, a host
    /// without process data). Set before the grid builds its columns.
    /// </summary>
    public string? PidLinkHost { get; private set; }

    private string? PidLinkHostOf(string csvPath)
    {
        var host = PidLinks.HostFolderOfCsv(_treeFolder, csvPath);
        return host != null && CanShowProcessTree(host) ? host : null;
    }

    /// <summary>
    /// A click on a PID link in the grid (data row <paramref name="row"/> of <paramref name="csvPath"/>): opens the split with the host's tree (reused for the same host,
    /// correlated if needed) and selects the instance the row points at, using the row's time column and, for a memory CSV, its CreateTime.
    /// </summary>
    public async Task OpenProcessAtAsync(string csvPath, long row, int pid)
    {
        try
        {
            bool current = string.Equals(csvPath, CurrentFile, StringComparison.OrdinalIgnoreCase);
            var host = current ? PidLinkHost : PidLinkHostOf(csvPath);
            if (host == null || pid <= 0) return;
            DateTime? time = null, create = null;
            int? childPid = null;
            if (current && _index is { } idx && row >= 0 && row < idx.RowCount)
            {
                try
                {
                    var cells = idx.ReadRow(row);
                    time = PidLinks.RowTime(idx.Headers, cells);
                    childPid = PidLinks.RowChildPid(idx.Headers, cells, pid);
                    if (ProcessPidResolver.IsMemoryCsv(csvPath) || ProcessPidResolver.IsProcessListCsv(csvPath)) create = PidLinks.RowCreateTime(idx.Headers, cells);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { AppLog.Warn("PID link: could not read the row: " + ex.Message); }
            }
            AppLog.Info($"Analyze: PID {pid} of {Path.GetFileName(csvPath)} row {row + 1} opened in the process tree of {host}.");
            var tree = await OpenProcessTreeAsync(host);
            ProcessNode? child = null;
            if (tree != null && childPid is { } cp && cp != pid) child = tree.FindNode(cp, time, csvPath, create);
            tree?.SelectPid(pid, time, csvPath, create, child);
        }
        catch (Exception ex) { ShowError("Could not open the PID in the process tree", ex); }
    }

    /// <summary>
    /// Host menu "Rebuild process correlation": queues the built-in correlator for the host (forced, even when up to date); when it has finished the case tree
    /// refreshes (case job watcher) and an open tree of that host reloads from the new files.
    /// </summary>
    public void RebuildProcessCorrelation(string hostFolder)
    {
        var job = EnqueueProcessCorrelation(hostFolder, force: true);
        if (job == null) { StatusText = "Could not rebuild the process correlation: the EZ Forensics executable path is unknown."; return; }
        var name = Path.GetFileName(hostFolder.TrimEnd('\\', '/'));
        StatusText = $"Rebuilding the process correlation of {name} (see the Jobs list on Ingest)…";
        AppLog.Info($"Analyze: process correlation of {hostFolder} queued.");
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Job.State) || !job.IsFinished) return;
            AppServices.OnUi(() =>
            {
                StatusText = job.State == JobState.Succeeded ? $"Process correlation of {name} rebuilt."
                    : $"The process correlation of {name} did not finish ({job.StatusReason ?? job.State.ToString()}); see the Jobs list on Ingest.";
                if (job.State == JobState.Succeeded)
                {
                    ProcessCorrelationRebuilt?.Invoke(hostFolder);
                    if (ProcessTree is { } open && SamePath(open.HostFolder, hostFolder)) _ = OpenNewProcessTreeAsync(hostFolder);
                }
            });
        };
    }

    /// <summary>The built-in correlator job of the host (the same job as the Ingest step); null when the executable path is unknown.</summary>
    private Job? EnqueueProcessCorrelation(string hostFolder, bool force)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        var caseDir = Path.GetDirectoryName(Path.GetFullPath(hostFolder).TrimEnd('\\', '/')) ?? hostFolder;
        return S.Queue.Enqueue(ProcessCorrelator.Spec(exe, hostFolder, force), caseDir);
    }

    /// <summary>
    /// Processes page, output missing or stale: runs the correlator job for the host (an unfinished one of the same host is awaited instead of queueing another) and
    /// completes when it has finished. Null = it succeeded; else why not. Cancelling only stops the wait: the job keeps running in the Jobs list on Ingest.
    /// </summary>
    public async Task<string?> CorrelateProcessesAsync(string hostFolder, CancellationToken ct)
    {
        var host = Path.GetFullPath(hostFolder).TrimEnd('\\', '/');
        var job = S.Queue.Jobs.FirstOrDefault(j => !j.IsFinished && j.Spec.ToolId == ProcessCorrelator.ToolId
            && j.Spec.Args.SkipWhile(a => a != "--host").Skip(1).FirstOrDefault() is { } h && string.Equals(Path.GetFullPath(h).TrimEnd('\\', '/'), host, StringComparison.OrdinalIgnoreCase));
        if (job == null)
        {
            job = EnqueueProcessCorrelation(hostFolder, force: false);
            if (job == null) return "the EZ Forensics executable path is unknown";
            AppLog.Info($"Analyze: process correlation of {hostFolder} queued (output missing or out of date).");
        }
        await job.Recorded.WaitAsync(ct);
        return job.State == JobState.Succeeded ? null : job.StatusReason ?? job.State.ToString();
    }

    /// <summary>
    /// Process tree "Flag all" / "Flag rows": gives the rows the last used flag in each CSV. The open grid's CSV goes through its own flag store (so the grid redraws and
    /// its next save keeps them); any other CSV opens its sidecar, flags and saves it now. Returns how many rows changed.
    /// </summary>
    internal int FlagProcessRows(IReadOnlyList<ProcessCsvRows> files)
    {
        var id = EffectiveLastFlagId();
        int changed = 0;
        foreach (var f in files)
        {
            if (f.Rows.Count == 0) continue;
            if (_flags != null && CurrentFile != null && SamePath(f.CsvPath, CurrentFile))
            {
                _flags.DefaultFlagId = id;
                changed += _flags.SetFlag(f.Rows, id);
                AfterFlagEdit(f.Rows.ToList());
                continue;
            }
            try
            {
                using var store = RowFlagStore.Open(f.CsvPath);
                changed += store.SetFlag(f.Rows, id);
                store.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Error("Could not flag the process rows of " + f.CsvPath, ex);
            }
        }
        return changed;
    }

    /// <summary>
    /// Detail card "Open rows in grid": opens the CSV and applies the rows' filter. A single "column = value" (a memory facet's PID) goes into the column's filter-row
    /// cell; anything else (events: EventId + TimeCreated second, ORed per row) goes into the extra filter group.
    /// </summary>
    public void OpenProcessRowsInGrid(string csvPath, FilterNode filter)
    {
        _ = OpenProcessRowsAsync(csvPath, filter);
    }

    private async Task OpenProcessRowsAsync(string csvPath, FilterNode filter)
    {
        await GuardAsync(async () =>
        {
            if (!string.Equals(csvPath, CurrentFile, StringComparison.OrdinalIgnoreCase)) await OpenCsvAsync(csvPath);
            if (!RevealFile(csvPath)) { await RefreshTreeAsync(); RevealFile(csvPath); }
            if (!string.Equals(csvPath, CurrentFile, StringComparison.OrdinalIgnoreCase)) return;
            GlobalText = "";
            FlagFilter = null;
            EditFilters(() =>
            {
                foreach (var c in Cells) c.ResetSilently();
                Extra.Children.Clear();
                if (filter is FilterCondition { Op: ColumnFilterMode.Equals } fc && CellFor(fc.Column) is { } cell) cell.Set(ColumnFilterMode.Equals, fc.Value);
                else Extra.Children.Add(filter.Clone());
            });
        }, "Could not open the rows in the table");
    }
}
