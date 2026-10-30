using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzForensics.Core.Execution;

public sealed class ManifestOutputFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Name the tool gave the file when EZ Forensics renamed it afterwards (run-time prefix removed); null = not renamed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalName { get; set; }
    /// <summary>Full path the tool wrote the file to, when EZ Forensics renamed or moved it; null = not renamed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalPath { get; set; }
    /// <summary>Artifact id (<c>prefetch</c>, <c>amcache-program-entries</c>, <c>zeek-conn</c>...); null for jobs without a triage origin.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactId { get; set; }
    /// <summary>Tool that produced the file (the job's ToolId).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolId { get; set; }
    /// <summary>Host folder the file belongs to (case layout v2); null when unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }
}

/// <summary>One evidence system a run processed (from host discovery).</summary>
public sealed class ManifestSource
{
    public string RootPath { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>"SYSTEM hive ComputerName", "folder name", "default", "live system".</summary>
    public string? HostNameSource { get; set; }
    /// <summary>LiveVolume, MountedImageOrRawCopy or CopiedCollection.</summary>
    public string? AcquisitionKind { get; set; }
    public string? OsFamily { get; set; }

    // ---- archive provenance (null when the evidence did not come from an archive; absent in older manifests). Never a password.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePath { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchiveSha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchiveFormat { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ArchiveEncrypted { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtractedTo { get; set; }
    /// <summary>Outer to inner archive paths for nested archives.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ArchiveChain { get; set; }
    /// <summary>ModifiedOnly, All, Mixed or DosLocal.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampCoverage { get; set; }
}

/// <summary>An archive whose extracted content a run processed.</summary>
public sealed class ManifestArchive
{
    public string ArchivePath { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Format { get; set; }
    public long? Size { get; set; }
    public string? ModifiedUtc { get; set; }
    public bool Encrypted { get; set; }
    /// <summary>"ZipCrypto", "AES-256"...; the password itself is never recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptionMethod { get; set; }
    public string? ExtractedTo { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Chain { get; set; }
    public string? TimestampCoverage { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Engine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EngineVersion { get; set; }
}

public sealed class ManifestJob
{
    public Guid JobId { get; set; }
    public string ToolId { get; set; } = "";
    public string Label { get; set; } = "";
    public string? ExePath { get; set; }
    public string? ExeFileVersion { get; set; }
    public string? ExeSha1 { get; set; }
    public string? ExeSha256 { get; set; }
    public string? Flavor { get; set; }
    public List<string> Argv { get; set; } = new();
    public string? CommandLine { get; set; }
    public string? StartUtc { get; set; }
    public string? EndUtc { get; set; }
    public int? ExitCode { get; set; }
    public string State { get; set; } = "";
    public string? LogPath { get; set; }
    public string OutputDir { get; set; } = "";
    public List<ManifestOutputFile> OutputFiles { get; set; } = new();
    /// <summary>Engine facts for non-EZ jobs (e.g. zeek.version, zeek.engine, docker.repoDigest, input.sha256); null when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Details { get; set; }

    // ---- triage origin (case layout v2); all null for jobs without one and in manifests written before these fields existed
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleId { get; set; }
    /// <summary>Default artifact id of the job (per-file ids are on <see cref="ManifestOutputFile.ArtifactId"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemRoot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcquisitionKind { get; set; }
    /// <summary>Folder the tool wrote into before its files were published (case layout v2).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StagingDir { get; set; }
    /// <summary>File-system facts of the inputs, captured before the tool started (<see cref="Execution.InputFacts"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManifestInputFact>? InputFacts { get; set; }
    /// <summary>What the input facts leave out (live volume: no hashes; limits reached...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFactsNote { get; set; }
    /// <summary>True when the job was cancelled / timed out and its outputs were published as <c>*_PARTIAL</c> (incomplete).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PartialOutputs { get; set; }
    /// <summary>Files this job produced that could not be published and are still in <see cref="StagingDir"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LeftInStaging { get; set; }
}

public sealed class RunManifest
{
    /// <summary>Case layout written by this run's triage jobs (2 = <c>&lt;case&gt;\&lt;Host&gt;\&lt;Category&gt;\&lt;Artifact&gt;.csv</c>); null = legacy / none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LayoutVersion { get; set; }
    /// <summary>Case folder at the time of the run (paths below are absolute; the case index rebases them when the case was moved).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CaseDir { get; set; }
    /// <summary>Evidence systems the run's jobs came from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManifestSource>? Sources { get; set; }
    /// <summary>Archives the run's evidence was extracted from.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManifestArchive>? Archives { get; set; }

    /// <summary>Reads a run manifest (any version; unknown fields are ignored, missing ones stay null).</summary>
    public static RunManifest Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var m = JsonSerializer.Deserialize<RunManifest>(fs, ManifestWriter.ReadOptions) ?? new RunManifest();
        // Tolerate hand-edited / damaged manifests: null lists and null entries are dropped instead of throwing.
        m.Jobs = (m.Jobs ?? new()).Where(j => j != null).ToList();
        foreach (var j in m.Jobs)
        {
            j.ToolId = NormalizeToolId(j.ToolId) ?? "";
            j.Argv ??= new();
            j.OutputFiles = (j.OutputFiles ?? new()).Where(f => f != null).ToList();
            foreach (var f in j.OutputFiles) f.ToolId = NormalizeToolId(f.ToolId);
        }
        return m;
    }

    /// <summary>Maps the built-in tools' EZ Hub era ids (in older manifests) to the current ids; any other id is returned as is.</summary>
    public static string? NormalizeToolId(string? toolId) => toolId switch
    {
        Persistence.PersistenceCollector.LegacyToolId => Persistence.PersistenceCollector.ToolId,
        SystemInfo.ComputerDetailsStore.LegacyToolId => SystemInfo.ComputerDetailsStore.ToolId,
        _ => toolId,
    };

    /// <summary><see cref="Load"/>, or null when the file is missing or not a manifest.</summary>
    public static RunManifest? TryLoad(string path)
    {
        try { return Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or NullReferenceException) { return null; }
    }

    public string AppVersion { get; set; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    public string Machine { get; set; } = Environment.MachineName;
    public string User { get; set; } = Environment.UserName;
    public bool IsElevated { get; set; } = ManifestWriter.DetectElevated();
    /// <summary>The source the user chose (a folder, a drive, an image or an archive file).</summary>
    public string? SourcePath { get; set; }
    /// <summary>
    /// Folders the run's jobs actually read when they differ from <see cref="SourcePath"/> (archived evidence: the extracted roots of this ingest);
    /// absent = the source path itself (and in older manifests).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SourceRoots { get; set; }
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public List<ManifestJob> Jobs { get; set; } = new();
}

public sealed class ManifestWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Options for reading manifests of any version (case-insensitive names, comments and trailing commas tolerated).</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Sha1, string Sha256)> _exeHashes = new(StringComparer.OrdinalIgnoreCase);

    public ManifestWriter(string caseDir)
    {
        CaseDir = caseDir;
        Path = System.IO.Path.Combine(caseDir, $"{Case.CaseIndex.ManifestPrefix}{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
        Manifest = new RunManifest { CaseDir = caseDir };
    }

    /// <summary>Records the evidence system of a triage job (once per root) and the case layout version; does not write the file.</summary>
    public void NoteOrigin(JobSpec spec)
    {
        var p = spec.Provenance;
        lock (_lock)
        {
            if (spec.Output != null) Manifest.LayoutVersion = Math.Max(Manifest.LayoutVersion ?? 0, Case.CaseLayout.CurrentVersion);
            if (p == null) return;
            // The archive and every archive it was extracted from (the outer archive is the evidence the user supplied).
            if (p.Archive is { } a) foreach (var o in a.SelfAndParents()) NoteArchive(o);
            if (string.IsNullOrEmpty(p.SystemRoot)) return;
            Manifest.Sources ??= new List<ManifestSource>();
            if (Manifest.Sources.Any(s => string.Equals(s.RootPath, p.SystemRoot, StringComparison.OrdinalIgnoreCase))) return;
            Manifest.Sources.Add(new ManifestSource
            {
                RootPath = p.SystemRoot!,
                Host = p.Host ?? "",
                HostNameSource = p.HostNameSource,
                AcquisitionKind = p.AcquisitionKind,
                OsFamily = p.OsFamily,
                ArchivePath = p.Archive?.ArchivePath,
                ArchiveSha256 = p.Archive?.Sha256,
                ArchiveFormat = p.Archive?.Format.ToString(),
                ArchiveEncrypted = p.Archive?.Encrypted,
                ExtractedTo = p.Archive?.ExtractedTo,
                ArchiveChain = p.Archive?.Chain.ToList(),
                TimestampCoverage = p.Archive?.TimestampCoverage.ToString(),
            });
        }
    }

    // caller holds _lock
    private void NoteArchive(Archives.ArchiveOrigin a)
    {
        Manifest.Archives ??= new List<ManifestArchive>();
        if (Manifest.Archives.Any(x => string.Equals(x.ArchivePath, a.ArchivePath, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(x.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase))) return;
        long? size = null; string? mtime = null;
        try { var fi = new FileInfo(a.ArchivePath); if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.ToString("o"); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        Manifest.Archives.Add(new ManifestArchive
        {
            ArchivePath = a.ArchivePath, Sha256 = a.Sha256, Format = a.Format.ToString(), Size = size, ModifiedUtc = mtime,
            Encrypted = a.Encrypted, EncryptionMethod = a.EncryptionMethod, ExtractedTo = a.ExtractedTo,
            Chain = a.Chain.ToList(), TimestampCoverage = a.TimestampCoverage.ToString(),
        });
    }

    public string CaseDir { get; }
    public string Path { get; }
    public RunManifest Manifest { get; }

    public string? SourcePath
    {
        get => Manifest.SourcePath;
        set { lock (_lock) Manifest.SourcePath = value; }
    }

    public IReadOnlyList<string>? SourceRoots
    {
        get => Manifest.SourceRoots;
        set { lock (_lock) Manifest.SourceRoots = value is { Count: > 0 } ? value.ToList() : null; }
    }

    /// <summary>Record a finished job and rewrite the manifest atomically.</summary>
    public void RecordAndWrite(Job job)
    {
        var mj = new ManifestJob
        {
            JobId = job.Id,
            ToolId = job.Spec.ToolId,
            Label = job.Spec.Label,
            ExePath = job.ExePath,
            ExeFileVersion = job.ExeVersion,
            Flavor = job.Flavor?.ToString(),
            Argv = (job.ResolvedArgs ?? job.Spec.Args).ToList(),
            CommandLine = job.CommandLine,
            StartUtc = job.StartUtc?.ToUniversalTime().ToString("o"),
            EndUtc = job.EndUtc?.ToUniversalTime().ToString("o"),
            ExitCode = job.ExitCode,
            State = job.State.ToString(),
            LogPath = job.LogPath,
            OutputDir = job.Spec.OutputDir,
            Category = string.IsNullOrEmpty(job.Spec.Category) ? null : job.Spec.Category,
            RuleId = NullIfEmpty(job.Spec.Provenance?.RuleId),
            ArtifactId = job.Spec.Output?.ArtifactId ?? job.Spec.Provenance?.ArtifactId,
            Host = job.Spec.Provenance?.Host,
            SystemRoot = job.Spec.Provenance?.SystemRoot,
            AcquisitionKind = job.Spec.Provenance?.AcquisitionKind,
            StagingDir = job.Spec.Output?.StagingDir,
            InputFacts = job.InputFacts.Count > 0 ? job.InputFacts.ToList() : null,
            InputFactsNote = job.InputFactsNote,
            PartialOutputs = job.PartialOutputs ? true : null,
            LeftInStaging = job.StagingLeftovers.Count > 0 ? job.StagingLeftovers.ToList() : null,
        };
        var details = job.Details;
        if (details.Count > 0) mj.Details = new Dictionary<string, string>(details);
        if (job.ExePath != null && File.Exists(job.ExePath))
        {
            lock (_lock)
            {
                if (!_exeHashes.TryGetValue(job.ExePath, out var h))
                {
                    h = (Hash(job.ExePath, SHA1.Create()), Hash(job.ExePath, SHA256.Create()));
                    _exeHashes[job.ExePath] = h;
                }
                mj.ExeSha1 = h.Sha1;
                mj.ExeSha256 = h.Sha256;
            }
        }
        // Hashes are taken from the final (renamed) files; a rename never changes the content.
        var renamed = job.OutputRenames.GroupBy(r => r.FinalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var toolId = job.Spec.ToolId;
        var host = job.Spec.Provenance?.Host;
        if (job.Spec.Output is { } plan)
        {
            // Layout v2: the category folder is shared by many jobs, so only this job's published files (and the files it produced that
            // are still in its staging folder; never stale files of an earlier run) are listed.
            var files = job.PublishedOutputs.Select(p => (Path: p.FinalPath, p.OriginalPath, (string?)p.ArtifactId)).ToList();
            files.AddRange(job.StagingLeftovers.Select(x => (x, (string)null!, (string?)plan.ArtifactId)));
            foreach (var (path, original, artifactId) in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                var of = new ManifestOutputFile
                {
                    Path = path, ArtifactId = artifactId, ToolId = toolId, Host = host,
                    OriginalName = original == null ? null : System.IO.Path.GetFileName(original), OriginalPath = original,
                };
                try { of.Size = new FileInfo(path).Length; of.Sha256 = Hash(path, SHA256.Create()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                mj.OutputFiles.Add(of);
            }
        }
        else try
        {
            if (Directory.Exists(job.Spec.OutputDir))
                foreach (var f in Directory.EnumerateFiles(job.Spec.OutputDir, "*", SearchOption.AllDirectories)
                             .Where(x => !OutputNaming.IsEzForensicsSidecar(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    renamed.TryGetValue(System.IO.Path.GetFullPath(f), out var rn);
                    try
                    {
                        mj.OutputFiles.Add(new ManifestOutputFile
                        {
                            Path = f,
                            Size = new FileInfo(f).Length,
                            Sha256 = Hash(f, SHA256.Create()),
                            OriginalName = rn?.OriginalName,
                            OriginalPath = rn?.OriginalPath,
                            ToolId = toolId,
                            Host = host,
                        });
                    }
                    catch (IOException) { mj.OutputFiles.Add(new ManifestOutputFile { Path = f, OriginalName = rn?.OriginalName, OriginalPath = rn?.OriginalPath, ToolId = toolId, Host = host }); }
                }
        }
        catch (IOException) { }

        lock (_lock)
        {
            Manifest.Jobs.Add(mj);
            Directory.CreateDirectory(CaseDir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Manifest, JsonOptions), new System.Text.UTF8Encoding(false));
            File.Move(tmp, Path, overwrite: true);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static string Hash(string path, HashAlgorithm alg)
    {
        using (alg)
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            return Convert.ToHexString(alg.ComputeHash(fs));
    }

    public static bool DetectElevated()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
