using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Catalog;
using EzForensics.Core.Execution;
using EzForensics.Core.Plaso;
using EzForensics.Core.Tools;

namespace EzForensics.App.ViewModels;

public sealed partial class RemoteItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private RemoteFileStatus _status;

    public RemoteItem(RemoteToolFile file)
    {
        File = file;
        _status = file.Status;
        _isSelected = file.Status == RemoteFileStatus.Changed;
    }

    public RemoteToolFile File { get; }
    public string Name => File.Name;
    /// <summary>Name without the .zip extension (display only).</summary>
    public string DisplayName => ToolDisplay.StripZip(File.Name);
    public string Flavor => File.IsNet9 ? "net9" : "net4";
    public string Size => FormatSize(File.Size);

    public static string FormatSize(long size) => size <= 0 ? "" : size >= 1 << 20 ? $"{size / (double)(1 << 20):0.0} MB" : $"{size / 1024.0:0} KB";
}

public enum ToolRowKind { EzZip, Plaso, Volatility, Zeek, Rita, Maps }

/// <summary>One row of the Overview tools table: a catalog tool (and its download), plaso / Volatility / Zeek / RITA, or the maps / batch files sync.</summary>
public sealed class ToolRow : ObservableObject
{
    private readonly string _localSize;
    private bool _selected;
    private string _status, _version, _path;
    private string? _pending;

    public ToolRow(ToolRowKind kind, string group, int groupOrder, string name, string status, string version, string path, string localSize,
        RemoteItem? remote = null, string zip = "")
    {
        Kind = kind; Group = group; GroupOrder = groupOrder; Name = name; _status = status; _version = version; _path = path;
        _localSize = localSize; Remote = remote; Zip = zip;
        if (remote != null) { _selected = remote.IsSelected; remote.PropertyChanged += OnRemoteChanged; }
    }

    public ToolRowKind Kind { get; }
    public string Group { get; }
    public int GroupOrder { get; }
    public string Name { get; }
    public string Zip { get; }
    public RemoteItem? Remote { get; }
    /// <summary>Download size from the update check, else the size recorded when it was installed.</summary>
    public string Size => Remote is { Size.Length: > 0 } r ? r.Size : _localSize;

    /// <summary>Status text; a pending text ("Queued", "Installing…") while the primary action runs wins over the real status.</summary>
    public string Status => _pending ?? _status;
    public string Version => _version;
    public string Path => _path;

    /// <summary>Full product version of the exe (EZ tools append "+commit"); the table shows the short version.</summary>
    public string? VersionToolTip
    {
        get
        {
            if (Kind != ToolRowKind.EzZip || string.IsNullOrEmpty(_path)) return null;
            try
            {
                var pv = System.Diagnostics.FileVersionInfo.GetVersionInfo(_path).ProductVersion;
                if (string.IsNullOrWhiteSpace(pv)) return null;
                int plus = pv.IndexOf('+');
                if (plus < 0) return pv;
                var hash = pv[(plus + 1)..];
                return $"{pv[..plus]} (build {(hash.Length > 8 ? hash[..8] : hash)})";
            }
            catch (Exception) { return null; }
        }
    }

    public void Update(string status, string version, string path)
    {
        if (_status != status) { _status = status; OnPropertyChanged(nameof(Status)); }
        if (_version != version) { _version = version; OnPropertyChanged(nameof(Version)); }
        if (_path != path) { _path = path; OnPropertyChanged(nameof(Path)); }
    }

    public void SetPending(string? text)
    {
        if (_pending == text) return;
        _pending = text;
        OnPropertyChanged(nameof(Status));
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (Remote != null && Remote.IsSelected != value) Remote.IsSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnRemoteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteItem.IsSelected) && Remote != null && _selected != Remote.IsSelected)
        {
            _selected = Remote.IsSelected;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public void Detach()
    {
        if (Remote != null) Remote.PropertyChanged -= OnRemoteChanged;
    }
}

public sealed partial class GuiToolTile : ObservableObject
{
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private string? _path;

    public GuiToolTile(ToolDefinition def) => Def = def;

    public ToolDefinition Def { get; }
    public string Name => Def.DisplayName;
    public string Description => Def.Description;
    public bool RequiresAdmin => Def.RequiresAdmin;
    public string Glyph => Def.Id switch
    {
        "RegistryExplorer" => "",
        "ShellBagsExplorer" => "",
        "MFTExplorer" => "",
        "JumpListExplorer" => "",
        "SDBExplorer" => "",
        "TimelineExplorer" => "",
        _ => "",
    };
}

public sealed partial class ToolManagerViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    /// <summary>plaso timeline options (Timeline page).</summary>
    public PlasoOptionsViewModel PlasoOptions { get; } = new();

    [ObservableProperty] private string _toolsRoot = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSyncing;
    /// <summary>The primary action is installing the ticked rows.</summary>
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _remoteSummary = "";
    private bool _autoChecked;

    public ToolManagerViewModel()
    {
        foreach (var d in S.Catalog.Where(d => d.Kind == ToolKind.Gui).OrderBy(d => d.DisplayName))
            GuiTools.Add(new GuiToolTile(d));
        ToolsRoot = S.Settings.ToolsRoot;
        S.ToolsRescanned += OnRescanned;
        S.PlasoChanged += OnPlasoChanged;
        S.EnsurePlasoProbed();
        ToolRowsView = new ListCollectionView(ToolRows)
        {
            GroupDescriptions = { new PropertyGroupDescription(nameof(ToolRow.Group)) },
            SortDescriptions = { new SortDescription(nameof(ToolRow.GroupOrder), ListSortDirection.Ascending), new SortDescription(nameof(ToolRow.Name), ListSortDirection.Ascending) },
        };
        OnPlasoChanged();
        _ = CheckLatestPlasoAsync();
        Zeek = new ZeekCardViewModel(ShowError, ShowMessage);
        Rita = new RitaCardViewModel(ShowError, ShowMessage);
        Volatility = new VolatilityCardViewModel(ShowError, ShowMessage);
        foreach (var card in new INotifyPropertyChanged[] { Zeek, Rita, Volatility }) card.PropertyChanged += OnCardChanged;
        S.ZeekChanged += RefreshSpecialRows;
        S.RitaChanged += RefreshSpecialRows;
        S.VolatilityChanged += RefreshSpecialRows;
        RefreshSpecialRows();
    }

    /// <summary>"Zeek (packet capture → logs)" card.</summary>
    public ZeekCardViewModel Zeek { get; }

    /// <summary>"RITA (beacons, long connections, DNS tunneling)" card.</summary>
    public RitaCardViewModel Rita { get; }


    /// <summary>"Volatility 3 (memory images)" card.</summary>
    public VolatilityCardViewModel Volatility { get; }

    public override string Title => "Tools";
    public ObservableCollection<ToolRow> ToolRows { get; } = new();
    /// <summary>The table's grouped view (Disk, Memory, Network, Timeline, GUI tools, Maintenance).</summary>
    public ICollectionView ToolRowsView { get; }
    public ObservableCollection<RemoteItem> Remote { get; } = new();
    public ObservableCollection<GuiToolTile> GuiTools { get; } = new();
    public bool HasRemote => Remote.Count > 0;

    /// <summary>Ticked rows.</summary>
    public int SelectedCount => ToolRows.Count(r => r.IsSelected);
    /// <summary>The button under the table: checks for updates until something is ticked, then installs / updates the ticked rows.</summary>
    public string PrimaryActionText => SelectedCount == 0 ? "Check for updates" : $"Install / Update ({SelectedCount})";

    /// <summary>Header checkbox: true / false when every selectable row agrees, null when mixed.</summary>
    public bool? AllSelected
    {
        get
        {
            var rows = ToolRows;
            if (rows.Count == 0 || rows.All(r => !r.IsSelected)) return false;
            return rows.All(r => r.IsSelected) ? true : null;
        }
        set
        {
            bool on = value == true;
            foreach (var r in ToolRows) r.IsSelected = on;
            RaiseSelection();
        }
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(AllSelected));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolRow.IsSelected)) RaiseSelection();
    }

    public override void OnNavigatedTo()
    {
        ToolsRoot = S.Settings.ToolsRoot;
        OnRescanned();
        OnPlasoChanged();
        if (!_pythonProbed) _ = ProbePythonAsync();
        if (_latest == null && !_checkingLatest) _ = CheckLatestPlasoAsync();
        // one quiet check per session, so status and sizes show without a click
        if (!_autoChecked && Remote.Count == 0) { _autoChecked = true; _ = CheckCoreAsync(quiet: true); }
        Zeek.OnNavigatedTo();
        Rita.OnNavigatedTo();
        Volatility.OnNavigatedTo();
    }

    private void OnRescanned()
    {
        ToolsRoot = S.Settings.ToolsRoot;
        RebuildToolRows();
        foreach (var g in GuiTools)
        {
            var t = S.ResolveNonBlocking(g.Def.Exe);
            g.Installed = t != null;
            g.Path = t?.Path;
        }
    }

    private ToolRow? _plasoRow, _volatilityRow, _zeekRow, _ritaRow;
    /// <summary>"Queued" / "Installing…" per row while the primary action runs; kept across table rebuilds.</summary>
    private readonly Dictionary<(ToolRowKind, string), string> _rowPending = new();
    private const int MaintenanceOrder = 99;

    private void RebuildToolRows()
    {
        var root = S.Settings.ToolsRoot;
        var local = SafeReadDetails(root);
        var keep = ToolRows.Where(r => r.IsSelected).Select(r => (r.Kind, r.Name)).ToHashSet();
        var rows = new List<ToolRow>();
        foreach (var d in S.Catalog)
        {
            var t = S.ResolveNonBlocking(d.Exe);
            var r = string.IsNullOrEmpty(d.Zip) ? null : Remote.FirstOrDefault(x => x.Name.Equals(d.Zip, StringComparison.OrdinalIgnoreCase));
            bool update = t != null && r is { Status: not RemoteFileStatus.UpToDate };
            var g = ToolGroups.Of(d);
            rows.Add(new ToolRow(ToolRowKind.EzZip, ToolGroups.Info(g).Title, (int)g, d.DisplayName, ToolDisplay.StatusOf(t != null, update),
                ToolDisplay.FormatVersion(t?.Version), t?.Path ?? "", LocalSize(local, d.Zip, t), r, d.Zip));
        }
        _plasoRow ??= SpecialRow(ToolRowKind.Plaso, ToolGroup.Timeline, "log2timeline / plaso");
        _volatilityRow ??= SpecialRow(ToolRowKind.Volatility, ToolGroup.Memory, "Volatility 3");
        _zeekRow ??= SpecialRow(ToolRowKind.Zeek, ToolGroup.Network, "Zeek");
        _ritaRow ??= SpecialRow(ToolRowKind.Rita, ToolGroup.Network, "RITA");
        rows.Add(_plasoRow); rows.Add(_volatilityRow); rows.Add(_zeekRow); rows.Add(_ritaRow);
        rows.Add(MapsRow(root));
        foreach (var row in rows)
        {
            if (keep.Contains((row.Kind, row.Name))) row.IsSelected = true;
            if (_rowPending.TryGetValue((row.Kind, row.Name), out var pending)) row.SetPending(pending);
        }
        foreach (var old in ToolRows) { old.PropertyChanged -= OnRowChanged; old.Detach(); }
        ToolRows.Clear();
        foreach (var row in rows) ToolRows.Add(row);
        foreach (var row in ToolRows) row.PropertyChanged += OnRowChanged;
        RefreshSpecialRows();
        RaiseSelection();
    }

    private static ToolRow SpecialRow(ToolRowKind kind, ToolGroup g, string name) =>
        new(kind, ToolGroups.Info(g).Title, (int)g, name, "", "", "", "");

    private static string Dash(string? s) => string.IsNullOrEmpty(s) || s == "-" ? "" : s;

    /// <summary>Re-reads status / version / location of the plaso, Volatility, Zeek and RITA rows from their view models (no rebuild).</summary>
    private void RefreshSpecialRows()
    {
        if (Volatility == null) return;
        _plasoRow?.Update(PlasoStatus, Dash(PlasoVersion), Dash(PlasoLocation));
        _volatilityRow?.Update(Volatility.StatusLabel, Dash(Volatility.VersionText), Dash(Volatility.LocationText));
        _zeekRow?.Update(Zeek.StatusLabel, Dash(Zeek.VersionText), Dash(Zeek.LocationText));
        _ritaRow?.Update(Rita.StatusLabel, Dash(Rita.VersionText), Dash(Rita.LocationText));
        OnPropertyChanged(nameof(CanAct));
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Status" or "StatusLabel" or "VersionText" or "IsBusy") RefreshSpecialRows();
    }

    private bool AnyCardBusy => Zeek.IsBusy || Rita.IsBusy || Volatility.IsBusy || IsPlasoInstalling;

    /// <summary>"Maps and batch files" row: newest write time and count of the Maps / BatchExamples files under the tools root.</summary>
    private ToolRow MapsRow(string root)
    {
        var files = SnapshotSyncFiles();
        var status = files.Count == 0 ? "Never synced" : $"Synced {files.Values.Max().ToLocalTime():yyyy-MM-dd}";
        return new ToolRow(ToolRowKind.Maps, "Maintenance", MaintenanceOrder, "Maps and batch files", status, files.Count == 0 ? "" : $"{files.Count} files", root, "");
    }

    private static IReadOnlyList<RemoteDetail> SafeReadDetails(string root)
    {
        try { return ToolUpdater.ReadDetails(Path.Combine(root, ToolUpdater.DetailsFileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<RemoteDetail>(); }
    }

    /// <summary>Size recorded in the tools root's download list when the tool was installed (same .NET build first).</summary>
    private static string LocalSize(IReadOnlyList<RemoteDetail> local, string zip, InstalledTool? t)
    {
        if (string.IsNullOrEmpty(zip)) return "";
        var hits = local.Where(l => l.Name.Equals(zip, StringComparison.OrdinalIgnoreCase) && l.Size > 0).ToList();
        if (hits.Count == 0) return "";
        bool net9 = t?.Flavor != BuildFlavor.Net4;
        var size = (hits.FirstOrDefault(h => h.IsNet9 == net9) ?? hits[0]).Size;
        return RemoteItem.FormatSize(size);
    }

        private async Task CheckForUpdatesAsync() => await CheckCoreAsync(quiet: false);

    /// <summary>Reads the EZ tools index; quiet (the automatic check on first open) only leaves a caption when the site is not reachable.</summary>
    private async Task CheckCoreAsync(bool quiet)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressText = "Checking https://tools.ericzimmermanstools.com …";
        Progress = 0;
        try
        {
            using var http = S.CreateHttpClient();
            var updater = new ToolUpdater(http);
            var root = S.Settings.ToolsRoot;
            var list = await updater.CheckAsync(root, S.Settings.NetVersionToDownload, _cts.Token);
            Remote.Clear();
            foreach (var f in list.OrderBy(f => f.Status == RemoteFileStatus.UpToDate).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Remote.Add(new RemoteItem(f));
            OnPropertyChanged(nameof(HasRemote));
            RebuildToolRows();
            int changed = list.Count(f => f.Status == RemoteFileStatus.Changed);
            RemoteSummary = changed == 0 ? $"Checked {DateTime.Now:HH:mm}: no updates." : $"Checked {DateTime.Now:HH:mm}: {changed} update(s) ticked.";
            ProgressText = "";
        }
        catch (OperationCanceledException) { ProgressText = "Cancelled."; }
        catch (Exception ex)
        {
            ProgressText = "";
            if (quiet) { RemoteSummary = "The EZ tools site was not reachable (check the proxy settings)."; AppLog.Warn($"Automatic tool update check failed: {ex.Message}"); }
            else ShowError("Update check failed (check the proxy settings)", ex);
        }
        finally { IsBusy = false; }
    }

    /// <summary>The button under the table: nothing ticked = check for updates (EZ site and the latest versions of plaso, Volatility, Zeek);
    /// else install / update the ticked rows: EZ zips, the maps sync, then plaso, Volatility, Zeek and RITA one after another.</summary>
    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (IsInstalling || IsBusy || IsSyncing || AnyCardBusy) return;
        if (SelectedCount == 0)
        {
            await Task.WhenAll(CheckCoreAsync(quiet: false), CheckLatestPlasoAsync(), Zeek.RefreshLatestAsync(), Volatility.RefreshLatestAsync());
            return;
        }
        var special = new (ToolRowKind Kind, Func<bool> Installed, IAsyncRelayCommand Install, IAsyncRelayCommand Update)[]
        {
            (ToolRowKind.Plaso, () => PlasoFound, InstallPlasoCommand, UpdatePlasoCommand),
            (ToolRowKind.Volatility, () => Volatility.Found, Volatility.InstallCommand, Volatility.UpdateCommand),
            (ToolRowKind.Zeek, () => Zeek.Found, Zeek.InstallCommand, Zeek.UpdateCommand),
            (ToolRowKind.Rita, () => Rita.Installed, Rita.InstallCommand, Rita.InstallCommand),
        };
        // every ticked row stays ticked until the whole run is over; then only the rows that installed are unticked
        var ticked = ToolRows.Where(r => r.IsSelected).Select(r => (r.Kind, r.Name)).ToList();
        var done = new HashSet<(ToolRowKind, string)>();
        foreach (var k in ticked) Pend(k, "Queued");
        IsInstalling = true;
        try
        {
            var ez = ticked.Where(k => k.Kind == ToolRowKind.EzZip).ToList();
            if (ez.Count > 0)
            {
                foreach (var k in ez) Pend(k, "Installing…");
                if (Remote.Count == 0) await CheckCoreAsync(quiet: false);
                bool net9 = S.Settings.NetVersionToDownload != 4;
                var sel = new List<RemoteItem>();
                var missing = new List<string>();
                foreach (var k in ez)
                {
                    var zip = RowOf(k)?.Zip ?? "";
                    var hits = Remote.Where(x => x.Name.Equals(zip, StringComparison.OrdinalIgnoreCase)).ToList();
                    var hit = hits.FirstOrDefault(x => x.File.IsNet9 == net9) ?? hits.FirstOrDefault();
                    if (hit == null) missing.Add(k.Name); else if (!sel.Contains(hit)) sel.Add(hit);
                }
                if (missing.Count > 0)
                    ShowMessage($"No download was found for: {string.Join(", ", missing)}. Check the connection and try again.", InfoSeverity.Warning);
                if (sel.Count > 0 && await DownloadAsync(sel, clearSelection: false))
                    foreach (var k in ez) if (RowOf(k)?.Status == ToolDisplay.StatusOf(true, false)) done.Add(k);
                foreach (var k in ez) Pend(k, null);
            }
            foreach (var k in ticked.Where(k => k.Kind == ToolRowKind.Maps))
            {
                Pend(k, "Installing…");
                await SyncMaps();
                Pend(k, null);
                RebuildToolRows();
                done.Add(k);
            }
            foreach (var x in special)
            {
                var k = ticked.FirstOrDefault(t => t.Kind == x.Kind);
                if (k == default) continue;
                Pend(k, "Installing…");
                try { await (x.Installed() ? x.Update : x.Install).ExecuteAsync(null); }
                catch (Exception ex) { ShowError($"Could not install {k.Name}", ex); }
                Pend(k, null);
                if (x.Installed()) done.Add(k);
            }
        }
        finally
        {
            foreach (var k in ticked) Pend(k, null);
            IsInstalling = false;
        }
        foreach (var k in done) if (RowOf(k) is { } row) row.IsSelected = false;
        var failed = ticked.Where(k => !done.Contains(k)).Select(k => k.Name).ToList();
        if (failed.Count > 0)
            ShowMessage($"Not installed: {string.Join(", ", failed)}. They are still ticked, so Install / Update tries them again.", InfoSeverity.Warning);
    }

    private ToolRow? RowOf((ToolRowKind Kind, string Name) k) => ToolRows.FirstOrDefault(r => r.Kind == k.Kind && r.Name == k.Name);

    private void Pend((ToolRowKind Kind, string Name) k, string? text)
    {
        if (text == null) _rowPending.Remove(k); else _rowPending[k] = text;
        RowOf(k)?.SetPending(text);
    }

    /// <summary>Downloads and extracts the given zips; true when it finished. clearSelection = false keeps the ticks (the primary action unticks at the end).</summary>
    private async Task<bool> DownloadAsync(List<RemoteItem> sel, bool clearSelection = true)
    {
        if (IsBusy) return false;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        try
        {
            using var http = S.CreateHttpClient();
            var updater = new ToolUpdater(http);
            var progress = new Progress<UpdateProgress>(p =>
            {
                double frac = p.TotalBytes is > 0 ? Math.Min(1, (double)p.BytesReceived / p.TotalBytes.Value) : (p.Phase == "Done" ? 1 : 0);
                Progress = p.FileCount == 0 ? 0 : ((p.FileIndex - 1) + frac) / p.FileCount;
                ProgressText = $"{p.Phase} {p.Name} ({p.FileIndex}/{p.FileCount})";
            });
            await updater.DownloadAsync(sel.Select(s => s.File), S.Settings.ToolsRoot, progress, _cts.Token);
            foreach (var s in sel) { s.Status = RemoteFileStatus.UpToDate; if (clearSelection) s.IsSelected = false; }
            RebuildToolRows();
            Progress = 1;
            ProgressText = $"Downloaded {sel.Count} file(s).";
            await S.RescanAsync();
            ShowMessage($"Downloaded and extracted {sel.Count} file(s) into {S.Settings.ToolsRoot}.", InfoSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) { ProgressText = "Download cancelled."; }
        catch (Exception ex)
        {
            ShowError("Download failed", ex);
            try { await S.RescanAsync(); } catch { }
        }
        finally { IsBusy = false; }
        return false;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncMaps()
    {
        IsSyncing = true;
        SyncStatus = "Downloading EvtxECmd maps and KAPE batch files…";
        try
        {
            await GuardAsync(async () =>
            {
                // Runs on the private maintenance queue (never the Jobs page); output goes to the app log.
                var logDir = S.Maintenance.NewLogDir("sync");
                var before = SnapshotSyncFiles();
                int ok = 0, failed = 0;
                foreach (var id in new[] { "EvtxECmd", "RECmd", "SQLECmd" })
                {
                    var def = ToolCatalog.Find(id);
                    if (def == null) continue;
                    SyncStatus = $"Syncing {def.DisplayName} maps and batch files…";
                    var spec = new JobSpec(def.Id, def.Exe, new[] { "--sync" }, Path.Combine(logDir, def.Id), $"{def.DisplayName} --sync", "Maintenance");
                    var job = await S.Maintenance.RunAsync(spec, line => AppLog.Info($"[{def.Id} --sync] {line}"), logDir: logDir);
                    if (job.State == JobState.Succeeded) ok++; else { failed++; AppLog.Warn($"{def.DisplayName} --sync: {job.StatusReason}"); }
                }
                var after = SnapshotSyncFiles();
                int added = after.Keys.Count(k => !before.ContainsKey(k));
                int updated = after.Count(kv => before.TryGetValue(kv.Key, out var t) && t != kv.Value);
                var where = $"under {S.Settings.ToolsRoot} (Maps and BatchExamples folders)";
                ShowMessage(failed == 0
                    ? $"Synced {ok} tool(s): {added} file(s) added, {updated} updated {where}."
                    : $"Synced {ok} tool(s), {failed} failed (see the app log): {added} file(s) added, {updated} updated {where}.",
                    failed == 0 ? InfoSeverity.Success : InfoSeverity.Warning);
            }, "Could not sync the maps");
        }
        finally { IsSyncing = false; SyncStatus = ""; }
    }

    private bool CanSync() => !IsSyncing;
    partial void OnIsSyncingChanged(bool value) { SyncMapsCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanAct)); }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanAct));
    /// <summary>The table's button and checkboxes wait while a check, download, sync or install run is going.</summary>
    public bool CanAct => !IsInstalling && !IsBusy && !IsSyncing && (Volatility == null || !AnyCardBusy);

    private Dictionary<string, DateTime> SnapshotSyncFiles()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = S.Settings.ToolsRoot;
            if (!Directory.Exists(root)) return map;
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .Where(d => { var n = Path.GetFileName(d); return n.Equals("Maps", StringComparison.OrdinalIgnoreCase) || n.Equals("BatchExamples", StringComparison.OrdinalIgnoreCase); }))
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    map[f] = File.GetLastWriteTimeUtc(f);
        }
        catch { }
        return map;
    }

    [RelayCommand]
    private void LaunchGui(GuiToolTile? tile)
    {
        if (tile == null) return;
        if (!tile.Installed || tile.Path == null) { _ = DownloadToolAsync(tile); return; }
        try
        {
            GuiLauncher.Launch(tile.Path, null, tile.RequiresAdmin);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowMessage($"{tile.Name} was not started (elevation cancelled).", InfoSeverity.Warning);
        }
        catch (Exception ex) { ShowError($"Could not start {tile.Name}", ex); }
    }

        private async Task DownloadToolAsync(GuiToolTile? tile)
    {
        if (tile != null) await InstallToolAsync(tile.Def);
    }

    /// <summary>Installs (or reinstalls / updates) one catalog tool: downloads its zip from the EZ tools index into the tools root.</summary>
        private async Task InstallToolAsync(ToolDefinition? def)
    {
        if (def == null) return;
        if (Remote.Count == 0) await CheckForUpdatesAsync();
        var match = Remote.Where(r => r.Name.Equals(def.Zip, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0)
        {
            if (Remote.Count > 0) ShowMessage($"{def.Zip} is not in the remote index for the selected .NET version.", InfoSeverity.Warning);
            return;
        }
        await DownloadAsync(match);
    }

    // ------------------------------------------------------------------ log2timeline / plaso

    private PythonInfo? _python;
    private bool _pythonProbed;
    private CancellationTokenSource? _plasoCts;
    private PlasoRelease? _latest;
    private string? _latestError;
    private bool _checkingLatest;
    private string? _toolVersionsKey;

    [ObservableProperty] private bool _isPlasoInstalling;
    [ObservableProperty] private LogBuffer? _plasoLog;
    [ObservableProperty] private Job? _plasoJob;
    [ObservableProperty] private string _pythonText = "Checking for Python…";
    [ObservableProperty] private string _plasoStepText = "";
    [ObservableProperty] private double _plasoSetupProgress;
    [ObservableProperty] private bool _plasoSetupDeterminate;
    [ObservableProperty] private string _psortVersion = "-";
    [ObservableProperty] private string _pinfoVersion = "-";

    private static PlasoLocator NewLocator() => new();

    public bool IsProbingPlaso => S.IsProbingPlaso;
    public PlasoInstall? Plaso => S.Plaso?.Selected;
    public bool PlasoFound => Plaso != null;
    public bool PlasoIsManaged => NewLocator().IsManaged(Plaso);
    public bool ManagedFilesExist => NewLocator().UninstallTargets().Any(Directory.Exists);

    public PlasoToolStatus PlasoToolStatus => PlasoToolState.Evaluate(S.IsProbingPlaso || S.Plaso == null, Plaso?.Version, PlasoFound,
        _latest?.Version, _pythonProbed ? _python != null : null);
    public string PlasoStatus => PlasoToolState.Label(PlasoToolStatus);
    public bool PlasoUpdateAvailable => PlasoToolStatus == PlasoToolStatus.UpdateAvailable;

    private (int DownloadMB, int DiskMB) Estimate => PlasoSetup.Estimate(needsPython: _python == null);
    public string InstallButtonText => $"Install  (≈{Estimate.DownloadMB} MB)";
    public string InstallToolTip => (_python == null ? $"Downloads a private Python {PrivatePython.Default.Version} (python.org's NuGet package, SHA-512 verified) to {PrivatePython.DefaultDir}, then " : $"Uses {_python.Exe} to ")
        + $"create {PlasoLocator.DefaultManagedVenv} and run pip install plaso. No admin rights, no PATH changes. ≈{Estimate.DownloadMB} MB download, ≈{Estimate.DiskMB} MB on disk, usually 1-3 minutes.";

    /// <summary>One plain-language line: what the state is and what to click.</summary>
    public string PlasoHeadline => PlasoToolStatus switch
    {
        PlasoToolStatus.Detecting => "Looking for log2timeline / plaso…",
        PlasoToolStatus.Installed when Plaso!.Kind == PlasoEngineKind.Docker => $"Ready: plaso {Plaso.Version} runs from the Docker image {PlasoLocator.DockerImage}.",
        PlasoToolStatus.Installed => _latest != null ? $"Ready: plaso {Plaso!.Version} is the latest version. Super timelines can use it on the Ingest page."
            : $"Ready: plaso {Plaso!.Version}. Super timelines can use it on the Ingest page.",
        PlasoToolStatus.UpdateAvailable => PlasoIsManaged || Plaso!.Kind == PlasoEngineKind.Docker
            ? $"Update available: {Plaso!.Version} → {_latest!.Version}. Click Update (one click, runs pip / docker pull as a job)."
            : $"Update available: {Plaso!.Version} → {_latest!.Version}. This copy is not managed by EZ Forensics ({Plaso.Location}): update it the way it was installed, or click Install for EZ Forensics' own copy.",
        PlasoToolStatus.PythonMissing => $"Not installed. Click Install (≈{Estimate.DownloadMB} MB, about 1-3 min): EZ Forensics downloads a private Python and plaso for you. Nothing else to set up.",
        _ => $"Not installed. Click Install (≈{Estimate.DownloadMB} MB, about 1-3 min). Nothing else to set up.",
    };

    public string PlasoVersion => Plaso?.Version ?? "-";
    public string LatestVersionText => _latest != null
        ? $"{_latest.Version}" + (_latest.UploadedUtc is { } u ? $"  (released {u:yyyy-MM-dd}, PyPI)" : "  (PyPI)")
        : _checkingLatest ? "Checking PyPI…" : _latestError != null ? $"Unknown ({_latestError})" : "-";
    public string PlasoEngineType => Plaso == null ? "-" : $"{Plaso.KindLabel} ({Plaso.OriginLabel})";
    public string PlasoLocation => Plaso?.Location ?? "-";
    public string PlasoNotes => S.Plaso == null ? "" : string.Join(Environment.NewLine, S.Plaso.Notes);
    public bool HasPlasoNotes => S.Plaso is { Notes.Count: > 0 } && !PlasoFound;
    public string PlasoConfiguredPath => string.IsNullOrWhiteSpace(S.Settings.PlasoPath) ? "(automatic)" : S.Settings.PlasoPath!;
    public bool HasPython => _python != null;
    public bool ShowPythonGuidance => _pythonProbed && _python == null && !PlasoFound;
    public bool CanPullDocker => S.Plaso is { DockerAvailable: true, DockerImagePresent: false } && !IsPlasoInstalling;
    public bool ShowInstall => !PlasoIsManaged;
    public bool ShowUpdate => PlasoFound;
    public string UpdateButtonText => PlasoUpdateAvailable ? $"Update to {_latest!.Version}" : "Update";
    public string PrivatePythonVersion => PrivatePython.Default.Version;

    private void OnPlasoChanged()
    {
        RaisePlasoState();
        _plasoCommand?.Refresh();
        _ = ProbeToolVersionsAsync();
    }

    private void RaisePlasoState()
    {
        foreach (var n in new[] { nameof(IsProbingPlaso), nameof(Plaso), nameof(PlasoFound), nameof(PlasoIsManaged), nameof(ManagedFilesExist),
                     nameof(PlasoToolStatus), nameof(PlasoStatus), nameof(PlasoUpdateAvailable), nameof(PlasoHeadline), nameof(PlasoVersion),
                     nameof(LatestVersionText), nameof(PlasoEngineType), nameof(PlasoLocation), nameof(PlasoNotes), nameof(HasPlasoNotes),
                     nameof(PlasoConfiguredPath), nameof(HasPython), nameof(ShowPythonGuidance), nameof(CanPullDocker), nameof(ShowInstall),
                     nameof(ShowUpdate), nameof(UpdateButtonText), nameof(InstallButtonText), nameof(InstallToolTip) })
            OnPropertyChanged(n);
        InstallPlasoCommand.NotifyCanExecuteChanged();
        RefreshSpecialRows();
        UpdatePlasoCommand.NotifyCanExecuteChanged();
        UninstallPlasoCommand.NotifyCanExecuteChanged();
        CancelPlasoSetupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPlasoInstallingChanged(bool value) => RaisePlasoState();

    private async Task ProbePythonAsync()
    {
        _python = await Task.Run(() => NewLocator().FindSupportedPython());
        _pythonProbed = true;
        PythonText = _python != null
            ? $"Python {_python.Version} ({_python.Origin}): {_python.Exe}"
            : $"None needed: Install downloads a private Python {PrivatePython.Default.Version} (no Python {PlasoLocator.MinPython}-3.13 found via py -3 / PATH).";
        RaisePlasoState();
    }

    /// <summary>Latest plaso on PyPI (proxy from Settings, 15 s timeout). Failures only show "Unknown".</summary>
    private async Task CheckLatestPlasoAsync()
    {
        if (_checkingLatest) return;
        _checkingLatest = true;
        RaisePlasoState();
        try
        {
            using var http = S.CreateHttpClient();
            _latest = await PlasoReleaseCheck.GetLatestAsync(http);
            _latestError = null;
        }
        catch (Exception ex)
        {
            _latestError = ex is TimeoutException ? "PyPI timed out; check the proxy in Settings" : "PyPI not reachable; check the proxy in Settings";
            AppLog.Error("plaso latest-version check failed", ex);
        }
        finally
        {
            _checkingLatest = false;
            RaisePlasoState();
        }
    }

    /// <summary>psort / pinfo --version of the selected install (background; skipped for Docker).</summary>
    private async Task ProbeToolVersionsAsync()
    {
        var i = Plaso;
        var key = i == null ? null : i.Location + "|" + i.Version + "|" + i.Kind;
        if (key == _toolVersionsKey) return;
        _toolVersionsKey = key;
        if (i == null) { PsortVersion = PinfoVersion = "-"; return; }
        if (i.Kind == PlasoEngineKind.Docker) { PsortVersion = PinfoVersion = $"in the Docker image ({i.Version})"; return; }
        PsortVersion = PinfoVersion = "…";
        var loc = new PlasoLocator(versionTimeout: TimeSpan.FromSeconds(30));
        var pinfoCmd = PlasoLocator.PinfoCommand(i);
        var ps = Task.Run(() => loc.ProbeVersion(i.Psort));
        var pi = Task.Run(() => pinfoCmd == null ? null : loc.ProbeVersion(pinfoCmd));
        var (psV, piV) = (await ps, await pi);
        if (_toolVersionsKey != key) return;
        PsortVersion = psV ?? "not found";
        PinfoVersion = piV ?? "not found";
    }

    [RelayCommand]
    private async Task DetectPlasoAsync() => await GuardAsync(async () =>
    {
        _toolVersionsKey = null;
        var latest = CheckLatestPlasoAsync();
        var d = await S.RefreshPlasoAsync();
        await ProbePythonAsync();
        await latest;
        ShowMessage(d.Selected != null ? $"Found {d.Selected.Summary}." : "plaso was not found. Click Install to set it up in one step.",
            d.Selected != null ? InfoSeverity.Success : InfoSeverity.Warning);
    }, "plaso detection failed");

    [RelayCommand]
    private async Task SetPlasoFolderAsync()
    {
        var d = Dialogs.PickFolder("Select a folder with log2timeline/psort (or a Python venv with plaso)", S.Settings.PlasoPath);
        if (d != null) await SetPlasoPathAsync(d);
    }

    [RelayCommand]
    private async Task SetPlasoPythonAsync()
    {
        var f = Dialogs.PickFile("Select the python.exe that has plaso installed", "python.exe|python*.exe|All files|*.*", S.Settings.PlasoPath);
        if (f != null) await SetPlasoPathAsync(f);
    }

    /// <summary>Browse to an existing log2timeline.exe / log2timeline.py (or a python.exe with plaso); saved as the plaso path.</summary>
    [RelayCommand]
    private async Task UseExistingPlasoAsync()
    {
        var f = Dialogs.PickFile("Select log2timeline.exe or log2timeline.py (psort must be next to it), or a python.exe that has plaso",
            "log2timeline|log2timeline.exe;log2timeline.py|Python with plaso|python*.exe|All files|*.*", S.Settings.PlasoPath);
        if (f != null) await SetPlasoPathAsync(f);
    }

    [RelayCommand]
    private async Task ClearPlasoPathAsync() => await SetPlasoPathAsync(null);

    private async Task SetPlasoPathAsync(string? path)
    {
        await GuardAsync(async () =>
        {
            S.Settings.PlasoPath = path;
            S.SaveSettings();
            var d = await S.RefreshPlasoAsync();
            ShowMessage(d.Selected != null ? $"Using {d.Selected.Summary}." : "plaso was not found at that location (log2timeline and psort must be in the same folder).",
                d.Selected != null ? InfoSeverity.Success : InfoSeverity.Warning);
        }, "Could not set the plaso path");
    }

    [RelayCommand]
    private void OpenPlasoFolder() => Guard(() =>
    {
        var p = Plaso is { Kind: not PlasoEngineKind.Docker } i ? i.Location : PlasoLocator.ManagedRoot;
        if (p == PlasoLocator.ManagedRoot) Directory.CreateDirectory(p);
        ShellOps.OpenFolder(p);
    }, "Could not open the plaso folder");

    private bool CanInstallPlaso() => !IsPlasoInstalling;
    private bool CanCancelPlasoSetup() => IsPlasoInstalling;

    [RelayCommand(CanExecute = nameof(CanCancelPlasoSetup))]
    private void CancelPlasoSetup() => _plasoCts?.Cancel();

    private string MaintenanceDir() => S.Maintenance.NewLogDir("plaso");

    private void BeginSetup(string firstLine)
    {
        IsPlasoInstalling = true;
        _plasoCts = new CancellationTokenSource();
        PlasoLog?.Dispose();
        PlasoLog = new LogBuffer();
        PlasoLog.Enqueue(firstLine);
        PlasoSetupDeterminate = false;
        PlasoSetupProgress = 0;
    }

    private void EndSetup()
    {
        IsPlasoInstalling = false;
        PlasoStepText = "";
        _plasoCts?.Dispose();
        _plasoCts = null;
    }

    /// <summary>
    /// One click from zero: (1) a Python 3.10-3.13 (EZ Forensics' private one, else an existing one, else download python.org's
    /// NuGet package, SHA-512 verified, into %LOCALAPPDATA%\EzForensicsData\plaso\python), (2) create the managed venv, (3) pip install
    /// plaso, (4) detect it and make it the engine (clears a configured path that points elsewhere).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallPlaso))]
    private async Task InstallPlasoAsync()
    {
        if (IsPlasoInstalling) return;
        BeginSetup("> Install log2timeline / plaso");
        var ct = _plasoCts!.Token;
        var loc = NewLocator();
        var outDir = MaintenanceDir();
        try
        {
            Directory.CreateDirectory(PlasoLocator.ManagedRoot);
            var (venvArgs, venvPython, _) = loc.InstallCommands();
            if (!File.Exists(venvPython))
            {
                PlasoStepText = "Step 1 of 3: Python";
                if (!_pythonProbed || _python == null) await ProbePythonAsync();
                var py = _python;
                if (py == null)
                {
                    PlasoSetupDeterminate = true;
                    using var http = S.CreateHttpClient();
                    var log = PlasoLog!;
                    await PrivatePython.ProvisionAsync(http, PrivatePython.Default, loc.PrivatePythonDir,
                        line => log.Enqueue(line), new Progress<double>(p => PlasoSetupProgress = p), ct);
                    PlasoSetupDeterminate = false;
                    await ProbePythonAsync();
                    py = _python;
                    if (py == null || !py.IsPrivate)
                    {
                        ShowMessage("The private Python was extracted but does not start. See the log; click Install to try again.", InfoSeverity.Error);
                        return;
                    }
                }
                else PlasoLog!.Enqueue($"Using {py.Origin} Python {py.Version}: {py.Exe}");

                PlasoStepText = "Step 2 of 3: creating the plaso venv";
                var venvJob = await RunSetupStepAsync(PlasoSetup.CreateVenvStep(loc, py), outDir, ct);
                if (venvJob.State == JobState.Cancelled || ct.IsCancellationRequested) { await CleanupFailedInstallAsync(loc, "Install cancelled."); return; }
                if (venvJob.State != JobState.Succeeded || !File.Exists(venvPython))
                {
                    await CleanupFailedInstallAsync(loc, $"Creating the venv failed: {venvJob.StatusReason}. See the log below or the Jobs page, then click Install to retry.");
                    return;
                }
            }
            else PlasoLog!.Enqueue($"Managed venv already exists: {loc.ManagedVenv}");

            PlasoStepText = "Step 3 of 3: pip install plaso (downloads ≈35 MB)";
            var pipJob = await RunSetupStepAsync(PlasoSetup.PipStep(loc, update: false), outDir, ct);
            if (pipJob.State == JobState.Cancelled || ct.IsCancellationRequested) { ShowMessage("Install cancelled. Click Install to resume.", InfoSeverity.Warning); return; }
            if (pipJob.State != JobState.Succeeded)
            {
                ShowMessage($"pip install plaso failed: {pipJob.StatusReason}. Check the internet connection / proxy (pip uses the system proxy or HTTPS_PROXY), then click Install to retry. Log below.", InfoSeverity.Error);
                return;
            }
            await AutoConfigureAsync("plaso installed");
        }
        catch (OperationCanceledException)
        {
            await CleanupFailedInstallAsync(loc, "Install cancelled. Nothing was left behind; click Install to start again.");
        }
        catch (Exception ex)
        {
            PlasoLog?.Enqueue("ERROR: " + ex.Message);
            ShowError("Install plaso failed (click Install to retry)", ex);
        }
        finally { EndSetup(); }
    }

    /// <summary>After a failed first install, remove the half-made venv (never one that already worked).</summary>
    private async Task CleanupFailedInstallAsync(PlasoLocator loc, string message)
    {
        ShowMessage(message, InfoSeverity.Warning);
        if (File.Exists(Path.Combine(loc.ManagedVenv, "Scripts", "log2timeline.exe"))) return;
        await Task.Run(() => PrivatePython.TryDelete(loc.ManagedVenv));
    }

    /// <summary>Detect, make the result the engine (clear a configured path that hides the managed venv) and report.</summary>
    private async Task AutoConfigureAsync(string what)
    {
        var d = await S.RefreshPlasoAsync();
        if (!NewLocator().IsManaged(d.Selected) && !string.IsNullOrWhiteSpace(S.Settings.PlasoPath))
        {
            PlasoLog?.Enqueue($"Configured plaso path '{S.Settings.PlasoPath}' replaced by automatic detection (EZ Forensics managed venv).");
            S.Settings.PlasoPath = null;
            S.SaveSettings();
            d = await S.RefreshPlasoAsync();
        }
        if (_latest == null) await CheckLatestPlasoAsync();
        RaisePlasoState();
        if (d.Selected != null)
        {
            PlasoLog?.Enqueue($"Detected: {d.Selected.Summary}");
            ShowMessage($"{what}: {d.Selected.Summary}. It is now the super timeline engine on the Ingest page.", InfoSeverity.Success);
        }
        else ShowMessage("pip finished but plaso did not answer to --version. See the log, then click Detect again.", InfoSeverity.Warning);
    }

    private bool CanUpdatePlaso() => !IsPlasoInstalling && PlasoFound;

    /// <summary>pip install --upgrade plaso in the managed venv (or docker pull for the Docker engine), as a job.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdatePlaso))]
    private async Task UpdatePlasoAsync()
    {
        var current = Plaso;
        if (current == null || IsPlasoInstalling) return;
        var loc = NewLocator();
        PlasoSetupStep step;
        if (current.Kind == PlasoEngineKind.Docker)
        {
            var docker = SystemPlasoProbe.Instance.FindOnPath("docker.exe");
            if (docker == null) { ShowMessage("Docker is not available.", InfoSeverity.Warning); return; }
            step = PlasoSetup.DockerPullStep(docker);
        }
        else if (loc.IsManaged(current) && File.Exists(loc.ManagedPython)) step = PlasoSetup.PipStep(loc, update: true);
        else
        {
            ShowMessage($"plaso at {current.Location} was not installed by EZ Forensics, so EZ Forensics does not update it. Update it the way it was installed, or click Install to add EZ Forensics' own copy (it is then used automatically).", InfoSeverity.Warning);
            return;
        }
        BeginSetup($"> Update plaso {current.Version}" + (_latest != null ? $" (latest on PyPI: {_latest.Version})" : ""));
        try
        {
            PlasoStepText = step.Label;
            var job = await RunSetupStepAsync(step, MaintenanceDir(), _plasoCts!.Token);
            if (job.State == JobState.Cancelled) { ShowMessage("Update cancelled. The installed plaso was not changed if pip had not started replacing files; click Detect again to check.", InfoSeverity.Warning); return; }
            if (job.State != JobState.Succeeded)
            {
                ShowMessage($"Update failed: {job.StatusReason}. See the log below or the Jobs page.", InfoSeverity.Error);
                return;
            }
            _toolVersionsKey = null;
            var before = current.Version;
            await AutoConfigureAsync("plaso updated");
            if (Plaso?.Version == before) ShowMessage($"plaso {before} is already the latest version pip can install.", InfoSeverity.Success);
        }
        catch (Exception ex) { ShowError("Update plaso failed", ex); }
        finally { EndSetup(); }
    }

    private bool CanUninstallPlaso() => !IsPlasoInstalling;

    /// <summary>Removes the managed venv and the private Python only (never a plaso or Python installed elsewhere).</summary>
    [RelayCommand(CanExecute = nameof(CanUninstallPlaso))]
    private async Task UninstallPlasoAsync()
    {
        var loc = NewLocator();
        var targets = loc.UninstallTargets().Where(Directory.Exists).ToList();
        if (targets.Count == 0) { ShowMessage("EZ Forensics has no managed plaso or private Python to remove.", InfoSeverity.Info); return; }
        if (S.HasActiveJob("plaso"))
        {
            ShowMessage("A plaso job is still running. Wait for it or cancel it on the Jobs page, then click Uninstall again.", InfoSeverity.Warning);
            return;
        }
        if (!EzForensics.App.Services.Dialogs.Confirm("Uninstall plaso?", "Removes EZ Forensics' copy of log2timeline / plaso. Your timelines are kept.", "Uninstall",
                details: string.Join("\n", targets), defaultNo: true)) return;
        IsPlasoInstalling = true;
        try
        {
            var removed = await Task.Run(() => PlasoSetup.Uninstall(loc));
            if (!string.IsNullOrWhiteSpace(S.Settings.PlasoPath) && S.Settings.PlasoPath!.StartsWith(PlasoLocator.ManagedRoot, StringComparison.OrdinalIgnoreCase))
            {
                S.Settings.PlasoPath = null;
                S.SaveSettings();
            }
            _toolVersionsKey = null;
            var d = await S.RefreshPlasoAsync();
            await ProbePythonAsync();
            ShowMessage($"Removed {string.Join(" and ", removed)}." + (d.Selected != null ? $" Now using {d.Selected.Summary}." : " Click Install to set plaso up again."),
                InfoSeverity.Success);
        }
        catch (Exception ex) { ShowError("Uninstall plaso failed", ex); }
        finally { IsPlasoInstalling = false; }
    }

    [RelayCommand]
    private async Task PullDockerImageAsync()
    {
        var docker = S.Plaso?.DockerAvailable == true ? SystemPlasoProbe.Instance.FindOnPath("docker.exe") : null;
        if (docker == null) { ShowMessage("Docker is not available.", InfoSeverity.Warning); return; }
        BeginSetup($"> docker pull {PlasoLocator.DockerImage}");
        try
        {
            var job = await RunSetupStepAsync(PlasoSetup.DockerPullStep(docker), MaintenanceDir(), _plasoCts!.Token);
            var d = await S.RefreshPlasoAsync();
            ShowMessage(job.State == JobState.Succeeded && d.Selected != null ? $"Docker image ready: {d.Selected.Summary}." : $"docker pull: {job.StatusReason}",
                job.State == JobState.Succeeded ? InfoSeverity.Success : InfoSeverity.Error);
        }
        catch (Exception ex) { ShowError("docker pull failed", ex); }
        finally { EndSetup(); }
    }

    private Task<Job> RunSetupStepAsync(PlasoSetupStep step, string outDir, CancellationToken ct) =>
        PlasoSetup.RunStepAsync(S.Maintenance.Queue, step, outDir, job =>
        {
            PlasoJob = job;
            PlasoLog?.Enqueue($"> {step.Label}");
            job.Output += (_, line) => AppLog.Info($"[{step.Label}] {line}");
        }, ct);

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }), "Could not open the link");
    }
}
