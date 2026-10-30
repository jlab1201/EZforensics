using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EzForensics.Core.Execution;

/// <summary>One output file EZ Forensics renamed after a job: the tool-produced path and the final path.</summary>
public sealed record OutputRename(string OriginalPath, string FinalPath)
{
    public string OriginalName => Path.GetFileName(OriginalPath);
    public string FinalName => Path.GetFileName(FinalPath);
}

/// <summary>
/// File-name rules for EZ tool output. EZ tools prefix every output with the run time:
/// <c>yyyyMMddHHmmss_</c> (most tools, e.g. <c>20261007123456_EvtxECmd_Output.csv</c>) or
/// <c>yyyyMMddHHmmssffffff_</c> (SQLECmd, 20 digits). RECmd also writes its plugin files into a
/// <c>yyyyMMddHHmmss</c> sub-folder as <c>&lt;stamp&gt;_&lt;Plugin&gt;__&lt;hive&gt;.csv</c>.
/// </summary>
public static class OutputNaming
{
    /// <summary>EZ switches that set an explicit output file name. Files named this way are never renamed.</summary>
    public static readonly IReadOnlyList<string> FileNameFlags = new[] { "--csvf", "--jsonf", "--xmlf", "--htmlf", "--bodyf" };

    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex PrefixRx = new(@"^(?<stamp>\d{14,21})_(?<rest>.+)$", Opts);
    private static readonly Regex StampOnlyRx = new(@"^\d{14,21}$", Opts);
    private static readonly Regex CopySuffixRx = new(@" \(\d+\)$", Opts);

    /// <summary>True when the first 14 digits are a plausible yyyyMMddHHmmss run time.</summary>
    public static bool IsRunStamp(string digits)
    {
        if (digits.Length < 14 || digits.Length > 21) return false;
        foreach (var c in digits) if (c is < '0' or > '9') return false;
        return DateTime.TryParseExact(digits[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
               && dt.Year is >= 1990 and <= 2199;
    }

    /// <summary>Strips a leading EZ run-time prefix. False (and <paramref name="stripped"/> = the name) when there is none.</summary>
    public static bool TryStripTimestampPrefix(string fileName, out string stripped)
    {
        stripped = fileName;
        var m = PrefixRx.Match(fileName);
        if (!m.Success || !IsRunStamp(m.Groups["stamp"].Value)) return false;
        var rest = m.Groups["rest"].Value;
        // Never reduce a name to nothing or to a bare extension ("20261007123456_.csv").
        if (Path.GetFileNameWithoutExtension(rest).Length == 0) return false;
        stripped = rest;
        return true;
    }

    public static string StripTimestampPrefix(string fileName) => TryStripTimestampPrefix(fileName, out var s) ? s : fileName;

    public static bool HasTimestampPrefix(string fileName) => TryStripTimestampPrefix(fileName, out _);

    /// <summary>True for a folder named only by an EZ run time (RECmd plugin output folder).</summary>
    public static bool IsTimestampFolderName(string name) => StampOnlyRx.IsMatch(name) && IsRunStamp(name);

    /// <summary>
    /// Name used to recognise an output file regardless of naming style: the run-time prefix and a
    /// collision suffix (" (2)") are removed. <c>20261007123456_EvtxECmd_Output.csv</c>,
    /// <c>EvtxECmd_Output.csv</c> and <c>EvtxECmd_Output (2).csv</c> all give <c>EvtxECmd_Output.csv</c>.
    /// </summary>
    public static string CanonicalName(string fileName)
    {
        var name = StripTimestampPrefix(Path.GetFileName(fileName));
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var trimmed = CopySuffixRx.Replace(stem, "");
        return trimmed.Length == 0 ? name : trimmed + ext;
    }

    /// <summary><c>dir\Name.ext</c> -> <c>dir\Name (n).ext</c> (n &gt;= 2; the last extension only).</summary>
    public static string WithCopyNumber(string path, int n)
    {
        if (n < 2) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        return Path.Combine(dir, $"{stem} ({n}){ext}");
    }

    /// <summary>The path itself when nothing exists there, else the first free <c>Name (n).ext</c>.</summary>
    public static string UniquePath(string path)
    {
        for (int n = 1; n < 100_000; n++)
        {
            var p = WithCopyNumber(path, n);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
        throw new IOException("No free file name for " + path);
    }

    /// <summary>Suffixes of EZ Forensics' own helper files written next to CSVs (Results tree stats cache, row flags).</summary>
    public static readonly IReadOnlyList<string> SidecarSuffixes = new[] { ".ezstats.json", ".ezflags.json" };

    /// <summary>
    /// True for EZ Forensics' own sidecars (<c>*.ezstats.json</c>, <c>*.ezflags.json</c>): they are never job outputs, never
    /// renamed, never hashed into the run manifest and never counted when a job's outcome is classified.
    /// </summary>
    public static bool IsEzForensicsSidecar(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return false;
        foreach (var s in SidecarSuffixes)
            if (pathOrName.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>File names the user set explicitly with --csvf/--jsonf/--xmlf/--htmlf/--bodyf (case-insensitive).</summary>
    public static ISet<string> UserNamedFiles(IReadOnlyList<string> args)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (!FileNameFlags.Contains(args[i], StringComparer.OrdinalIgnoreCase)) continue;
            var v = args[i + 1];
            if (string.IsNullOrWhiteSpace(v)) continue;
            try { set.Add(Path.GetFileName(v.Trim())); } catch (ArgumentException) { set.Add(v.Trim()); }
        }
        return set;
    }
}

/// <summary>Files and folders that existed in an output folder before a job ran.</summary>
public sealed class OutputSnapshot
{
    private readonly Dictionary<string, (long Size, DateTime WriteUtc)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }

    private OutputSnapshot(string root) { Root = Path.GetFullPath(root); }

    public static OutputSnapshot Capture(string dir)
    {
        var s = new OutputSnapshot(dir);
        if (!Directory.Exists(s.Root)) return s;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var d in Directory.EnumerateDirectories(s.Root, "*", opts)) s._dirs.Add(d);
            foreach (var f in Directory.EnumerateFiles(s.Root, "*", opts))
            {
                if (OutputNaming.IsEzForensicsSidecar(f)) continue;
                try { var fi = new FileInfo(f); s._files[f] = (fi.Length, fi.LastWriteTimeUtc); }
                catch (IOException) { s._files[f] = (-1, DateTime.MinValue); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return s;
    }

    /// <summary>True for a file that did not exist before, or whose size or last write time changed.</summary>
    public bool IsNewOrChanged(string file)
    {
        if (OutputNaming.IsEzForensicsSidecar(file)) return false; // EZ Forensics' own helper files are never outputs
        if (!_files.TryGetValue(file, out var old)) return true;
        try { var fi = new FileInfo(file); return fi.Length != old.Size || fi.LastWriteTimeUtc != old.WriteUtc; }
        catch (IOException) { return false; }
    }

    public bool IsNewDirectory(string dir) => !_dirs.Contains(dir.TrimEnd('\\', '/'));
}

/// <summary>Post-run rename step: removes the EZ run-time prefix from the files a job created.</summary>
public static class OutputRenamer
{
    /// <summary>
    /// Renames every file created or modified during the run (compared with <paramref name="before"/>) inside
    /// <paramref name="before"/>.Root: the run-time prefix is stripped, new run-time-only folders (RECmd plugin
    /// folders) are flattened into their parent and new prefixed folders lose the prefix. RECmd's plugin separator
    /// <c>Plugin__hive</c> becomes <c>Plugin_hive</c>. Files named in <paramref name="userNamed"/> are left alone.
    /// An existing name is never overwritten: the file becomes <c>Name (2).ext</c>, <c>Name (3).ext</c>, ...
    /// Files are processed in path order, so within one run the earliest stamp gets the plain name.
    /// </summary>
    public static IReadOnlyList<OutputRename> StripTimestampPrefixes(OutputSnapshot before, IEnumerable<string>? userNamed = null,
        string? toolId = null, Action<string>? log = null)
    {
        var root = before.Root;
        var result = new List<OutputRename>();
        if (!Directory.Exists(root)) return result;
        var keep = new HashSet<string>(userNamed ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool recmd = string.Equals(toolId, "RECmd", StringComparison.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", opts).Where(before.IsNewOrChanged)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("# Output rename skipped: " + ex.Message);
            return result;
        }

        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (keep.Contains(name)) continue;

            var rel = Path.GetRelativePath(root, f);
            var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetDir = root;
            var cur = root;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                cur = Path.Combine(cur, segs[i]);
                if (before.IsNewDirectory(cur))
                {
                    if (OutputNaming.IsTimestampFolderName(segs[i])) { touchedDirs.Add(cur); continue; }
                    if (OutputNaming.TryStripTimestampPrefix(segs[i], out var sd)) { touchedDirs.Add(cur); targetDir = Path.Combine(targetDir, sd); continue; }
                }
                targetDir = Path.Combine(targetDir, segs[i]);
            }

            var newName = name;
            if (OutputNaming.TryStripTimestampPrefix(name, out var stripped))
            {
                newName = stripped;
                if (recmd)
                {
                    var sep = newName.IndexOf("__", StringComparison.Ordinal);
                    if (sep > 0) newName = newName[..sep] + "_" + newName[(sep + 2)..];
                }
            }

            var target = Path.Combine(targetDir, newName);
            if (string.Equals(target, f, StringComparison.Ordinal)) continue;
            try
            {
                Directory.CreateDirectory(targetDir);
                var final = MoveUnique(f, target);
                result.Add(new OutputRename(f, final));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"# Could not rename {name}: {ex.Message}");
            }
        }

        // Remove the now-empty run-time folders (only folders this run created; never non-empty ones).
        foreach (var d in touchedDirs.OrderByDescending(d => d.Length))
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (result.Count > 0) log?.Invoke($"# Renamed {result.Count} output file(s) without the run-time prefix (original names are in the manifest).");
        return result;
    }

    /// <summary>Moves without ever overwriting: <paramref name="target"/>, else <c>Name (2).ext</c>, <c>Name (3).ext</c>, ...</summary>
    public static string MoveUnique(string source, string target)
    {
        for (int n = 1; n < 100_000; n++)
        {
            var p = OutputNaming.WithCopyNumber(target, n);
            if (File.Exists(p) || Directory.Exists(p)) continue;
            try { File.Move(source, p, overwrite: false); return p; }
            catch (IOException) when (File.Exists(p) || Directory.Exists(p)) { }
        }
        throw new IOException("No free file name for " + target);
    }
}

/// <summary>
/// One entry of the deterministic rename table (case layout v2). <see cref="Pattern"/> is matched against the stem of a file a tool
/// wrote (run-time prefix, a leading '!' and the extension removed); <c>{csvf}</c> in the pattern stands for the stem of
/// <see cref="OutputPlan.FixedFileName"/> (never matches when the job had none). <see cref="Template"/> builds the final stem:
/// <c>{base}</c> = <see cref="OutputPlan.ArtifactName"/>, <c>{v}</c> = group "v" as '_'-joined alphanumeric segments, <c>{V}</c> = group "v" in
/// PascalCase. <see cref="ArtifactId"/> may use <c>{v:id}</c> (group "v" in kebab-case) or <c>{default}</c> (<see cref="OutputPlan.ArtifactId"/>).
/// The qualifier is appended after the template (<c>&lt;Artifact&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;]</c>).
/// </summary>
public sealed record OutputNameRule(string ToolId, string Pattern, string Template, string ArtifactId);

/// <summary>A file moved from a job's staging folder to its final place.</summary>
public sealed record PublishedOutput(string OriginalPath, string FinalPath, string ArtifactId)
{
    public string FinalName => Path.GetFileName(FinalPath);
}

/// <summary>Artifact naming convention of case layout v2: <c>&lt;Artifact&gt;[_&lt;Variant&gt;][_&lt;Qualifier&gt;].csv</c>, no tool prefixes.</summary>
public static class ArtifactNaming
{
    /// <summary>Rules of this pseudo tool apply to every tool after its own rules (a <c>--csvf</c> name, then <c>{base}_{v}</c>).</summary>
    public const string FallbackToolId = "*";

    /// <summary>The full rename table, in match order (first match wins; the tool's own rules, then the fallback).</summary>
    public static readonly IReadOnlyList<OutputNameRule> RenameTable = new OutputNameRule[]
    {
        new("PECmd", @"^(PECmd_Output|{csvf})_Timeline$", "{base}_Timeline", "prefetch-timeline"),
        new("PECmd", @"^(PECmd_Output|{csvf})$", "{base}", "prefetch"),
        new("EvtxECmd", @"^(EvtxECmd_Output|{csvf})$", "{base}", "eventlog"),
        new("MFTECmd", @"^MFTECmd_\$MFT_Output$", "MFT", "mft"),
        new("MFTECmd", @"^MFTECmd_\$J_Output$", "UsnJournal", "usnjournal"),
        new("MFTECmd", @"^MFTECmd_\$Boot_Output$", "MFT_Boot", "mft-boot"),
        new("MFTECmd", @"^MFTECmd_\$SDS_Output$", "MFT_SDS", "mft-sds"),
        new("MFTECmd", @"^MFTECmd_\$I30_Output$", "MFT_I30", "i30"),
        new("MFTECmd", @"^MFTECmd_\$LogFile_Output$", "MFT_LogFile", "mft-logfile"),
        new("MFTECmd", @"^{csvf}$", "{base}", "{default}"),
        new("AmcacheParser", @"^Amcache_ShortCuts$", "{base}_Shortcuts", "amcache-shortcuts"),
        new("AmcacheParser", @"^Amcache_(?<v>.+)$", "{base}_{v}", "amcache-{v:id}"),
        new("AppCompatCacheParser", @"^(AppCompatCache|{csvf})$", "{base}", "shimcache"),
        new("RECmd", @"^RECmd_Batch_.*_Output$", "{base}", "registry"),
        new("RECmd", @"^{csvf}$", "{base}", "registry"),
        new("RECmd", @"^(?<v>[^_]+)__.*$", "{base}_{v}", "registry-plugin"),
        new("SBECmd", @"^.+_(?<v>UsrClass|NTUSER)$", "{base}_{v}", "shellbags"),
        new("SBECmd", @"^Deduplicated$", "{base}_Deduplicated", "shellbags"),
        new("SBECmd", @"^SBECmd_Messages$", "{base}_Messages", "shellbags"),
        new("LECmd", @"^(LECmd_Output|{csvf})$", "{base}", "lnk"),
        new("JLECmd", @"^AutomaticDestinations$", "{base}_Auto", "jumplist-automatic"),
        new("JLECmd", @"^CustomDestinations$", "{base}_Custom", "jumplist-custom"),
        new("RBCmd", @"^(RBCmd_Output|{csvf})$", "{base}", "recyclebin"),
        new("SrumECmd", @"^SrumECmd_(?<v>.+)_Output$", "{base}_{v}", "srum"),
        new("SumECmd", @"^SumECmd_(?<v>.+?)(_Output)?$", "{base}_{v}", "ual"),
        // WxTCmd may put the input's folder (the user) in front of the table name (Testuser_Activity); it is dropped here because
        // the user is appended as the qualifier (ActivitiesCache_<user>, not ActivitiesCache_<user>_Activity_<user>).
        new("WxTCmd", @"^(.+_)?Activity$", "{base}", "activitiescache"),
        new("WxTCmd", @"^(.+_)?Activity_PackageIDs$", "{base}_PackageIDs", "activitiescache-packages"),
        new("WxTCmd", @"^(.+_)?Activity_?(?<v>.+)$", "{base}_{v}", "activitiescache"),
        new("RecentFileCacheParser", @"^(RecentFileCacheParser_Output|{csvf})$", "{base}", "recentfilecache"),
        new("SQLECmd", @"^(?<v>.+)$", "{base}_{v}", "sqlite"),
        // EzHub_*: the collector's file names before the rename (legacy cases)
        new(Persistence.PersistenceCollector.ToolId, @"^(EzForensics|EzHub)_Persistence$", "{base}", "persistence"),
        new(Persistence.PersistenceCollector.ToolId, @"^(EzForensics|EzHub)_ScheduledTasks$", "{base}_ScheduledTasks", "persistence-tasks"),
        new(Persistence.PersistenceCollector.ToolId, @"^(EzForensics|EzHub)_WmiPersistence$", "{base}_Wmi", "persistence-wmi"),
        // legacy: cases from before Autoruns was removed
        new("Autoruns", @"^Autoruns_.+$", "{base}", "autoruns"),
        new("Autoruns", @"^autorunsc_.+_raw$", "{base}_Raw", "autoruns"),
        new("Zeek", @"^(?<v>.+)$", "{base}_{V}", "zeek-{v:id}"),
        // RITA: the converter writes Rita_Threats / Rita_Beacons / Rita_LongConnections / Rita_DnsTunneling (artifact ids rita-threats ...).
        new("Rita", @"^Rita_(?<v>.+)$", "{base}_{V}", "rita-{v:id}"),
        // Volatility 3: one plugin per job, stdout captured as <Variant>.csv; the artifact id comes from plugins.json (OutputPlan.ArtifactId).
        new("Volatility3", @"^(?<v>.+)$", "{base}_{V}", "{default}"),
        new(FallbackToolId, @"^{csvf}$", "{base}", "{default}"),
        new(FallbackToolId, @"^(?<v>.+)$", "{base}_{v}", "{default}"),
    };

    /// <summary>
    /// Case layout v2 file names (collision number allowed) → tool and artifact id, in match order. Used to classify files without a
    /// manifest entry. Group "v" (when present) gives the artifact id suffix in kebab-case.
    /// </summary>
    public static readonly IReadOnlyList<(string Pattern, string ToolId, string ArtifactId)> NewNamePatterns = new[]
    {
        (@"^Prefetch_Timeline(_[0-9]+)?\.csv$", "PECmd", "prefetch-timeline"),
        (@"^Prefetch(_[0-9]+)?\.csv$", "PECmd", "prefetch"),
        (@"^EventLogs(_[0-9]+)?\.csv$", "EvtxECmd", "eventlog"),
        (@"^MFT_Boot(_[0-9]+)?\.csv$", "MFTECmd", "mft-boot"),
        (@"^MFT_SDS(_[0-9]+)?\.csv$", "MFTECmd", "mft-sds"),
        (@"^MFT_I30(_[0-9]+)?\.csv$", "MFTECmd", "i30"),
        (@"^MFT_LogFile(_[0-9]+)?\.csv$", "MFTECmd", "mft-logfile"),
        (@"^MFT(_[0-9]+)?\.csv$", "MFTECmd", "mft"),
        (@"^UsnJournal(_[0-9]+)?\.csv$", "MFTECmd", "usnjournal"),
        (@"^Amcache_Shortcuts(_[0-9]+)?\.csv$", "AmcacheParser", "amcache-shortcuts"),
        (@"^Amcache_(?<v>[A-Za-z0-9]+)(_[0-9]+)?\.csv$", "AmcacheParser", "amcache-{v:id}"),
        (@"^ShimCache(_[0-9]+)?\.csv$", "AppCompatCacheParser", "shimcache"),
        (@"^Registry_(SYSTEM|SOFTWARE|SAM|SECURITY|DEFAULT|NTUSER|UsrClass)(_[A-Za-z0-9]+)*\.csv$", "RECmd", "registry"),
        (@"^Registry_[A-Za-z0-9]+(_[A-Za-z0-9]+)*\.csv$", "RECmd", "registry-plugin"),
        (@"^ShellBags(_[A-Za-z0-9]+)*\.csv$", "SBECmd", "shellbags"),
        (@"^LNK(_[A-Za-z0-9]+)*\.csv$", "LECmd", "lnk"),
        (@"^JumpLists_Auto(_[A-Za-z0-9]+)*\.csv$", "JLECmd", "jumplist-automatic"),
        (@"^JumpLists_Custom(_[A-Za-z0-9]+)*\.csv$", "JLECmd", "jumplist-custom"),
        (@"^RecycleBin(_[0-9]+)?\.csv$", "RBCmd", "recyclebin"),
        (@"^SRUM_[A-Za-z0-9]+(_[A-Za-z0-9]+)*\.csv$", "SrumECmd", "srum"),
        (@"^UAL_[A-Za-z0-9]+(_[A-Za-z0-9]+)*\.csv$", "SumECmd", "ual"),
        (@"^ActivitiesCache_PackageIDs(_[A-Za-z0-9]+)*\.csv$", "WxTCmd", "activitiescache-packages"),
        (@"^ActivitiesCache(_[A-Za-z0-9]+)*\.csv$", "WxTCmd", "activitiescache"),
        (@"^RecentFileCache(_[0-9]+)?\.csv$", "RecentFileCacheParser", "recentfilecache"),
        (@"^SQLite_[A-Za-z0-9]+(_[A-Za-z0-9]+)*\.csv$", "SQLECmd", "sqlite"),
        (@"^Persistence_ScheduledTasks(_[0-9]+)?\.csv$", Persistence.PersistenceCollector.ToolId, "persistence-tasks"),
        (@"^Persistence_Wmi(_[0-9]+)?\.csv$", Persistence.PersistenceCollector.ToolId, "persistence-wmi"),
        (@"^Persistence(_[0-9]+)?\.csv$", Persistence.PersistenceCollector.ToolId, "persistence"),
        (@"^Processes(_[0-9]+)?\.csv$", Processes.ProcessCorrelator.ToolId, "processes"),
        // legacy: cases from before Autoruns was removed
        (@"^Autoruns_[A-Za-z0-9]+(_[A-Za-z0-9]+)*\.csv$", "Autoruns", "autoruns"),
        (@"^Zeek_(?<v>[A-Za-z0-9]+)(_[A-Za-z0-9]+)*\.(csv|log)$", "Zeek", "zeek-{v:id}"),
        (@"^Rita_(?<v>[A-Za-z0-9]+)(_[A-Za-z0-9]+)*\.csv$", "Rita", "rita-{v:id}"),
    };

    private const RegexOptions RxOpts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly Regex NonAlnumRx = new(@"[^A-Za-z0-9]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex[] NewNameRx = NewNamePatterns.Select(p => new Regex(p.Pattern, RxOpts | RegexOptions.Compiled)).ToArray();
    private const int MaxStemLength = 150;

    /// <summary>Alphanumeric-only qualifier part (<c>john.doe</c> → <c>johndoe</c>); a value without any ASCII letter or digit becomes
    /// <c>X</c> + 8 hex of its SHA-1; null / blank → null.</summary>
    public static string? SanitizeQualifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = NonAlnumRx.Replace(value, "");
        if (s.Length == 0) return "X" + ShortHash(value);
        return s.Length > 40 ? s[..32] + ShortHash(value) : s;
    }

    /// <summary>Qualifier from several parts (each sanitised, empty parts dropped), joined by '_': (<c>NTUSER</c>, <c>j.doe</c>) → <c>NTUSER_jdoe</c>.</summary>
    public static string? Qualifier(params string?[] parts)
    {
        var list = parts.Select(SanitizeQualifier).Where(p => p != null).ToList();
        return list.Count == 0 ? null : string.Join("_", list);
    }

    /// <summary>Variant segments: runs of other characters become one '_' (<c>SUMMARY ClientsDetailed</c> → <c>SUMMARY_ClientsDetailed</c>).</summary>
    public static string Variant(string? value) =>
        string.Join("_", NonAlnumRx.Split(value ?? "").Where(s => s.Length > 0));

    /// <summary>PascalCase of the alphanumeric segments (<c>dce_rpc</c> → <c>DceRpc</c>, <c>conn-summary</c> → <c>ConnSummary</c>).</summary>
    public static string Pascal(string? value) =>
        string.Concat(NonAlnumRx.Split(value ?? "").Where(s => s.Length > 0).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    /// <summary>kebab-case id (<c>AssociatedFileEntries</c> → <c>associated-file-entries</c>, <c>dce_rpc</c> and <c>DceRpc</c> → <c>dce-rpc</c>).</summary>
    public static string KebabId(string? value)
    {
        var parts = new List<string>();
        foreach (var seg in NonAlnumRx.Split(value ?? "").Where(s => s.Length > 0))
        {
            var sb = new StringBuilder();
            for (int i = 0; i < seg.Length; i++)
            {
                if (i > 0 && char.IsUpper(seg[i]) && char.IsLower(seg[i - 1])) { parts.Add(sb.ToString()); sb.Clear(); }
                sb.Append(char.ToLowerInvariant(seg[i]));
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
        }
        return string.Join("-", parts);
    }

    /// <summary>The stem a tool's file is matched on: run-time prefix, a leading '!' and the extension removed.</summary>
    public static string ToolStem(string fileName)
    {
        var name = OutputNaming.StripTimestampPrefix(Path.GetFileName(fileName)).TrimStart('!');
        var ext = Path.GetExtension(name);
        return ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
    }

    /// <summary>
    /// Final stem (qualifier included, no collision number) and artifact id for a file <paramref name="toolId"/> wrote under
    /// <paramref name="plan"/>. Pure; see <see cref="RenameTable"/>.
    /// </summary>
    public static (string Stem, string ArtifactId) Map(string toolId, OutputPlan plan, string fileName)
    {
        var stem = ToolStem(fileName);
        var csvfStem = plan.FixedFileName != null ? Path.GetFileNameWithoutExtension(plan.FixedFileName) : null;
        // A file the tool wrote under its --csvf name keeps it (that name already holds the qualifier).
        if (csvfStem != null && string.Equals(stem, csvfStem, StringComparison.OrdinalIgnoreCase))
        {
            var fixedRule = RulesFor(toolId).FirstOrDefault(r => r.Pattern.Contains("{csvf}") && Compile(r, csvfStem).IsMatch(stem));
            return (Limit(csvfStem), fixedRule == null ? plan.ArtifactId : ArtifactIdFor(fixedRule.ArtifactId, null, plan));
        }
        foreach (var rule in RulesFor(toolId))
        {
            var m = Compile(rule, csvfStem).Match(stem);
            if (!m.Success) continue;
            var v = m.Groups["v"].Success ? m.Groups["v"].Value : null;
            var built = rule.Template
                .Replace("{base}", plan.ArtifactName, StringComparison.Ordinal)
                .Replace("{v}", Variant(v), StringComparison.Ordinal)
                .Replace("{V}", Pascal(v), StringComparison.Ordinal);
            built = Variant(built);   // drops empty segments ("Prefetch_" when v is empty)
            if (built.Length == 0) built = plan.ArtifactName;
            if (!string.IsNullOrEmpty(plan.Qualifier)) built += "_" + plan.Qualifier;
            return (Limit(built), ArtifactIdFor(rule.ArtifactId, v, plan));
        }
        var fb = plan.ArtifactName + (string.IsNullOrEmpty(plan.Qualifier) ? "" : "_" + plan.Qualifier);
        return (Limit(fb), plan.ArtifactId);
    }

    /// <summary>Final file name before collision numbering: the <see cref="Map"/> stem + the file's own extension (lower-case).</summary>
    public static string FinalFileName(string toolId, OutputPlan plan, string fileName)
    {
        var (stem, _) = Map(toolId, plan, fileName);
        var ext = Path.GetExtension(OutputNaming.StripTimestampPrefix(Path.GetFileName(fileName))).ToLowerInvariant();
        if (ext.Length == 0) ext = ".dat";
        return stem + ext;
    }

    /// <summary><c>dir\Name.ext</c> → <c>dir\Name_n.ext</c> (n ≥ 2).</summary>
    public static string WithNumber(string path, int n)
    {
        if (n < 2) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name);
        return Path.Combine(dir, $"{name[..^ext.Length]}_{n}{ext}");
    }

    /// <summary>Tool and artifact id for a case layout v2 file name (<c>Prefetch.csv</c>, <c>LNK_jdoe_2.csv</c>, <c>Zeek_DceRpc_cap.csv</c>); null when not recognised.</summary>
    public static (string ToolId, string ArtifactId)? IdentifyNewName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        for (int i = 0; i < NewNamePatterns.Count; i++)
        {
            var m = NewNameRx[i].Match(name);
            if (!m.Success) continue;
            var id = NewNamePatterns[i].ArtifactId;
            if (id.Contains("{v:id}")) id = id.Replace("{v:id}", KebabId(m.Groups["v"].Value), StringComparison.Ordinal);
            return (NewNamePatterns[i].ToolId, id);
        }
        return null;
    }

    /// <summary>
    /// Artifact id for a layout v1 file written by <paramref name="toolId"/> (prefixed, stripped or <c>(n)</c>-numbered names), from the
    /// tool's rename-table entries; null when the tool or the name is not recognised. RECmd plugin files renamed by the v1 renamer
    /// (<c>TypedURLs_NTUSER.DAT.csv</c>) count as <c>registry-plugin</c>.
    /// </summary>
    public static string? IdentifyLegacyName(string toolId, string fileName)
    {
        var stem = ToolStem(OutputNaming.CanonicalName(fileName));
        var probe = new OutputPlan("", "X", "", null, null);
        foreach (var rule in RenameTable.Where(r => r.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase)))
        {
            var m = Compile(rule, null).Match(stem);
            if (m.Success) return ArtifactIdFor(rule.ArtifactId, m.Groups["v"].Success ? m.Groups["v"].Value : null, probe);
        }
        if (toolId.Equals("RECmd", StringComparison.OrdinalIgnoreCase)) return "registry-plugin";
        return null;
    }

    private static IEnumerable<OutputNameRule> RulesFor(string toolId) =>
        RenameTable.Where(r => r.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase))
            .Concat(RenameTable.Where(r => r.ToolId.Equals(FallbackToolId, StringComparison.Ordinal)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> RxCache = new(StringComparer.Ordinal);

    private static Regex Compile(OutputNameRule rule, string? csvfStem)
    {
        var pattern = rule.Pattern.Replace("{csvf}", csvfStem == null ? "(?!)" : Regex.Escape(csvfStem), StringComparison.Ordinal);
        return RxCache.GetOrAdd(pattern, p => new Regex(p, RxOpts));
    }

    private static string ArtifactIdFor(string template, string? v, OutputPlan plan) =>
        template.Replace("{default}", plan.ArtifactId, StringComparison.Ordinal)
                .Replace("{v:id}", KebabId(v), StringComparison.Ordinal);

    private static string Limit(string stem) =>
        stem.Length <= MaxStemLength ? stem : stem[..(MaxStemLength - 9)].TrimEnd('_') + "_" + ShortHash(stem);

    private static string ShortHash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..8];
}

/// <summary>Post-run publish step of case layout v2: moves a job's new files from its staging folder to the final category folder.</summary>
public static class OutputPublisher
{
    /// <summary>
    /// Moves every file created or changed in <paramref name="before"/>.Root (the staging folder) into <paramref name="finalDir"/> as
    /// <see cref="ArtifactNaming.FinalFileName"/>; sub-folders are flattened. Never overwrites: a taken name becomes <c>Name_2.ext</c>,
    /// <c>Name_3.ext</c>, ... Files are processed in path order. Empty staging folders are removed afterwards.
    /// </summary>
    /// <param name="partial">The tool was cancelled / timed out: every final name gets <see cref="PartialQualifier"/> (<c>MFT_PARTIAL.csv</c>).</param>
    public static IReadOnlyList<PublishedOutput> Publish(string toolId, OutputPlan plan, string finalDir, OutputSnapshot before, Action<string>? log = null,
        bool partial = false)
    {
        var result = new List<PublishedOutput>();
        var root = before.Root;
        if (!Directory.Exists(root)) return result;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", opts).Where(before.IsNewOrChanged)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("# Publishing the outputs failed: " + ex.Message);
            return result;
        }
        try { Directory.CreateDirectory(finalDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("# Cannot create the output folder: " + ex.Message);
            return result;
        }
        foreach (var f in files)
        {
            var (_, artifactId) = ArtifactNaming.Map(toolId, plan, f);
            var finalName = ArtifactNaming.FinalFileName(toolId, plan, f);
            if (partial) finalName = PartialName(finalName);
            var target = Path.Combine(finalDir, finalName);
            try
            {
                var final = MoveNumbered(f, target);
                result.Add(new PublishedOutput(f, final, artifactId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"# Could not move {Path.GetFileName(f)} out of the staging folder: {ex.Message}");
            }
        }
        RemoveEmptyStaging(root);
        if (result.Count > 0)
            log?.Invoke($"# Published {result.Count} output file(s) to {finalDir}: {string.Join(", ", result.Select(r => r.FinalName))} (the tool's names are in the manifest).");
        return result;
    }

    /// <summary>Qualifier appended to the outputs of a cancelled / timed-out job.</summary>
    public const string PartialQualifier = "PARTIAL";

    /// <summary><c>MFT.csv</c> → <c>MFT_PARTIAL.csv</c> (still a valid layout v2 output name).</summary>
    public static string PartialName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return fileName[..^ext.Length] + "_" + PartialQualifier + ext;
    }

    /// <summary>Files in the staging folder that this job produced (new or changed since <paramref name="before"/>) and that are still there.</summary>
    public static IReadOnlyList<string> Leftovers(OutputSnapshot before)
    {
        try
        {
            if (!Directory.Exists(before.Root)) return Array.Empty<string>();
            return Directory.EnumerateFiles(before.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(f => !OutputNaming.IsEzForensicsSidecar(f) && before.IsNewOrChanged(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Moves without ever overwriting: <paramref name="target"/>, else <c>Name_2.ext</c>, <c>Name_3.ext</c>, ...</summary>
    public static string MoveNumbered(string source, string target)
    {
        for (int n = 1; n < 100_000; n++)
        {
            var p = ArtifactNaming.WithNumber(target, n);
            if (File.Exists(p) || Directory.Exists(p)) continue;
            try { File.Move(source, p, overwrite: false); return p; }
            catch (IOException) when (File.Exists(p) || Directory.Exists(p)) { }
        }
        throw new IOException("No free file name for " + target);
    }

    /// <summary>Deletes the staging folder bottom-up while empty, and the case's <c>.ezstaging</c> folder when nothing else is staged.</summary>
    public static void RemoveEmptyStaging(string stagingDir)
    {
        try
        {
            if (!Directory.Exists(stagingDir)) return;
            foreach (var d in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!Directory.EnumerateFileSystemEntries(stagingDir).Any()) Directory.Delete(stagingDir);
            var parent = Path.GetDirectoryName(stagingDir.TrimEnd('\\', '/'));
            if (parent != null && Path.GetFileName(parent).Equals(OutputPlan.StagingFolderName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Creates the staging folder; the case-level <c>.ezstaging</c> folder is marked Hidden so the Results tree and scanners skip it.</summary>
    public static void PrepareStaging(string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var parent = Path.GetDirectoryName(stagingDir.TrimEnd('\\', '/'));
        if (parent == null || !Path.GetFileName(parent).Equals(OutputPlan.StagingFolderName, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var di = new DirectoryInfo(parent);
            if (!di.Attributes.HasFlag(FileAttributes.Hidden)) di.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }
}
