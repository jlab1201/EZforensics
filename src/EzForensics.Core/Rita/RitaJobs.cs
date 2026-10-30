using System.Globalization;
using EzForensics.Core.Execution;
using EzForensics.Core.Platforms;

namespace EzForensics.Core.Rita;

/// <summary>What one RITA job analyses and where its results go (<see cref="RitaJobs.CreateSpec"/>).</summary>
public sealed record RitaJobRequest
{
    /// <summary>The case folder.</summary>
    public required string CaseDir { get; init; }
    /// <summary>Host folder of the analysis; null = the case-wide <c>_Case</c> folder (the captures with no identified host).</summary>
    public string? Host { get; init; }
    /// <summary>Final folder of the Rita_*.csv files; null = <see cref="RitaJobs.OutputDirFor"/>.</summary>
    public string? OutputDir { get; init; }
    /// <summary>Zeek jobs to wait for (their logs are analysed when they succeeded). May still be queued or running.</summary>
    public IReadOnlyList<Job> ZeekJobs { get; init; } = Array.Empty<Job>();
    /// <summary>Zeek logs that already exist (e.g. a re-run of RITA on an analysed case).</summary>
    public IReadOnlyList<RitaCaptureLogs> Captures { get; init; } = Array.Empty<RitaCaptureLogs>();
    public RitaOptions Options { get; init; } = RitaOptions.Default;
    /// <summary>Upper bound of the conn.log bytes (e.g. the captures' total size) for the job-level timeout; the import itself is bounded precisely once the logs exist.</summary>
    public long InputBytesHint { get; init; }
    public string? Label { get; init; }
    /// <summary>Provenance recorded in the manifest (null = rule "rita", host <see cref="Host"/> or <see cref="RitaJobs.CaseWideHost"/>).</summary>
    public JobProvenance? Provenance { get; init; }
}

/// <summary>
/// RITA analyses as JobQueue jobs: one per host folder with Zeek output (<c>&lt;case&gt;\&lt;Host&gt;\Network\Rita_*.csv</c>), the case-wide folder of captures with no identified host included
/// (<c>&lt;case&gt;\_Case\Network\Rita_*.csv</c>). A job waits for its Zeek jobs outside a queue slot (<see cref="JobSpec.PrepareOutsideSlot"/>),
/// continues with the ones that succeeded, stages their logs under the original Zeek names and runs <see cref="RitaCommandBuilder"/>'s
/// pipeline; <see cref="RitaResultConverter"/> writes the CSVs.
/// </summary>
public static class RitaJobs
{
    public const string ToolId = "Rita";
    public const string Category = "Network";
    public const string RuleId = "rita";
    /// <summary>Layout v2 base name (<c>Rita_Threats.csv</c>...).</summary>
    public const string OutputName = "Rita";
    public const string DefaultArtifactId = "rita-threats";
    /// <summary>Case-wide folder of the captures with no identified host (<c>&lt;case&gt;\_Case\Network</c>) and of their RITA analysis.</summary>
    public const string CaseWideHost = "_Case";
    /// <summary>Layout v1 (no case.json): <c>&lt;case&gt;\Network\Rita[\_Case]</c>.</summary>
    public const string LegacyFolder = "Rita";

    /// <summary>Job-level timeout: 20 min plus 30 min per GB of conn.log.</summary>
    public static TimeSpan TimeoutFor(long connLogBytes) => TimeSpan.FromMinutes(20 + 30.0 * Math.Max(0, connLogBytes) / (1L << 30));

    public static bool IsHostLayout(string caseDir) => Zeek.ZeekJobs.IsHostLayout(caseDir);

    /// <summary>Layout v2: <c>&lt;case&gt;\&lt;Host or _Case&gt;\Network</c>; layout v1: <c>&lt;case&gt;\Network\Rita</c> (host) / <c>...\Rita\_Case</c>.</summary>
    public static string OutputDirFor(string caseDir, string? host) => IsHostLayout(caseDir)
        ? Path.Combine(caseDir, host == null ? CaseWideHost : ArtifactOutputLayout.SafeHostFolder(host), Category)
        : host == null ? Path.Combine(caseDir, Category, LegacyFolder, CaseWideHost) : Path.Combine(caseDir, Category, LegacyFolder, ArtifactOutputLayout.SafeHostFolder(host));

    /// <summary><c>&lt;case&gt;\.ezstaging\Rita_&lt;host|case&gt;_&lt;hash6&gt;</c> (hidden; never shared by two jobs).</summary>
    public static string StagingDirFor(string caseDir, string? host)
    {
        var db = RitaCommandBuilder.DatabaseName(caseDir, host);
        var name = "Rita_" + (host == null ? "Case" : ArtifactNaming.SanitizeQualifier(host) ?? "Host") + "_" + db[^6..];
        return Path.Combine(caseDir, OutputPlan.StagingFolderName, name);
    }

    /// <summary>
    /// The pipeline's work folder (<c>&lt;case&gt;\.ezstaging\Rita_&lt;host|Case&gt;_&lt;hash6&gt;.work</c>, hidden, outside the publishing
    /// staging folder): the input list, RITA's raw view and the enrichment files; deleted when the job ends.
    /// </summary>
    public static string WorkDirFor(string caseDir, string? host) => StagingDirFor(caseDir, host) + ".work";

    /// <summary>The Zeek logs of a finished Zeek job: layout v2 published logs (original names from the staging copies), else the *.log of its output folder.</summary>
    public static RitaCaptureLogs? LogsOf(Job zeekJob)
    {
        var capture = zeekJob.Details.TryGetValue("input.path", out var input) && input.Length > 0 ? Path.GetFileName(input) : zeekJob.Spec.Label;
        var files = new List<string>();
        if (zeekJob.PublishedOutputs.Count > 0)
        {
            foreach (var p in zeekJob.PublishedOutputs)
            {
                if (!p.FinalPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || !File.Exists(p.FinalPath)) continue;
                // the staging name (conn.log) is the original Zeek name; the published one is Zeek_Conn_<capture>.log
                var orig = RitaCommandBuilder.OriginalLogName(p.OriginalPath) ?? RitaCommandBuilder.OriginalLogName(p.FinalPath);
                if (orig != null) files.Add(p.FinalPath);
            }
        }
        else if (Directory.Exists(zeekJob.Spec.OutputDir))
        {
            files.AddRange(Directory.EnumerateFiles(zeekJob.Spec.OutputDir, "*.log").Where(f => RitaCommandBuilder.OriginalLogName(f) != null)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        return files.Count == 0 ? null : new RitaCaptureLogs(capture, files);
    }

    /// <summary>
    /// Zeek logs RITA reads under <paramref name="folder"/> (recursive), one capture per folder, and per capture name in a layout v2
    /// Network folder (<c>Zeek_Conn_&lt;capture&gt;.log</c>); e.g. the output of an earlier Zeek run (Tools page, RITA Command tile).
    /// </summary>
    public static IReadOnlyList<RitaCaptureLogs> CapturesIn(string folder)
    {
        var groups = new Dictionary<string, (string Capture, List<string> Files)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return Array.Empty<RitaCaptureLogs>();
        var files = Directory.EnumerateFiles(folder, "*.log", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => RitaCommandBuilder.OriginalLogName(f) != null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var dir = Path.GetDirectoryName(f) ?? folder;
            var m = V2CaptureRx.Match(Path.GetFileNameWithoutExtension(f));
            var capture = m.Success ? m.Groups["c"].Value : Path.GetFileName(dir.TrimEnd('\\', '/'));
            var key = dir + "|" + capture;
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (capture, new List<string>());
            g.Files.Add(f);
        }
        return groups.Values.Select(g => new RitaCaptureLogs(g.Capture, g.Files)).ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex V2CaptureRx =
        new(@"^Zeek_(?:Conn|OpenConn|Dns|Http|OpenHttp|Ssl|OpenSsl)_(?<c>.+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Total size of the conn / open_conn logs (drives the import timeout).</summary>
    public static long ConnBytes(IEnumerable<RitaStagedLog> logs) =>
        logs.Where(l => l.Name is "conn.log" or "open_conn.log").Sum(l => { try { return new FileInfo(l.SourcePath).Length; } catch (Exception) { return 0L; } });

    /// <summary>
    /// Waits until every Zeek job has finished and been recorded; returns the logs of those that succeeded and a note per failed one.
    /// <paramref name="status"/> receives the waiting text ("Waiting for 2 of 3 Zeek job(s)").
    /// </summary>
    public static async Task<(IReadOnlyList<RitaCaptureLogs> Logs, IReadOnlyList<string> Failed)> WaitForZeekAsync(IReadOnlyList<Job> zeekJobs,
        Action<string>? status, CancellationToken ct, TimeSpan? poll = null)
    {
        var all = Task.WhenAll(zeekJobs.Select(j => j.Recorded));
        while (!all.IsCompleted)
        {
            int left = zeekJobs.Count(j => !j.Recorded.IsCompleted);
            status?.Invoke($"Waiting for {left} of {zeekJobs.Count} Zeek job(s)");
            await Task.WhenAny(all, Task.Delay(poll ?? TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        var logs = new List<RitaCaptureLogs>();
        var failed = new List<string>();
        foreach (var j in zeekJobs)
        {
            var name = j.Details.TryGetValue("input.path", out var p) && p.Length > 0 ? Path.GetFileName(p) : j.Spec.Label;
            if (j.State != JobState.Succeeded) { failed.Add($"{name}: Zeek {j.State.ToString().ToLowerInvariant()}{(string.IsNullOrEmpty(j.StatusReason) ? "" : " (" + j.StatusReason + ")")}"); continue; }
            if (LogsOf(j) is { } l) logs.Add(l);
            else failed.Add($"{name}: Zeek wrote no conn / dns / http / ssl log");
        }
        return (logs, failed);
    }

    /// <summary>
    /// The job for <paramref name="request"/>. Layout v2 cases publish through an <see cref="OutputPlan"/> (<c>Rita_*.csv</c>, artifact ids
    /// rita-threats / rita-beacons / rita-long-connections / rita-dns-tunneling); layout v1 cases get the files directly in the output folder.
    /// </summary>
    public static JobSpec CreateSpec(RitaInstall install, RitaJobRequest request)
    {
        if (install.Engine == RitaEngine.Docker) throw new NotSupportedException(RitaSetup.DockerNotSupportedMessage);
        var caseDir = Path.GetFullPath(request.CaseDir);
        var v2 = IsHostLayout(caseDir);
        var finalDir = request.OutputDir ?? OutputDirFor(caseDir, request.Host);
        var staging = v2 ? StagingDirFor(caseDir, request.Host) : finalDir;
        var db = RitaCommandBuilder.DatabaseName(caseDir, request.Host);
        var options = request.Options;
        var workDir = WorkDirFor(caseDir, request.Host);
        var inv = RitaCommandBuilder.Build(install, workDir, db, options);
        var scope = request.Host == null ? "case-wide" : request.Host;
        var details = new Dictionary<string, string>(install.ManifestDetails())
        {
            ["rita.db"] = db,
            ["rita.scope"] = request.Host == null ? "case" : "host",
            ["rita.internalSubnets"] = string.Join(",", RitaOptions.NormalizeSubnets(options.InternalSubnets)),
            ["rita.onlineThreatIntel"] = options.OnlineThreatIntel ? "true" : "false",
            ["rita.import"] = "one-off --rebuild (each capture one bucket)",
            ["output.format"] = "CSV (rita view --stdout + first / last seen from ClickHouse, UTC)",
        };
        var state = new RunState();
        var zeekJobs = request.ZeekJobs;
        var given = request.Captures;

        async Task PreProcess(JobPostProcessContext ctx, CancellationToken ct)
        {
            var job = ctx.Job;
            var captures = new List<RitaCaptureLogs>(given);
            if (zeekJobs.Count > 0)
            {
                ctx.Log($"# Waiting for {zeekJobs.Count} Zeek job(s)…");
                var started = DateTime.UtcNow;
                var (logs, failed) = await WaitForZeekAsync(zeekJobs, s => job.StatusReason = s, ct).ConfigureAwait(false);
                ctx.Log($"# Zeek finished after {Memory.MemorySymbolGate.FormatElapsed(DateTime.UtcNow - started)}: {logs.Count} of {zeekJobs.Count} capture(s) usable.");
                foreach (var f in failed) ctx.Log("# Not analysed: " + f);
                if (failed.Count > 0) ctx.SetDetail("rita.capturesSkipped", string.Join(" | ", failed));
                captures.AddRange(logs);
            }
            var staged = RitaCommandBuilder.Stage(captures);
            if (staged.Count == 0)
                throw new InvalidOperationException(zeekJobs.Count > 0
                    ? $"none of the {zeekJobs.Count} Zeek job(s) produced logs RITA can read (conn / dns / http / ssl); see the Zeek jobs"
                    : "no Zeek logs to analyse (conn / dns / http / ssl)");
            state.Captures = staged.Select(s => s.Capture).Distinct(StringComparer.Ordinal).ToList();
            var connBytes = ConnBytes(staged);
            var text = RitaCommandBuilder.InputsText(staged, RitaCommandBuilder.EnginePath(install), TimeoutFor(connBytes));
            // One analysis at a time (they share one ClickHouse), waited for here, outside a queue slot and before the process timeout
            // starts: a job queued behind another must neither hold a slot nor time out (its cancel command would stop the other's
            // rita / ClickHouse). Taken before the work folder is touched: a second job of the same scope shares that folder.
            await AcquireAnalysisAsync(ctx, ct).ConfigureAwait(false);
            DeleteWorkDir(workDir);
            OutputPublisher.PrepareStaging(workDir);
            File.WriteAllText(Path.Combine(workDir, RitaCommandBuilder.InputsFile), text, new System.Text.UTF8Encoding(false));
            ctx.SetDetail("rita.captures", string.Join(" | ", state.Captures));
            ctx.SetDetail("rita.connLogBytes", connBytes.ToString(CultureInfo.InvariantCulture));
            ctx.Log($"# RITA {scope}: {state.Captures.Count} capture(s), {staged.Count} log(s), conn.log {connBytes / 1_048_576.0:0.0} MB, database {db}");
        }

        async Task PostProcess(JobPostProcessContext ctx, CancellationToken ct)
        {
            await Task.Yield();
            if (ctx.ExitCode != 0) return;   // the job fails on its exit code; nothing to convert (Finally deletes the work folder)
            var r = RitaResultConverter.Convert(workDir, ctx.OutputDir, state.Captures, ct);
            if (r.RitaVersion != null) ctx.SetDetail("rita.version", r.RitaVersion);
            if (r.ClickHouseVersion != null) ctx.SetDetail("clickhouse.version", r.ClickHouseVersion);
            ctx.SetDetail("rita.db", r.Database ?? db);
            if (r.DataSpanHours is { } h) ctx.SetDetail("rita.dataSpanHours", RitaResultConverter.FormatHours(h));
            if (r.FirstUtc is { } f) ctx.SetDetail("rita.firstSeenUtc", Timeline.TimelineTimestamp.Format(f));
            if (r.LastUtc is { } l) ctx.SetDetail("rita.lastSeenUtc", Timeline.TimelineTimestamp.Format(l));
            ctx.SetDetail("rita.findings", $"threats={r.Threats},beacons={r.Beacons},longConnections={r.LongConnections},dnsTunneling={r.DnsTunneling}");
            if (r.NoData)
            {
                ctx.SetDetail("rita.note", "no data: RITA found no connections between the internal subnets and outside");
                ctx.Log("# RITA found nothing to analyse: no traffic between the internal subnets (Settings → RITA) and other addresses. Header-only CSVs were written.");
            }
            if (r.MissingSpans > 0) ctx.Log($"# {r.MissingSpans} finding(s) without first / last seen (left empty).");
            ctx.Log($"# RITA: {r.Threats} finding(s): {r.Beacons} beacon(s), {r.LongConnections} long connection(s), {r.DnsTunneling} DNS tunneling;"
                    + (r.DataSpanHours is { } hh ? $" data span {RitaResultConverter.FormatHours(hh)} h" + (hh < 24 ? " (under 24 h: RITA's scores assume about a day of traffic)" : "") : ""));
        }

        // Only the job that took the analysis lock owns the work folder: one that never got that far (cancelled while waiting, or failed
        // because another job of the same scope holds the staging folder) must not delete the running job's files.
        void Finally(Job job)
        {
            if (!AnalysisHolders.TryRemove(job.Id, out _)) return;
            try { DeleteWorkDir(workDir); }
            finally { AnalysisLock.Release(); }
        }

        var spec = new JobSpec(ToolId, Path.GetFileName(inv.Exe), inv.Args, finalDir,
            request.Label ?? $"RITA ({scope}): {Path.GetFileName(caseDir.TrimEnd('\\', '/'))}", Category,
            ExePath: inv.Exe, Timeout: JobTimeout(request.InputBytesHint), CancelCommand: inv.CancelCommand,
            WorkingDir: v2 ? null : finalDir, ExeVersion: install.RitaVersion, Environment: inv.Environment,
            PostProcess: PostProcess, ManifestDetails: details, PreProcess: PreProcess, Finally: Finally,
            Output: v2 ? new OutputPlan(staging, OutputName, DefaultArtifactId) : null,
            Provenance: request.Provenance ?? new JobProvenance { RuleId = RuleId, ArtifactId = DefaultArtifactId, Host = request.Host ?? CaseWideHost, HashInputs = false },
            PrepareOutsideSlot: true);
        return spec;
    }

    /// <summary>
    /// Process timeout of the job: <see cref="TimeoutFor"/> of the size hint plus 15 min (start-up, view, enrichment), 6 h without a hint.
    /// The import itself is bounded precisely by the pipeline (<c>#timeout</c> from the real conn.log size).
    /// </summary>
    public static TimeSpan JobTimeout(long inputBytesHint) => inputBytesHint > 0 ? TimeoutFor(inputBytesHint) + TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6);

    /// <summary>Status text of a RITA job waiting for another analysis to finish (<see cref="AcquireAnalysisAsync"/>).</summary>
    public const string WaitingForAnalysisReason = "Waiting for another RITA analysis to finish (one at a time)";

    private static readonly SemaphoreSlim AnalysisLock = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> AnalysisHolders = new();

    /// <summary>Number of RITA jobs holding the analysis lock (0 or 1; tests).</summary>
    public static int AnalysesRunning => AnalysisHolders.Count;

    private static async Task AcquireAnalysisAsync(JobPostProcessContext ctx, CancellationToken ct)
    {
        var job = ctx.Job;
        if (AnalysisHolders.ContainsKey(job.Id)) return;
        if (!AnalysisLock.Wait(0))
        {
            ctx.Log("# Waiting for another RITA analysis to finish (they share one ClickHouse database server)…");
            job.StatusReason = WaitingForAnalysisReason;
            var started = DateTime.UtcNow;
            await AnalysisLock.WaitAsync(ct).ConfigureAwait(false);
            ctx.Log($"# The other RITA analysis finished after {Memory.MemorySymbolGate.FormatElapsed(DateTime.UtcNow - started)}.");
        }
        if (!AnalysisHolders.TryAdd(job.Id, 0)) AnalysisLock.Release();
    }

    private sealed class RunState
    {
        public volatile IReadOnlyList<string> Captures = Array.Empty<string>();
    }

    /// <summary>Deletes the work folder and, when nothing else is staged, the case's <c>.ezstaging</c> folder.</summary>
    public static void DeleteWorkDir(string workDir)
    {
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var parent = Path.GetDirectoryName(workDir.TrimEnd('\\', '/'));
            if (parent != null && Path.GetFileName(parent).Equals(OutputPlan.StagingFolderName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
