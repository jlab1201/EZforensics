using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EzForensics.Core.SystemInfo;

namespace EzForensics.Core.Persistence;

/// <summary>
/// Scheduled tasks: the XML definitions under <c>Windows\System32\Tasks\**</c> plus the SOFTWARE hive's TaskCache
/// (<c>Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache</c>), which gives the registration time in UTC (DynamicInfo) and
/// still lists tasks whose XML file was deleted.
/// </summary>
public static class ScheduledTasks
{
    public const string TaskCacheKey = @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private const int MaxXmlBytes = 4 << 20;

    /// <summary>Parses one task XML file (UTF-16 or UTF-8, with or without a declaration). Never throws: errors go to <see cref="ScheduledTaskInfo.Error"/>.</summary>
    public static ScheduledTaskInfo ParseFile(string file, string tasksRoot)
    {
        var rel = file.StartsWith(tasksRoot, StringComparison.OrdinalIgnoreCase) ? file[tasksRoot.Length..] : Path.GetFileName(file);
        var info = new ScheduledTaskInfo { TaskPath = "\\" + rel.Replace(Path.DirectorySeparatorChar, '\\').TrimStart('\\'), XmlFile = file };
        try
        {
            var fi = new FileInfo(file);
            info.FileModifiedUtc = fi.LastWriteTimeUtc;
            if (fi.Length > MaxXmlBytes) { info.Error = $"XML larger than {MaxXmlBytes >> 20} MB, not parsed"; return info; }
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            Parse(Decode(bytes), info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            info.Error = ex.Message;
        }
        return info;
    }

    /// <summary>UTF-16 LE/BE (with or without BOM, as the Task Scheduler writes it) or UTF-8.</summary>
    public static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 4 && b[1] == 0 && b[3] == 0) return Encoding.Unicode.GetString(b);
        return Encoding.UTF8.GetString(b);
    }

    public static void Parse(string xml, ScheduledTaskInfo info)
    {
        // The declaration often says encoding="UTF-16" although we already decoded the text: drop it.
        var start = xml.IndexOf("<Task", StringComparison.Ordinal);
        if (start < 0) throw new XmlException("No <Task> element.");
        var doc = XDocument.Parse(xml[start..], LoadOptions.None);
        var root = doc.Root!;
        XElement? E(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        string? V(XElement? parent, string name) => E(parent, name)?.Value.Trim();
        var reg = E(root, "RegistrationInfo");
        info.Author = V(reg, "Author");
        info.Description = V(reg, "Description");
        info.RegistrationDateRaw = V(reg, "Date");
        if (V(reg, "URI") is { Length: > 0 } uri) info.TaskPath = uri.StartsWith('\\') ? uri : "\\" + uri;
        var settings = E(root, "Settings");
        info.Enabled = Bool(V(settings, "Enabled")) ?? true;
        info.Hidden = Bool(V(settings, "Hidden")) ?? false;
        var principal = E(root, "Principals")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Principal");
        info.UserId = V(principal, "UserId") ?? V(principal, "GroupId");
        info.LogonType = V(principal, "LogonType");
        info.RunLevel = V(principal, "RunLevel");
        foreach (var t in E(root, "Triggers")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var parts = new List<string> { t.Name.LocalName };
            if (V(t, "StartBoundary") is { } sb) parts.Add("start " + sb);
            if (E(t, "Repetition") is { } rep && V(rep, "Interval") is { } iv) parts.Add("every " + iv);
            if (V(t, "Delay") is { } d) parts.Add("delay " + d);
            if (V(t, "UserId") is { } u) parts.Add("user " + u);
            if (V(t, "Subscription") is { } sub) parts.Add("subscription " + Clip(sub, 200));
            if (Bool(V(t, "Enabled")) == false) parts.Add("(disabled)");
            info.Triggers.Add(string.Join(" ", parts));
        }
        foreach (var a in E(root, "Actions")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            switch (a.Name.LocalName)
            {
                case "Exec": info.Actions.Add(new TaskAction("Exec", V(a, "Command") ?? "", V(a, "Arguments") ?? "", V(a, "WorkingDirectory"))); break;
                case "ComHandler": info.Actions.Add(new TaskAction("ComHandler", "", "", null, V(a, "ClassId"), V(a, "Data"))); break;
                case "SendEmail": info.Actions.Add(new TaskAction("SendEmail", "Send e-mail to " + V(a, "To"), "")); break;
                case "ShowMessage": info.Actions.Add(new TaskAction("ShowMessage", "Show message " + V(a, "Title"), "")); break;
            }
        }
    }

    private static bool? Bool(string? s) => s == null ? null : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>All task XML files under <c>&lt;root&gt;\Windows\System32\Tasks</c> (and the legacy <c>Windows\Tasks\*.job</c> count as a note).</summary>
    public static List<ScheduledTaskInfo> ReadFolder(string systemRoot, List<string> notes, CancellationToken ct = default)
    {
        var dir = Path.Combine(systemRoot, "Windows", "System32", "Tasks");
        var list = new List<ScheduledTaskInfo>();
        if (!Directory.Exists(dir)) { notes.Add(@"Windows\System32\Tasks not found in the source: scheduled tasks come from the TaskCache only (if the SOFTWARE hive exists)."); return list; }
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 8 };   // reparse points are followed: bound a junction loop
        foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(ParseFile(f, dir));
        }
        var legacy = Path.Combine(systemRoot, "Windows", "Tasks");
        try
        {
            if (Directory.Exists(legacy) && Directory.EnumerateFiles(legacy, "*.job").Take(1000).Count() is var n and > 0)
                notes.Add($@"{n} legacy .job task file(s) in Windows\Tasks (binary format, not parsed; open them with a dedicated parser).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>
    /// Completes the tasks from the TaskCache: registration time (DynamicInfo FILETIME, UTC), last run, and adds the tasks that exist
    /// only in the registry (XML deleted). Falls back to RegistrationInfo/Date converted with the system's ActiveTimeBias.
    /// </summary>
    public static void MergeTaskCache(List<ScheduledTaskInfo> tasks, IRegistryHive? software, int? activeTimeBiasMinutes, List<string> notes)
    {
        var byPath = tasks.GroupBy(t => t.TaskPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var cacheTasks = software?.GetKey(TaskCacheKey + @"\Tasks");
        if (cacheTasks != null)
        {
            foreach (var k in cacheTasks.SubKeys)
            {
                var path = k.Value("Path")?.AsString();
                if (string.IsNullOrEmpty(path)) continue;
                if (!byPath.TryGetValue(path, out var t))
                {
                    t = new ScheduledTaskInfo { TaskPath = path, Author = k.Value("Author")?.AsString(), Description = k.Value("Description")?.AsString() };
                    foreach (var a in ParseActions(k.Value("Actions")?.Raw)) t.Actions.Add(a);
                    tasks.Add(t);
                    byPath[path] = t;
                }
                t.InTaskCache = true;
                t.TaskCacheId = k.Name;
                if (k.Value("DynamicInfo")?.Raw is { Length: >= 12 } di)
                {
                    var created = RegistryAseps.FileTime(BitConverter.ToInt64(di, 4));
                    if (created != null) { t.RegisteredUtc = created; t.RegisteredSource = "TaskCache DynamicInfo (UTC)"; }
                    if (di.Length >= 20) t.LastRunUtc = RegistryAseps.FileTime(BitConverter.ToInt64(di, 12));
                }
            }
        }
        else if (software != null) notes.Add("SOFTWARE hive has no TaskCache key.");

        foreach (var t in tasks.Where(t => t.RegisteredUtc == null && t.RegistrationDateRaw != null))
        {
            if (DateTimeOffset.TryParse(t.RegistrationDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && HasZone(t.RegistrationDateRaw!))
            {
                t.RegisteredUtc = dto.UtcDateTime;
                t.RegisteredSource = "RegistrationInfo/Date (with time zone)";
            }
            else if (DateTime.TryParse(t.RegistrationDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local) && activeTimeBiasMinutes is { } bias)
            {
                t.RegisteredUtc = DateTime.SpecifyKind(local.AddMinutes(bias), DateTimeKind.Utc);
                t.RegisteredSource = $"RegistrationInfo/Date (local) + ActiveTimeBias {bias} min";
            }
        }
    }

    private static bool HasZone(string s) => s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d\d:\d\d$");

    /// <summary>
    /// TaskCache\Tasks\{id}\Actions blob: u16 version, [u32 len + UTF-16 context], then per action a u16 type (0x6666 Exec,
    /// 0x7777 COM handler, 0x8888 e-mail, 0x9999 message), u32 len + id, and for Exec u32 len + command / arguments / working dir.
    /// Best effort and bounds-checked: returns what could be read.
    /// </summary>
    public static List<TaskAction> ParseActions(byte[]? b)
    {
        var list = new List<TaskAction>();
        if (b == null || b.Length < 6) return list;
        int p = 0;
        bool Str(out string s)
        {
            s = "";
            if (p + 4 > b.Length) return false;
            int len = BitConverter.ToInt32(b, p);
            if (len < 0 || p + 4 + len > b.Length) return false;
            s = Encoding.Unicode.GetString(b, p + 4, len & ~1).TrimEnd('\0');
            p += 4 + len;
            return true;
        }
        try
        {
            var version = BitConverter.ToUInt16(b, 0);
            p = 2;
            if (version >= 2 && !Str(out _)) return list; // context / principal id
            while (p + 2 <= b.Length && list.Count < 32)
            {
                var type = BitConverter.ToUInt16(b, p);
                p += 2;
                if (!Str(out _)) break; // action id
                if (type == 0x6666)
                {
                    if (!Str(out var cmd) || !Str(out var args) || !Str(out var wd)) break;
                    list.Add(new TaskAction("Exec", cmd, args, wd.Length > 0 ? wd : null));
                    if (version >= 3) p += 2; // flags
                }
                else if (type == 0x7777)
                {
                    if (p + 16 > b.Length) break;
                    var clsid = new Guid(b.AsSpan(p, 16)).ToString("B");
                    p += 16;
                    Str(out var data);
                    list.Add(new TaskAction("ComHandler", "", "", null, clsid, data));
                }
                else break; // e-mail / message actions: deprecated, layout not needed
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException) { }
        return list;
    }

    /// <summary>One <see cref="PersistenceEntry"/> per task action (a task without actions gives one row).</summary>
    public static IEnumerable<PersistenceEntry> ToEntries(ScheduledTaskInfo t, bool tasksFolderCollected = true)
    {
        var actions = t.Actions.Count > 0 ? t.Actions : new List<TaskAction> { new("None", "", "") };
        foreach (var a in actions)
        {
            var e = new PersistenceEntry
            {
                Category = PersistenceCategory.ScheduledTasks,
                Location = t.XmlFile != null ? @"Windows\System32\Tasks" : @"TaskCache (SOFTWARE)",
                EntryName = t.TaskPath,
                Command = a.CommandLine,
                ImagePath = a.Kind == "Exec" ? a.Command.Trim('"') : a.ClassId ?? "",
                User = "",
                Enabled = t.Enabled == null ? "" : t.Enabled.Value ? "Yes" : "No",
                Source = t.XmlFile ?? $@"SOFTWARE\{TaskCacheKey}\Tasks\{t.TaskCacheId}",
                TaskRegisteredUtc = t.RegisteredUtc,
                Notes = string.Join("; ", new[]
                {
                    t.Author is { Length: > 0 } au ? "Author=" + au : null,
                    t.UserId is { Length: > 0 } u ? "RunAs=" + u : null,
                    t.RunLevel is { Length: > 0 } rl ? "RunLevel=" + rl : null,
                    t.Triggers.Count > 0 ? "Triggers=" + string.Join(" | ", t.Triggers) : null,
                    t.RegistrationDateRaw is { } rd ? "RegistrationDate=" + rd : null,
                    t.RegisteredSource is { } rs ? "Registered from " + rs : null,
                    t.LastRunUtc is { } lr ? $"LastRun={lr.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC" : null,
                    a.WorkingDirectory is { Length: > 0 } wd ? "WorkingDir=" + wd : null,
                    t.FileModifiedUtc is { } xm ? $"XmlModified={xm.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC" : null,
                    t.Error is { } err ? "XML error: " + err : null,
                }.Where(x => x != null)),
            };
            // Hidden is normal for Windows' own tasks: flag it only outside \Microsoft\ or when the action is not a Windows binary / COM handler.
            if (t.Hidden == true && (!t.TaskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                                     || (a.Kind == "Exec" && !System.Text.RegularExpressions.Regex.IsMatch(a.Command.Trim('"'), @"^(%windir%|%systemroot%|C:\\Windows)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase))))
                e.Flag(PersistenceHeuristics.HiddenTask);
            if (t.XmlMissing && tasksFolderCollected) e.Flag(PersistenceHeuristics.TaskXmlMissing);
            if (t.XmlMissing && !tasksFolderCollected) e.Notes = string.Join("; ", new[] { e.Notes, @"Windows\System32\Tasks was not collected: details from the TaskCache only" }.Where(x => x.Length > 0));
            yield return e;
        }
    }
}
