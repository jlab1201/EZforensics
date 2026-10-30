using EzForensics.Core.SystemInfo;
using C = EzForensics.Core.Persistence.PersistenceCategory;
using H = EzForensics.Core.Persistence.PersistenceHeuristics;

namespace EzForensics.Core.Persistence;

/// <summary>
/// Registry autostart extensibility points (ASEPs) read from offline hives (<see cref="IRegistryHive"/>; the evidence is opened
/// read-only from temporary copies by <see cref="ZimmermanHiveOpener"/>). Pure functions over the hive view, unit-tested with
/// <see cref="InMemoryHive"/>.
/// </summary>
public static partial class RegistryAseps
{
    private const string CV = @"Microsoft\Windows\CurrentVersion";
    private const string NTCV = @"Microsoft\Windows NT\CurrentVersion";
    private static readonly string[] SoftwareRoots = { "", "WOW6432Node\\" };

    // ------------------------------------------------------------------ SOFTWARE (HKLM\SOFTWARE)

    public static void FromSoftware(IRegistryHive sw, PersistenceResult r, OfflineChecker? chk = null)
    {
        var approved = StartupApproved(sw, CV + @"\Explorer\StartupApproved");
        foreach (var wow in SoftwareRoots)
        {
            var approvedRun = wow.Length == 0 ? approved.Run : approved.Run32;
            foreach (var k in new[] { "Run", "RunOnce", "RunServices", "RunServicesOnce", @"Policies\Explorer\Run" })
                Values(sw, "SOFTWARE", wow + CV + "\\" + k, C.RunKeys, "", r, chk, approvedRun);
            var once = sw.GetKey(wow + CV + @"\RunOnceEx");
            if (once != null)
                foreach (var sub in once.SubKeys)
                    Values(sw, "SOFTWARE", $"{wow}{CV}\\RunOnceEx\\{sub.Name}", C.RunKeys, "", r, chk);

            // Winlogon
            var wl = sw.GetKey(wow + NTCV + @"\Winlogon");
            if (wl != null)
            {
                foreach (var (name, def) in new[] { ("Shell", "explorer.exe"), ("Userinit", @"C:\Windows\system32\userinit.exe,"), ("Taskman", null), ("AppSetup", null),
                             ("VmApplet", "SystemPropertiesPerformance.exe /pagefile"), ("GinaDLL", null) })
                {
                    var v = wl.Value(name);
                    if (v == null) continue;
                    var e = FromValue(C.Winlogon, "SOFTWARE", wow + NTCV + @"\Winlogon", wl, v, "");
                    if (def != null && !SameDefault(v.AsString(), def)) e.Flag(H.NonDefaultValue);
                    if (def == null && !string.IsNullOrWhiteSpace(v.AsString())) e.Flag(H.NonDefaultValue);
                    Add(r, e, chk);
                }
                var notify = wl.SubKey("Notify");
                if (notify != null)
                    foreach (var n in notify.SubKeys)
                        if (n.Value("DllName") is { } dll)
                            Add(r, FromValue(C.Winlogon, "SOFTWARE", wow + NTCV + @"\Winlogon\Notify\" + n.Name, n, dll, "", entryName: "Notify\\" + n.Name), chk);
            }

            // AppInit_DLLs
            var win = sw.GetKey(wow + NTCV + @"\Windows");
            if (win?.Value("AppInit_DLLs") is { } appInit && !string.IsNullOrWhiteSpace(appInit.AsString()))
            {
                var load = win.Value("LoadAppInit_DLLs")?.AsNumber();
                foreach (var dll in SplitList(appInit.AsString()!))
                {
                    var e = FromValue(C.AppInit, "SOFTWARE", wow + NTCV + @"\Windows", win, appInit, "");
                    e.Command = dll;
                    e.ImagePath = dll;
                    e.Enabled = load == 1 ? "Yes" : "No";
                    e.Notes = $"LoadAppInit_DLLs={(load?.ToString() ?? "missing")}";
                    if (load == 1) e.Flag(H.AppInitLoaded);
                    Add(r, e, chk);
                }
            }

            // Image File Execution Options: Debugger / GlobalFlag (silent process exit) / VerifierDlls
            var ifeo = sw.GetKey(wow + NTCV + @"\Image File Execution Options");
            if (ifeo != null)
                foreach (var img in ifeo.SubKeys)
                {
                    var key = wow + NTCV + @"\Image File Execution Options\" + img.Name;
                    if (img.Value("Debugger") is { } dbg && !string.IsNullOrWhiteSpace(dbg.AsString()))
                        Add(r, FromValue(C.ImageHijacks, "SOFTWARE", key, img, dbg, "", entryName: img.Name + " (Debugger)").Flag(H.IfeoDebugger), chk);
                    if (img.Value("VerifierDlls") is { } ver && !string.IsNullOrWhiteSpace(ver.AsString()))
                        Add(r, FromValue(C.ImageHijacks, "SOFTWARE", key, img, ver, "", entryName: img.Name + " (VerifierDlls)"), chk);
                    var gf = img.Value("GlobalFlag")?.AsNumber();
                    if (gf is { } g && (g & 0x200) != 0)
                    {
                        var spe = sw.GetKey(wow + NTCV + @"\SilentProcessExit\" + img.Name);
                        var mon = spe?.Value("MonitorProcess")?.AsString() ?? "";
                        var e = new PersistenceEntry
                        {
                            Category = C.ImageHijacks, Location = "HKLM\\SOFTWARE\\" + key, EntryName = img.Name + " (GlobalFlag 0x200 + SilentProcessExit)",
                            Command = mon, ImagePath = H.ImageFromCommand(mon), Source = "SOFTWARE\\" + key + "\\GlobalFlag",
                            KeyLastWriteUtc = spe?.LastWriteUtc ?? img.LastWriteUtc,
                            Notes = $"GlobalFlag=0x{g:X}; ReportingMode={spe?.Value("ReportingMode")?.AsNumber()?.ToString() ?? "-"}",
                        }.Flag(H.SilentExit);
                        Add(r, e, chk);
                    }
                }

            Values(sw, "SOFTWARE", wow + CV + @"\Explorer\ShellServiceObjectDelayLoad", C.Explorer, "", r, chk, resolveClsid: sw);
            Values(sw, "SOFTWARE", wow + CV + @"\Explorer\ShellExecuteHooks", C.Explorer, "", r, chk, resolveClsid: sw);
            var bho = sw.GetKey(wow + CV + @"\Explorer\Browser Helper Objects");
            if (bho != null)
                foreach (var b in bho.SubKeys)
                {
                    var dll = ClsidServer(sw, b.Name, wow) ?? "";
                    Add(r, new PersistenceEntry
                    {
                        Category = C.Explorer, Location = $"HKLM\\SOFTWARE\\{wow}{CV}\\Explorer\\Browser Helper Objects", EntryName = b.Name + " (BHO)",
                        Command = dll, ImagePath = dll, Source = $"SOFTWARE\\{wow}{CV}\\Explorer\\Browser Helper Objects\\{b.Name}", KeyLastWriteUtc = b.LastWriteUtc,
                        Notes = Str(b.DefaultValue) ?? "",
                    }, chk);
                }

            // Active Setup
            var setup = sw.GetKey(wow + @"Microsoft\Active Setup\Installed Components");
            if (setup != null)
                foreach (var comp in setup.SubKeys)
                    if (comp.Value("StubPath") is { } stub && !string.IsNullOrWhiteSpace(stub.AsString()))
                    {
                        var e = FromValue(C.ActiveSetup, "SOFTWARE", wow + @"Microsoft\Active Setup\Installed Components\" + comp.Name, comp, stub, "",
                            entryName: (Str(comp.DefaultValue) is { Length: > 0 } dn ? dn + " " : "") + comp.Name);
                        var inst = comp.Value("IsInstalled")?.AsNumber();
                        e.Enabled = inst == 0 ? "No" : "Yes";
                        Add(r, e, chk);
                    }

            Values(sw, "SOFTWARE", wow + @"Microsoft\NetSh", C.Netsh, "", r, chk);
            Single(sw, "SOFTWARE", wow + NTCV + @"\AeDebug", "Debugger", C.Debuggers, "", r, chk,
                note: k => "Auto=" + (Str(k.Value("Auto")) ?? "-"));
            Single(sw, "SOFTWARE", wow + @"Microsoft\Command Processor", "AutoRun", C.CommandProcessor, "", r, chk);
        }

        Single(sw, "SOFTWARE", @"Policies\Microsoft\Windows NT\Terminal Services", "InitialProgram", C.TerminalServer, "", r, chk);
        ApprovedShellExtensions(sw, r, chk);
        SoftwareExtra(sw, r, chk);
    }

    // ------------------------------------------------------------------ SYSTEM (current control set)

    public static string? CurrentControlSet(IRegistryHive system)
    {
        var cur = system.GetKey("Select")?.Value("Current")?.AsNumber();
        if (cur is > 0 and < 1000 && system.GetKey($"ControlSet{cur:000}") != null) return $"ControlSet{cur:000}";
        if (system.GetKey("ControlSet001") != null) return "ControlSet001";
        return system.GetKey("CurrentControlSet") != null ? "CurrentControlSet" : null;
    }

    public static void FromSystem(IRegistryHive sys, PersistenceResult r, OfflineChecker? chk = null)
    {
        var cs = CurrentControlSet(sys);
        if (cs == null) { r.Notes.Add("SYSTEM: no control set found (Select\\Current / ControlSet001)."); return; }

        var sm = sys.GetKey(cs + @"\Control\Session Manager");
        if (sm != null)
            foreach (var (name, def) in new[] { ("BootExecute", "autocheck autochk *"), ("SetupExecute", ""), ("Execute", ""), ("S0InitialCommand", "") })
                if (sm.Value(name) is { } v)
                    foreach (var item in v.AsMulti().DefaultIfEmpty(""))
                    {
                        if (item.Length == 0 && def.Length == 0) continue;
                        var e = FromValue(C.BootExecute, "SYSTEM", cs + @"\Control\Session Manager", sm, v, "");
                        e.Command = item;
                        e.ImagePath = H.ImageFromCommand(item);
                        if (!SameDefault(item, def)) e.Flag(H.NonDefaultValue);
                        Add(r, e, chk);
                    }
        Single(sys, "SYSTEM", cs + @"\Control\SafeBoot", "AlternateShell", C.BootExecute, "", r, chk, def: "cmd.exe");
        Values(sys, "SYSTEM", cs + @"\Control\Session Manager\AppCertDlls", C.AppCert, "", r, chk);

        // LSA packages: one row per package, non-default names flagged.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "msv1_0", "scecli", "rassfm", "kerberos", "schannel", "wdigest", "tspkg", "pku2u", "cloudap", "negoexts", "livessp", "credssp", "\"\"", "" };
        foreach (var (key, val) in new[] { (@"Control\Lsa", "Authentication Packages"), (@"Control\Lsa", "Notification Packages"),
                     (@"Control\Lsa", "Security Packages"), (@"Control\Lsa\OSConfig", "Security Packages"), (@"Control\SecurityProviders", "SecurityProviders") })
        {
            var k = sys.GetKey(cs + "\\" + key);
            if (k?.Value(val) is not { } v) continue;
            foreach (var item in v.AsMulti().SelectMany(SplitList).Where(i => i.Length > 0 && i != "\"\""))
            {
                var e = FromValue(C.Lsa, "SYSTEM", cs + "\\" + key, k, v, "");
                e.EntryName = $"{val}: {item}";
                e.Command = item;
                e.ImagePath = item.Contains('.') ? item : item + ".dll";
                if (!known.Contains(item) && !known.Contains(Path.GetFileNameWithoutExtension(item))) e.Flag(H.NonDefaultValue);
                Add(r, e, chk);
            }
        }

        foreach (var (key, val) in new[] { (@"Control\Print\Monitors", "Driver"), (@"Control\Print\Providers", "Name") })
        {
            var parent = sys.GetKey(cs + "\\" + key);
            if (parent == null) continue;
            foreach (var m in parent.SubKeys)
                if (m.Value(val) is { } v && !string.IsNullOrWhiteSpace(v.AsString()))
                    Add(r, FromValue(C.Print, "SYSTEM", $"{cs}\\{key}\\{m.Name}", m, v, "", entryName: m.Name), chk);
        }

        var tp = sys.GetKey(cs + @"\Services\W32Time\TimeProviders");
        if (tp != null)
            foreach (var p in tp.SubKeys)
                if (p.Value("DllName") is { } v)
                {
                    var e = FromValue(C.TimeProviders, "SYSTEM", cs + @"\Services\W32Time\TimeProviders\" + p.Name, p, v, "", entryName: p.Name);
                    var en = p.Value("Enabled")?.AsNumber();
                    e.Enabled = en == null ? "" : en == 0 ? "No" : "Yes";
                    Add(r, e, chk);
                }

        Single(sys, "SYSTEM", cs + @"\Control\Terminal Server\WinStations\RDP-Tcp", "InitialProgram", C.TerminalServer, "", r, chk, def: "");
        Single(sys, "SYSTEM", cs + @"\Control\Terminal Server\Wds\rdpwd", "StartupPrograms", C.TerminalServer, "", r, chk, def: "rdpclip");

        Services(sys, cs, r, chk);
        SystemExtra(sys, cs, r, chk);
    }

    private static readonly string[] StartNames = { "Boot", "System", "Automatic", "Manual", "Disabled" };

    private static void Services(IRegistryHive sys, string cs, PersistenceResult r, OfflineChecker? chk)
    {
        var services = sys.GetKey(cs + @"\Services");
        if (services == null) return;
        foreach (var s in services.SubKeys)
        {
            var type = s.Value("Type")?.AsNumber();
            if (type == null) continue;
            var image = Str(s.Value("ImagePath")) ?? "";
            var start = s.Value("Start")?.AsNumber();
            var dll = Str(s.SubKey("Parameters")?.Value("ServiceDll")) ?? Str(s.Value("ServiceDll"));
            bool driver = (type & 0xB) != 0 && (type & 0x30) == 0; // 1 kernel, 2 file system, 8 recognizer
            if (driver && image.Length == 0) image = $@"System32\drivers\{s.Name}.sys";
            var e = new PersistenceEntry
            {
                Category = driver ? C.Drivers : C.Services,
                Location = $"HKLM\\SYSTEM\\{cs}\\Services",
                EntryName = s.Name,
                Command = image,
                ImagePath = dll ?? H.ImageFromCommand(image),
                Enabled = start == null ? "" : start == 4 ? "No" : "Yes",
                Source = $"SYSTEM\\{cs}\\Services\\{s.Name}",
                KeyLastWriteUtc = s.LastWriteUtc,
                Notes = string.Join("; ", new[]
                {
                    "Start=" + (start is >= 0 and <= 4 ? StartNames[start.Value] : start?.ToString() ?? "-"),
                    "Type=" + TypeName(type.Value),
                    Str(s.Value("ObjectName")) is { } on ? "Account=" + on : null,
                    Str(s.Value("DisplayName")) is { } dn && !dn.StartsWith('@') ? "DisplayName=" + dn : null,
                    dll != null ? "ServiceDll=" + dll : null,
                }.Where(x => x != null)),
            };
            Add(r, e, chk);
        }
    }

    private static string TypeName(long t) => t switch
    {
        1 => "Kernel driver", 2 => "File system driver", 8 => "Recognizer driver", 0x10 => "Own process", 0x20 => "Shared process",
        0x110 => "Own process (interactive)", 0x120 => "Shared process (interactive)", 0x50 => "User own process", 0x60 => "User shared process",
        _ => "0x" + t.ToString("X"),
    };

    // ------------------------------------------------------------------ NTUSER.DAT / UsrClass.dat

    public static void FromUser(IRegistryHive nt, string user, PersistenceResult r, OfflineChecker? chk = null, IRegistryHive? software = null)
    {
        var tag = $"NTUSER.DAT[{user}]";
        var approved = StartupApproved(nt, @"Software\" + CV + @"\Explorer\StartupApproved");
        foreach (var k in new[] { "Run", "RunOnce", "RunServices", "RunServicesOnce", @"Policies\Explorer\Run" })
            Values(nt, tag, @"Software\" + CV + "\\" + k, C.RunKeys, user, r, chk, approved.Run, hkcu: true);
        foreach (var k in new[] { "Run", "RunOnce" })
            Values(nt, tag, @"Software\WOW6432Node\" + CV + "\\" + k, C.RunKeys, user, r, chk, approved.Run32, hkcu: true);
        Single(nt, tag, @"Software\" + NTCV + @"\Windows", "Load", C.RunKeys, user, r, chk, hkcu: true);
        Single(nt, tag, @"Software\" + NTCV + @"\Windows", "Run", C.RunKeys, user, r, chk, hkcu: true);
        Single(nt, tag, @"Software\" + NTCV + @"\Winlogon", "Shell", C.Winlogon, user, r, chk, hkcu: true, flagAny: true);
        Single(nt, tag, @"Environment", "UserInitMprLogonScript", C.LogonScripts, user, r, chk, hkcu: true, flagAny: true);
        Single(nt, tag, @"Software\Microsoft\Command Processor", "AutoRun", C.CommandProcessor, user, r, chk, hkcu: true);
        Single(nt, tag, @"Control Panel\Desktop", "SCRNSAVE.EXE", C.Screensaver, user, r, chk, hkcu: true,
            note: k => "ScreenSaveActive=" + (Str(k.Value("ScreenSaveActive")) ?? "-"));
        ComServers(nt, tag, @"Software\Classes\CLSID", user, r, chk, software);
        UserExtra(nt, tag, user, r, chk, software);
    }

    public static void FromUserClasses(IRegistryHive usrClass, string user, PersistenceResult r, OfflineChecker? chk = null, IRegistryHive? software = null)
    {
        ComServers(usrClass, $"UsrClass.dat[{user}]", "CLSID", user, r, chk, software);
        ImageHijackClasses(usrClass, $"UsrClass.dat[{user}]", "", @"HKCU\Software\Classes\", user, r, chk);
        // Per-user shell handlers: the user's own CLSID registrations first (64-bit, then 32-bit), then the machine's.
        string? Resolve(string clsid) => new[] { @"CLSID\", @"Wow6432Node\CLSID\" }
            .Select(root => usrClass.GetKey(root + clsid))
            .Select(k => k == null ? null : Str(k.SubKey("InprocServer32")?.DefaultValue) ?? Str(k.SubKey("LocalServer32")?.DefaultValue))
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? (software != null ? ClsidServer(software, clsid, "") : null);
        ShellHandlers(usrClass, r, chk, Resolve, $"UsrClass.dat[{user}]", @"HKCU\Software\Classes\", "", user);
    }

    /// <summary>Per-user COM servers (HKCU\Software\Classes\CLSID): COM hijack candidates; flagged when they shadow an HKLM class.</summary>
    private static void ComServers(IRegistryHive hive, string tag, string clsidKey, string user, PersistenceResult r, OfflineChecker? chk, IRegistryHive? software)
    {
        var root = hive.GetKey(clsidKey);
        if (root == null) return;
        foreach (var c in root.SubKeys)
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                var sk = c.SubKey(server);
                var path = Str(sk?.DefaultValue);
                if (sk == null || string.IsNullOrWhiteSpace(path)) continue;
                var e = new PersistenceEntry
                {
                    Category = C.ComHijack, Location = @"HKCU\Software\Classes\CLSID", EntryName = c.Name + " " + server,
                    Command = path!, ImagePath = server == "LocalServer32" ? H.ImageFromCommand(path) : path!, User = user,
                    Source = $"{tag}\\{clsidKey}\\{c.Name}\\{server}", KeyLastWriteUtc = sk.LastWriteUtc ?? c.LastWriteUtc,
                    Notes = Str(c.DefaultValue) is { Length: > 0 } n ? n : "",
                };
                if (software?.GetKey(@"Classes\CLSID\" + c.Name) != null) e.Flag(H.ComShadowsHklm);
                Add(r, e, chk);
            }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Approved(Dictionary<string, (bool Enabled, DateTime? DisabledUtc)> Run, Dictionary<string, (bool Enabled, DateTime? DisabledUtc)> Run32,
        Dictionary<string, (bool Enabled, DateTime? DisabledUtc)> StartupFolder);

    /// <summary>Explorer\StartupApproved\Run|Run32|StartupFolder: first byte even = enabled, odd = disabled (+ FILETIME of the change at 4..12).</summary>
    private static Approved StartupApproved(IRegistryHive hive, string key)
    {
        Dictionary<string, (bool, DateTime?)> Read(string sub)
        {
            var d = new Dictionary<string, (bool, DateTime?)>(StringComparer.OrdinalIgnoreCase);
            var k = hive.GetKey(key + "\\" + sub);
            if (k == null) return d;
            foreach (var v in k.Values)
            {
                if (v.Raw.Length < 1) continue;
                bool enabled = (v.Raw[0] & 1) == 0;
                DateTime? t = null;
                if (!enabled && v.Raw.Length >= 12) t = FileTime(BitConverter.ToInt64(v.Raw, 4));
                d[v.Name] = (enabled, t);
            }
            return d;
        }
        return new Approved(Read("Run"), Read("Run32"), Read("StartupFolder"));
    }

    /// <summary>StartupApproved\StartupFolder state for a user's (or ProgramData's) startup folder file.</summary>
    public static (bool Enabled, DateTime? DisabledUtc)? StartupFolderState(IRegistryHive? hive, bool hkcu, string fileName)
    {
        if (hive == null) return null;
        var a = StartupApproved(hive, (hkcu ? @"Software\" : "") + CV + @"\Explorer\StartupApproved");
        return a.StartupFolder.TryGetValue(fileName, out var s) ? s : null;
    }

    public static DateTime? FileTime(long ft)
    {
        if (ft <= 0) return null;
        try { var d = DateTime.FromFileTimeUtc(ft); return d.Year is < 1980 or > 2200 ? null : d; } catch { return null; }
    }

    private static void Values(IRegistryHive hive, string tag, string key, string category, string user, PersistenceResult r, OfflineChecker? chk,
        Dictionary<string, (bool Enabled, DateTime? DisabledUtc)>? approved = null, bool hkcu = false, IRegistryHive? resolveClsid = null)
    {
        var k = hive.GetKey(key);
        if (k == null) return;
        foreach (var v in k.Values)
        {
            if (v.Name.Length == 0 && string.IsNullOrEmpty(v.AsString())) continue; // empty default value
            var e = FromValue(category, tag, key, k, v, user, hkcu);
            if (resolveClsid != null)
            {
                var clsid = (v.AsString() ?? "").Trim();
                if (clsid.StartsWith('{') && ClsidServer(resolveClsid, clsid, "") is { } dll) { e.ImagePath = dll; e.Notes = "CLSID " + clsid; e.Command = dll; }
                else if (v.Name.StartsWith('{') && ClsidServer(resolveClsid, v.Name, "") is { } dll2) { e.ImagePath = dll2; e.Notes = "CLSID " + v.Name; e.Command = dll2; }
            }
            if (approved != null && approved.TryGetValue(v.Name, out var st))
            {
                e.Enabled = st.Enabled ? "Yes" : "No";
                if (!st.Enabled) e.Notes = Join(e.Notes, "Disabled in StartupApproved" + (st.DisabledUtc is { } t ? $" at {t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC" : ""));
            }
            Add(r, e, chk);
        }
    }

    private static void Single(IRegistryHive hive, string tag, string key, string value, string category, string user, PersistenceResult r, OfflineChecker? chk,
        string? def = null, bool hkcu = false, bool flagAny = false, Func<RegKey, string>? note = null)
    {
        var k = hive.GetKey(key);
        if (k?.Value(value) is not { } v) return;
        var s = v.AsString();
        if (string.IsNullOrWhiteSpace(s) && def is null or "") return;
        var e = FromValue(category, tag, key, k, v, user, hkcu);
        if ((def != null && !SameDefault(s, def)) || (flagAny && !string.IsNullOrWhiteSpace(s))) e.Flag(H.NonDefaultValue);
        if (note != null) e.Notes = Join(e.Notes, note(k));
        Add(r, e, chk);
    }

    private static PersistenceEntry FromValue(string category, string tag, string key, RegKey k, RegValue v, string user, bool hkcu = false, string? entryName = null)
    {
        var data = v.AsString() ?? (v.Raw.Length > 0 ? "0x" + v.Hex() : "");
        var hive = tag.StartsWith("NTUSER", StringComparison.OrdinalIgnoreCase) || hkcu ? "HKCU" : "HKLM\\" + tag;
        return new PersistenceEntry
        {
            Category = category,
            Location = hive + "\\" + key,
            EntryName = entryName ?? (v.Name.Length == 0 ? "(default)" : v.Name),
            Command = data,
            ImagePath = H.ImageFromCommand(data),
            User = user,
            Source = $"{tag}\\{key}\\{(v.Name.Length == 0 ? "(default)" : v.Name)}",
            KeyLastWriteUtc = k.LastWriteUtc,
        };
    }

    private static void Add(PersistenceResult r, PersistenceEntry e, OfflineChecker? chk)
    {
        H.Apply(e, chk);
        r.Entries.Add(e);
    }

    /// <summary>
    /// Where the 64-bit (<paramref name="wow"/> empty) or 32-bit classes live in an offline SOFTWARE hive. The 32-bit classes are
    /// physically under <c>Classes\Wow6432Node</c>; <c>WOW6432Node\Classes</c> is only a registry link (not followed offline), kept as a
    /// fallback for hives where it holds real keys.
    /// </summary>
    internal static string[] ClassesRoots(string wow) => wow.Length == 0 ? new[] { @"Classes\" } : new[] { @"Classes\Wow6432Node\", @"WOW6432Node\Classes\" };

    /// <summary>InprocServer32 (or LocalServer32) of a CLSID in SOFTWARE\Classes, or the 32-bit classes (see <see cref="ClassesRoots"/>).</summary>
    public static string? ClsidServer(IRegistryHive software, string clsid, string wow)
    {
        var roots = wow.Length > 0 ? ClassesRoots(wow).Concat(ClassesRoots("")) : ClassesRoots("").Concat(ClassesRoots("WOW6432Node\\"));
        foreach (var root in roots.Select(x => x + @"CLSID\"))
        {
            var k = software.GetKey(root + clsid);
            if (k == null) continue;
            var s = Str(k.SubKey("InprocServer32")?.DefaultValue) ?? Str(k.SubKey("LocalServer32")?.DefaultValue);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    /// <summary>Shell Extensions\Approved: hundreds of entries, almost all Windows'. Lists only those whose server is outside the Windows
    /// folder, plus one summary row with the totals.</summary>
    private static void ApprovedShellExtensions(IRegistryHive sw, PersistenceResult r, OfflineChecker? chk)
    {
        var key = CV + @"\Shell Extensions\Approved";
        var k = sw.GetKey(key);
        if (k == null) return;
        int total = 0, windows = 0, listed = 0;
        foreach (var v in k.Values)
        {
            if (!v.Name.StartsWith('{')) continue;
            total++;
            var dll = ClsidServer(sw, v.Name, "");
            if (dll == null || IsWindowsPath(dll)) { windows++; continue; }
            listed++;
            var e = FromValue(C.Explorer, "SOFTWARE", key, k, v, "");
            e.EntryName = $"{v.AsString()} {v.Name} (shell extension)";
            e.Command = dll;
            e.ImagePath = dll;
            e.Notes = "CLSID " + v.Name;
            Add(r, e, chk);
        }
        r.Entries.Add(new PersistenceEntry
        {
            Category = C.Explorer, Location = "HKLM\\SOFTWARE\\" + key, EntryName = "(summary)",
            Command = $"{total} approved shell extensions: {listed} outside the Windows folder listed, {windows} Windows or unresolved not listed",
            Source = "SOFTWARE\\" + key, KeyLastWriteUtc = k.LastWriteUtc,
        });
    }

    private static bool IsWindowsPath(string p)
    {
        var s = p.Trim('"').TrimStart('\\');
        return s.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase) || s.StartsWith("%windir%", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) || s.StartsWith("SystemRoot", StringComparison.OrdinalIgnoreCase)
            || (!s.Contains('\\') && !s.Contains('/')); // bare DLL name: loaded from System32
    }

    private static IEnumerable<string> SplitList(string s) =>
        s.Split(new[] { ',', ' ', ';', '\0' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => x.Length > 0);

    private static bool SameDefault(string? actual, string def) =>
        string.Equals(Normalize(actual), Normalize(def), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? s) => (s ?? "").Trim().TrimEnd(',').Replace("\"", "").Replace(@"C:\WINDOWS", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase).Trim();

    private static string? Str(RegValue? v) => v?.AsString() is { } s ? s.Trim() : null;

    private static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "; " + b;
}
