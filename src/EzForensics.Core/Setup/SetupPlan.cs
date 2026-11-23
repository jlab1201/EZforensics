using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Runtimes;
using EzForensics.Core.Rita;
using EzForensics.Core.Zeek;

namespace EzForensics.Core.Setup;

public enum SetupStepKind
{
    /// <summary>Part of EZ Forensics (nothing to do; shown so the user sees it is ready).</summary>
    BuiltIn,
    InstallDotnet,
    DownloadEzTools,
    SyncMaps,
    ProvisionPython,
    CreatePlasoVenv,
    InstallPlaso,
    /// <summary>Volatility 3: venv on the private Python, then <c>pip install volatility3==&lt;pin&gt; ...</c>.</summary>
    InstallVolatility,
    /// <summary>Optional offline Windows symbol pack (windows.zip) extracted into the Volatility symbols folder.</summary>
    InstallVolatilitySymbols,
    /// <summary>One Explore tool (DIE, ExifTool, CyberChef): pinned zip download, SHA-256 check, extract into the per-user tools folder. No admin.</summary>
    InstallExploreTool,
    /// <summary>Local AI for Spectre: Ollama (official per-user installer), its tuning, the recommended model, then the assistant is pointed at it. No admin.</summary>
    InstallLocalAi,
    /// <summary><c>wsl --install --no-distribution</c>, elevated (UAC); Windows must restart afterwards.</summary>
    EnableWsl,
    /// <summary>EZ Forensics' private WSL distro: pinned rootfs download + SHA-256, <c>wsl --import</c>, first-boot script (no admin).</summary>
    InstallEzDistro,
    InstallZeekWsl,
    /// <summary>RITA (GPL-3.0) in the EZ Forensics distro: pinned rita binary + ClickHouse, downloaded from upstream.</summary>
    InstallRita,
    /// <summary>Something the user must do first (WSL is not available on this Windows): explained, nothing run.</summary>
    ZeekGuidance,
}

public enum SetupStepState { Queued, Running, Done, AlreadyInstalled, Failed, Skipped, Cancelled, WaitingForRestart, ActionNeeded }

/// <summary>
/// What the user picked: item ids and the licences accepted in the UI. Network (Zeek, RITA) always runs in EZ Forensics' private
/// WSL distro: RITA is WSL-only, so setup offers no Docker choice (the Tools page can still use an existing Docker install of Zeek).
/// </summary>
public sealed class SetupSelection
{
    public HashSet<string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Licences the user accepted. Never filled automatically.</summary>
    public HashSet<string> AcceptedLicenses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SetupProfile Profile { get; set; } = SetupProfile.Custom;

    public bool Has(string id) => Items.Contains(id);

    public SetupSelection Clone()
    {
        var c = new SetupSelection { Profile = Profile };
        c.Items.UnionWith(Items);
        c.AcceptedLicenses.UnionWith(AcceptedLicenses);
        return c;
    }

    /// <summary>
    /// The items of a profile. Minimal = every default item of the default bundles (Windows artifacts, incl. the EZ viewers
    /// + super timeline): net9-only items stay selected on a machine without .NET 9 because the private .NET 9 runtime is
    /// selected with them. Everything = every available item, opt-in ones included. The private .NET 9 runtime is never selected when the machine already
    /// has .NET 9 (the wizard shows it as already installed). Opt-in items (the offline symbol pack) are selected only by Everything. Licensed items still need their
    /// licence (<see cref="AcceptedLicenses"/>). Custom starts from Minimal.
    /// </summary>
    public static SetupSelection ForProfile(SetupProfile profile, SetupCatalog catalog, SystemCheckResult? system = null)
    {
        var s = new SetupSelection { Profile = profile };
        bool net9 = system?.HasNet9 ?? true;
        foreach (var b in catalog.Bundles.Where(b => !b.ComingSoon))
        {
            bool bundleOn = profile == SetupProfile.Everything || b.DefaultOn;
            if (!bundleOn) continue;
            foreach (var i in b.Items.Where(i => !i.ComingSoon))
            {
                if (i.Hidden) continue; // hidden items (the WSL distro) come in through Requires
                if (i.Id == SetupIds.LocalAi) // Everything always ticks it (a weak PC gets the small model); Minimal only when this PC runs a local model well
                {
                    if (profile == SetupProfile.Everything || (system != null && Assistant.LocalAiAdvisor.Recommend(system).RecommendLocal)) s.Items.Add(i.Id);
                    continue;
                }
                if (i.OptIn && profile != SetupProfile.Everything) continue; // opt-in extras: only Everything (or the user's tick)
                if (i.Id == SetupIds.Dotnet9 && net9) continue;
                bool on = profile == SetupProfile.Everything || i.DefaultOn;
                if (on) s.Items.Add(i.Id);
            }
        }
        return s;
    }

    /// <summary>"minimal" | "everything" | "custom" (case-insensitive), as a CLI would accept it. "recommended" (the old name of Minimal) is an alias.</summary>
    public static bool TryParseProfile(string? text, out SetupProfile profile)
    {
        var t = text?.Trim();
        if (string.Equals(t, "recommended", StringComparison.OrdinalIgnoreCase)) { profile = SetupProfile.Minimal; return true; }
        return Enum.TryParse(t, ignoreCase: true, out profile) && Enum.IsDefined(profile);
    }
}

/// <summary>One step of a setup plan, with its live state (raised on the runner's synchronization context).</summary>
public sealed class SetupStep : INotifyPropertyChanged
{
    private SetupStepState _state;
    private string? _message;
    private double? _progress;
    private readonly StringBuilder _log = new();

    public required string Id { get; init; }
    public required SetupStepKind Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>Plain-language description of what will happen (shown before anything runs).</summary>
    public string Detail { get; init; } = "";
    /// <summary>The catalog item this step belongs to.</summary>
    public string? ItemId { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public int DownloadMB { get; init; }
    public int DiskMB { get; init; }
    public int EstimatedSeconds { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool RequiresRestart { get; init; }
    /// <summary>Extra data for the executor (EZ zips, net version, distro name...).</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Zips { get; init; } = Array.Empty<string>();

    public SetupStepState State { get => _state; set => Set(ref _state, value, nameof(State), nameof(IsFinished), nameof(Succeeded), nameof(StateLabel)); }
    /// <summary>Result / failure reason in plain language.</summary>
    public string? Message { get => _message; set => Set(ref _message, value, nameof(Message)); }
    /// <summary>0..1, or null when indeterminate.</summary>
    public double? Progress { get => _progress; set => Set(ref _progress, value, nameof(Progress)); }

    /// <summary>"≈85 MB" download, or empty when nothing is downloaded.</summary>
    public string SizeText => DownloadMB > 0 && State != SetupStepState.AlreadyInstalled ? "≈" + SetupPlan.FormatMB(DownloadMB) : "";

    public bool Succeeded => State is SetupStepState.Done or SetupStepState.AlreadyInstalled;
    public bool IsFinished => State is not (SetupStepState.Queued or SetupStepState.Running);

    public string StateLabel => State switch
    {
        SetupStepState.Queued => "Queued",
        SetupStepState.Running => "Running",
        SetupStepState.Done => "Done",
        SetupStepState.AlreadyInstalled => "Already installed",
        SetupStepState.Failed => "Failed",
        SetupStepState.Skipped => "Skipped",
        SetupStepState.Cancelled => "Cancelled",
        SetupStepState.WaitingForRestart => "After restart",
        SetupStepState.ActionNeeded => "Action needed",
        _ => State.ToString(),
    };

    public string LogText { get { lock (_log) return _log.ToString(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised for every log line (on the runner's synchronization context).</summary>
    public event Action<SetupStep, string>? LogLine;

    internal SynchronizationContext? Context { get; set; }

    public void Log(string line)
    {
        lock (_log) _log.AppendLine(line);
        Post(() => LogLine?.Invoke(this, line));
    }

    internal void ResetForRetry()
    {
        State = SetupStepState.Queued;
        Message = null;
        Progress = null;
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Post(() => { foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); });
    }

    private void Post(Action a)
    {
        var ctx = Context;
        if (ctx == null || ctx == SynchronizationContext.Current) a();
        else ctx.Post(_ => a(), null);
    }
}

/// <summary>The ordered steps for a selection on this machine, with totals. Built by <see cref="SetupPlanner.Plan"/>.</summary>
public sealed class SetupPlan
{
    public required SetupSelection Selection { get; init; }
    public required IReadOnlyList<SetupStep> Steps { get; init; }
    /// <summary>Things the user should know (an item added for a dependency, a licence missing, ...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>4 or 9: which EZ builds will be downloaded.</summary>
    public int EzNetVersion { get; init; } = 9;

    public IEnumerable<SetupStep> ToRun => Steps.Where(s => s.State != SetupStepState.AlreadyInstalled && s.Kind != SetupStepKind.BuiltIn);
    public int TotalDownloadMB => ToRun.Sum(s => s.DownloadMB);
    public int TotalDiskMB => ToRun.Sum(s => s.DiskMB);
    public TimeSpan EstimatedDuration => TimeSpan.FromSeconds(ToRun.Sum(s => s.EstimatedSeconds));
    public bool NeedsAdmin => ToRun.Any(s => s.RequiresAdmin);
    public bool NeedsRestart => ToRun.Any(s => s.RequiresRestart);
    public bool IsEmpty => !ToRun.Any();

    public bool AllSucceeded => Steps.All(s => s.Succeeded);
    public bool AnyFailed => Steps.Any(s => s.State is SetupStepState.Failed or SetupStepState.ActionNeeded or SetupStepState.Skipped or SetupStepState.Cancelled);
    public bool WaitingForRestart => Steps.Any(s => s.State == SetupStepState.WaitingForRestart);

    /// <summary>"about 4 min" style.</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? "under a minute" : t.TotalMinutes < 90 ? $"about {Math.Ceiling(t.TotalMinutes):0} min" : $"about {t.TotalHours:0.#} h";

    public static string FormatMB(int mb) => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

    /// <summary>Elapsed time: "04:07" (mm:ss), "1:02:03" (h:mm:ss) from an hour on.</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Plain-text summary (for logs and a future CLI).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var s in Steps)
            sb.Append("- ").Append(s.Title).Append(s.State == SetupStepState.AlreadyInstalled ? " (already installed)" : s.Kind == SetupStepKind.BuiltIn ? " (built in)" : "")
              .Append(s.DownloadMB > 0 && s.State != SetupStepState.AlreadyInstalled ? $", ~{FormatMB(s.DownloadMB)}" : "").AppendLine();
        sb.Append($"Total download ~{FormatMB(TotalDownloadMB)}, ~{FormatMB(TotalDiskMB)} on disk, {FormatDuration(EstimatedDuration)}.");
        foreach (var n in Notes) sb.AppendLine().Append("Note: ").Append(n);
        return sb.ToString();
    }
}

/// <summary>Turns a selection plus the system check into an ordered step list. Pure: no I/O.</summary>
public static class SetupPlanner
{
    /// <summary>Assumed download speed for the time estimate (MB/s, about 30 Mbit/s).</summary>
    public const double AssumedMBps = 4;

    private static int Secs(int downloadMB, int fixedSeconds) => fixedSeconds + (int)Math.Ceiling(downloadMB / AssumedMBps);

    public static SetupPlan Plan(SetupCatalog catalog, SetupSelection selection, SystemCheckResult system)
    {
        var notes = new List<string>();
        var sel = selection.Clone();

        // Drop unknown / coming-soon ids.
        sel.Items.RemoveWhere(id => catalog.Find(id) is not { ComingSoon: false });

        // Requirements: explicit ones, and .NET 9 for net9-only items on a machine without it.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var id in sel.Items.ToList())
            {
                var item = catalog.Find(id)!;
                foreach (var r in item.Requires)
                    if (!SkipRequirement(item.Id, r, system) && sel.Items.Add(r))
                    {
                        changed = true;
                        // the hidden WSL distro is an implementation detail of Zeek / RITA: no note for it
                        if (catalog.Find(r) is not { Hidden: true }) notes.Add($"{catalog.Find(r)?.Title ?? r} added: {item.Title} needs it.");
                    }
                if (item.RequiresNet9 && !system.HasNet9 && sel.Items.Add(SetupIds.Dotnet9))
                {
                    changed = true;
                    notes.Add($"{item.Title} needs the .NET 9 and 10 runtimes, which are not installed: private copies were added (per user, no admin).");
                }
            }
        }

        // Licences: never assumed. An item whose licence was not accepted is left out.
        foreach (var id in sel.Items.ToList())
        {
            var item = catalog.Find(id)!;
            if (item.License == null) continue;
            bool accepted = sel.AcceptedLicenses.Contains(item.License);
            if (!accepted)
            {
                sel.Items.Remove(id);
                notes.Add($"{item.Title} is left out: its licence was not accepted.");
            }
        }

        var steps = new List<SetupStep>();
        SetupStep Add(SetupStep s) { steps.Add(s); return s; }

        // Built-in features (always ready).
        foreach (var id in new[] { SetupIds.PersistenceBuiltIn, SetupIds.ComputerDetails })
            if (sel.Has(id))
            {
                var item = catalog.Find(id)!;
                Add(new SetupStep { Id = id, Kind = SetupStepKind.BuiltIn, ItemId = id, Title = item.Title, Detail = item.Description, State = SetupStepState.Done, Message = "Ready (part of EZ Forensics)." });
            }

        // .NET 9 (private) before the EZ tools, so their net9 builds are chosen.
        SetupStep? dotnet = null;
        if (sel.Has(SetupIds.Dotnet9))
        {
            var item = catalog.Find(SetupIds.Dotnet9)!;
            dotnet = Add(new SetupStep
            {
                Id = "dotnet9", Kind = SetupStepKind.InstallDotnet, ItemId = item.Id, Title = "Install the .NET 9 and 10 Desktop Runtimes (private copy)",
                Detail = "Downloads Microsoft's official dotnet-install.ps1 from https://dot.net/v1/dotnet-install.ps1 and installs the .NET 9 and 10 Desktop Runtimes into EZ Forensics' data folder (per user, no admin rights, no PATH or system changes).",
                DownloadMB = item.DownloadMB, DiskMB = item.DiskMB, EstimatedSeconds = Secs(item.DownloadMB, 30),
            });
            if (system.HasNet9)
            {
                dotnet.State = SetupStepState.AlreadyInstalled;
                dotnet.Message = system.HasSystemNet9 ? "The .NET 9 and 10 runtimes are already installed on this computer." : "EZ Forensics' private .NET 9 and 10 runtimes are already installed.";
            }
        }
        bool net9 = system.HasNet9 || dotnet != null;
        int netVersion = net9 ? 9 : 4;

        // EZ tools (one download step for every selected zip).
        var zips = sel.Items.Select(id => catalog.Find(id)!).SelectMany(i => i.Zips).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SetupStep? ez = null;
        if (zips.Count > 0)
        {
            var net9Only = zips.Where(catalog.IsNet9Only).ToList();
            if (!net9 && net9Only.Count > 0)
                notes.Add($"Without .NET 9 these tools exist only as net9 builds and are skipped: {string.Join(", ", net9Only.Select(z => Path.GetFileNameWithoutExtension(z)))}.");
            var toGet = zips.Where(z => net9 || !catalog.IsNet9Only(z)).ToList();
            var missing = toGet.Where(z => !system.InstalledZips.Contains(z, StringComparer.OrdinalIgnoreCase)).ToList();
            int dl = SetupCatalog.ZipsMB(missing);
            ez = Add(new SetupStep
            {
                Id = "ez-tools", Kind = SetupStepKind.DownloadEzTools, ItemId = SetupIds.EzCli,
                Title = $"Download Eric Zimmerman's tools ({toGet.Count} {(net9 ? "net9" : "net4")} build{(toGet.Count == 1 ? "" : "s")})",
                Detail = $"Downloads {string.Join(", ", toGet.Select(Path.GetFileNameWithoutExtension))} from download.ericzimmermanstools.com into the tools folder.",
                DependsOn = dotnet != null && dotnet.State != SetupStepState.AlreadyInstalled ? new[] { dotnet.Id } : Array.Empty<string>(),
                DownloadMB = dl, DiskMB = (int)(dl * 2.4), EstimatedSeconds = Secs(dl, 10 + 2 * missing.Count),
                Zips = toGet, Args = new Dictionary<string, string> { ["netVersion"] = netVersion.ToString() },
            });
            if (missing.Count == 0)
            {
                ez.State = SetupStepState.AlreadyInstalled;
                ez.Message = "All selected tools are already in the tools folder (Tools checks for updates).";
            }
        }

        if (sel.Has(SetupIds.EzSync))
        {
            var item = catalog.Find(SetupIds.EzSync)!;
            Add(new SetupStep
            {
                Id = "ez-sync", Kind = SetupStepKind.SyncMaps, ItemId = item.Id, Title = "Update maps and batch files (EvtxECmd, RECmd, SQLECmd --sync)",
                Detail = "Runs each tool with --sync as a job: the latest maps and batch files come from Eric Zimmerman's GitHub repositories.",
                DependsOn = ez != null && ez.State != SetupStepState.AlreadyInstalled ? new[] { ez.Id } : Array.Empty<string>(),
                DownloadMB = item.DownloadMB, DiskMB = item.DiskMB, EstimatedSeconds = Secs(item.DownloadMB, 40),
            });
        }

        // Python for plaso and Volatility: one private-Python step, shared (only when no usable Python exists).
        SetupStep? py = null;
        bool plasoNeeded = sel.Has(SetupIds.Plaso) && system.PlasoVersion == null;
        bool volNeeded = sel.Has(SetupIds.Volatility) && system.VolatilityVersion == null;
        if (system.Python == null && (plasoNeeded || volNeeded))
        {
            py = Add(new SetupStep
            {
                Id = "python", Kind = SetupStepKind.ProvisionPython, ItemId = plasoNeeded ? SetupIds.Plaso : SetupIds.Volatility, Title = $"Download a private Python {PrivatePython.Default.Version}",
                Detail = $"No Python 3.10-3.13 was found, so python.org's official NuGet package (from api.nuget.org, SHA-512 checked) is extracted into EZ Forensics' data folder. No installer, no admin rights, no PATH changes.",
                DownloadMB = PlasoSetup.PythonDownloadMB, DiskMB = PlasoSetup.PythonDiskMB, EstimatedSeconds = Secs(PlasoSetup.PythonDownloadMB, 15),
            });
        }

        // plaso: venv, pip.
        if (sel.Has(SetupIds.Plaso))
        {
            if (system.PlasoVersion != null)
            {
                Add(new SetupStep
                {
                    Id = "plaso", Kind = SetupStepKind.InstallPlaso, ItemId = SetupIds.Plaso, Title = "log2timeline / plaso",
                    State = SetupStepState.AlreadyInstalled, Message = $"plaso {system.PlasoVersion} is already installed.",
                });
            }
            else
            {
                var venv = Add(new SetupStep
                {
                    Id = "plaso-venv", Kind = SetupStepKind.CreatePlasoVenv, ItemId = SetupIds.Plaso, Title = "Create the plaso Python environment",
                    Detail = py != null ? "python -m venv with the private Python." : $"python -m venv with {system.Python}.",
                    DependsOn = py != null ? new[] { py.Id } : Array.Empty<string>(), EstimatedSeconds = 25,
                });
                Add(new SetupStep
                {
                    Id = "plaso-pip", Kind = SetupStepKind.InstallPlaso, ItemId = SetupIds.Plaso, Title = "pip install plaso",
                    Detail = "Installs log2timeline / plaso and its dependencies from PyPI into that environment.",
                    DependsOn = new[] { venv.Id }, DownloadMB = PlasoSetup.PackagesDownloadMB, DiskMB = PlasoSetup.VenvDiskMB,
                    EstimatedSeconds = Secs(PlasoSetup.PackagesDownloadMB, 80),
                });
            }
        }

        // Volatility 3: its own venv on the same Python, pip install of the pinned release.
        SetupStep? vol = null;
        if (sel.Has(SetupIds.Volatility))
        {
            if (system.VolatilityVersion != null)
            {
                vol = Add(new SetupStep
                {
                    Id = "volatility", Kind = SetupStepKind.InstallVolatility, ItemId = SetupIds.Volatility, Title = "Volatility 3",
                    State = SetupStepState.AlreadyInstalled, Message = $"Volatility 3 {system.VolatilityVersion} is already installed.",
                });
            }
            else
            {
                vol = Add(new SetupStep
                {
                    Id = "volatility", Kind = SetupStepKind.InstallVolatility, ItemId = SetupIds.Volatility, Title = $"Install Volatility 3 ({VolatilitySetup.PinnedVersion})",
                    Detail = $"Creates a Python environment (with {(py != null ? "the private Python" : system.Python)}) and runs pip install volatility3=={VolatilitySetup.PinnedVersion} " +
                             $"{string.Join(" ", VolatilitySetup.Packages)} from PyPI.",
                    DependsOn = py != null ? new[] { py.Id } : Array.Empty<string>(),
                    DownloadMB = VolatilitySetup.PackagesDownloadMB, DiskMB = VolatilitySetup.VenvDiskMB,
                    EstimatedSeconds = Secs(VolatilitySetup.PackagesDownloadMB, 90),
                });
            }
        }

        if (sel.Has(SetupIds.VolatilitySymbols))
        {
            var item = catalog.Find(SetupIds.VolatilitySymbols)!;
            var sym = Add(new SetupStep
            {
                Id = "volatility-symbols", Kind = SetupStepKind.InstallVolatilitySymbols, ItemId = item.Id, Title = "Download the Windows symbol pack (offline)",
                Detail = $"Downloads {VolatilitySetup.SymbolPackUrl} (≈{SetupPlan.FormatMB(item.DownloadMB)}) and extracts it into Volatility's symbols folder, so memory images can be analysed offline.",
                DependsOn = vol != null && vol.State != SetupStepState.AlreadyInstalled ? new[] { vol.Id } : Array.Empty<string>(),
                DownloadMB = item.DownloadMB, DiskMB = item.DiskMB, EstimatedSeconds = Secs(item.DownloadMB, 60),
            });
            if (system.VolatilitySymbolsInstalled) { sym.State = SetupStepState.AlreadyInstalled; sym.Message = "The Windows symbol pack is already installed."; }
        }

        foreach (var (itemId, spec) in new[] { (SetupIds.ExploreDie, Explore.ExploreToolReleases.Die), (SetupIds.ExploreExifTool, Explore.ExploreToolReleases.ExifTool),
                     (SetupIds.ExploreCyberChef, Explore.ExploreToolReleases.CyberChef) })
        {
            if (!sel.Has(itemId)) continue;
            int dl = (int)Math.Max(1, spec.Size / (1024 * 1024));
            var step = new SetupStep
            {
                Id = itemId, Kind = SetupStepKind.InstallExploreTool, ItemId = itemId, Title = $"Install {spec.Name} ({spec.Version})",
                Detail = $"Downloads {spec.Url} and checks its SHA-256 before extracting it into EZ Forensics' tools folder (Explore page).",
                DownloadMB = dl, DiskMB = (int)Math.Max(1, spec.Size * 3 / (1024 * 1024)), EstimatedSeconds = Secs(dl, 20),
            };
            Add(step);
            if (system.ExploreCurrent.Contains(spec.Tool.ToString())) { step.State = SetupStepState.AlreadyInstalled; step.Message = $"{spec.Name} {spec.Version} is already installed."; }
        }

        if (sel.Has(SetupIds.LocalAi))
        {
            var (model, gb) = Assistant.LocalAiAdvisor.ModelToInstall(Assistant.LocalAiAdvisor.Recommend(system));
            int dl = (int)(gb * 1024) + (system.Ollama.Installed ? 0 : 1300);
            var step = new SetupStep
            {
                Id = SetupIds.LocalAi, Kind = SetupStepKind.InstallLocalAi, ItemId = SetupIds.LocalAi, Title = $"Local AI: Ollama + {model}",
                Detail = $"Installs Ollama from ollama.com (per user, no admin), downloads {model} (≈{gb:0.#} GB) and points Spectre at it. Anything already there is skipped.",
                DownloadMB = dl, DiskMB = dl * 2, EstimatedSeconds = Secs(dl, 40),
                Args = new Dictionary<string, string> { ["model"] = model },
            };
            Add(step); // never "already installed": the step also points Spectre at the model, and skips what is there
        }

        if (sel.Has(SetupIds.Zeek) || sel.Has(SetupIds.EzDistro) || sel.Has(SetupIds.Rita)) PlanNetwork(sel, system, notes, Add);

        return new SetupPlan { Selection = sel, Steps = Order(steps), Notes = notes, EzNetVersion = netVersion };
    }

    /// <summary>
    /// Run order: built-in features, then every step that needs administrator rights (one UAC prompt right after Install, so the
    /// user can walk away), then everything that does not need WSL (EZ tools, .NET, Python, plaso, Volatility...), and the
    /// WSL / network steps (EZ Forensics distro, Zeek, RITA) last. A restart that enabling WSL needs is therefore only asked once,
    /// at the very end. Stable: the order inside each group is kept, and dependencies always point to earlier steps.
    /// </summary>
    public static IReadOnlyList<SetupStep> Order(IEnumerable<SetupStep> steps) => steps.OrderBy(Group).ToList();

    private static int Group(SetupStep s) => s switch
    {
        { Kind: SetupStepKind.BuiltIn } => 0,
        { RequiresAdmin: true } => 1,
        { Kind: SetupStepKind.InstallEzDistro or SetupStepKind.InstallZeekWsl or SetupStepKind.InstallRita or SetupStepKind.ZeekGuidance } => 3,
        _ => 2,
    };

    /// <summary>Zeek needs the private distro unless Zeek already works somewhere (RITA always needs it).</summary>
    private static bool SkipRequirement(string itemId, string required, SystemCheckResult system) =>
        itemId.Equals(SetupIds.Zeek, StringComparison.OrdinalIgnoreCase) && required.Equals(SetupIds.EzDistro, StringComparison.OrdinalIgnoreCase) &&
        system.ZeekSummary != null;

    /// <summary>Network: WSL (enable it if needed) -> the private EZ Forensics distro -> Zeek and RITA in it.</summary>
    private static void PlanNetwork(SetupSelection sel, SystemCheckResult system, List<string> notes, Func<SetupStep, SetupStep> add)
    {
        bool zeek = sel.Has(SetupIds.Zeek), distro = sel.Has(SetupIds.EzDistro);
        if (zeek && system.ZeekSummary != null)
        {
            add(new SetupStep { Id = "zeek", Kind = SetupStepKind.InstallZeekWsl, ItemId = SetupIds.Zeek, Title = "Zeek", State = SetupStepState.AlreadyInstalled, Message = $"Zeek is already available: {system.ZeekSummary}." });
            zeek = false;
        }
        if (!zeek && !distro) return;

        SetupStep? enable = null;
        switch (system.Wsl)
        {
            case WslStatus.Enabled:
                break;
            case WslStatus.NotEnabled or WslStatus.Unknown:
                enable = add(new SetupStep
                {
                    Id = "enable-wsl", Kind = SetupStepKind.EnableWsl, ItemId = SetupIds.EzDistro, Title = "Enable WSL (Windows Subsystem for Linux)",
                    Detail = "Runs first, so Windows asks for administrator permission once, right after you click Install: 'wsl --install --no-distribution' (or DISM on older Windows) enables WSL. Everything else installs without admin. If Windows must restart, setup asks once at the end and continues by itself after you sign in again.",
                    RequiresAdmin = true, RequiresRestart = true, DownloadMB = 0, EstimatedSeconds = 180,
                });
                notes.Add("The EZ Forensics Linux environment (Network / Zeek / RITA) needs WSL, which is not enabled: Windows asks once for administrator permission at the start, and probably needs one restart at the end.");
                break;
            default:
                add(new SetupStep
                {
                    Id = "ez-distro", Kind = SetupStepKind.ZeekGuidance, ItemId = SetupIds.EzDistro, Title = "Network: WSL is not available",
                    Detail = "WSL is not available on this version of Windows, so Zeek and RITA cannot be installed. Skip Network.",
                });
                return;
        }

        var ez = add(new SetupStep
        {
            Id = "ez-distro", Kind = SetupStepKind.InstallEzDistro, ItemId = SetupIds.EzDistro,
            Title = "Create the EZ Forensics Linux environment (WSL)" + (enable != null ? " (after the restart)" : ""),
            Detail = $"Downloads the official {EzDistro.Rootfs.Display} image from cdimage.ubuntu.com (≈{EzDistro.Rootfs.Bytes >> 20} MB, SHA-256 checked) and creates a private WSL distro named {EzDistro.Name} with 'wsl --import' in {EzDistro.InstallDir} (per user, no admin rights). Then installs a few base packages with apt. Your own WSL distros are not touched.",
            DependsOn = enable != null ? new[] { enable.Id } : Array.Empty<string>(),
            DownloadMB = EzDistro.DownloadMB, DiskMB = EzDistro.DiskMB, EstimatedSeconds = Secs(EzDistro.DownloadMB, 90),
        });
        if (system.EzDistroReady)
        {
            ez.State = SetupStepState.AlreadyInstalled;
            ez.Message = $"The EZ Forensics Linux environment is already installed: {system.EzDistroState!.Summary}.";
        }
        SetupStep? zeekStep = null;
        if (zeek)
        zeekStep = add(new SetupStep
        {
            Id = "zeek", Kind = SetupStepKind.InstallZeekWsl, ItemId = SetupIds.Zeek, Title = "Zeek: install in the EZ Forensics Linux environment" + (enable != null ? " (after the restart)" : ""),
            Detail = $"Inside the {EzDistro.Name} WSL distro, as its root user (no Windows admin rights): adds the official Zeek package repository (openSUSE Build Service, security:zeek) with its signing key and runs apt-get install zeek.",
            DependsOn = ez.State != SetupStepState.AlreadyInstalled ? new[] { ez.Id } : Array.Empty<string>(),
            DownloadMB = ZeekSetup.WslDownloadMB, DiskMB = ZeekSetup.WslDiskMB, EstimatedSeconds = Secs(ZeekSetup.WslDownloadMB, 180),
            Args = new Dictionary<string, string> { ["distro"] = EzDistro.Name },
        });

        if (sel.Has(SetupIds.Rita))
        {
            var deps = new List<string>();
            if (ez.State != SetupStepState.AlreadyInstalled) deps.Add(ez.Id);
            if (zeekStep != null) deps.Add(zeekStep.Id);
            var rita = add(new SetupStep
            {
                Id = "rita", Kind = SetupStepKind.InstallRita, ItemId = SetupIds.Rita, Title = "RITA: install in the EZ Forensics Linux environment" + (enable != null ? " (after the restart)" : ""),
                Detail = $"RITA {RitaRelease.Version} (GPL-3.0) is downloaded from upstream, not bundled: the official image layer from ghcr.io (SHA-256 checked) and ClickHouse {RitaRelease.ClickHouseVersion} from GitHub. Both are installed in the {EzDistro.Name} WSL distro, as its root user (no Windows admin rights).",
                DependsOn = deps.ToArray(),
                DownloadMB = RitaRelease.DownloadMB, DiskMB = RitaRelease.DiskMB, EstimatedSeconds = Secs(RitaRelease.DownloadMB, 120),
            });
            if (system.RitaVersion != null && !system.RitaUpdateAvailable) { rita.State = SetupStepState.AlreadyInstalled; rita.Message = $"RITA {system.RitaVersion} is already installed."; }
        }
    }
}
