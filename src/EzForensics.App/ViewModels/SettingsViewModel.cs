using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>Options page: one editable flag (stable id, label, colour).</summary>
public sealed partial class FlagEditItem : ObservableObject
{
    public FlagEditItem(EzForensics.Core.Results.FlagDefinition def)
    {
        Id = def.Id;
        _label = def.Label;
        _color = def.Color;
        _colorText = def.Color;
    }

    public string Id { get; }
    public bool IsBuiltIn => EzForensics.Core.Results.FlagCatalog.IsBuiltInId(Id);
    [ObservableProperty] private string _label;
    /// <summary>Last valid colour ("#RRGGBB"); drives the swatch.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Brush))] private string _color;
    /// <summary>What the hex box shows while typing (may be incomplete).</summary>
    [ObservableProperty] private string _colorText;
    public System.Windows.Media.Brush Brush => FlagBrushes.For(Color);

    partial void OnColorTextChanged(string value)
    {
        if (EzForensics.Core.Results.FlagCatalog.TryParseColor(value, out var n)) Color = n;
    }

    partial void OnColorChanged(string value)
    {
        if (!string.Equals(ColorText, value, StringComparison.OrdinalIgnoreCase)) ColorText = value;
    }

    public EzForensics.Core.Results.FlagDefinition ToDefinition() => new(Id, Label, Color);
}

public sealed partial class SettingsViewModel : PageViewModel
{
    /// <summary>Flags being edited (order = menu / legend / filter order); saved into <see cref="AppSettings.Flags"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<FlagEditItem> FlagItems { get; } = new();
    /// <summary>Preset flag colours (readable on light and dark themes).</summary>
    public static string[] FlagPresetColors { get; } =
        { "#3B82F6", "#06B6D4", "#A855F7", "#EC4899", "#F59E0B", "#F97316", "#EF4444", "#22C55E", "#84CC16", "#9CA3AF" };

    [ObservableProperty] private AppSettings _edit = new();
    [ObservableProperty] private bool _isDirty;

    public SettingsViewModel() => Load();

    public override string Title => "Options";
    public string SettingsPath => S.Store.Path;
    public string LogPath => AppLog.LogPath;

    public static Array Themes => Enum.GetValues(typeof(AppTheme));
    /// <summary>Options > Installation > Tool logging.</summary>
    public static Array ToolLogLevels => Enum.GetValues(typeof(EzForensics.Core.Execution.ToolLogLevel));

    public static double[] FontSizes { get; } = { 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 };
    private static IReadOnlyList<string>? _fontFamilies;
    /// <summary>Installed font families, sorted (loaded once, on first use).</summary>
    public static IReadOnlyList<string> FontFamilies => _fontFamilies ??= LoadFontFamilies();

    private static IReadOnlyList<string> LoadFontFamilies()
    {
        try
        {
            return System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not list system fonts", ex);
            return new[] { "Segoe UI", "Consolas" };
        }
    }

    public override void OnNavigatedTo()
    {
        if (!IsDirty) Load();
    }

    private void Load()
    {
        if (Edit != null) Edit.PropertyChanged -= OnEditChanged;
        var e = new AppSettings();
        Copy(S.Settings, e);
        e.PropertyChanged += OnEditChanged;
        Edit = e;
        LoadFlagItems(e.Flags);
        LoadHotKeys(e.HotKeys);
        RefreshSelectionRows();
        IsDirty = false;
    }

    private void LoadFlagItems(IEnumerable<EzForensics.Core.Results.FlagDefinition> flags)
    {
        foreach (var f in FlagItems) f.PropertyChanged -= OnFlagItemChanged;
        FlagItems.Clear();
        foreach (var d in flags) AddFlagItem(new FlagEditItem(d));
    }

    private void AddFlagItem(FlagEditItem item)
    {
        item.PropertyChanged += OnFlagItemChanged;
        FlagItems.Add(item);
    }

    private void OnFlagItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FlagEditItem.Brush)) IsDirty = true;
    }

    /// <summary>Adds a custom flag (new stable id) at the end of the list.</summary>
    [RelayCommand]
    private void AddFlag()
    {
        var n = FlagItems.Count + 1;
        AddFlagItem(new FlagEditItem(new EzForensics.Core.Results.FlagDefinition(EzForensics.Core.Results.FlagCatalog.NewCustomId(), $"Custom flag {n}", "#06B6D4")));
        IsDirty = true;
    }

    /// <summary>Removes a flag (at least one stays). Rows already flagged with it keep their flag id; Reset to defaults restores the standard five.</summary>
    [RelayCommand]
    private void RemoveFlag(FlagEditItem? item)
    {
        if (item == null || FlagItems.Count <= 1) return;
        item.PropertyChanged -= OnFlagItemChanged;
        FlagItems.Remove(item);
        IsDirty = true;
    }

    [RelayCommand]
    private void MoveFlagUp(FlagEditItem? item)
    {
        var i = item == null ? -1 : FlagItems.IndexOf(item);
        if (i <= 0) return;
        FlagItems.Move(i, i - 1);
        IsDirty = true;
    }

    [RelayCommand]
    private void MoveFlagDown(FlagEditItem? item)
    {
        var i = item == null ? -1 : FlagItems.IndexOf(item);
        if (i < 0 || i >= FlagItems.Count - 1) return;
        FlagItems.Move(i, i + 1);
        IsDirty = true;
    }

    /// <summary>Restores the standard five flags (labels, colours, order); custom flags are removed from the list.</summary>
    [RelayCommand]
    private void ResetFlags()
    {
        LoadFlagItems(EzForensics.Core.Results.FlagCatalog.Defaults());
        IsDirty = true;
    }

    private void OnEditChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        IsDirty = true;
        if (e.PropertyName == nameof(AppSettings.Theme)) ThemeManager.Apply(Edit.Theme); // preview immediately
        else if (e.PropertyName is nameof(AppSettings.UiFontFamily) or nameof(AppSettings.UiFontSize)
                 or nameof(AppSettings.MonoFontFamily) or nameof(AppSettings.MonoFontSize))
            ApplyFonts(Edit);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await GuardAsync(async () =>
        {
            var live = S.Settings;
            bool rootChanged = !string.Equals(live.ToolsRoot, Edit.ToolsRoot, StringComparison.OrdinalIgnoreCase);
            Edit.Flags = FlagItems.Select(f => f.ToDefinition()).ToList();
            Edit.HotKeys = HotKeyOverrides();
            Copy(Edit, live);
            S.SaveSettings();
            S.Queue.MaxParallel = live.MaxParallel;
            S.Queue.Timeout = TimeSpan.FromMinutes(Math.Max(1, live.JobTimeoutMinutes));
            ThemeManager.Apply(live.Theme);
            ApplyFonts(live);
            IsDirty = false;
            if (rootChanged) await S.RescanAsync();
            ShowMessage("Settings saved.", InfoSeverity.Success);
        }, "Could not save settings");
    }

    [RelayCommand]
    private void Revert()
    {
        Load();
        ThemeManager.Apply(S.Settings.Theme);
        ApplyFonts(S.Settings);
        Banner = null;
    }

    [RelayCommand]
    private void BrowseToolsRoot()
    {
        var d = Dialogs.PickFolder("EZ Tools folder", Edit.ToolsRoot);
        if (d != null) Edit.ToolsRoot = d;
    }

    [RelayCommand]
    private void BrowseCaseRoot()
    {
        var d = Dialogs.PickFolder("Case root folder", Edit.CaseRoot);
        if (d != null) Edit.CaseRoot = d;
    }

    // ---- Updates (Installation card) ----
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckAppUpdateCommand), nameof(InstallAppUpdateCommand))] private bool _updateBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallAppUpdateCommand))] private bool _updateFound;

    /// <summary>Asks the update feed (the override in the box, else the project's GitHub releases) whether a newer version exists.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task CheckAppUpdateAsync()
    {
        UpdateBusy = true;
        UpdateFound = false;
        UpdateStatus = "Checking for updates…";
        try
        {
            var text = await AppUpdater.CheckAsync(Edit.EffectiveUpdateFeedUrl, apply: false);
            UpdateStatus = text;
            UpdateFound = text.Contains("is available", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed", ex);
            UpdateStatus = "Could not check for updates: " + ex.Message;
        }
        finally { UpdateBusy = false; }
    }

    /// <summary>Downloads the found update and restarts into it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallAppUpdateAsync()
    {
        UpdateBusy = true;
        UpdateStatus = "Downloading the update…";
        try
        {
            UpdateStatus = await AppUpdater.CheckAsync(Edit.EffectiveUpdateFeedUrl, apply: true,
                p => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateStatus = $"Downloading the update… {p}%"));
        }
        catch (Exception ex)
        {
            AppLog.Error("Update install failed", ex);
            UpdateStatus = "Could not install the update: " + ex.Message;
        }
        finally { UpdateBusy = false; }
    }

    private bool CanUpdate() => !UpdateBusy;
    private bool CanInstallUpdate() => !UpdateBusy && UpdateFound;

    public static void Copy(AppSettings from, AppSettings to)
    {
        to.ToolsRoot = from.ToolsRoot;
        to.PreferredFlavor = from.PreferredFlavor;
        to.ProxyMode = from.ProxyMode;
        to.ProxyUrl = from.ProxyUrl;
        to.MaxParallel = from.MaxParallel;
        to.JobTimeoutMinutes = from.JobTimeoutMinutes;
        to.CaseRoot = from.CaseRoot;
        to.DateFormat = from.DateFormat;
        to.RecmdBatchFile = from.RecmdBatchFile;
        to.Theme = from.Theme;
        to.NetVersionToDownload = from.NetVersionToDownload;
        to.UiFontFamily = from.UiFontFamily;
        to.UiFontSize = from.UiFontSize;
        to.MonoFontFamily = from.MonoFontFamily;
        to.MonoFontSize = from.MonoFontSize;
        to.StripTimestampPrefix = from.StripTimestampPrefix;
        to.ToolLogLevel = from.ToolLogLevel;
        to.ShowAnalyzeHints = from.ShowAnalyzeHints;
        to.HotKeys = new Dictionary<string, string>(from.HotKeys);
        to.Flags = from.Flags.Select(f => f.Clone()).ToList();
        to.FlagLastId = from.FlagLastId;
        to.FlagRowTint = from.FlagRowTint;
        to.EscapeFormulasOnCopy = from.EscapeFormulasOnCopy;
        to.KeepExtractedEvidence = from.KeepExtractedEvidence;
        to.IngestSelections = from.IngestSelections.Select(i => i.Clone()).ToList();
        to.UpdateFeedUrl = string.IsNullOrWhiteSpace(from.UpdateFeedUrl) ? null : from.UpdateFeedUrl.Trim();
    }

    private static void ApplyFonts(AppSettings s) =>
        Appearance.ApplyFonts(s.UiFontFamily, s.UiFontSize, s.MonoFontFamily, s.MonoFontSize);
}
