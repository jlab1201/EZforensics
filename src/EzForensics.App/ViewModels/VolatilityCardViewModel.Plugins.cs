using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Memory;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Volatility card, "Ingest plugins": the plugin set Ingest plans for every memory image (Windows and Linux lists of the catalog),
/// saved in <c>AppSettings.VolatilityPlugins</c>. <c>windows.info</c> is always on (the other plugins wait for its symbol check).
/// </summary>
public sealed partial class VolatilityCardViewModel
{
    private ObservableCollection<MemoryPluginItem>? _windowsPlugins;
    private ObservableCollection<MemoryPluginItem>? _linuxPlugins;
    private bool _loadingPlugins;

    public ObservableCollection<MemoryPluginItem> WindowsPlugins => _windowsPlugins ?? LoadPlugins().Windows;
    public ObservableCollection<MemoryPluginItem> LinuxPlugins => _linuxPlugins ?? LoadPlugins().Linux;
    private IEnumerable<MemoryPluginItem> AllPluginItems => WindowsPlugins.Concat(LinuxPlugins);

    /// <summary>"10 of 23 plugins" (Windows + Linux), shown on the collapsed list and on the Ingest page.</summary>
    public string PluginsSummary => IngestPluginSummary(S.Settings.VolatilityPlugins);

    /// <summary>Selected / total plugins of the Ingest set (Windows and Linux catalog entries).</summary>
    public static string IngestPluginSummary(IEnumerable<string>? configured)
    {
        int win = MemoryPlugins.SelectedFor(MemoryOs.Windows, configured).Count, lin = MemoryPlugins.SelectedFor(MemoryOs.Linux, configured).Count;
        int total = MemoryPlugins.ForOs(MemoryOs.Windows).Count + MemoryPlugins.ForOs(MemoryOs.Linux).Count;
        return $"{win + lin} of {total} plugins";
    }

    private (ObservableCollection<MemoryPluginItem> Windows, ObservableCollection<MemoryPluginItem> Linux) LoadPlugins()
    {
        _loadingPlugins = true;
        try
        {
            ObservableCollection<MemoryPluginItem> Load(MemoryOs os)
            {
                var on = MemoryPlugins.SelectedFor(os, S.Settings.VolatilityPlugins).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var list = new ObservableCollection<MemoryPluginItem>();
                foreach (var d in MemoryPlugins.ForOs(os))
                {
                    var item = new MemoryPluginItem(d) { IsSelected = on.Contains(d.Id) };
                    item.PropertyChanged += OnPluginChanged;
                    list.Add(item);
                }
                return list;
            }
            _windowsPlugins = Load(MemoryOs.Windows);
            _linuxPlugins = Load(MemoryOs.Linux);
            _probedKey = null;
            _ = RefreshPluginProbeAsync();
            return (_windowsPlugins, _linuxPlugins);
        }
        finally { _loadingPlugins = false; }
    }

    private string? _probedKey;

    /// <summary>
    /// Disables the plugins the installed vol does not list (<see cref="VolatilityPluginProbe"/>, <c>vol -h</c> once per vol path and version). Fails open: no install,
    /// or a probe that read nothing, leaves every plugin selectable. An unavailable plugin is shown unticked; the saved set changes only on the next edit.
    /// </summary>
    private async Task RefreshPluginProbeAsync()
    {
        if (_windowsPlugins == null || _linuxPlugins == null) return;
        var install = Install;
        var key = install == null ? "" : install.Exe + "|" + install.Version;
        if (key == _probedKey) return;
        _probedKey = key;
        IReadOnlySet<string>? available = null;
        if (install != null)
        {
            try { available = await Task.Run(() => VolatilityPluginProbe.Get(install)); }
            catch (Exception ex) { AppLog.Info("Volatility plugin probe: " + ex.Message); }
        }
        if (key != _probedKey) return;   // the install changed meanwhile; the newer call applies its own result
        _loadingPlugins = true;
        try
        {
            foreach (var p in AllPluginItems)
            {
                p.ProbedVersion = install?.Version ?? "";
                p.IsAvailable = VolatilityPluginProbe.IsListed(p.Def, available);
                if (!p.IsAvailable) p.IsSelected = false;
            }
        }
        finally { _loadingPlugins = false; }
        var missing = AllPluginItems.Where(p => !p.IsAvailable).Select(p => p.Plugin).ToList();
        if (missing.Count > 0) AppLog.Info($"Volatility {install?.Version}: not listed by vol -h, disabled in the plugin list: {string.Join(", ", missing)}");
    }

    private void OnPluginChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryPluginItem.IsSelected) && !_loadingPlugins) SavePlugins();
    }

    private void SavePlugins()
    {
        S.Settings.VolatilityPlugins = AllPluginItems.Where(p => p.IsSelected || p.IsRequired).Select(p => p.Id).ToList();
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
        OnPropertyChanged(nameof(PluginsSummary));
        RefreshCommandRows();
        S.RaiseMemoryPluginsChanged();
    }

    private void SetAll(Func<MemoryPluginItem, bool> on)
    {
        _loadingPlugins = true;
        try { foreach (var p in AllPluginItems) p.IsSelected = p.IsRequired || on(p); }
        finally { _loadingPlugins = false; }
        SavePlugins();
    }

    [RelayCommand]
    private void PluginDefaults() => SetAll(p => p.Def.DefaultSelected);

    [RelayCommand]
    private void PluginsAll() => SetAll(_ => true);
}
