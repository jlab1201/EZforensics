using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Catalog;
using EzForensics.Core.Tools;

namespace EzForensics.App.ViewModels;

/// <summary>Release facts of one installed tool: what is installed, whether the EZ tools site has a newer build, and when the maps / batch files were synced (installs and syncs run from the Overview table).</summary>
public sealed partial class ToolReleaseViewModel : ObservableObject
{
    public const string SourceText = "Eric Zimmerman's tools (ericzimmerman.github.io)";
    private static readonly HashSet<string> SyncTools = new(StringComparer.OrdinalIgnoreCase) { "EvtxECmd", "RECmd", "SQLECmd" };

    private readonly ToolDefinition _def;
    private readonly ToolManagerViewModel _manager;

    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _flavor = "";
    [ObservableProperty] private string _exePath = "";
    [ObservableProperty] private string _installedDate = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _mapsSummary = "";
    [ObservableProperty] private string _syncMessage = "";

    public ToolReleaseViewModel(ToolDefinition def, ToolManagerViewModel manager)
    {
        _def = def;
        _manager = manager;
        HasMaps = SyncTools.Contains(def.Id);
        manager.Remote.CollectionChanged += OnRemoteChanged;
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ToolManagerViewModel.IsSyncing) && !manager.IsSyncing) RefreshMaps();
        };
        Refresh();
    }

    public string Source => SourceText;
    public ToolDefinition Def => _def;
    /// <summary>Folder that holds the exe ("Installation Path").</summary>
    public string InstallPath => string.IsNullOrEmpty(ExePath) ? "" : Path.GetDirectoryName(ExePath) ?? "";
    /// <summary>Only EvtxECmd, RECmd and SQLECmd have maps / batch files to sync.</summary>
    public bool HasMaps { get; }
    public string MapsTitle => _def.Id.Equals("RECmd", StringComparison.OrdinalIgnoreCase) ? "Batch files" : "Maps";

    private void OnRemoteChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>Re-reads the installed facts, the update status and the maps folder.</summary>
    public void Refresh()
    {
        var t = AppServices.Current.ResolveNonBlocking(_def.Exe);
        Version = string.IsNullOrWhiteSpace(t?.Version) ? "unknown" : ToolDisplay.FormatVersion(t!.Version);
        Flavor = t == null ? "" : t.Flavor == BuildFlavor.Net9 ? ".NET 10" : "net4 (.NET Framework)";
        ExePath = t?.Path ?? "";
        OnPropertyChanged(nameof(InstallPath));
        InstalledDate = t != null && File.Exists(t.Path)
            ? File.GetLastWriteTime(t.Path).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "";

        var remote = _manager.Remote.FirstOrDefault(r => r.Name.Equals(_def.Zip, StringComparison.OrdinalIgnoreCase));
        if (_manager.Remote.Count == 0) { UpdateStatus = "Not checked yet (Tools Overview, Check for updates)."; UpdateAvailable = false; }
        else if (remote == null) { UpdateStatus = "Not listed on the EZ tools site for the selected .NET version."; UpdateAvailable = false; }
        else if (remote.Status == RemoteFileStatus.UpToDate) { UpdateStatus = "Up to date with the EZ tools site."; UpdateAvailable = false; }
        else { UpdateStatus = $"A newer build is available ({remote.Size}). Install it from the Tools table on the Overview tab."; UpdateAvailable = true; }
        RefreshMaps();
    }

    private void RefreshMaps()
    {
        if (!HasMaps) { MapsSummary = ""; return; }
        try
        {
            var dir = string.IsNullOrEmpty(ExePath) ? null : Path.GetDirectoryName(ExePath);
            var files = new List<string>();
            if (dir != null && Directory.Exists(dir))
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly)
                             .Where(d => { var n = Path.GetFileName(d); return n.Equals("Maps", StringComparison.OrdinalIgnoreCase) || n.Equals("BatchExamples", StringComparison.OrdinalIgnoreCase); }))
                    files.AddRange(Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories));
            MapsSummary = files.Count == 0
                ? "Never synced: no maps or batch files found next to the tool."
                : $"Last synced {files.Max(File.GetLastWriteTime).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)} · {files.Count} file(s)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MapsSummary = "Could not read the maps folder."; }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try { ShellOps.OpenFolder(ExePath); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { SyncMessage = ex.Message; }
    }
}
