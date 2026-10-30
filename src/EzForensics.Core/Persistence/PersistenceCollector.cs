using System.Globalization;
using System.Text;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Timeline;
using EzForensics.Core.Triage;

namespace EzForensics.Core.Persistence;

/// <summary>
/// EZ Forensics' built-in offline persistence collector: registry ASEPs, scheduled tasks, startup folders and WMI subscriptions of a
/// Windows system root found in the evidence (a KAPE copy, a mounted image or a copied C: drive). Needs no external tool and
/// works with partial collections: whatever is present is read, whatever is missing is reported in the notes. The evidence is
/// only read (hives through <see cref="ZimmermanHiveOpener"/>: temporary copies, transaction logs replayed in memory).
/// </summary>
public sealed class PersistenceCollector
{
    public const string ToolId = "EzForensics.Persistence";
    /// <summary>The id older run manifests (EZ Hub era) carry; read as <see cref="ToolId"/> (<see cref="Execution.RunManifest.NormalizeToolId"/>).</summary>
    public const string LegacyToolId = "EzHub.Persistence";
    public const string Category = "Persistence";
    public const string Switch = "--persistence";
    public const string MainCsv = "EzForensics_Persistence.csv", TasksCsv = "EzForensics_ScheduledTasks.csv", WmiCsv = "EzForensics_WmiPersistence.csv";

    public static readonly string[] Headers =
    {
        "DateTimeUtc", "TimestampDescription", "Category", "Location", "EntryName", "Command", "ImagePath", "User", "Enabled", "Flags", "Source", "Notes",
        "KeyLastWriteUtc", "TaskRegisteredUtc", "FileCreatedUtc", "FileModifiedUtc",
        // Autoruns (autorunsc -a * -c -h -s -t) columns not already above. Profile = User, Image Path = ImagePath, Launch String = Command.
        // Signer and the hashes stay empty here; a separate hash + signature check fills them.
        "Description", "Company", "Version", "Signer", "MD5", "SHA-1", "PESHA-1", "PESHA-256", "SHA-256", "IMP",
    };

    private readonly IHiveOpener _opener;
    public PersistenceCollector(IHiveOpener? opener = null) => _opener = opener ?? new ZimmermanHiveOpener();

    /// <summary>True when <paramref name="root"/> holds anything this collector reads (hives, tasks, WMI repository, user profiles).</summary>
    public static bool HasWindowsSystem(string root)
    {
        try
        {
            return File.Exists(Path.Combine(root, "Windows", "System32", "config", "SOFTWARE"))
                || File.Exists(Path.Combine(root, "Windows", "System32", "config", "SYSTEM"))
                || Directory.Exists(Path.Combine(root, "Windows", "System32", "Tasks"))
                || File.Exists(WmiPersistence.RepositoryPath(root))
                || UserProfiles(root).Any(p => p.NtUser != null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public sealed record Profile(string Name, string Dir, string? NtUser, string? UsrClass);

    private static readonly EnumerationOptions Insensitive = new() { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>Folders under <c>Users</c> (junction-like legacy names skipped) with their NTUSER.DAT / UsrClass.dat when present.</summary>
    public static IReadOnlyList<Profile> UserProfiles(string root)
    {
        var users = Path.Combine(root, "Users");
        var list = new List<Profile>();
        if (!Directory.Exists(users)) return list;
        foreach (var d in Directory.EnumerateDirectories(users, "*", Insensitive))
        {
            var name = Path.GetFileName(d);
            if (name is "All Users" or "Default User") continue;
            try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            var nt = Directory.EnumerateFiles(d, "NTUSER.DAT", Insensitive).FirstOrDefault();
            var ucDir = Path.Combine(d, "AppData", "Local", "Microsoft", "Windows");
            var uc = Directory.Exists(ucDir) ? Directory.EnumerateFiles(ucDir, "UsrClass.dat", Insensitive).FirstOrDefault() : null;
            list.Add(new Profile(name, d, nt, uc));
        }
        return list;
    }

    public PersistenceResult Collect(string systemRoot, Action<string>? log = null, CancellationToken ct = default)
    {
        var r = new PersistenceResult { SystemRoot = systemRoot };
        var chk = new OfflineChecker(systemRoot);
        var opened = new List<IRegistryHive>();
        IRegistryHive? Open(string path, string name)
        {
            try
            {
                var h = _opener.Open(path, name);
                opened.Add(h);
                r.Hives.Add(h.Info);
                log?.Invoke($"Read {name}: {path} ({h.Info.Size:N0} bytes, SHA-256 {h.Info.Sha256})" + (h.Info.Note != null ? " - " + h.Info.Note : ""));
                if (h.Info.Note != null) r.Notes.Add($"{name}: {h.Info.Note}");
                return h;
            }
            catch (Exception ex)
            {
                r.Notes.Add($"{name} could not be read ({path}): {ex.Message}");
                log?.Invoke(r.Notes[^1]);
                return null;
            }
        }
        string? Hive(string name) => ComputerDetailsCollector.HivePath(systemRoot, name);

        try
        {
            var swPath = Hive("SOFTWARE");
            var sysPath = Hive("SYSTEM");
            var software = swPath != null ? Open(swPath, "SOFTWARE") : null;
            if (swPath == null) r.Notes.Add(@"SOFTWARE hive not found (Windows\System32\config\SOFTWARE): machine Run keys, Winlogon, IFEO, Active Setup and TaskCache skipped.");
            var system = sysPath != null ? Open(sysPath, "SYSTEM") : null;
            if (sysPath == null) r.Notes.Add(@"SYSTEM hive not found (Windows\System32\config\SYSTEM): services, drivers, LSA, boot execute and print monitors skipped.");
            ct.ThrowIfCancellationRequested();

            if (software != null) Step("SOFTWARE ASEPs", () => RegistryAseps.FromSoftware(software, r, chk));
            if (system != null) Step("SYSTEM ASEPs", () => RegistryAseps.FromSystem(system, r, chk));
            int? bias = null;
            if (system != null && RegistryAseps.CurrentControlSet(system) is { } cs
                && system.GetKey(cs + @"\Control\TimeZoneInformation")?.Value("ActiveTimeBias")?.AsNumber() is { } b)
                bias = unchecked((int)(uint)b);

            foreach (var p in UserProfiles(systemRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (p.NtUser != null && Open(p.NtUser, $"NTUSER.DAT[{p.Name}]") is { } nt)
                    Step($"NTUSER.DAT of {p.Name}", () => RegistryAseps.FromUser(nt, p.Name, r, chk, software));
                if (p.UsrClass != null && Open(p.UsrClass, $"UsrClass.dat[{p.Name}]") is { } uc)
                    Step($"UsrClass.dat of {p.Name}", () => RegistryAseps.FromUserClasses(uc, p.Name, r, chk, software));
                Step($"Startup folder of {p.Name}", () => StartupFolders.Read(Path.Combine(p.Dir, StartupFolders.UserRelative), p.Name, r, chk,
                    opened.FirstOrDefault(h => h.Info.Name == $"NTUSER.DAT[{p.Name}]"), true));
            }
            Step("Startup folder (All Users)", () => StartupFolders.Read(Path.Combine(systemRoot, StartupFolders.CommonRelative), "", r, chk, software, false));

            Step("Scheduled tasks", () =>
            {
                var tasks = ScheduledTasks.ReadFolder(systemRoot, r.Notes, ct);
                var tasksDir = Directory.Exists(Path.Combine(systemRoot, "Windows", "System32", "Tasks"));
                ScheduledTasks.MergeTaskCache(tasks, software, bias, r.Notes);
                r.Tasks.AddRange(tasks.OrderBy(t => t.TaskPath, StringComparer.OrdinalIgnoreCase));
                foreach (var t in r.Tasks)
                    foreach (var e in ScheduledTasks.ToEntries(t, tasksDir)) { PersistenceHeuristics.Apply(e, chk); r.Entries.Add(e); }
                log?.Invoke($"Scheduled tasks: {r.Tasks.Count} ({r.Tasks.Count(t => t.XmlMissing)} only in TaskCache)");
            });

            var wmi = WmiPersistence.RepositoryPath(systemRoot);
            if (File.Exists(wmi))
                Step("WMI repository", () =>
                {
                    r.Wmi.AddRange(WmiPersistence.Scan(wmi, ct));
                    foreach (var w in r.Wmi) { var e = WmiPersistence.ToEntry(w, wmi); PersistenceHeuristics.Apply(e, chk); r.Entries.Add(e); }
                    log?.Invoke($"WMI: {r.Wmi.Count} filter-to-consumer binding(s) in {wmi}");
                });
            else r.Notes.Add(@"WMI repository not found (Windows\System32\wbem\Repository\OBJECTS.DATA): WMI subscriptions skipped.");

            Step("Image version info", () => PeVersionInfo.Enrich(r.Entries, systemRoot));
        }
        finally
        {
            foreach (var h in opened) h.Dispose();
        }
        return r;

        void Step(string what, Action a)
        {
            ct.ThrowIfCancellationRequested();
            int before = r.Entries.Count;
            try { a(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                r.Notes.Add($"{what}: failed ({ex.GetType().Name}: {ex.Message}); other locations were still read.");
                log?.Invoke(r.Notes[^1]);
            }
            log?.Invoke($"{what}: {r.Entries.Count - before} entr{(r.Entries.Count - before == 1 ? "y" : "ies")}");
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>Writes <c>EzForensics_Persistence.csv</c> (+ scheduled-task and WMI detail CSVs when there are any). Returns the files written.</summary>
    public static IReadOnlyList<string> Write(PersistenceResult r, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var files = new List<string>();
        var main = Path.Combine(outDir, MainCsv);
        WriteCsv(main, Headers, r.Entries
            .OrderBy(e => e.Category, StringComparer.Ordinal).ThenBy(e => e.User, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.EntryName, StringComparer.OrdinalIgnoreCase)
            .Select(e =>
            {
                var (t, d) = e.Primary;
                return new[] { Ts(t), d, e.Category, e.Location, e.EntryName, e.Command, e.ImagePath, e.User, e.Enabled, string.Join("; ", e.Flags), e.Source, e.Notes,
                    Ts(e.KeyLastWriteUtc), Ts(e.TaskRegisteredUtc), Ts(e.FileCreatedUtc), Ts(e.FileModifiedUtc),
                    e.Description, e.Company, e.Version, e.Signer, e.Md5, e.Sha1, e.PeSha1, e.PeSha256, e.Sha256, e.Imp };
            }));
        files.Add(main);
        if (r.Tasks.Count > 0)
        {
            var p = Path.Combine(outDir, TasksCsv);
            WriteCsv(p, new[] { "DateTimeUtc", "TaskPath", "Enabled", "Hidden", "Actions", "Triggers", "Author", "RunAs", "LogonType", "RunLevel", "Description",
                    "RegistrationDate", "RegisteredFrom", "LastRunUtc", "InTaskCache", "TaskCacheId", "XmlFile", "XmlModifiedUtc", "Error" },
                r.Tasks.Select(t => new[]
                {
                    Ts(t.RegisteredUtc), t.TaskPath, B(t.Enabled), B(t.Hidden), string.Join(" | ", t.Actions.Select(a => a.CommandLine)), string.Join(" | ", t.Triggers),
                    t.Author ?? "", t.UserId ?? "", t.LogonType ?? "", t.RunLevel ?? "", t.Description ?? "", t.RegistrationDateRaw ?? "", t.RegisteredSource ?? "",
                    Ts(t.LastRunUtc), t.InTaskCache ? "Yes" : "No", t.TaskCacheId ?? "", t.XmlFile ?? "(deleted: only in TaskCache)", Ts(t.FileModifiedUtc), t.Error ?? "",
                }));
            files.Add(p);
        }
        if (r.Wmi.Count > 0)
        {
            var p = Path.Combine(outDir, WmiCsv);
            WriteCsv(p, new[] { "ConsumerType", "ConsumerName", "FilterName", "Namespace", "FilterQuery", "ScriptingEngine", "Command", "LikelyLegitimate", "ConsumerOffset", "FilterOffset" },
                r.Wmi.Select(w => new[] { w.ConsumerType, w.ConsumerName, w.FilterName, w.Namespace ?? "", w.FilterQuery, w.ScriptingEngine ?? "", w.Command,
                    w.LikelyLegitimate ? "Yes" : "No", w.ConsumerOffset >= 0 ? "0x" + w.ConsumerOffset.ToString("X") : "", w.FilterOffset >= 0 ? "0x" + w.FilterOffset.ToString("X") : "" }));
            files.Add(p);
        }
        return files;
    }

    private static string B(bool? b) => b == null ? "" : b.Value ? "Yes" : "No";
    private static string Ts(DateTime? t) => t is { } v ? TimelineTimestamp.Format(v) : "";

    public static void WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.NewLine = "\r\n";
                w.WriteLine(string.Join(",", headers.Select(Esc)));
                foreach (var row in rows) w.WriteLine(string.Join(",", row.Select(Esc)));
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static string Esc(string? s)
    {
        s ??= "";
        if (s.Length > 32000) s = s[..32000] + "…(truncated)";
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ------------------------------------------------------------------ headless job: EzForensics.exe --persistence --root <r> [--root ...] --out <dir>

    /// <summary>Entry point of the built-in job. Exit code 0 = written, 1 = bad arguments / nothing to read, 2 = failed.</summary>
    public static int RunCommandLine(IReadOnlyList<string> args, TextWriter stdout, CancellationToken ct = default, IHiveOpener? opener = null)
    {
        var roots = new List<string>();
        string? outDir = null, source = null;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Count: roots.Add(args[++i]); break;
                case "--source" when i + 1 < args.Count: source = args[++i]; break;
                case "--out" when i + 1 < args.Count: outDir = args[++i]; break;
            }
        }
        if (outDir == null || (roots.Count == 0 && source == null))
        {
            stdout.WriteLine("Usage: --persistence (--root <system root> ... | --source <evidence folder>) --out <folder>");
            return 1;
        }
        if (roots.Count == 0)
            roots.AddRange(SourceLayoutDetector.Detect(source!).SystemRoots.Where(HasWindowsSystem));
        roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0)
        {
            stdout.WriteLine("Error: no Windows system root (hives, Windows\\System32\\Tasks, WMI repository or user profiles) was found.");
            return 1;
        }
        try
        {
            var collector = new PersistenceCollector(opener);
            foreach (var root in roots)
            {
                stdout.WriteLine("System root: " + root);
                var res = collector.Collect(root, stdout.WriteLine, ct);
                var dir = roots.Count == 1 ? outDir : Path.Combine(outDir, FolderName(root));
                foreach (var f in Write(res, dir)) stdout.WriteLine("Wrote " + f);
                stdout.WriteLine($"Entries: {res.Entries.Count}");
                foreach (var (cat, n) in res.CountsByCategory()) stdout.WriteLine($"  {cat}: {n}");
                stdout.WriteLine($"Flagged entries: {res.Entries.Count(e => e.Flags.Count > 0)}");
                foreach (var n in res.Notes) stdout.WriteLine("Note: " + n);
            }
            return 0;
        }
        catch (OperationCanceledException) { stdout.WriteLine("Cancelled."); return 2; }
        catch (Exception ex)
        {
            stdout.WriteLine("Error: persistence collection failed: " + ex.Message);
            return 2;
        }
    }

    public static string FolderName(string root)
    {
        var name = string.Concat(root.TrimEnd('\\', '/').Replace(":", "").Select(c => c is '\\' or '/' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('_');
        if (name.Length > 60) name = name[^60..];
        return name.Length == 0 ? "root" : name;
    }

    public static string Summary(PersistenceResult r) =>
        string.Join(", ", r.CountsByCategory().Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} {kv.Value}")));
}
