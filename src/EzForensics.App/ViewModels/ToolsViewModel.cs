using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Catalog;
using EzForensics.Core.EventLogs;
using EzForensics.Core.Execution;

namespace EzForensics.App.ViewModels;

public enum ToolEntryKind { Cli, Gui, Special }

/// <summary>One entry of a category page: a catalog CLI tool (command form), a catalog GUI tool (launcher) or a special tool
/// (plaso, Zeek, RITA, Volatility) whose install + configuration card is shown.</summary>
public sealed partial class ToolListItem : ObservableObject
{
    [ObservableProperty] private bool _installed;

    public ToolListItem(ToolDefinition def, GuiToolTile? tile = null)
    {
        Def = def;
        Tile = tile;
        Kind = def.Kind == ToolKind.Gui ? ToolEntryKind.Gui : ToolEntryKind.Cli;
        Group = ToolGroups.Of(def);
        Id = def.Id;
        DisplayName = def.DisplayName;
        Category = def.Category;
        Description = def.Description;
        Usage = def.Usage;
        (Header, HeaderSort) = HeaderOf();
    }

    public ToolListItem(ToolsSection section, ToolGroup group, string displayName, string description)
    {
        Kind = ToolEntryKind.Special;
        Section = section;
        Group = group;
        Id = section.ToString();
        DisplayName = displayName;
        Category = ToolGroups.Info(group).Title;
        Description = description;
        Usage = section switch
        {
            ToolsSection.Plaso => EzForensics.Core.Tools.ToolDisplay.PlasoUsage,
            ToolsSection.Zeek => EzForensics.Core.Tools.ToolDisplay.ZeekUsage,
            ToolsSection.Rita => EzForensics.Core.Tools.ToolDisplay.RitaUsage,
            ToolsSection.Explore => "Used by the Explore page. Files are only read, never run.",
            ToolsSection.Documents => "Local AI (Ollama) runs the assistant on this PC. The documents it searches are managed on the AI page.",
            ToolsSection.Spectre => "Run by the Spectre engine inside the EZ Forensics Linux environment (WSL). Nothing is installed until you click.",
            _ => EzForensics.Core.Tools.ToolDisplay.VolatilityUsage,
        };
        (Header, HeaderSort) = HeaderOf();
    }

    /// <summary>List header and its sort key: Analyze shows Disk, Timeline, Memory, Network; AI shows its two items; the others keep the catalog category.</summary>
    private (string, string) HeaderOf() => ToolGroups.PageOf(Group) switch
    {
        ToolPage.Analyze => (ToolGroups.Info(Group).Title, Group switch { ToolGroup.Disk => "0", ToolGroup.Timeline => "1", ToolGroup.Memory => "2", _ => "3" }),
        ToolPage.Ai => Group == ToolGroup.Spectre ? ("Spectre engine tools", "1") : ("Local AI & documents", "0"),
        _ => (ToolCategoryBadge.Label(Category), Category),
    };

    public ToolDefinition? Def { get; }
    public GuiToolTile? Tile { get; }
    public ToolsSection? Section { get; }
    public ToolEntryKind Kind { get; }
    public ToolGroup Group { get; }
    public ToolPage Page => ToolGroups.PageOf(Group);
    public string Header { get; private set; } = "";
    public string HeaderSort { get; private set; } = "";
    public string Id { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string Description { get; }
    public string Usage { get; }
}

/// <summary>One tab of the Tools sub-navigation: Overview (Card == null) or a page.</summary>
public sealed partial class ToolTab : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public ToolTab(ToolPageCard? card) => Card = card;

    public ToolPageCard? Card { get; }
    public string Title => Card?.Title ?? "Overview";
    public string AutomationId => "Tools.Tab." + (Card == null ? "Overview" : Card.Info.Page.ToString());
}

/// <summary>Installed / missing counts of a set of tools; the base of the page cards and the Analyze chips.</summary>
public abstract partial class ToolCountsBase : ObservableObject
{
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _installedCount;

    public int Missing => Total - InstalledCount;
    public string Summary => Total == 0 ? "No tools"
        : $"{Total} tool{(Total == 1 ? "" : "s")} · {InstalledCount} installed" + (Missing > 0 ? $" · {Missing} missing" : "");
    public bool AllInstalled => Total > 0 && Missing == 0;
    public string Counts => $"{InstalledCount}/{Total}";

    partial void OnTotalChanged(int value) => Raise();
    partial void OnInstalledCountChanged(int value) => Raise();
    private void Raise()
    {
        OnPropertyChanged(nameof(Missing));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AllInstalled));
        OnPropertyChanged(nameof(Counts));
    }
}

/// <summary>A small Disk / Memory / Network chip on the Analyze card; clicking opens Analyze at that section.</summary>
public sealed class ToolGroupChip : ToolCountsBase
{
    public ToolGroupChip(ToolGroup group, params ToolGroup[] also) { Groups = new[] { group }.Concat(also).ToArray(); Title = ToolGroups.Info(group).Title; }
    public ToolGroup[] Groups { get; }
    public string Title { get; }
    public string AutomationId => "Tools.Chip." + Title;
}

/// <summary>A page card on the Tools landing page (Analyze, Explore, AI, GUI).</summary>
public sealed class ToolPageCard : ToolCountsBase
{
    public ToolPageCard(ToolPageInfo info)
    {
        Info = info;
        if (info.Page == ToolPage.Analyze)
            Chips = new[] { new ToolGroupChip(ToolGroup.Disk, ToolGroup.Timeline), new ToolGroupChip(ToolGroup.Memory), new ToolGroupChip(ToolGroup.Network) };
    }

    public ToolPageInfo Info { get; }
    public ToolGroupChip[] Chips { get; } = Array.Empty<ToolGroupChip>();
    public bool HasChips => Chips.Length > 0;
    public string Title => Info.Title;
    public string Description => Info.Description;
    public string AutomationId => "Tools.Card." + Info.Title;
}

/// <summary>The Tools page: category cards, and a category page (tool list; install when missing, configuration when installed).</summary>
public sealed partial class ToolsViewModel : PageViewModel
{
    private readonly Dictionary<string, ToolFormViewModel> _forms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped whenever install state is re-read, so list section headers recount their installed tools.</summary>
    [ObservableProperty] private int _installedStamp;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ToolListItem? _selectedTool;
    [ObservableProperty] private ToolFormViewModel? _form;
    [ObservableProperty] private Job? _currentJob;
    [ObservableProperty] private ToolPageCard? _selectedPage;

    public ToolsViewModel()
    {
        Manager = new ToolManagerViewModel();
        var tiles = Manager.GuiTools.ToDictionary(t => t.Def.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var d in S.Catalog.Where(d => d.Kind == ToolKind.Cli))
            Tools.Add(new ToolListItem(d));
        foreach (var d in S.Catalog.Where(d => d.Kind == ToolKind.Gui))
            Tools.Add(new ToolListItem(d, tiles.GetValueOrDefault(d.Id)));
        Tools.Add(new ToolListItem(ToolsSection.Plaso, ToolGroup.Timeline, "log2timeline / plaso", "Super timelines of the evidence."));
        Tools.Add(new ToolListItem(ToolsSection.Zeek, ToolGroup.Network, "Zeek", "Packet captures to Zeek logs and CSV."));
        Tools.Add(new ToolListItem(ToolsSection.Rita, ToolGroup.Network, "RITA", "Beacons, long connections, DNS tunneling over Zeek logs."));
        Tools.Add(new ToolListItem(ToolsSection.Volatility, ToolGroup.Memory, "Volatility 3", "Memory image analysis."));
        Tools.Add(new ToolListItem(ToolsSection.Explore, ToolGroup.Explore, "Explore tools", "DIE, ExifTool and CyberChef for the Explore page."));
        Tools.Add(new ToolListItem(ToolsSection.Spectre, ToolGroup.Spectre, "Spectre engine tools", "Status, version and Install / Update for every tool Spectre needs."));
        Tools.Add(new ToolListItem(ToolsSection.Documents, ToolGroup.Ai, "AI", "Use local AI (Ollama). Documents for the assistant are managed on the AI page."));
        AiDocuments = new AiDocumentsViewModel(() => Manager.Spectre.Found, ShowMessage);
        // The card count follows the AI entry's installed state (it is probed asynchronously, after the list was built).
        AiDocuments.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AiDocumentsViewModel.LocalAiInstalled)) RefreshInstalled(); };
        AiDocuments.LocalAi.Finished += _ => RefreshInstalled();
        ExploreTools = new ExploreToolsViewModel(() => { RefreshInstalled(); S.RaiseExploreToolsChanged(); });
        foreach (var info in ToolGroups.Pages) Cards.Add(new ToolPageCard(info));
        Tabs.Add(new ToolTab(null));
        foreach (var c in Cards) Tabs.Add(new ToolTab(c));
        UpdateTabs();

        ToolsView = CollectionViewSource.GetDefaultView(Tools);
        ToolsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolListItem.Header)));
        ToolsView.SortDescriptions.Add(new SortDescription(nameof(ToolListItem.HeaderSort), ListSortDirection.Ascending));
        ToolsView.SortDescriptions.Add(new SortDescription(nameof(ToolListItem.DisplayName), ListSortDirection.Ascending));
        ToolsView.Filter = o => o is ToolListItem t && SelectedPage != null && t.Page == SelectedPage.Info.Page
            && (string.IsNullOrWhiteSpace(Search)
                || t.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || t.Category.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || t.Description.Contains(Search, StringComparison.OrdinalIgnoreCase));
        S.ToolsRescanned += OnRescanned;
        S.PlasoChanged += RefreshInstalled;
        S.ZeekChanged += RefreshInstalled;
        S.RitaChanged += RefreshInstalled;
        S.VolatilityChanged += RefreshInstalled;
        Manager.Spectre.Changed += RefreshInstalled;
        S.ToolsSectionRequested += OpenSection;
        InitZeek();
        RefreshInstalled();
    }

    public override string Title => "Tools";

    /// <summary>Install / update / configuration of the special tools and the tools folder (hosted by this page).</summary>
    public ToolManagerViewModel Manager { get; }
    public ObservableCollection<ToolListItem> Tools { get; } = new();
    public ObservableCollection<ToolPageCard> Cards { get; } = new();
    public ObservableCollection<ToolTab> Tabs { get; } = new();
    public ICollectionView ToolsView { get; }
    public LogBuffer Log { get; } = new();

    public bool IsLanding => SelectedPage == null;
    public bool IsCategory => SelectedPage != null;
    public string PageTitle => SelectedPage?.Title ?? "";
    public string PageDescription => SelectedPage?.Description ?? "";

    /// <summary>Selected entry, by kind: the command form (installed CLI tool), the install panel (missing CLI / GUI tool), the launcher (installed GUI tool), or a special tool's card.</summary>
    public bool ShowForm => SelectedTool is { Kind: ToolEntryKind.Cli, Installed: true };
    public bool ShowInstall => SelectedTool is { Kind: ToolEntryKind.Cli or ToolEntryKind.Gui, Installed: false };
    public bool ShowGui => SelectedTool is { Kind: ToolEntryKind.Gui, Installed: true };
    public ExploreToolsViewModel ExploreTools { get; }
    public bool ShowExplore => SelectedTool?.Section == ToolsSection.Explore;
    public AiDocumentsViewModel AiDocuments { get; }
    public bool ShowAi => SelectedTool?.Section == ToolsSection.Documents;
    public SpectreToolsViewModel SpectreTools => Manager.Spectre;
    public bool ShowSpectre => SelectedTool?.Section == ToolsSection.Spectre;
    public bool ShowSpecial => SelectedTool?.Kind == ToolEntryKind.Special && SelectedTool.Section is not (ToolsSection.Explore or ToolsSection.Spectre or ToolsSection.Documents);
    public string SpecialSection => SelectedTool?.Section?.ToString() ?? "General";
    public bool IsZeekSelected => SelectedTool?.Section == ToolsSection.Zeek;
    /// <summary>The Zeek run panel (options and Command tile): on the Zeek entry, also before Zeek is set up (the preview then uses placeholders, Run is disabled).</summary>
    public bool ShowZeekPanel => IsZeekSelected;
    /// <summary>The shared bottom output block is Zeek's; CLI tool forms show their own Output section under the buttons.</summary>
    public bool ShowRunLog => IsZeekSelected;

    public override void OnNavigatedTo()
    {
        UpdateFirstRun();
        Manager.OnNavigatedTo();
        RefreshInstalled();
        _ = AiDocuments.RefreshAsync();   // the AI entry's installed state (Ollama) comes from a probe, not from the tool locator
    }

    /// <summary>Installed facts of the selected GUI tool (its page has no command form).</summary>
    [ObservableProperty] private ToolReleaseViewModel? _guiRelease;

    /// <summary>Back to the Overview tab (installs and updates live in its Tools table).</summary>
    [RelayCommand]
    private void GoOverview() => SelectedPage = null;

    /// <summary>Open a category page (a card was clicked).</summary>
    [RelayCommand]
    private void OpenGroup(ToolPageCard? card)
    {
        if (card == null) return;
        SelectedPage = card;
    }

    /// <summary>A Disk / Memory / Network chip: Analyze opens with that section's first tool selected.</summary>
    [RelayCommand]
    private void OpenChip(ToolGroupChip? chip)
    {
        if (chip == null) return;
        SelectedPage = Cards.First(c => c.Info.Page == ToolPage.Analyze);
        SelectedTool = ToolsView.Cast<ToolListItem>().FirstOrDefault(t => chip.Groups.Contains(t.Group));
    }

    /// <summary>Select a tab of the sub-navigation (Overview or a category).</summary>
    [RelayCommand]
    private void SelectTab(ToolTab? tab)
    {
        if (tab == null) return;
        SelectedPage = tab.Card;
    }

    private void UpdateTabs()
    {
        foreach (var t in Tabs) t.IsSelected = t.Card == SelectedPage;
    }

    /// <summary>Jumps to a special tool's entry (Ingest notices, banners).</summary>
    public void OpenSection(ToolsSection section)
    {
        var item = Tools.FirstOrDefault(t => t.Section == section);
        if (item == null) return;
        SelectedPage = Cards.First(c => c.Info.Page == item.Page);
        SelectedTool = item;
    }

    /// <summary>Select a catalog tool by id (used by other pages); opens its category.</summary>
    public void Select(string toolId)
    {
        var item = Tools.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        SelectedPage = Cards.First(c => c.Info.Page == item.Page);
        SelectedTool = item;
    }

    partial void OnSelectedPageChanged(ToolPageCard? value)
    {
        Search = "";
        UpdateTabs();
        ToolsView.Refresh();
        SelectedTool = value == null ? null : ToolsView.Cast<ToolListItem>().FirstOrDefault();
        OnPropertyChanged(nameof(IsLanding));
        OnPropertyChanged(nameof(IsCategory));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
    }

    private void OnRescanned() => RefreshInstalled();

    private bool IsInstalled(ToolListItem t) => t.Kind switch
    {
        ToolEntryKind.Special => t.Section switch
        {
            ToolsSection.Plaso => S.Plaso?.Selected != null,
            ToolsSection.Zeek => S.Zeek?.Selected != null,
            ToolsSection.Rita => S.Rita?.Installed == true,
            ToolsSection.Explore => new EzForensics.Core.Explore.ExploreToolLocator().All().All(x => x.IsInstalled),
            ToolsSection.Spectre => Manager.Spectre.Found,
            ToolsSection.Documents => AiDocuments?.LocalAiInstalled == true,
            _ => S.Volatility != null,
        },
        _ => S.IsInstalled(t.Def!.Exe),
    };

    /// <summary>Re-reads every entry's installed state and the card counts.</summary>
    private void RefreshInstalled()
    {
        foreach (var t in Tools) t.Installed = IsInstalled(t);
        InstalledStamp++;
        foreach (var f in _forms.Values) f.RefreshExe();
        foreach (var c in Cards)
        {
            var g = Tools.Where(t => t.Page == c.Info.Page).ToList();
            c.Total = g.Count;
            c.InstalledCount = g.Count(t => t.Installed);
            foreach (var chip in c.Chips)
            {
                var cg = g.Where(t => chip.Groups.Contains(t.Group)).ToList();
                chip.Total = cg.Count;
                chip.InstalledCount = cg.Count(t => t.Installed);
            }
        }
        UpdateFirstRun();
        RaiseSelectionState();
    }

    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowGui));
        OnPropertyChanged(nameof(ShowSpecial));
        OnPropertyChanged(nameof(ShowExplore));
        OnPropertyChanged(nameof(ShowSpectre));
        OnPropertyChanged(nameof(ShowAi));
        OnPropertyChanged(nameof(SpecialSection));
        OnPropertyChanged(nameof(IsZeekSelected));
        OnPropertyChanged(nameof(ShowZeekPanel));
        OnPropertyChanged(nameof(ShowRunLog));
    }

    partial void OnSearchChanged(string value) => ToolsView.Refresh();

    partial void OnSelectedToolChanged(ToolListItem? value)
    {
        RaiseSelectionState();
        GuiRelease = null;
        if (value == null) { Form = null; return; }
        if (value.Kind == ToolEntryKind.Special)
        {
            Form = null;
            if (value.Section == ToolsSection.Explore) ExploreTools.Refresh();
            if (value.Section == ToolsSection.Spectre) Manager.Spectre.EnsureProbed();
            if (value.Section == ToolsSection.Documents) _ = AiDocuments.RefreshAsync();
            if (value.Section == ToolsSection.Zeek) { S.EnsureZeekProbed(); RaiseZeek(); }
            return;
        }
        try
        {
            if (!_forms.TryGetValue(value.Def!.Id, out var f))
            {
                f = new ToolFormViewModel(value.Def, Manager);
                foreach (var row in f.IngestCommands) row.RunRequested += RunRow;
                _forms[value.Def.Id] = f;
            }
            Form = value.Kind == ToolEntryKind.Cli ? f : null;
            if (value.Kind == ToolEntryKind.Gui) { f.Release?.Refresh(); GuiRelease = f.Release; }
        }
        catch (Exception ex)
        {
            Form = null;
            ShowError($"Could not build the form for {value.DisplayName}", ex);
        }
    }

    /// <summary>Queues the spec for the tool form (Evtx rerun prompt, maintenance routing, current job, installed/admin hints).</summary>
    private Job? Submit(ToolFormViewModel f, JobSpec spec)
    {
        var outDir = spec.OutputDir;
        if (f.Evtx != null)
        {
            var existing = EvtxOutputPolicy.FindExisting(outDir);
            var choice = existing.Count == 0 ? EvtxRerunChoice.CreateNew : EvtxRerunPrompt.Ask(existing, 1);
            if (choice == EvtxRerunChoice.Cancel) { ShowMessage("Run cancelled: nothing was queued.", InfoSeverity.Info); return null; }
            spec = EvtxOutputPolicy.Apply(spec, choice);
        }
        // --sync (maps / batch files) is maintenance: the private queue, never the Ingest job list.
        var sync = spec.Args.Contains("--sync");
        var job = sync ? S.Maintenance.Queue.Enqueue(spec, outDir) : S.Queue.Enqueue(spec, outDir);
        if (sync) S.Output.Attach(job);
        f.CurrentJob = job;
        f.Log.Attach(job, S.Output);
        f.HasRun = true;
        f.RollAutoOutputDir();
        if (!f.IsInstalled)
            ShowMessage($"{f.Def.Exe} was not found under the tools root; the job will fail. Install it from this page.", InfoSeverity.Warning);
        else if (f.Def.RequiresAdmin && !Elevation.IsAdministrator)
            ShowMessage($"{f.Def.DisplayName} usually needs Administrator rights. Restart EZ Forensics as Administrator if it fails.", InfoSeverity.Warning);
        else Banner = null;
        return job;
    }

    private void RunRow(IngestCommandViewModel row)
    {
        var f = Form;
        if (f == null || !f.IngestCommands.Contains(row)) return;
        Guard(() =>
        {
            var args = row.ParsedArgs;
            string all = string.Join("\n", args);
            // The Source row is the input; only ask when it is empty.
            string? file = row.HasSource ? row.SourcePath : row.PickSource();
            if (string.IsNullOrWhiteSpace(file)) return;
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                ShowMessage($"The source does not exist: {file}. Pick it again with Browse.", InfoSeverity.Warning);
                return;
            }
            var dir = Directory.Exists(file) ? file : (Path.GetDirectoryName(file) ?? file);
            string hive = "", root = "";
            if (all.Contains("{softwareHive}", StringComparison.Ordinal))
            {
                hive = Dialogs.PickFile("Select the SOFTWARE hive", null, null) ?? "";
                if (hive.Length == 0) return;
            }
            if (all.Contains("{root}", StringComparison.Ordinal))
            {
                root = Dialogs.PickFolder("Select the system root (the drive or folder holding Windows\\ and Users\\)") ?? "";
                if (root.Length == 0) return;
            }
            var outDir = f.OutputDir;
            var expanded = args.Select(a => a
                .Replace("{file}", file, StringComparison.Ordinal)
                .Replace("{dir}", dir, StringComparison.Ordinal)
                .Replace("{out}", outDir, StringComparison.Ordinal)
                .Replace("{root}", root, StringComparison.Ordinal)
                .Replace("{softwareHive}", hive, StringComparison.Ordinal)).ToList();
            expanded = S.ExpandAppVariables(expanded, toolId: f.Def.Id).ToList();
            f.LastSource = file;
            row.Attach(Submit(f, new JobSpec(f.Def.Id, f.Def.Exe, expanded, outDir, $"{f.Def.DisplayName}: {row.Label}", f.Def.Category, f.Def.Exclusive)));
        }, "Could not start the job");
    }

    [RelayCommand]
    private void CancelJob() => CurrentJob?.Cancel();

    [RelayCommand]
    private void OpenOutput() => Guard(() =>
    {
        var dir = Form != null ? Form.CurrentJob?.Spec.OutputDir ?? Form.OutputDir : CurrentJob?.Spec.OutputDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            ShowMessage($"The output folder does not exist (nothing was written there): {dir}", InfoSeverity.Warning);
            return;
        }
        var main = Form?.MainOutputFile;
        ShellOps.OpenFolder(main != null && File.Exists(main) ? main : dir);
    }, "Could not open the output folder");

    /// <summary>Opens the run's main CSV in Analyze, with the output folder as the tree root.</summary>
    [RelayCommand]
    private void OpenOutputInAnalyze() => Guard(() =>
    {
        var dir = Form?.CurrentJob?.Spec.OutputDir ?? Form?.OutputDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            ShowMessage($"The output folder does not exist (nothing was written there): {dir}", InfoSeverity.Warning);
            return;
        }
        var main = Form?.MainOutputFile;
        if (main != null && File.Exists(main) && main.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) S.OpenInResults(dir, main);
        else { S.LastCaseDir = dir; S.Navigate<ResultsViewModel>(); }
    }, "Could not open the output in Analyze");

    /// <summary>Opens a path from the Output tile (source or output folder, or a file) in Explorer.</summary>
    [RelayCommand]
    private void OpenPath(string? path) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path) && !Directory.Exists(path)) { ShowMessage($"Not found: {path}", InfoSeverity.Warning); return; }
        ShellOps.OpenFolder(path);
    }, "Could not open the path");

}
