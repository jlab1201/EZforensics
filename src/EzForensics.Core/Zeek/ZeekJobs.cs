using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Zeek;

/// <summary>
/// Zeek runs as JobQueue jobs (logged, cancellable, on the Jobs page, in the run manifest): one job per capture, output in
/// <c>&lt;case&gt;\Network\Zeek\&lt;capture name&gt;\</c>. After Zeek exits, the job converts every <c>*.log</c> to CSV
/// (<see cref="ZeekLogConverter"/>) and records the input SHA-256 and the engine facts in the manifest.
/// </summary>
public static class ZeekJobs
{
    public const string ToolId = "Zeek";
    public const string Category = "Network";
    public const string NetworkFolder = "Network";
    public const string ZeekFolder = "Zeek";

    /// <summary>True when <paramref name="caseDir"/> carries the case layout v2 marker (<c>case.json</c>, layoutVersion 2 or later).</summary>
    public static bool IsHostLayout(string caseDir) => EzForensics.Core.Case.CaseLayout.ReadMarker(caseDir) is { LayoutVersion: >= EzForensics.Core.Case.CaseLayout.CurrentVersion };

    /// <summary>
    /// Where Zeek output goes: <c>&lt;case&gt;\Network\Zeek</c> (layout v1, standalone Tools-page runs) or, in a layout v2 case,
    /// <c>&lt;case&gt;\_Case\Network</c> (the case-wide folder of captures with no identified host; triage places captures under their host).
    /// </summary>
    public static string OutputRoot(string caseDir) => IsHostLayout(caseDir)
        ? Platforms.ArtifactOutputLayout.DirectoryFor(caseDir, Rita.RitaJobs.CaseWideHost, Category)
        : Path.Combine(caseDir, NetworkFolder, ZeekFolder);

    /// <summary>Folder name for one capture: its file name with characters Windows does not allow replaced.</summary>
    public static string FolderNameFor(string pcapPath)
    {
        var name = Path.GetFileName(pcapPath.TrimEnd('\\', '/'));
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim(' ', '.');
        if (s.Length > 120) s = s[..80] + "_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        if (s.Length == 0) return "capture";
        // CON, NUL, COM1 ... (with or without an extension) cannot be folder names on Windows.
        return System.Text.RegularExpressions.Regex.IsMatch(s.Split('.')[0].TrimEnd(), "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "_" + s : s;
    }

    /// <summary>
    /// One output folder per capture under <paramref name="outputRoot"/>; captures with the same file name (from different
    /// folders) get <c>name (2)</c>, <c>name (3)</c>, ... in input order.
    /// </summary>
    public static IReadOnlyList<(string Pcap, string OutputDir)> AssignOutputDirs(IEnumerable<string> pcaps, string outputRoot)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var p in pcaps)
        {
            var baseName = FolderNameFor(p);
            var name = baseName;
            for (int n = 2; !used.Add(name); n++) name = $"{baseName} ({n})";
            list.Add((p, Path.Combine(outputRoot, name)));
        }
        return list;
    }

    /// <summary>Per-job timeout: 30 min plus 20 min per GB of capture (a multi-GB capture can take a long time).</summary>
    public static TimeSpan TimeoutFor(long bytes) => TimeSpan.FromMinutes(30 + 20.0 * Math.Max(0, bytes) / (1L << 30));

    public static JobSpec CreateSpec(ZeekInstall install, string pcap, string outputDir, ZeekRunOptions? options = null, string? label = null)
    {
        var o = new ZeekRunOptions
        {
            Pcap = pcap,
            OutputDir = outputDir,
            IncludeLocal = options?.IncludeLocal ?? true,
            ExtraScripts = options?.ExtraScripts.ToList() ?? new List<string>(),
            ExtraArgs = options?.ExtraArgs ?? Array.Empty<string>(),
        };
        var inv = ZeekCommandBuilder.Build(install, o);
        long size = 0;
        try { size = new FileInfo(pcap).Length; } catch (Exception) { }
        var details = new Dictionary<string, string>(install.ManifestDetails())
        {
            ["input.path"] = Path.GetFullPath(pcap),
            ["input.size"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["zeek.args"] = string.Join(" ", ZeekCommandBuilder.ZeekArgs(Path.GetFileName(pcap), o.IncludeLocal && install.HasLocalScript, o.AllExtra)),
            ["output.format"] = "Zeek TSV logs (*.log) + CSV (*.csv, DateTimeUtc added)",
        };
        if (inv.ContainerName != null) details["docker.container"] = inv.ContainerName;
        return new JobSpec(ToolId, Path.GetFileName(inv.Exe), inv.Args, outputDir, label ?? $"Zeek: {Path.GetFileName(pcap)}", Category,
            ExePath: inv.Exe, Timeout: TimeoutFor(size), CancelCommand: inv.CancelCommand, WorkingDir: outputDir,
            ExeVersion: install.Version, Environment: inv.Environment, PostProcess: PostProcessAsync, ManifestDetails: details);
    }

    /// <summary>Converts the logs to CSV, then hashes the input capture into the manifest details.</summary>
    public static async Task PostProcessAsync(JobPostProcessContext ctx, CancellationToken ct)
    {
        await Task.Yield();
        var results = ZeekLogConverter.ConvertFolder(ctx.OutputDir, ctx.Log, ct);
        int ok = results.Count(r => r.Succeeded);
        ctx.SetDetail("zeek.logs", string.Join(",", results.Where(r => r.Succeeded).Select(r => r.LogName)));
        ctx.Log(results.Count == 0
            ? "# Zeek wrote no logs (the capture may be empty, truncated or not readable)."
            : $"# {ok} of {results.Count} Zeek log(s) converted to CSV in {ctx.OutputDir}");
        if (ctx.Job.Details.TryGetValue("input.path", out var input) && File.Exists(input))
        {
            ctx.Log("# Hashing the input capture (SHA-256)…");
            using var sha = SHA256.Create();
            await using var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            ctx.SetDetail("input.sha256", Convert.ToHexString(hash));
        }
        if (results.Count > 0 && ok == 0) throw new InvalidDataException("none of the Zeek logs could be converted to CSV");
    }

    /// <summary>
    /// Queues one job per capture and returns the jobs (not awaited). Layout v1 case: <c>&lt;caseDir&gt;\Network\Zeek\&lt;name&gt;</c>.
    /// Layout v2 case (<c>case.json</c>): <c>&lt;caseDir&gt;\&lt;Host or _Case&gt;\Network\Zeek_&lt;Log&gt;_&lt;capture&gt;.csv/.log</c>, the same as triage
    /// (<see cref="Triage.NetworkTriage.Plan"/>): <paramref name="systems"/>, <paramref name="evidence"/> (<see cref="Triage.NetworkTriage.ReadEvidence"/>)
    /// and the case's earlier hosts assign each capture to its host; a capture with no identified host goes to <c>_Case</c>.
    /// </summary>
    public static IReadOnlyList<Job> Enqueue(JobQueue queue, ZeekInstall install, IEnumerable<string> pcaps, string caseDir, ZeekRunOptions? options = null,
        IReadOnlyList<Triage.DiscoveredSystem>? systems = null, IReadOnlyDictionary<string, Network.PcapEvidence>? evidence = null)
    {
        var jobs = new List<Job>();
        if (IsHostLayout(caseDir))
        {
            var files = pcaps.Select(p => new Network.PcapFile(p, SizeOf(p), FormatOf(p))).ToList();
            foreach (var planned in Triage.NetworkTriage.Plan(files, caseDir, install, null, systems,
                         evidence == null ? null : p => evidence.TryGetValue(p, out var e) ? e : null, SystemInfo.ComputerDetailsStore.KnownHosts(caseDir)))
            {
                var spec = planned.Spec;
                if (options != null)   // same staging folder, output plan and provenance, with the caller's Zeek options
                    spec = CreateSpec(install, planned.Artifact.MatchedPath, spec.Output!.StagingDir, options, spec.Label)
                        with { OutputDir = spec.OutputDir, Output = spec.Output, Provenance = spec.Provenance };
                jobs.Add(queue.Enqueue(spec, caseDir));
            }
            return jobs;
        }
        foreach (var (pcap, dir) in AssignOutputDirs(pcaps, OutputRoot(caseDir)))
            jobs.Add(queue.Enqueue(CreateSpec(install, pcap, dir, options), caseDir));
        return jobs;
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }

    private static Network.PcapFormat FormatOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[12];
            int n = fs.Read(head, 0, head.Length);
            return Network.PcapDetector.FromMagic(head.AsSpan(0, n)) ?? Network.PcapFormat.Pcap;
        }
        catch (Exception) { return Network.PcapFormat.Pcap; }
    }
}
