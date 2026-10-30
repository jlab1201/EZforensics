using System.Text;
using EzForensics.Core.SystemInfo;
using C = EzForensics.Core.Persistence.PersistenceCategory;
using H = EzForensics.Core.Persistence.PersistenceHeuristics;

namespace EzForensics.Core.Persistence;

/// <summary>
/// The Sysinternals Autoruns locations the first collector did not cover (shell handlers, Internet Explorer, codecs, Office add-ins,
/// Credential Providers, Group Policy scripts, KnownDLLs, Winsock/namespace providers, Network providers, image-hijack file
/// associations). Same pattern as the rest of <see cref="RegistryAseps"/>: pure functions over the offline hive view.
/// </summary>
public static partial class RegistryAseps
{
    private static readonly string[] ShellHandlerKeys =
    {
        @"*\shellex\ContextMenuHandlers", @"AllFilesystemObjects\shellex\ContextMenuHandlers", @"Directory\shellex\ContextMenuHandlers",
        @"Directory\Background\shellex\ContextMenuHandlers", @"Folder\shellex\ContextMenuHandlers", @"Drive\shellex\ContextMenuHandlers",
        @"Directory\shellex\DragDropHandlers", @"Folder\shellex\DragDropHandlers", @"Drive\shellex\DragDropHandlers",
        @"*\shellex\PropertySheetHandlers", @"AllFilesystemObjects\shellex\PropertySheetHandlers", @"Directory\shellex\PropertySheetHandlers",
        @"Folder\shellex\PropertySheetHandlers", @"Directory\shellex\CopyHookHandlers", @"Folder\shellex\CopyHookHandlers",
        @"Folder\shellex\ColumnHandlers",
    };

    private static readonly string[] DirectShowCategories =
    {
        "{083863F1-70DE-11d0-BD40-00A0C911CE86}", // legacy AM filters
        "{33D9A760-90C8-11d0-BD43-00A0C911CE86}", // video compressors
        "{33D9A761-90C8-11d0-BD43-00A0C911CE86}", // audio compressors
        "{DA4E3DA0-D07D-11d0-BD50-00A0C911CE86}", // ActiveMovie filter category
    };

    // ------------------------------------------------------------------ SOFTWARE

    private static void SoftwareExtra(IRegistryHive sw, PersistenceResult r, OfflineChecker? chk)
    {
        foreach (var wow in SoftwareRoots)
        {
            string? Resolve(string clsid) => ClsidServer(sw, clsid, wow);

            // Shell: icon overlays, shared task scheduler, context menu / drag-drop / property sheet / copy hook / column handlers.
            ClsidSubKeys(sw, r, chk, Resolve, C.Explorer, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + CV + @"\Explorer\ShellIconOverlayIdentifiers", "", " (icon overlay)");
            Values(sw, "SOFTWARE", wow + CV + @"\Explorer\SharedTaskScheduler", C.Explorer, "", r, chk, resolveClsid: sw);
            ClsidSubKeys(sw, r, chk, Resolve, C.Explorer, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + CV + @"\Explorer\ShellServiceObjects", "", " (shell service object)");
            foreach (var classes in ClassesRoots(wow))
                ShellHandlers(sw, r, chk, Resolve, "SOFTWARE", "HKLM\\SOFTWARE\\", classes, "");

            // Credential providers (Logon screen).
            foreach (var k in new[] { "Credential Providers", "Credential Provider Filters", "PLAP Providers" })
                ClsidSubKeys(sw, r, chk, Resolve, C.Winlogon, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + CV + @"\Authentication\" + k, "", " (" + k.TrimEnd('s').ToLowerInvariant() + ")");
            Single(sw, "SOFTWARE", wow + NTCV + @"\Winlogon", "UIHost", C.Winlogon, "", r, chk, def: "logonui.exe");

            // Internet Explorer.
            foreach (var k in new[] { CV + @"\Explorer\Toolbar", @"Microsoft\Internet Explorer\Toolbar", @"Microsoft\Internet Explorer\URLSearchHooks" })
                ClsidValues(sw, r, chk, Resolve, C.InternetExplorer, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + k, "", k.EndsWith("URLSearchHooks") ? " (URL search hook)" : " (toolbar)");
            IeExtensions(sw, r, chk, Resolve, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + @"Microsoft\Internet Explorer\Extensions", "");

            // Codecs: Drivers32, DirectShow filters, Media Foundation.
            Values(sw, "SOFTWARE", wow + NTCV + @"\Drivers32", C.Codecs, "", r, chk);
            Codecs(sw, r, chk, Resolve, wow);

            // Office add-ins (COM and VSTO).
            OfficeAddins(sw, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + @"Microsoft\Office", "", r, chk, null, wow);

            // Group Policy scripts (machine startup/shutdown).
            GroupPolicyScripts(sw, "SOFTWARE", "HKLM\\SOFTWARE\\", wow + CV + @"\Group Policy\Scripts", "", r, chk);
            // Group Policy state: State\Machine\Scripts (startup/shutdown) and State\<user SID>\Scripts (logon/logoff of users who
            // signed in on this machine; HKLM keeps them, the user's own hive may not). Profile = the SID for user states.
            var state = sw.GetKey(wow + CV + @"\Group Policy\State");
            if (state != null)
                foreach (var s in state.SubKeys)
                    GroupPolicyScripts(sw, "SOFTWARE", "HKLM\\SOFTWARE\\", $@"{wow}{CV}\Group Policy\State\{s.Name}\Scripts",
                        s.Name.Equals("Machine", StringComparison.OrdinalIgnoreCase) ? "" : s.Name, r, chk);
        }

        // File associations are shared between the 64- and 32-bit views (not redirected), so they are read once.
        ImageHijackClasses(sw, "SOFTWARE", @"Classes\", @"HKLM\SOFTWARE\Classes\", "", r, chk);
    }

    /// <summary>Shell extension handlers (context menu, drag-drop, property sheet, copy hook, column) below one classes root.</summary>
    private static void ShellHandlers(IRegistryHive hive, PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string tag, string locPrefix, string classesRoot, string user)
    {
        foreach (var h in ShellHandlerKeys)
            ClsidSubKeys(hive, r, chk, resolve, C.Explorer, tag, locPrefix, classesRoot + h, user, " (" + HandlerKind(h) + ")");
    }

    private static string HandlerKind(string key) =>
        key.Contains("ContextMenu") ? "context menu handler" : key.Contains("DragDrop") ? "drag-drop handler" : key.Contains("PropertySheet") ? "property sheet handler"
        : key.Contains("CopyHook") ? "copy hook" : "column handler";

    // ------------------------------------------------------------------ SYSTEM

    private static void SystemExtra(IRegistryHive sys, string cs, PersistenceResult r, OfflineChecker? chk)
    {
        // KnownDLLs: DLLs the loader maps from System32 without a path search.
        var kd = sys.GetKey(cs + @"\Control\Session Manager\KnownDLLs");
        if (kd != null)
            foreach (var v in kd.Values)
            {
                if (v.Name.StartsWith("DllDirectory", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(v.AsString())) continue;
                var e = FromValue(C.KnownDlls, "SYSTEM", cs + @"\Control\Session Manager\KnownDLLs", kd, v, "");
                e.ImagePath = v.AsString()!.Trim();
                Add(r, e, chk);
            }

        Winsock(sys, cs, r, chk);

        // Network providers: Control\NetworkProvider\Order\ProviderOrder lists service names; each service gives ProviderPath.
        var order = sys.GetKey(cs + @"\Control\NetworkProvider\Order");
        if (order?.Value("ProviderOrder") is { } po)
            foreach (var name in (po.AsString() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var np = sys.GetKey($@"{cs}\Services\{name}\NetworkProvider");
                if (np?.Value("ProviderPath") is not { } pp || string.IsNullOrWhiteSpace(pp.AsString())) continue;
                var e = FromValue(C.NetworkProviders, "SYSTEM", $@"{cs}\Services\{name}\NetworkProvider", np, pp, "", entryName: Str(np.Value("Name")) ?? name);
                e.Notes = "Service " + name;
                Add(r, e, chk);
            }
    }

    private static void Winsock(IRegistryHive sys, string cs, PersistenceResult r, OfflineChecker? chk)
    {
        var baseKey = cs + @"\Services\WinSock2\Parameters";
        foreach (var entries in new[] { "Catalog_Entries", "Catalog_Entries64" })
        {
            var proto = sys.GetKey($@"{baseKey}\Protocol_Catalog9\{entries}");
            if (proto != null)
                foreach (var s in proto.SubKeys)
                {
                    var raw = s.Value("PackedCatalogItem")?.Raw;
                    if (raw == null || raw.Length < 4) continue;
                    // Layout: CHAR szLibraryPath[MAX_PATH] (ANSI) then WSAPROTOCOL_INFOW, whose WCHAR szProtocol[256] is at +116.
                    var path = AnsiZ(raw, 0, Math.Min(LibraryPathBytes, raw.Length));
                    if (path.Length == 0) continue;
                    var name = raw.Length >= ProtocolNameOffset + 2 ? WideZ(raw, ProtocolNameOffset, Math.Min(512, raw.Length - ProtocolNameOffset)) : "";
                    var key = $@"{baseKey}\Protocol_Catalog9\{entries}\{s.Name}";
                    Add(r, new PersistenceEntry
                    {
                        Category = C.Winsock, Location = "HKLM\\SYSTEM\\" + $@"{baseKey}\Protocol_Catalog9\{entries}", EntryName = name.Length > 0 ? name : s.Name,
                        Command = path, ImagePath = path, Source = "SYSTEM\\" + key + "\\PackedCatalogItem", KeyLastWriteUtc = s.LastWriteUtc,
                        Notes = "Winsock protocol catalog entry " + s.Name,
                    }, chk);
                }
            var ns = sys.GetKey($@"{baseKey}\NameSpace_Catalog5\{entries}");
            if (ns != null)
                foreach (var s in ns.SubKeys)
                {
                    if (s.Value("LibraryPath") is not { } lp || string.IsNullOrWhiteSpace(lp.AsString())) continue;
                    var en = s.Value("Enabled")?.AsNumber();
                    var e = FromValue(C.Winsock, "SYSTEM", $@"{baseKey}\NameSpace_Catalog5\{entries}\{s.Name}", s, lp, "", entryName: Str(s.Value("DisplayString")) ?? s.Name);
                    e.Enabled = en == null ? "" : en == 0 ? "No" : "Yes";
                    e.Notes = "Winsock namespace provider " + s.Name;
                    Add(r, e, chk);
                }
        }
    }

    internal const int LibraryPathBytes = 260, ProtocolNameOffset = LibraryPathBytes + 116;

    private static string AnsiZ(byte[] b, int offset, int count)
    {
        if (count < 1 || offset >= b.Length) return "";
        var z = Array.IndexOf(b, (byte)0, offset, count);
        return Encoding.Latin1.GetString(b, offset, (z >= 0 ? z : offset + count) - offset).Trim();
    }

    private static string WideZ(byte[] b, int offset, int count)
    {
        if (count < 2 || offset >= b.Length) return "";
        var s = Encoding.Unicode.GetString(b, offset, count & ~1);
        var z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    // ------------------------------------------------------------------ NTUSER.DAT

    private static void UserExtra(IRegistryHive nt, string tag, string user, PersistenceResult r, OfflineChecker? chk, IRegistryHive? software)
    {
        string? Resolve(string clsid) => (nt.GetKey(@"Software\Classes\CLSID\" + clsid) is { } k
                ? Str(k.SubKey("InprocServer32")?.DefaultValue) ?? Str(k.SubKey("LocalServer32")?.DefaultValue) : null)
            ?? (software != null ? ClsidServer(software, clsid, "") : null);

        foreach (var k in new[] { @"Software\Microsoft\Internet Explorer\Toolbar", @"Software\Microsoft\Internet Explorer\URLSearchHooks" })
            ClsidValues(nt, r, chk, Resolve, C.InternetExplorer, tag, "HKCU\\", k, user, k.EndsWith("URLSearchHooks") ? " (URL search hook)" : " (toolbar)");
        IeExtensions(nt, r, chk, Resolve, tag, "HKCU\\", @"Software\Microsoft\Internet Explorer\Extensions", user);
        ClsidSubKeys(nt, r, chk, Resolve, C.Explorer, tag, "HKCU\\", @"Software\" + CV + @"\Explorer\ShellIconOverlayIdentifiers", user, " (icon overlay)");
        ClsidSubKeys(nt, r, chk, Resolve, C.Explorer, tag, "HKCU\\", @"Software\" + CV + @"\Explorer\ShellServiceObjects", user, " (shell service object)");
        OfficeAddins(nt, tag, "HKCU\\", @"Software\Microsoft\Office", user, r, chk, software, "");
        OfficeAddins(nt, tag, "HKCU\\", @"Software\WOW6432Node\Microsoft\Office", user, r, chk, software, "WOW6432Node\\");
        GroupPolicyScripts(nt, tag, "HKCU\\", @"Software\" + CV + @"\Group Policy\Scripts", user, r, chk);
        var state = nt.GetKey(@"Software\" + CV + @"\Group Policy\State");
        if (state != null)
            foreach (var s in state.SubKeys)
                GroupPolicyScripts(nt, tag, "HKCU\\", $@"Software\{CV}\Group Policy\State\{s.Name}\Scripts", user, r, chk);
    }

    // ------------------------------------------------------------------ helpers

    private static void ClsidEntry(PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string category, string locPrefix, string tag, string key,
        string clsid, string name, string user, string notes, DateTime? t, bool emitUnresolved = false)
    {
        var server = resolve(clsid);
        if (server == null && !emitUnresolved) return;
        Add(r, new PersistenceEntry
        {
            Category = category, Location = locPrefix + key, EntryName = name, Command = server ?? "", ImagePath = server != null ? H.ImageFromCommand(server) : "",
            User = user, Source = $"{tag}\\{key}\\{clsid}", KeyLastWriteUtc = t, Notes = Join("CLSID " + clsid, notes),
        }, chk);
    }

    /// <summary>Subkeys of <paramref name="key"/> whose name (or default value) is a CLSID: one entry per resolvable handler.</summary>
    private static void ClsidSubKeys(IRegistryHive hive, PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string category, string tag, string locPrefix,
        string key, string user, string suffix)
    {
        var parent = hive.GetKey(key);
        if (parent == null) return;
        foreach (var s in parent.SubKeys)
        {
            var def = Str(s.DefaultValue);
            var clsid = def != null && def.StartsWith('{') ? def : s.Name.StartsWith('{') ? s.Name : null;
            if (clsid == null) continue;
            var label = s.Name.StartsWith('{') ? (def is { Length: > 0 } && !def.StartsWith('{') ? def : s.Name) : s.Name.Trim();
            ClsidEntry(r, chk, resolve, category, locPrefix, tag, key, clsid, label + suffix, user, "", s.LastWriteUtc);
        }
    }

    /// <summary>Values of <paramref name="key"/> named by a CLSID (or holding one): IE toolbars, URL search hooks.</summary>
    private static void ClsidValues(IRegistryHive hive, PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string category, string tag, string locPrefix,
        string key, string user, string suffix)
    {
        var k = hive.GetKey(key);
        if (k == null) return;
        foreach (var v in k.Values)
        {
            var data = v.AsString();
            var clsid = v.Name.StartsWith('{') ? v.Name : data != null && data.StartsWith('{') ? data : null;
            if (clsid == null) continue;
            ClsidEntry(r, chk, resolve, category, locPrefix, tag, key, clsid, clsid + suffix, user, data != null && data != clsid ? data : "", k.LastWriteUtc);
        }
    }

    /// <summary>Internet Explorer Extensions\{GUID}: toolbar buttons / menu items that run an Exec program, a script or a CLSID extension.</summary>
    private static void IeExtensions(IRegistryHive hive, PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string tag, string locPrefix, string key, string user)
    {
        var parent = hive.GetKey(key);
        if (parent == null) return;
        foreach (var s in parent.SubKeys)
        {
            var exec = Str(s.Value("Exec"));
            var script = Str(s.Value("Script"));
            var ext = Str(s.Value("CLSIDExtension")) ?? Str(s.Value("ClsidExtension"));
            var cmd = !string.IsNullOrWhiteSpace(exec) ? exec : !string.IsNullOrWhiteSpace(script) ? script : ext != null ? resolve(ext) : null;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var e = new PersistenceEntry
            {
                Category = C.InternetExplorer, Location = locPrefix + key,
                EntryName = (Str(s.Value("ButtonText")) ?? Str(s.Value("MenuText")) ?? s.Name) + " (IE extension)",
                Command = cmd!, ImagePath = H.ImageFromCommand(cmd), User = user, Source = $"{tag}\\{key}\\{s.Name}", KeyLastWriteUtc = s.LastWriteUtc,
                Notes = "Extension " + s.Name,
            };
            Add(r, e, chk);
        }
    }

    private static void Codecs(IRegistryHive sw, PersistenceResult r, OfflineChecker? chk, Func<string, string?> resolve, string wow)
    {
        foreach (var classes in ClassesRoots(wow))
        {
            // HKCR\Filter\{CLSID}
            ClsidSubKeys(sw, r, chk, resolve, C.Codecs, "SOFTWARE", "HKLM\\SOFTWARE\\", classes + "Filter", "", " (filter)");
            // DirectShow filter categories: CLSID\{category}\Instance\{n} with a CLSID value.
            foreach (var cat in DirectShowCategories)
            {
                var key = $@"{classes}CLSID\{cat}\Instance";
                var inst = sw.GetKey(key);
                if (inst == null) continue;
                foreach (var s in inst.SubKeys)
                {
                    var clsid = Str(s.Value("CLSID")) ?? (s.Name.StartsWith('{') ? s.Name : null);
                    if (clsid == null) continue;
                    ClsidEntry(r, chk, resolve, C.Codecs, "HKLM\\SOFTWARE\\", "SOFTWARE", key, clsid, (Str(s.Value("FriendlyName")) ?? s.Name) + " (DirectShow)", "", "", s.LastWriteUtc);
                }
            }
        }
        // Media Foundation transforms and handlers.
        var mf = wow + @"Microsoft\Windows Media Foundation";
        var tr = sw.GetKey(mf + @"\Transforms");
        if (tr != null)
            foreach (var s in tr.SubKeys.Where(x => x.Name.StartsWith('{')))
                ClsidEntry(r, chk, resolve, C.Codecs, "HKLM\\SOFTWARE\\", "SOFTWARE", mf + @"\Transforms", s.Name, (Str(s.Value("MFTName")) ?? Str(s.DefaultValue) ?? s.Name) + " (Media Foundation transform)", "", "", s.LastWriteUtc);
        foreach (var kind in new[] { "ByteStreamHandlers", "SchemeHandlers" })
        {
            var h = sw.GetKey($@"{mf}\{kind}");
            if (h == null) continue;
            foreach (var ext in h.SubKeys)
                foreach (var v in ext.Values.Where(x => x.Name.StartsWith('{')))
                    ClsidEntry(r, chk, resolve, C.Codecs, "HKLM\\SOFTWARE\\", "SOFTWARE", $@"{mf}\{kind}\{ext.Name}", v.Name,
                        $"{ext.Name} ({kind})", "", v.AsString() ?? "", ext.LastWriteUtc);
        }
    }

    /// <summary>Office add-ins: <c>Office\&lt;app&gt;\Addins\&lt;ProgID&gt;</c> and <c>Office\&lt;version&gt;\&lt;app&gt;\Addins\&lt;ProgID&gt;</c>.</summary>
    private static void OfficeAddins(IRegistryHive hive, string tag, string locPrefix, string root, string user, PersistenceResult r, OfflineChecker? chk, IRegistryHive? software, string wow)
    {
        var office = hive.GetKey(root);
        if (office == null) return;
        var groups = new List<(string Key, RegKey Addins)>();
        foreach (var app in office.SubKeys)
        {
            if (app.SubKey("Addins") is { } a) groups.Add(($@"{root}\{app.Name}\Addins", a));
            foreach (var sub in app.SubKeys)
                if (sub.Name != "Addins" && sub.SubKey("Addins") is { } a2) groups.Add(($@"{root}\{app.Name}\{sub.Name}\Addins", a2));
        }
        foreach (var (key, addins) in groups)
            foreach (var s in addins.SubKeys)
            {
                // ProgID -> CLSID -> server: from the user's own classes first, then HKLM (the SOFTWARE hive is "hive" itself for machine add-ins).
                var sw = software ?? hive;
                string? server = null;
                var clsid = (software != null ? Str(hive.GetKey(@"Software\Classes\" + s.Name + @"\CLSID")?.DefaultValue) : null)
                    ?? ClassesRoots(wow).Append(@"Classes\").Select(c => Str(sw.GetKey(c + s.Name + @"\CLSID")?.DefaultValue)).FirstOrDefault(x => x != null);
                if (clsid != null) server = ClsidServer(sw, clsid, wow);
                var manifest = Str(s.Value("Manifest"));
                var cmd = server ?? manifest ?? "";
                var lb = s.Value("LoadBehavior")?.AsNumber();
                var e = new PersistenceEntry
                {
                    Category = C.OfficeAddins, Location = locPrefix + key, EntryName = Str(s.Value("FriendlyName")) is { Length: > 0 } fn ? fn : s.Name,
                    Command = cmd, ImagePath = server != null ? H.ImageFromCommand(server) : manifest != null ? manifest.Split('|')[0] : "",
                    Enabled = lb == null ? "" : (lb & 1) == 1 ? "Yes" : "No", User = user, Source = $"{tag}\\{key}\\{s.Name}", KeyLastWriteUtc = s.LastWriteUtc,
                    Notes = Join($"ProgID {s.Name}; LoadBehavior={lb?.ToString() ?? "-"}", Str(s.Value("Description")) ?? ""),
                };
                if (cmd.Length == 0) continue;
                Add(r, e, chk);
            }
    }

    /// <summary>Group Policy scripts: any key below <paramref name="root"/> (Startup/Shutdown/Logon/Logoff\gpo\n) with a <c>Script</c> value.</summary>
    private static void GroupPolicyScripts(IRegistryHive hive, string tag, string locPrefix, string root, string user, PersistenceResult r, OfflineChecker? chk)
    {
        var top = hive.GetKey(root);
        if (top == null) return;
        void Walk(RegKey k, string path, int depth)
        {
            if (k.Value("Script") is { } sv && !string.IsNullOrWhiteSpace(sv.AsString()))
            {
                var script = sv.AsString()!.Trim();
                var args = Str(k.Value("Parameters"));
                var rel = path[(root.Length + 1)..];
                Add(r, new PersistenceEntry
                {
                    Category = C.GroupPolicyScripts, Location = locPrefix + path, EntryName = rel.Replace('\\', ' ').Trim(),
                    Command = string.IsNullOrEmpty(args) ? script : script + " " + args, ImagePath = script, User = user,
                    Source = $"{tag}\\{path}\\Script", KeyLastWriteUtc = k.LastWriteUtc, Notes = rel.Split('\\')[0] + " script",
                }, chk);
            }
            if (depth < 4)
                foreach (var s in k.SubKeys) Walk(s, path + "\\" + s.Name, depth + 1);
        }
        foreach (var s in top.SubKeys) Walk(s, root + "\\" + s.Name, 1);
    }

    private static readonly (string Class, string Ext)[] HijackClasses =
    {
        ("exefile", ".exe"), ("comfile", ".com"), ("batfile", ".bat"), ("cmdfile", ".cmd"), ("piffile", ".pif"), ("scrfile", ".scr"), ("htafile", ".hta"),
    };

    /// <summary>Autoruns "Image hijacks" file association checks: the open command of exefile/comfile/... and the .exe/.com/... class mapping.</summary>
    private static void ImageHijackClasses(IRegistryHive hive, string tag, string classesRoot, string locPrefix, string user, PersistenceResult r, OfflineChecker? chk)
    {
        foreach (var (cls, ext) in HijackClasses)
        {
            var key = $@"{classesRoot}{cls}\shell\open\command";
            var k = hive.GetKey(key);
            if (k?.DefaultValue is { } v && !string.IsNullOrWhiteSpace(v.AsString()))
            {
                var cmd = v.AsString()!.Trim();
                var e = FromValue(C.ImageHijacks, tag, key, k, v, user, hkcu: tag.StartsWith("UsrClass", StringComparison.OrdinalIgnoreCase));
                e.Location = locPrefix + $@"{cls}\shell\open\command";
                e.EntryName = cls + @"\shell\open\command";
                if (cmd.StartsWith("\"%1\"") || cmd.StartsWith("%1")) e.ImagePath = "";
                if (cls != "htafile" && !(cmd.StartsWith("\"%1\"") || cmd.StartsWith("%1"))) e.Flag(H.NonDefaultValue);
                if (cls == "htafile" && !cmd.Contains("mshta", StringComparison.OrdinalIgnoreCase)) e.Flag(H.NonDefaultValue);
                Add(r, e, chk);
            }
            var map = hive.GetKey($@"{classesRoot}{ext}");
            var mapped = Str(map?.DefaultValue);
            if (map != null && !string.IsNullOrEmpty(mapped) && !string.Equals(mapped, cls, StringComparison.OrdinalIgnoreCase) && ext != ".hta")
            {
                var e = new PersistenceEntry
                {
                    Category = C.ImageHijacks, Location = locPrefix + ext, EntryName = $"{ext} class mapping", Command = mapped!, User = user,
                    Source = $"{tag}\\{classesRoot}{ext}\\(default)", KeyLastWriteUtc = map.LastWriteUtc, Notes = $"Expected {cls}",
                }.Flag(H.NonDefaultValue);
                Add(r, e, chk);
            }
        }
    }
}
