using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Platforms;

namespace EzForensics.Core.Triage;

/// <summary>
/// Memory part of triage: every memory image found in the source (<see cref="MemoryImageScanner"/>) becomes one Volatility 3 job per selected
/// plugin (case layout v2: <c>&lt;case&gt;\&lt;Host&gt;\Memory\Memory_&lt;Variant&gt;[_&lt;image&gt;].csv</c>). Same shape as <see cref="NetworkTriage"/>.
/// </summary>
public static class MemoryTriage
{
    public const string RuleId = MemoryJobs.RuleId;
    /// <summary>Host name source when the host is the image's file name.</summary>
    public const string FromFileName = "file name";
    /// <summary>Exe of the placeholder jobs planned before Volatility is installed.</summary>
    public const string PlaceholderExe = "vol.exe";
    /// <summary>Volatility 3 (2.28) reads crash dump types 1 (full) and 5 (bitmap) only.</summary>
    public const string UnsupportedDumpNote = "Volatility 3 cannot read this dump type (kernel/active memory dump). Re-acquire a full or bitmap dump, or a raw image.";

    /// <summary>
    /// One planned job per selected plugin and image, the <c>windows.info</c> job first. Host of an image: (1) the discovered system whose root contains it
    /// (host, provenance, archive origin and acquisition kind of that system), (2) <see cref="MemoryHostHint"/> (a computer name equal to a system's
    /// host of this scan shares that host; images with the same name share a host), (3) the file name. Names are made unique against
    /// <paramref name="existingHosts"/> and the systems (" (2)"...). The qualifier is null for a host's first image, the image stem for later ones.
    /// </summary>
    /// <remarks>
    /// Reads up to 4 GB per image for the host hint when the image is not under a system root: call off the UI thread (cancellable).
    /// Only the selected plugins are planned (so the symbol gate is consistent): re-plan when the selection changes.
    /// With <paramref name="install"/> null (Volatility not set up yet) the jobs are placeholders (<see cref="IsPlaceholder"/>); plan again once it is installed.
    /// </remarks>
    /// <param name="selectedPluginIds">Plugin ids (plugins.json <c>id</c>) to plan; null = every <c>defaultSelected</c> plugin.</param>
    /// <param name="includePagefile">Add <c>--single-swap-locations</c> for a pagefile.sys next to the image (off by default).</param>
    /// <param name="notes">Receives plain-language notes (kernel dumps, Linux/macOS images without symbols...).</param>
    /// <param name="hostHint">Replaces <see cref="MemoryHostHint.TryGetComputerName"/> (tests).</param>
    /// <param name="pluginExtraArgs">The user's additional arguments by plugin id (<see cref="Commands.ExtraArgsStore.Volatility"/>, valid ones only); null = none.</param>
    public static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<MemoryImage> images, string caseDir, VolatilityInstall? install, string? sourceRoot = null,
        IReadOnlyList<DiscoveredSystem>? systems = null, IReadOnlyCollection<string>? selectedPluginIds = null, bool offline = false,
        bool includePagefile = false, IEnumerable<string>? existingHosts = null, ICollection<string>? notes = null,
        Func<string, CancellationToken, string?>? hostHint = null, IReadOnlyDictionary<string, string>? pluginExtraArgs = null, CancellationToken ct = default)
    {
        var list = new List<PlannedJob>();
        var usedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in existingHosts ?? Array.Empty<string>()) usedHosts.Add(TriagePlanner.HostFolder(h));
        foreach (var s in systems ?? Array.Empty<DiscoveredSystem>()) usedHosts.Add(TriagePlanner.HostFolder(s.HostName));
        var hintHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // computer name -> host (shared by images of one machine)
        var firstImageOfHost = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedQualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tool = install ?? new VolatilityInstall(PlaceholderExe, "", null, "", "", "");
        bool linuxIsf = HasLinuxSymbols(tool.SymbolsDir);
        var multiHost = false;
        var perImage = new List<(MemoryImage Image, string Host, string? Source, DiscoveredSystem? System)>();

        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            var system = SystemFor(image.Path, systems);
            string host, source;
            if (system != null) { host = TriagePlanner.HostFolder(system.HostName); source = system.HostNameSource; }
            else
            {
                string? name = null;
                if (image.Os != MemoryOs.Linux && image.Os != MemoryOs.Mac)
                {
                    try { name = hostHint != null ? hostHint(image.Path, ct) : MemoryHostHint.TryGetComputerName(image.Path, ct: ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = HostDiscovery.Sanitize(name.Trim());
                    source = MemoryHostHint.FromMemoryStrings;
                    var sys = systems?.FirstOrDefault(s => string.Equals(s.HostName, name, StringComparison.OrdinalIgnoreCase));
                    if (hintHosts.TryGetValue(name, out var shared)) host = shared;
                    else if (sys != null) { host = TriagePlanner.HostFolder(sys.HostName); system = sys; hintHosts[name] = host; }
                    else { host = UniqueHost(TriagePlanner.HostFolder(name), usedHosts); hintHosts[name] = host; }
                }
                else
                {
                    source = FromFileName;
                    host = UniqueHost(TriagePlanner.HostFolder(HostDiscovery.Sanitize(Path.GetFileNameWithoutExtension(image.Path))), usedHosts);
                }
            }
            perImage.Add((image, host, source, system));
        }
        multiHost = perImage.Select(p => p.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        foreach (var (image, host, hostSource, system) in perImage)
        {
            string? qualifier = null;
            if (!firstImageOfHost.Add(host))
                qualifier = TriagePlanner.UniqueQualifier(usedQualifiers, host, RuleId,
                    ArtifactNaming.Qualifier(Path.GetFileNameWithoutExtension(image.Path)) ?? "image");
            var finalDir = ArtifactOutputLayout.DirectoryFor(caseDir, host, MemoryJobs.Category);
            var staging = TriagePlanner.StagingDir(caseDir, host, RuleId, qualifier, image.Path);
            var gate = new MemorySymbolGate();

            if (!string.IsNullOrEmpty(image.Note)) notes?.Add($"{Path.GetFileName(image.Path)}: {image.Note}");
            var plugins = PluginsFor(image, selectedPluginIds, linuxIsf, out var skipNote);
            if (skipNote != null) notes?.Add($"{Path.GetFileName(image.Path)}: {skipNote}");
            if (image.Os == MemoryOs.Mac) notes?.Add($"{Path.GetFileName(image.Path)}: macOS memory images are not analysed (no plugins planned).");
            bool infoPlanned = false;

            foreach (var plugin in plugins.OrderByDescending(p => p.IsInfo))
            {
                JobSpec spec;
                try { spec = MemoryJobs.CreateSpec(tool, image, plugin, host, finalDir, staging, gate, offline, qualifier, includePagefile && plugin.Os == "windows",
                        extraArgs: pluginExtraArgs != null && pluginExtraArgs.TryGetValue(plugin.Id, out var extra) ? extra : null); }
                catch (ArgumentException) { continue; }
                infoPlanned |= plugin.IsInfo;
                if (multiHost) spec = spec with { Label = $"{host} · {spec.Label}" };
                spec = spec with
                {
                    Provenance = spec.Provenance! with
                    {
                        HostNameSource = hostSource, SystemRoot = system?.RootPath,
                        AcquisitionKind = system?.AcquisitionKind.ToString(), OsFamily = system?.OsFamily.ToString() ?? OsOf(image),
                        Archive = system?.Archive,
                    },
                };
                if (plugin.Id == "computername") spec = WithComputerNameCheck(spec, host);
                var artifact = new DetectedArtifact(RuleId, plugin.Label, MemoryJobs.ToolId, MemoryJobs.Category, image.Path, true, 1,
                    system?.RootPath ?? sourceRoot ?? "", false, plugin.DefaultSelected, system);
                list.Add(new PlannedJob(spec, true, artifact, false));
            }
            // No info job planned (not selected, or its spec could not be built): nobody would decide the gate. (The gate also opens itself when a
            // waiting plugin finds no info job queued, MemorySymbolGate.OrphanGrace; this just avoids the delay.)
            if (!infoPlanned) gate.Open();
        }
        return list;
    }

    /// <summary>True for a job planned before Volatility was installed.</summary>
    public static bool IsPlaceholder(JobSpec spec) => spec.ToolId == MemoryJobs.ToolId && spec.ExePath == PlaceholderExe;

    /// <summary>Plugins to plan for an image: Windows (and unknown OS) plugins, or the Linux ones when an ISF is present; nothing for macOS.</summary>
    public static IReadOnlyList<MemoryPluginDef> PluginsFor(MemoryImage image, IReadOnlyCollection<string>? selectedIds, bool linuxIsfPresent, out string? note)
    {
        note = null;
        if (image.Os == MemoryOs.Mac) return Array.Empty<MemoryPluginDef>();
        if (image.Format == MemoryFormat.WindowsCrashDump && image.DumpKind is DumpKind.Kernel or DumpKind.Active)
        {
            note = UnsupportedDumpNote;
            return Array.Empty<MemoryPluginDef>();
        }
        var os = image.Os == MemoryOs.Linux ? MemoryOs.Linux : MemoryOs.Windows;
        if (os == MemoryOs.Linux && !linuxIsfPresent)
        {
            note = "Linux image: no matching symbol file (ISF) found in the symbols folder; use Import ISF… to add one, then plan again.";
            return Array.Empty<MemoryPluginDef>();
        }
        return MemoryPlugins.ForOs(os)
            .Where(p => selectedIds == null ? p.DefaultSelected : selectedIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>True when the symbols folder holds Linux ISF files (<c>symbols\linux\...</c>).</summary>
    public static bool HasLinuxSymbols(string? symbolsDir)
    {
        if (string.IsNullOrEmpty(symbolsDir)) return false;
        try
        {
            var dir = Path.Combine(symbolsDir, "linux");
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// After the registry.printkey job: records the authoritative computer name in the manifest details (<c>computername.registry</c>) and, when it
    /// differs from the planned host, a note (<c>computername.note</c>) and a log line. The folder is never renamed.
    /// </summary>
    private static JobSpec WithComputerNameCheck(JobSpec spec, string host)
    {
        var inner = spec.PostProcess;
        var stdout = spec.StdoutPath;
        return spec with
        {
            PostProcess = async (ctx, ct) =>
            {
                if (inner != null) await inner(ctx, ct).ConfigureAwait(false);
                if (ctx.ExitCode != 0 || stdout == null || !File.Exists(stdout)) return;
                var name = ReadComputerName(stdout);
                if (name == null) return;
                ctx.SetDetail("computername.registry", name);
                if (!string.Equals(HostDiscovery.Sanitize(name), host, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
                {
                    var note = $"The registry in memory says the computer name is '{name}', but the host folder is '{host}'. The folder is not renamed.";
                    ctx.SetDetail("computername.note", note);
                    ctx.Log("# " + note);
                }
            },
        };
    }

    /// <summary>The <c>ComputerName</c> value in a (normalised) <c>windows.registry.printkey</c> CSV; null when absent.</summary>
    public static string? ReadComputerName(string csvPath)
    {
        try
        {
            using var reader = new StreamReader(csvPath, Encoding.UTF8);
            string[]? header = null;
            int nameCol = -1, dataCol = -1;
            foreach (var row in CsvRows(reader))
            {
                if (header == null)
                {
                    header = row;
                    nameCol = Array.FindIndex(row, h => h.Equals("Name", StringComparison.OrdinalIgnoreCase));
                    dataCol = Array.FindIndex(row, h => h.Equals("Data", StringComparison.OrdinalIgnoreCase));
                    if (nameCol < 0 || dataCol < 0) return null;
                    continue;
                }
                if (row.Length > Math.Max(nameCol, dataCol) && row[nameCol].Trim().Equals("ComputerName", StringComparison.OrdinalIgnoreCase))
                {
                    var v = row[dataCol].Trim().Trim('\0', '"', ' ');
                    if (v.Length > 0 && !v.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return v;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static IEnumerable<string[]> CsvRows(TextReader r)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        int c;
        while ((c = r.Read()) >= 0)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"') { if (r.Peek() == '"') { sb.Append('"'); r.Read(); } else quoted = false; }
                else sb.Append(ch);
            }
            else if (ch == '"') { quoted = true; any = true; }
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); any = true; }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && r.Peek() == '\n') r.Read();
                if (any || sb.Length > 0) { fields.Add(sb.ToString()); yield return fields.ToArray(); }
                fields.Clear(); sb.Clear(); any = false;
            }
            else { sb.Append(ch); any = true; }
        }
        if (any || sb.Length > 0) { fields.Add(sb.ToString()); yield return fields.ToArray(); }
    }

    private static string OsOf(MemoryImage image) => image.Os switch
    {
        MemoryOs.Windows => OsFamily.Windows.ToString(),
        MemoryOs.Linux => OsFamily.Linux.ToString(),
        MemoryOs.Mac => OsFamily.MacOS.ToString(),
        _ => OsFamily.Unknown.ToString(),
    };

    private static string UniqueHost(string host, HashSet<string> used)
    {
        var name = host;
        for (int i = 2; !used.Add(name); i++) name = $"{host} ({i})";
        return name;
    }

    private static DiscoveredSystem? SystemFor(string path, IReadOnlyList<DiscoveredSystem>? systems)
    {
        if (systems == null || systems.Count == 0) return null;
        return systems.Where(s => path.StartsWith(s.RootPath.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.RootPath.Length).FirstOrDefault();
    }
}
