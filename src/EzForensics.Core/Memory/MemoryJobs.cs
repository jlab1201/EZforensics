using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using EzForensics.Core.Execution;

namespace EzForensics.Core.Memory;

/// <summary>
/// Volatility 3 runs as JobQueue jobs: one <c>vol.exe</c> per plugin and image. Raw CSV stdout goes to
/// <c>&lt;staging&gt;\&lt;Variant&gt;.csv</c> (<see cref="JobSpec.StdoutPath"/>), PostProcess normalises it (<see cref="VolatilityCsv"/>) and the
/// publisher names it <c>Memory_&lt;Variant&gt;[_&lt;qualifier&gt;].csv</c> with artifact id <c>memory-&lt;id&gt;</c>.
/// The <c>windows.info</c> job runs first and decides the image's <see cref="MemorySymbolGate"/>; the other plugin jobs wait for it in
/// PreProcess and at most <see cref="MemorySymbolGate.MaxConcurrent"/> vol processes run per image.
/// </summary>
public static class MemoryJobs
{
    public const string ToolId = "Volatility3";
    public const string Category = "Memory";
    /// <summary>Layout v2 base name of every output (<c>Memory_PsList.csv</c>).</summary>
    public const string OutputName = "Memory";
    public const string RuleId = "memory";

    /// <summary>The embedded plugin catalog (<c>Resources/memory/plugins.json</c>).</summary>
    public static IReadOnlyList<MemoryPluginDef> LoadPlugins() => MemoryPlugins.All;

    /// <summary>Per-plugin timeout: <c>TimeoutBaseMin + TimeoutPerGbMin × size in GB</c>.</summary>
    public static TimeSpan TimeoutFor(MemoryPluginDef plugin, long imageBytes) =>
        TimeSpan.FromMinutes(plugin.TimeoutBaseMin + plugin.TimeoutPerGbMin * (Math.Max(0, imageBytes) / (double)(1L << 30)));

    /// <summary>The job's own staging folder: <c>&lt;stagingDir&gt;_&lt;Variant&gt;</c> (a sibling under <c>.ezstaging</c>, so the image's jobs never share one).</summary>
    public static string PluginStagingDir(string stagingDir, MemoryPluginDef plugin) => stagingDir.TrimEnd('\\', '/') + "_" + plugin.Variant;

    /// <summary>The file Volatility is pointed at: the <c>.vmem</c> for a VMware snapshot (<see cref="MemoryImage.CompanionPath"/>), else the image.</summary>
    public static string VolatilityInputPath(MemoryImage image) =>
        image.Format == MemoryFormat.VMware && !string.IsNullOrEmpty(image.CompanionPath) ? image.CompanionPath! : image.Path;

    /// <param name="stagingDir">Per-image staging base; the job writes into <see cref="PluginStagingDir"/>.</param>
    /// <param name="qualifier">Sanitised qualifier for a host's second and later images (<c>TriagePlanner.UniqueQualifier</c>); null = none.</param>
    /// <param name="pagefilePath">pagefile.sys for <paramref name="includePagefile"/>; null = looked up next to the image.</param>
    /// <param name="extraArgs">Extra plugin arguments typed by the user.</param>
    public static JobSpec CreateSpec(VolatilityInstall install, MemoryImage image, MemoryPluginDef plugin, string host, string outputDir, string stagingDir,
        MemorySymbolGate gate, bool offline, string? qualifier = null, bool includePagefile = false, string? pagefilePath = null, string? extraArgs = null)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(gate);
        var inputPath = VolatilityInputPath(image);
        var inv = VolatilityCommandBuilder.Build(install, inputPath, plugin, offline, extraArgs, includePagefile, pagefilePath);
        var staging = PluginStagingDir(stagingDir, plugin);
        var stdout = Path.Combine(staging, plugin.Variant + ".csv");

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = "Volatility 3",
            ["volatility.version"] = install.Version ?? "unknown",
            ["volatility.plugin"] = plugin.Plugin,
            ["symbols.mode"] = offline ? "offline" : "online (downloaded on demand)",
            ["symbols.dir"] = install.SymbolsDir,
            ["input.path"] = SafeFullPath(inputPath),
            ["input.size"] = image.Size.ToString(CultureInfo.InvariantCulture),
            ["image.format"] = image.Format.ToString(),
            ["memory.os"] = image.Os.ToString(),
            ["output.format"] = "CSV (Volatility -r csv, normalised: banner stripped, timestamps UTC)",
        };
        if (image.DumpKind is { } dk) details["dump.kind"] = dk.ToString();
        if (image.Format == MemoryFormat.WindowsCrashDump && MemoryImageDetector.ReadBugCheckCode(image.Path) is { } bugCheck)
        {
            // The advisories use memory.live: a dump Windows wrote at a real bug check has no page smear.
            details["dump.bugcheck"] = "0x" + bugCheck.ToString("X8", CultureInfo.InvariantCulture);
            details["memory.live"] = MemoryImageDetector.IsLiveDumpBugCheck(bugCheck) ? "true" : "false";
        }
        if (!string.IsNullOrEmpty(image.Note)) details["image.note"] = image.Note!;
        if (plugin.NeedsUserPages && image.DumpKind is DumpKind.Kernel or DumpKind.BitmapKernel)
            details["image.userPagesMissing"] = "true";
        int pf = inv.Args.ToList().IndexOf("--single-swap-locations");
        if (pf >= 0) details["input.pagefile"] = inv.Args[pf + 1];

        var state = new ConcurrentDictionary<Guid, RunState>();

        Task PreProcess(JobPostProcessContext ctx, CancellationToken ct) => PreProcessAsync(ctx, ct, gate, plugin, state);
        Task PostProcess(JobPostProcessContext ctx, CancellationToken ct) => PostProcessAsync(ctx, ct, gate, plugin, stdout, inputPath, state);
        void Finally(Job job) => FinallyStep(job, gate, plugin, state);

        return new JobSpec(ToolId, Path.GetFileName(inv.Exe), inv.Args, outputDir, $"Memory · {plugin.Label}: {Path.GetFileName(image.Path)}", Category,
            ExePath: inv.Exe, Timeout: TimeoutFor(plugin, image.Size), WorkingDir: staging, ExeVersion: install.Version,
            Environment: inv.Environment, PostProcess: PostProcess, ManifestDetails: details, PreProcess: PreProcess, Finally: Finally,
            Output: new OutputPlan(staging, OutputName, plugin.ArtifactId, qualifier),
            Provenance: new JobProvenance
            {
                RuleId = RuleId, ArtifactId = plugin.ArtifactId, Host = host,
                Inputs = new[] { new InputSelector(image.Path) }, HashInputs = false,   // the info job hashes the image itself (input.sha256)
            },
            StdoutPath: stdout, PrepareOutsideSlot: true,
            // The gate tracks its info jobs from the moment they are queued: a newer check starts at once, and a waiting plugin can tell
            // "info job still coming" from "no info job at all" (MemorySymbolGate.OrphanGrace).
            Enqueued: plugin.IsInfo ? gate.RegisterInfo : null);
    }

    /// <summary>Per-job runtime state (keyed on the job id: a spec can be enqueued more than once).</summary>
    private sealed class RunState
    {
        public volatile bool SymbolError;
        public Action<Job, string>? Handler;
    }

    private static async Task PreProcessAsync(JobPostProcessContext ctx, CancellationToken ct, MemorySymbolGate gate, MemoryPluginDef plugin,
        ConcurrentDictionary<Guid, RunState> states)
    {
        var job = ctx.Job;
        var st = states.GetOrAdd(job.Id, _ => new RunState());
        st.Handler = plugin.IsInfo
            ? (j, line) => { if (MemorySymbolGate.IsSymbolError(line)) st.SymbolError = true; gate.ReportInfoProgress(j, line); }
            : (_, line) => { if (MemorySymbolGate.IsSymbolError(line)) st.SymbolError = true; };
        job.Output += st.Handler;
        if (plugin.IsInfo)
        {
            // A re-run / retry of the info job is a new symbol check (the user may have imported symbols or gone online since).
            // Normally done when it was enqueued (RegisterInfo); kept here for a spec enqueued without the hook.
            gate.Rearm(job);
        }
        else if (gate.FailedBefore(job))
        {
            // The symbol check failed before this job was queued (Retry, or Run selected again): try again instead of failing on the old result.
            ctx.Log("# An earlier symbol check of this image failed; running the plugin anyway.");
        }
        else if (!gate.IsDecided)
        {
            ctx.Log("# Waiting for the windows.info job of this image (symbol check)…");
            var started = DateTime.UtcNow;
            try { await gate.WaitAsync(s => job.StatusReason = s, ct).ConfigureAwait(false); }
            catch (MemorySymbolsMissingException ex)
            {
                ctx.Log($"# Symbol check failed after waiting {MemorySymbolGate.FormatElapsed(DateTime.UtcNow - started)}: {ex.Message}");
                throw;
            }
            catch (OperationCanceledException)
            {
                ctx.Log($"# Cancelled while waiting for the symbol check ({MemorySymbolGate.FormatElapsed(DateTime.UtcNow - started)}).");
                throw;
            }
            ctx.Log($"# {gate.DecisionNote ?? "windows.info finished"} — starting {plugin.Plugin} (waited {MemorySymbolGate.FormatElapsed(DateTime.UtcNow - started)}).");
        }
        else await gate.WaitAsync(ct).ConfigureAwait(false);   // decided: returns at once (throws when the check failed)

        if (!gate.TryAcquireSlot(job))
        {
            ctx.Log($"# Waiting for a free Volatility slot (at most {MemorySymbolGate.MaxConcurrent} per image)…");
            job.StatusReason = MemorySymbolGate.WaitingForSlotReason;
        }
        await gate.AcquireSlotAsync(job, ct).ConfigureAwait(false);
    }

    private static async Task PostProcessAsync(JobPostProcessContext ctx, CancellationToken ct, MemorySymbolGate gate, MemoryPluginDef plugin,
        string stdout, string inputPath, ConcurrentDictionary<Guid, RunState> states)
    {
        await Task.Yield();
        bool symbolError = states.TryGetValue(ctx.Job.Id, out var st) && st.SymbolError;
        if (symbolError && ctx.ExitCode != 0) ctx.Log("ERROR: " + MemorySymbolGate.SymbolsMissingMessage);
        if (plugin.IsInfo)
        {
            // Decide the gate before the (slow) image hash so the other plugins can start.
            if (symbolError && ctx.ExitCode != 0) gate.Fail(MemorySymbolGate.SymbolsMissingMessage, ctx.Job);
            else gate.Open(ctx.Job, ctx.ExitCode == 0 ? "windows.info finished" : $"windows.info exited with code {ctx.ExitCode}; starting anyway");
            // vol.exe has exited: hand its slot to the next plugin while the image is hashed (a 2 GB image takes seconds to minutes).
            gate.ReleaseSlot(ctx.Job);
        }

        if (File.Exists(stdout))
        {
            try
            {
                var r = VolatilityCsv.Normalize(stdout, stdout, plugin, ct);
                ctx.SetDetail("volatility.rows", r.Rows.ToString(CultureInfo.InvariantCulture));
                ctx.Log($"# {r.Rows} row(s); {r.BannerLinesStripped} banner line(s) stripped; {r.TimestampsConverted} timestamp(s) converted to UTC.");
            }
            catch (InvalidDataException) when (ctx.ExitCode != 0)
            {
                // Volatility failed before printing its table: nothing worth publishing.
                TryDelete(stdout);
            }
        }
        else if (ctx.ExitCode == 0) throw new InvalidDataException("Volatility's output file was not written: " + stdout);

        if (plugin.IsInfo && ctx.ExitCode == 0 && File.Exists(inputPath))
        {
            ctx.Log("# Hashing the memory image (SHA-256)…");
            using var sha = SHA256.Create();
            await using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            ctx.SetDetail("input.sha256", Convert.ToHexString(hash));
        }
    }

    private static void FinallyStep(Job job, MemorySymbolGate gate, MemoryPluginDef plugin, ConcurrentDictionary<Guid, RunState> states)
    {
        states.TryRemove(job.Id, out var st);
        if (st?.Handler != null) job.Output -= st.Handler;
        gate.ReleaseSlot(job);
        if (plugin.IsInfo)
        {
            // Cancelled, timed out or failed before PostProcess: never leave the image's other jobs waiting.
            if (st?.SymbolError == true && job.State != JobState.Succeeded) gate.Fail(MemorySymbolGate.SymbolsMissingMessage, job);
            else gate.Open(job, job.State switch
            {
                JobState.Succeeded => "windows.info finished",
                JobState.Cancelled => "windows.info was cancelled; starting without the symbol check",
                JobState.TimedOut => "windows.info timed out; starting without the symbol check",
                _ => "windows.info failed" + (string.IsNullOrEmpty(job.StatusReason) ? "" : $" ({job.StatusReason})") + "; starting without the symbol check",
            });
        }
        // The outcome classifier quoted Volatility's last stderr line ("Unable to validate the plugin requirements: [...symbol_table_name]"):
        // show the same plain message as the image's other plugin jobs instead.
        if (st?.SymbolError == true && job.State == JobState.Failed)
            job.StatusReason = $"Exited with code {job.ExitCode}: {MemorySymbolGate.SymbolsMissingMessage}";
        // A cancelled / timed-out run published raw stdout as *_PARTIAL.csv: normalise it too (best effort).
        if (job.PartialOutputs)
            foreach (var p in job.PublishedOutputs.Where(p => p.FinalPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                try { VolatilityCsv.Normalize(p.FinalPath, p.FinalPath, plugin); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
    }

    /// <summary>
    /// A cancelled / timed-out job is about to publish its raw stdout as <c>_PARTIAL.csv</c>: deletes the files that hold no data row (only the
    /// banner and/or header, e.g. a psscan cancelled before its first hit) and logs why. Returns the deleted paths. Does not rewrite the kept files.
    /// </summary>
    public static IReadOnlyList<string> DropEmptyPartialCsvs(IEnumerable<string> stagedCsvs, Action<string> log)
    {
        var dropped = new List<string>();
        foreach (var f in stagedCsvs)
        {
            // Count rows via a scratch copy: the file itself stays raw, because FinallyStep normalises the published _PARTIAL.csv
            // (a second in-place pass would eat the backslash escapes of Volatility's "C:\Windows").
            bool empty;
            var scratch = f + ".rows";
            try { empty = VolatilityCsv.Normalize(f, scratch).Rows == 0; }
            catch (InvalidDataException) { empty = true; }          // not even a header
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            finally { TryDelete(scratch); }
            if (!empty) continue;
            TryDelete(f);
            dropped.Add(f);
            log($"# {Path.GetFileName(f)}: cancelled before any rows were produced; no partial output published.");
        }
        return dropped;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch (Exception) { return p; }
    }
}
