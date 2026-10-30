using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Network;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>One Volatility plugin of the Ingest plugin set (Tools, Volatility card).</summary>
public sealed partial class MemoryPluginItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public MemoryPluginItem(MemoryPluginDef def)
    {
        Def = def;
        _isSelected = def.DefaultSelected || def.IsInfo;
    }

    public MemoryPluginDef Def { get; }
    public string Id => Def.Id;
    public string Label => Def.Label;
    public string Plugin => Def.Plugin;
    /// <summary><c>windows.info</c> always runs when the image is analysed (it checks the symbols and warms the cache for the other plugins).</summary>
    public bool IsRequired => Def.IsInfo;
    /// <summary>False when the installed vol does not list this plugin (<see cref="VolatilityPluginProbe"/>); the box is then disabled. True while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange), nameof(ToolTip))]
    private bool _isAvailable = true;
    /// <summary>Version of the vol that was probed, for the tooltip of an unavailable plugin.</summary>
    public string ProbedVersion { get; set; } = "";
    public bool CanChange => !IsRequired && IsAvailable;
    public string AutomationId => "ToolManager.Volatility.Plugin_" + Def.Id;
    public string ToolTip => Def.Plugin + (Def.IsInfo ? "  (required: runs first, checks the symbols for the other plugins)" : "")
                             + (Def.NeedsUserPages ? "  ·  reads user-mode pages (incomplete on a kernel dump)" : "")
                             + (IsAvailable ? "" : "  ·  not offered by the installed Volatility" + (ProbedVersion.Length > 0 ? " " + ProbedVersion : "") + " (vol -h does not list it)");

    public override string ToString() => Label;
}

/// <summary>
/// One memory image found by the scan: what it is and the host it is filed under. Its plugins are the Ingest set of Tools
/// (<c>AppSettings.VolatilityPlugins</c>); each planned plugin is a row of Planned jobs (untick a row to skip it).
/// </summary>
public sealed partial class MemoryImageItem : ObservableObject
{
    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _hostSource = "";
    [ObservableProperty] private bool _hasPagefile;
    /// <summary>The card allows changes (Volatility is set up and no run is going on).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isEditable = true;

    /// <summary>The checkbox is enabled: the image can be analysed and the card allows changes.</summary>
    public bool CanToggle => CanAnalyse && IsEditable;

    public MemoryImageItem(MemoryImage image, bool linuxIsf)
    {
        Image = image;
        Os = image.Os == MemoryOs.Linux ? MemoryOs.Linux : MemoryOs.Windows;
        CanAnalyse = image.Os != MemoryOs.Mac && image.Format != MemoryFormat.Hibernation && (Os != MemoryOs.Linux || linuxIsf);
        _isIncluded = CanAnalyse;
        Notes = BuildNotes(image, linuxIsf);
    }

    public MemoryImage Image { get; }
    /// <summary>Plugin list the image uses (unknown OS: Windows).</summary>
    public MemoryOs Os { get; }
    public string Path => Image.Path;
    public string Name => Image.Name;
    public string SizeText => NetworkTriage.FormatSize(Image.Size);
    /// <summary>False for macOS images, hibernation files and Linux images without an ISF (flagged only).</summary>
    public bool CanAnalyse { get; }
    public string Notes { get; }
    public bool HasNotes => Notes.Length > 0;

    public string FormatText
    {
        get
        {
            var f = Image.Format switch
            {
                MemoryFormat.WindowsCrashDump => "Windows crash dump",
                MemoryFormat.Hibernation => "Hibernation file",
                MemoryFormat.LiME => "LiME (Linux)",
                MemoryFormat.VMware => "VMware snapshot",
                MemoryFormat.ElfCore => "ELF core (VirtualBox/QEMU)",
                MemoryFormat.Raw => "Raw memory",
                _ => "Memory image",
            };
            var kind = Image.DumpKind switch
            {
                DumpKind.Full or DumpKind.BitmapFull => "full",
                DumpKind.Kernel or DumpKind.BitmapKernel => "kernel only",
                DumpKind.Bitmap => "bitmap",
                DumpKind.Active => "active (kernel + active user pages)",
                _ => null,
            };
            var os = Image.Os == MemoryOs.Unknown ? null : Image.Os == MemoryOs.Mac ? "macOS" : Image.Os.ToString();
            return f + (kind != null ? $", {kind}" : "") + (os != null && Image.Format != MemoryFormat.LiME ? $"  ·  {os}" : "");
        }
    }

    /// <summary>The Detected artifacts card line: "memory.raw, 16.0 GB, Raw memory · Windows".</summary>
    public string DisplayText => $"{Name}, {SizeText}, {FormatText}";
    /// <summary>Card tooltip: path and host.</summary>
    public string CardToolTip => Path + (string.IsNullOrEmpty(Host) ? "" : Environment.NewLine + "Host: " + Host + (string.IsNullOrEmpty(HostSource) ? "" : $" (from {HostSource})"));

    public string HostDisplay => string.IsNullOrEmpty(Host) ? (IsIncluded && CanAnalyse ? "…" : "-") : Host;
    /// <summary>The host name is a guess (memory strings, file or folder name), not read from a SYSTEM hive or the running computer.</summary>
    public bool HasHostWarning => !string.IsNullOrEmpty(HostSource) && HostSource is not (HostDiscovery.FromSystemHive or ArtifactScanner.FromLiveSystem);
    public string HostWarningTip => HasHostWarning
        ? $"Host name taken from the {HostSource}, not from a SYSTEM hive. The registry.printkey job records the computer name in memory and notes a mismatch (the folder is not renamed)."
        : string.IsNullOrEmpty(HostSource) ? "Host name is known once the image is planned." : "Host name source: " + HostSource;
    public string SelectionText => !CanAnalyse ? "not analysed" : !IsIncluded ? "excluded" : $"{SelectedIds(AppServices.Current.Settings.VolatilityPlugins).Count} plugins";

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(HostDisplay));
        OnPropertyChanged(nameof(CardToolTip));
    }
    partial void OnHostSourceChanged(string value)
    {
        OnPropertyChanged(nameof(HasHostWarning));
        OnPropertyChanged(nameof(HostWarningTip));
        OnPropertyChanged(nameof(CardToolTip));
    }
    partial void OnIsIncludedChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HostDisplay));
    }

    public void RaiseSelection() => OnPropertyChanged(nameof(SelectionText));

    public override string ToString() => Name;

    /// <summary>
    /// Plugin ids to plan for this image from the Ingest set <paramref name="configured"/> (none when not analysable). An unchecked image is
    /// still planned: its rows are only excluded (<see cref="IngestExclusion"/>).
    /// </summary>
    public IReadOnlyList<string> SelectedIds(IEnumerable<string>? configured) =>
        CanAnalyse ? MemoryPlugins.SelectedFor(Os, configured) : Array.Empty<string>();

    private static string BuildNotes(MemoryImage image, bool linuxIsf)
    {
        var notes = new List<string>();
        if (image.Format == MemoryFormat.Hibernation)
            notes.Add($"hiberfil.sys is not supported by Volatility 3 {VolatilitySetup.PinnedVersion} (detected and listed only)");
        else if (image.Os == MemoryOs.Mac) notes.Add("macOS memory images are not analysed (flagged only)");
        else if (image.Os == MemoryOs.Linux && !linuxIsf)
            notes.Add("Linux image: needs a symbol file (ISF) matching its kernel. Tools > Volatility 3 > Import ISF…, then scan again");
        else if (!string.IsNullOrEmpty(image.Note)) notes.Add(image.Note!);
        if (image.DumpKind is DumpKind.Kernel or DumpKind.BitmapKernel && (image.Note == null || !image.Note.Contains("user-mode", StringComparison.OrdinalIgnoreCase)))
            notes.Add("user-mode pages missing");
        return string.Join("  ·  ", notes);
    }
}

/// <summary>
/// Ingest page: memory images (Volatility 3). The scan finds the images (<see cref="TriageScanResult.MemoryImages"/>); <see cref="MemoryTriage.Plan"/>
/// plans one job per image and selected plugin. Planning reads up to 4 GB per image for the host hint, so it runs off the UI thread (cancellable,
/// debounced after a checklist change); the host hints are cached so later re-plans (systems, case folder, Volatility installed) are cheap and
/// <see cref="BuildPlan"/> can include them synchronously.
/// </summary>
public sealed partial class TriageViewModel
{
    private IReadOnlyList<MemoryImage> _memoryImages = Array.Empty<MemoryImage>();
    private readonly ConcurrentDictionary<string, string?> _hostHints = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PlannedJob> _memoryPlan = Array.Empty<PlannedJob>();
    private string? _memoryPlanKey;
    private CancellationTokenSource? _memoryCts;
    private string? _memorySourceRoot;

    [ObservableProperty] private bool _isMemoryPlanning;
    /// <summary>Adds <c>--single-swap-locations &lt;pagefile.sys&gt;</c> when a pagefile sits next to the image (off by default).</summary>
    [ObservableProperty] private bool _includePagefile;

    public ObservableCollection<MemoryImageItem> MemoryImages { get; } = new();
    public bool HasMemoryImages => MemoryImages.Count > 0;
    public bool VolatilityReady => S.Volatility != null;
    public bool AnyPagefile => MemoryImages.Any(m => m.HasPagefile);

    private void RaiseMemoryIssue()
    {
        RefreshAvailability();
    }

    private void InitMemory()
    {
        S.VolatilityChanged += OnVolatilityChanged;
        S.MemoryPluginsChanged += OnMemoryPluginsChanged;
        S.EnsureVolatilityProbed();
    }

    /// <summary>The Ingest plugin set changed in Tools: new memory rows (ticks of the other rows are kept).</summary>
    private void OnMemoryPluginsChanged()
    {
        foreach (var m in MemoryImages) m.RaiseSelection();
        ScheduleMemoryReplan(0);
    }

    /// <summary>"Change in Tools": the Volatility card (setup, Ingest plugins, symbols).</summary>
    [RelayCommand]
    private void ConfigureMemory() => S.OpenToolsConfiguration(ToolsSection.Volatility);

    private void OnVolatilityChanged()
    {
        OnPropertyChanged(nameof(VolatilityReady));
        RaiseMemoryIssue();
        // Volatility installed / removed, offline mode or the symbols changed: the placeholders become real jobs (and vice versa).
        if (_memoryImages.Count > 0 && !S.IsProbingVolatility) ScheduleMemoryReplan(0);
    }

    partial void OnIncludePagefileChanged(bool value) => ScheduleMemoryReplan(0);

    partial void OnIsMemoryPlanningChanged(bool value) => RefreshRunGate();

    /// <summary>Called by the scan (before the plan is built): the images found, their checklist rows, and a fresh planning state.</summary>
    private void SetMemoryImages(IReadOnlyList<MemoryImage> images, string? sourceRoot)
    {
        _memoryCts?.Cancel();
        _memoryCts = null;
        IsMemoryPlanning = false;
        _memoryImages = images;
        _memorySourceRoot = sourceRoot;
        _memoryPlan = Array.Empty<PlannedJob>();
        _memoryPlanKey = null;
        foreach (var m in MemoryImages) Unhook(m);
        MemoryImages.Clear();
        bool linuxIsf = MemoryTriage.HasLinuxSymbols(S.Volatility?.SymbolsDir ?? new VolatilityLocator().SymbolsDir);
        foreach (var image in images)
        {
            var item = new MemoryImageItem(image, linuxIsf);
            item.PropertyChanged += OnMemoryItemChanged;
            MemoryImages.Add(item);
        }
        RaiseMemoryIssue();
        OnPropertyChanged(nameof(HasMemoryImages));
        OnPropertyChanged(nameof(AnyPagefile));
        if (images.Count > 0) _ = FindPagefilesAsync(MemoryImages.ToList());
    }

    /// <summary>Marks the images with a pagefile.sys next to them (the "Include pagefile" option is shown only then).</summary>
    private async Task FindPagefilesAsync(IReadOnlyList<MemoryImageItem> items)
    {
        try
        {
            var paths = items.Select(i => i.Path).ToList();
            var found = await Task.Run(() => paths.Where(p => VolatilityCommandBuilder.FindPagefile(p) != null).ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var i in items) i.HasPagefile = found.Contains(i.Path);
            OnPropertyChanged(nameof(AnyPagefile));
        }
        catch (Exception ex) { AppLog.Error("Pagefile lookup failed", ex); }
    }

    private void Unhook(MemoryImageItem m) => m.PropertyChanged -= OnMemoryItemChanged;

    private void OnMemoryItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MemoryImageItem.IsIncluded)) return;
        RaiseMemoryIssue();
        ApplyExclusions();
    }

    // ------------------------------------------------------------------ planning

    /// <summary>Everything a memory plan depends on, captured on the UI thread.</summary>
    private sealed record MemoryPlanInput(string Key, IReadOnlyList<MemoryImage> Images, IReadOnlyDictionary<string, IReadOnlyList<string>> Selected,
        string CaseDir, VolatilityInstall? Install, string? SourceRoot, IReadOnlyList<DiscoveredSystem> Systems, bool Offline, bool Pagefile,
        IReadOnlyDictionary<string, string> ExtraArgs);

    /// <summary>The plugin arguments saved on the Tools page (valid ones), by plugin id.</summary>
    private IReadOnlyDictionary<string, string> MemoryExtraArgs()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in MemoryPlugins.All)
            if (ExtraArgsStore.ValidText(S.Settings, ExtraArgsStore.Volatility(p.Id)) is { } t) d[p.Id] = t;
        return d;
    }

    private MemoryPlanInput MemoryInput(string caseDir)
    {
        // every image, whatever the Memory card (and the Disk card: an image under a system root is planned on its own)
        var active = _memoryImages;
        var items = MemoryImages.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var images = new List<MemoryImage>();
        foreach (var img in active)
        {
            var ids = items.TryGetValue(img.Path, out var it) ? it.SelectedIds(S.Settings.VolatilityPlugins) : Array.Empty<string>();
            if (ids.Count == 0) continue;
            selected[img.Path] = ids;
            images.Add(img);
        }
        var systems = LabelledSystems();
        var install = S.Volatility;
        bool offline = S.Settings.VolatilityOffline;
        var extra = MemoryExtraArgs();
        var key = string.Join("|", new[]
        {
            string.Join(";", extra.Select(kv => kv.Key + "=" + kv.Value)),
            caseDir, install?.Exe ?? "-", install?.Version ?? "-", offline.ToString(), IncludePagefile.ToString(),
            string.Join(";", systems.Select(s => s.RootPath + "=" + s.HostName)),
            string.Join(";", images.Select(i => i.Path + ":" + string.Join(",", selected[i.Path]))),
            MemoryTriage.HasLinuxSymbols(install?.SymbolsDir).ToString(),
        });
        return new MemoryPlanInput(key, images, selected, caseDir, install, _memorySourceRoot, systems, offline, IncludePagefile, extra);
    }

    private static string HintKey(MemoryImage i) => i.Path + "|" + i.Size;

    /// <summary>True when no image of <paramref name="input"/> still needs the (slow) memory-strings host hint.</summary>
    private bool HintsKnown(MemoryPlanInput input) => input.Images.All(i =>
        i.Os is MemoryOs.Linux or MemoryOs.Mac || _hostHints.ContainsKey(HintKey(i)) ||
        input.Systems.Any(s => i.Path.StartsWith(s.RootPath.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Memory rows for <see cref="BuildPlan"/>: the last plan when nothing changed, a fresh (cheap) one when the host hints are known, otherwise
    /// the last plan while a background plan is scheduled (it replaces the memory rows when done).
    /// </summary>
    private IReadOnlyList<PlannedJob> PlanMemory(string caseDir)
    {
        if (_memoryImages.Count == 0) return Array.Empty<PlannedJob>();
        var input = MemoryInput(caseDir);
        if (input.Key == _memoryPlanKey) return _memoryPlan;
        if (HintsKnown(input))
        {
            try
            {
                StoreMemoryPlan(input, ComputeMemoryPlan(input, CancellationToken.None));
                // A background plan still running was computed from older inputs (e.g. the previous case folder): it must not replace this one.
                _memoryCts?.Cancel();
                _memoryCts = null;
                IsMemoryPlanning = false;
                return _memoryPlan;
            }
            catch (Exception ex) { AppLog.Error("Memory planning failed", ex); }
        }
        ScheduleMemoryReplan(0);
        return _memoryPlan.Where(p => IsUnder(p.Spec.OutputDir, caseDir)).ToList();
    }

    /// <summary>One <see cref="MemoryTriage.Plan"/> for every image (hosts are assigned across images), then each image keeps only its selected plugins.</summary>
    private IReadOnlyList<PlannedJob> ComputeMemoryPlan(MemoryPlanInput input, CancellationToken ct)
    {
        if (input.Images.Count == 0) return Array.Empty<PlannedJob>();
        var union = input.Selected.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hints = _hostHints;
        var plan = MemoryTriage.Plan(input.Images, input.CaseDir, input.Install, input.SourceRoot, input.Systems, union, input.Offline, input.Pagefile,
            hostHint: (path, token) =>
            {
                var img = input.Images.First(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                var key = HintKey(img);
                if (hints.TryGetValue(key, out var cached)) return cached;
                var name = MemoryHostHint.TryGetComputerName(path, ct: token);
                hints.TryAdd(key, name);
                return name;
            }, pluginExtraArgs: input.ExtraArgs, ct: ct);
        var info = MemoryPlugins.Find("info")?.ArtifactId;
        return plan.Where(p =>
        {
            if (!input.Selected.TryGetValue(p.Artifact.MatchedPath, out var ids)) return false;
            var aid = p.Spec.Provenance?.ArtifactId;
            // the info job stays whenever it was planned for the image: the other plugins of the image wait for it (symbol gate)
            return aid == info || ids.Any(id => string.Equals(MemoryPlugins.Find(id)?.ArtifactId, aid, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private void StoreMemoryPlan(MemoryPlanInput input, IReadOnlyList<PlannedJob> plan)
    {
        _memoryPlan = plan;
        _memoryPlanKey = input.Key;
        foreach (var m in MemoryImages)
        {
            var job = plan.FirstOrDefault(p => string.Equals(p.Artifact.MatchedPath, m.Path, StringComparison.OrdinalIgnoreCase));
            m.Host = job?.Spec.Provenance?.Host ?? "";
            m.HostSource = job?.Spec.Provenance?.HostNameSource ?? "";
        }
    }

    /// <summary>Re-plans the memory rows off the UI thread after <paramref name="delayMs"/> (debounce); a newer call cancels an older one.</summary>
    private void ScheduleMemoryReplan(int delayMs = 350)
    {
        if (_memoryImages.Count == 0) return;
        _memoryCts?.Cancel();
        var cts = _memoryCts = new CancellationTokenSource();
        _ = MemoryReplanAsync(delayMs, cts);
    }

    private async Task MemoryReplanAsync(int delayMs, CancellationTokenSource cts)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            var caseDir = CaseFolder.Trim();
            if (caseDir.Length == 0 || _layout == null) return;
            var input = MemoryInput(caseDir);
            IsMemoryPlanning = true;
            var plan = await Task.Run(() => ComputeMemoryPlan(input, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _memoryCts != cts || RefuseWhileRunning("memory re-plan")) return;
            StoreMemoryPlan(input, plan);
            ReplaceMemoryRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Planning the memory image jobs failed", ex); }
        finally
        {
            if (_memoryCts == cts) IsMemoryPlanning = false;
        }
    }

    /// <summary>Replaces the memory rows of the plan with <see cref="_memoryPlan"/> (other rows and every tick are kept; new rows use their default).</summary>
    private void ReplaceMemoryRows()
    {
        if (RefuseWhileRunning("memory re-plan")) return;
        var ticks = WantedTicks();
        var items = Planned.OrderBy(p => p.Sequence).Where(p => p.Planned.Artifact.RuleId != MemoryTriage.RuleId).Select(p => (p.Planned, p.WantedSelected)).ToList();
        foreach (var p in _memoryPlan.Select(IngestDefaults.Apply))
            items.Add((p, ticks.TryGetValue(PlannedJobItem.KeyOf(p), out var on) ? on : p.SelectedByDefault));
        SetPlan(items);
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Before a run: placeholder memory rows are re-planned with the Volatility found now, and the info job of every image with selected plugins
    /// is ticked too (the other plugins wait for it). Returns the number of selected memory rows that cannot run because Volatility is missing.
    /// </summary>
    private int PrepareMemoryRun()
    {
        var mem = Planned.Where(p => p.IsSelected && p.Planned.Artifact.RuleId == MemoryTriage.RuleId).ToList();
        if (mem.Count == 0) return 0;
        if (S.Volatility != null && mem.Any(p => MemoryTriage.IsPlaceholder(p.Planned.Spec)))
        {
            var input = MemoryInput(CaseFolder.Trim());
            if (HintsKnown(input))   // never read an image for the host hint on the UI thread
            {
                StoreMemoryPlan(input, ComputeMemoryPlan(input, CancellationToken.None));
                ReplaceMemoryRows();
            }
        }
        var info = MemoryPlugins.Find("info")?.ArtifactId;
        var imagesWithJobs = Planned.Where(p => p.IsSelected && p.Planned.Artifact.RuleId == MemoryTriage.RuleId)
            .Select(p => p.Planned.Artifact.MatchedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Planned.Where(p => p.Planned.Artifact.RuleId == MemoryTriage.RuleId && !p.IsSelected &&
                                             p.Planned.Spec.Provenance?.ArtifactId == info && imagesWithJobs.Contains(p.Planned.Artifact.MatchedPath)))
            p.IsSelected = true;
        return Planned.Count(p => p.IsSelected && p.Planned.Artifact.RuleId == MemoryTriage.RuleId && !CanRunMemoryRow(p.Planned));
    }

    /// <summary>
    /// After a run (and after every run job's manifest record is written, see <see cref="Job.Recorded"/>): hosts that only have a memory
    /// image (no system root, so no computer-details job) get ComputerDetails.json from the windows.info output, off the UI thread.
    /// </summary>
    private static void WriteMemoryOnlyComputerDetails(string caseDir, IReadOnlyList<Job> runJobs)
    {
        var hosts = runJobs.Where(j => j.Spec.ToolId == MemoryJobs.ToolId && j.State == JobState.Succeeded
                                       && string.IsNullOrEmpty(j.Spec.Provenance?.SystemRoot) && !string.IsNullOrEmpty(j.Spec.Provenance?.Host))
            .Select(j => j.Spec.Provenance!.Host!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hosts.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var h in hosts)
            {
                try { EzForensics.Core.SystemInfo.MemoryDetailsProvider.WriteForMemoryOnlyHost(caseDir, h); }
                catch (Exception ex) { AppLog.Error("Computer details from the memory image failed for " + h, ex); }
            }
        });
    }

    /// <summary>A selected memory row at run time: false (skipped) while it is a placeholder (Volatility not set up).</summary>
    private static bool CanRunMemoryRow(PlannedJob planned) => !MemoryTriage.IsPlaceholder(planned.Spec);
}
