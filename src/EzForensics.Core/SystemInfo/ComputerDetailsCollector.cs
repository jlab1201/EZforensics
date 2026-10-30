using System.Globalization;
using System.Text;
using EzForensics.Core.Triage;

namespace EzForensics.Core.SystemInfo;

/// <summary>
/// Reads the identity of a Windows installation (computer name, OS, time zone, network, disks, users...) from its offline
/// SYSTEM / SOFTWARE / SAM hives, best effort: every field records the hive\key\value it came from, and unknown values are "-".
/// The evidence is only read (see <see cref="ZimmermanHiveOpener"/>).
/// </summary>
public sealed class ComputerDetailsCollector
{
    public const string Unknown = "-";
    public const string MemoryUnknown = "- (not recorded offline; available from a memory image)";

    public const string CatSystem = "System", CatOs = "Operating system", CatTime = "Time", CatHardware = "Hardware",
        CatMemory = "Memory", CatNetwork = "Network", CatKnownNetworks = "Known networks", CatDisks = "Disks and volumes",
        CatStorage = "Storage devices", CatUsers = "User accounts", CatProfiles = "User profiles";

    private readonly IHiveOpener _opener;

    public ComputerDetailsCollector(IHiveOpener? opener = null) => _opener = opener ?? new ZimmermanHiveOpener();

    /// <summary>More fact sources (hook for e.g. Volatility on a memory image).</summary>
    public List<IComputerDetailsProvider> Providers { get; } = new();

    /// <summary>System roots of a source: <see cref="SourceLayoutDetector"/>, or the source itself when it holds <c>Windows\System32\config</c>.</summary>
    public static IReadOnlyList<string> FindSystemRoots(string source)
    {
        var roots = new List<string>();
        try
        {
            foreach (var r in SourceLayoutDetector.Detect(source).SystemRoots)
                if (HivePath(r, "SYSTEM") != null || HivePath(r, "SOFTWARE") != null) roots.Add(r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (roots.Count == 0 && (HivePath(source, "SYSTEM") != null || HivePath(source, "SOFTWARE") != null)) roots.Add(source);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? HivePath(string systemRoot, string hive)
    {
        var p = Path.Combine(systemRoot, "Windows", "System32", "config", hive);
        return File.Exists(p) ? p : null;
    }

    public ComputerDetailsReport Collect(string source, Action<string>? log = null, CancellationToken ct = default)
    {
        var report = new ComputerDetailsReport { Source = source };
        var roots = FindSystemRoots(source);
        if (roots.Count == 0)
        {
            report.Notes.Add($"No Windows system root with registry hives (Windows\\System32\\config\\SYSTEM or SOFTWARE) was found under {source}.");
            log?.Invoke(report.Notes[^1]);
            return report;
        }
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke("System root: " + root);
            report.Systems.Add(CollectSystem(root, log, ct));
        }
        return report;
    }

    public SystemDetails CollectSystem(string systemRoot, Action<string>? log = null, CancellationToken ct = default)
    {
        var opened = new List<IRegistryHive>();
        var notes = new List<string>();
        IRegistryHive? Open(string name)
        {
            var p = HivePath(systemRoot, name);
            if (p == null) { notes.Add($"{name} hive not found under {systemRoot}\\Windows\\System32\\config."); return null; }
            try
            {
                var h = _opener.Open(p, name);
                opened.Add(h);
                log?.Invoke($"Read {name}: {p} ({h.Info.Size:N0} bytes, SHA-256 {h.Info.Sha256})" + (h.Info.Note != null ? " - " + h.Info.Note : ""));
                return h;
            }
            catch (Exception ex)
            {
                notes.Add($"{name} hive could not be read: {ex.Message}");
                log?.Invoke(notes[^1]);
                return null;
            }
        }
        try
        {
            var system = Open("SYSTEM");
            ct.ThrowIfCancellationRequested();
            var software = Open("SOFTWARE");
            ct.ThrowIfCancellationRequested();
            var sam = HivePath(systemRoot, "SAM") != null ? Open("SAM") : null;
            var details = Extract(systemRoot, system, software, sam, FileFacts.Read(systemRoot));
            details.Notes.InsertRange(0, notes);
            foreach (var p in Providers) p.Contribute(details, systemRoot, ct);
            return details;
        }
        finally
        {
            foreach (var h in opened) h.Dispose();
        }
    }

    // ------------------------------------------------------------------ extraction (pure; unit-tested with in-memory hives)

    public static SystemDetails Extract(string systemRoot, IRegistryHive? system, IRegistryHive? software, IRegistryHive? sam, FileFacts? files = null)
    {
        var d = new SystemDetails { SystemRoot = systemRoot };
        var f = d.Fields;
        foreach (var h in new[] { system, software, sam }) if (h != null) { d.Hives.Add(h.Info); if (h.Info.Note != null) d.Notes.Add($"{h.Info.Name}: {h.Info.Note}"); }
        files ??= new FileFacts();

        // ---- SYSTEM: control set
        string? cs = null;
        string csSource = "SYSTEM\\Select\\Current";
        if (system != null)
        {
            var cur = system.GetKey("Select")?.Value("Current")?.AsNumber();
            if (cur is > 0 and < 1000 && system.GetKey($"ControlSet{cur:000}") != null) cs = $"ControlSet{cur:000}";   // Select\Current may name a set the hive does not hold
            else if (system.GetKey("ControlSet001") != null) { cs = "ControlSet001"; csSource = "SYSTEM\\ControlSet001 (Select\\Current missing)"; }
            else if (system.GetKey("CurrentControlSet") != null) { cs = "CurrentControlSet"; csSource = "SYSTEM\\CurrentControlSet"; }
        }
        RegKey? Sys(string rel) => system == null || cs == null ? null : system.GetKey(cs + "\\" + rel);
        string SysSrc(string rel, string value) => $"SYSTEM\\{cs ?? "ControlSet00x"}\\{rel}\\{value}";
        RegKey? Soft(string rel) => software?.GetKey(rel);
        const string cv = @"Microsoft\Windows NT\CurrentVersion";

        // ---- System
        var computerName = Str(Sys(@"Control\ComputerName\ComputerName"), "ComputerName");
        var tcpip = Sys(@"Services\Tcpip\Parameters");
        var hostname = Str(tcpip, "Hostname") ?? Str(tcpip, "NV Hostname");
        var domain = Str(tcpip, "Domain") ?? Str(tcpip, "NV Domain");
        var dhcpDomain = Str(tcpip, "DhcpDomain");
        Add(f, CatSystem, "Computer name", computerName, SysSrc(@"Control\ComputerName\ComputerName", "ComputerName"));
        Add(f, CatSystem, "Host name (TCP/IP)", hostname, SysSrc(@"Services\Tcpip\Parameters", Str(tcpip, "Hostname") != null ? "Hostname" : "NV Hostname"));
        Add(f, CatSystem, "Domain (TCP/IP)", domain, SysSrc(@"Services\Tcpip\Parameters", Str(tcpip, "Domain") != null ? "Domain" : "NV Domain"));
        if (dhcpDomain != null) Add(f, CatSystem, "DHCP domain", dhcpDomain, SysSrc(@"Services\Tcpip\Parameters", "DhcpDomain"));
        var winlogon = Soft(cv + @"\Winlogon");
        var defDomain = Str(winlogon, "DefaultDomainName");
        Add(f, CatSystem, "Domain / workgroup (Winlogon)", defDomain ?? Str(winlogon, "CachePrimaryDomain"),
            "SOFTWARE\\" + cv + "\\Winlogon\\" + (defDomain != null ? "DefaultDomainName" : "CachePrimaryDomain"));
        Add(f, CatSystem, "Default user name (Winlogon)", Str(winlogon, "DefaultUserName"), "SOFTWARE\\" + cv + "\\Winlogon\\DefaultUserName");
        var logonUi = Soft(@"Microsoft\Windows\CurrentVersion\Authentication\LogonUI");
        if (logonUi != null)
            Add(f, CatSystem, "Last logged-on user", Str(logonUi, "LastLoggedOnUser"), @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\LastLoggedOnUser");
        Add(f, CatSystem, "Current control set", cs, csSource);
        Add(f, CatSystem, "System root (evidence)", systemRoot, "file system");

        // ---- Operating system
        var cvk = Soft(cv);
        var product = Str(cvk, "ProductName");
        var edition = Str(cvk, "EditionID");
        var displayVersion = Str(cvk, "DisplayVersion");
        var releaseId = Str(cvk, "ReleaseId");
        var build = Str(cvk, "CurrentBuild") ?? Str(cvk, "CurrentBuildNumber");
        var ubr = Num(cvk, "UBR");
        var csd = Str(cvk, "CSDVersion");
        string CvSrc(string v) => "SOFTWARE\\" + cv + "\\" + v;
        var osName = DeriveOsName(product, build);
        Add(f, CatOs, "Product name", product, CvSrc("ProductName"));
        if (osName != null && osName != product)
            Add(f, CatOs, "Operating system (derived)", osName, CvSrc("ProductName") + " + CurrentBuild (build 22000 and later is Windows 11; the registry keeps \"Windows 10\")");
        Add(f, CatOs, "Edition", edition, CvSrc("EditionID"));
        Add(f, CatOs, "Version", displayVersion ?? releaseId, CvSrc(displayVersion != null ? "DisplayVersion" : "ReleaseId"));
        Add(f, CatOs, "Build", build == null ? null : ubr != null ? $"{build}.{ubr}" : build, CvSrc("CurrentBuild") + (ubr != null ? " + UBR" : ""));
        if (csd != null) Add(f, CatOs, "Service pack", csd, CvSrc("CSDVersion"));
        Add(f, CatOs, "NT version", Str(cvk, "CurrentVersion"), CvSrc("CurrentVersion"));
        Add(f, CatOs, "Build lab", Str(cvk, "BuildLabEx") ?? Str(cvk, "BuildLab"), CvSrc(Str(cvk, "BuildLabEx") != null ? "BuildLabEx" : "BuildLab"));
        Add(f, CatOs, "Install date (UTC)", Unix(Num(cvk, "InstallDate")), CvSrc("InstallDate"));
        if (cvk?.Value("InstallTime") is { } it)
            Add(f, CatOs, "Install time (UTC)", FileTime(it.AsNumber()), CvSrc("InstallTime"));
        Add(f, CatOs, "Registered owner", Str(cvk, "RegisteredOwner"), CvSrc("RegisteredOwner"));
        Add(f, CatOs, "Registered organization", Str(cvk, "RegisteredOrganization"), CvSrc("RegisteredOrganization"));
        Add(f, CatOs, "Product ID", Str(cvk, "ProductId"), CvSrc("ProductId"));
        Add(f, CatOs, "System root", Str(cvk, "SystemRoot") ?? Str(cvk, "PathName"), CvSrc(Str(cvk, "SystemRoot") != null ? "SystemRoot" : "PathName"));
        var env = Sys(@"Control\Session Manager\Environment");
        string EnvSrc(string v) => SysSrc(@"Control\Session Manager\Environment", v);
        Add(f, CatOs, "Architecture", Arch(Str(env, "PROCESSOR_ARCHITECTURE")), EnvSrc("PROCESSOR_ARCHITECTURE"));

        // ---- Time
        var tz = Sys(@"Control\TimeZoneInformation");
        string TzSrc(string v) => SysSrc(@"Control\TimeZoneInformation", v);
        var tzName = Str(tz, "TimeZoneKeyName");
        Add(f, CatTime, "Time zone", tzName ?? Str(tz, "StandardName"), TzSrc(tzName != null ? "TimeZoneKeyName" : "StandardName"));
        Add(f, CatTime, "UTC offset (standard)", Bias(Num(tz, "Bias")), TzSrc("Bias"));
        if (tz?.Value("ActiveTimeBias") != null) Add(f, CatTime, "UTC offset (active at last update)", Bias(Num(tz, "ActiveTimeBias")), TzSrc("ActiveTimeBias"));
        if (tz?.Value("DaylightBias") != null)
        {
            var db = Num(tz, "DaylightBias");
            var dst = Num(tz, "DynamicDaylightTimeDisabled");
            var dstText = db == null ? null : $"daylight bias {(int)(uint)db.Value} min" + (dst == 1 ? " · automatic DST adjustment disabled" : dst == 0 ? " · automatic DST adjustment on" : "");
            Add(f, CatTime, "Daylight saving", dstText, TzSrc("DaylightBias") + " / DynamicDaylightTimeDisabled");
        }
        var shutdown = Sys(@"Control\Windows")?.Value("ShutdownTime");
        Add(f, CatTime, "Last shutdown (UTC)", shutdown == null ? null : FileTime(shutdown.AsNumber()), SysSrc(@"Control\Windows", "ShutdownTime"));

        // ---- Hardware
        Add(f, CatHardware, "Processor", Str(env, "PROCESSOR_IDENTIFIER"), EnvSrc("PROCESSOR_IDENTIFIER"));
        Add(f, CatHardware, "Logical processors", Str(env, "NUMBER_OF_PROCESSORS"), EnvSrc("NUMBER_OF_PROCESSORS"));
        AddHardwareIdentity(f, system, cs);

        // ---- Memory (no offline registry value holds installed RAM: HARDWARE is volatile)
        Add(f, CatMemory, "Installed memory", MemoryUnknown, "HKLM\\HARDWARE is built at boot and not saved; use a memory image (e.g. Volatility windows.info)");
        var mm = Sys(@"Control\Session Manager\Memory Management");
        string MmSrc(string v) => SysSrc(@"Control\Session Manager\Memory Management", v);
        if (mm?.Value("PagingFiles") is { } pf) Add(f, CatMemory, "Paging files (configured)", Join(pf.AsMulti()), MmSrc("PagingFiles"));
        if (mm?.Value("ExistingPageFiles") is { } epf) Add(f, CatMemory, "Paging files (existing at last boot)", Join(epf.AsMulti()), MmSrc("ExistingPageFiles"));
        if (files.HiberfilSize is { } hs)
            Add(f, CatMemory, "hiberfil.sys size", $"{FormatBytes(hs)} (a fraction of RAM: 75% by default on Windows 7, 40% on Windows 8 and later; reduced type 20%)", Path.Combine(systemRoot, "hiberfil.sys"));
        if (files.PagefileSize is { } ps) Add(f, CatMemory, "pagefile.sys size", FormatBytes(ps), Path.Combine(systemRoot, "pagefile.sys"));
        if (files.SwapfileSize is { } ss) Add(f, CatMemory, "swapfile.sys size", FormatBytes(ss), Path.Combine(systemRoot, "swapfile.sys"));

        // ---- Network interfaces
        var ips = new List<string>();
        var ifaces = Sys(InterfaceAddresses.InterfacesKey);
        if (ifaces != null)
        {
            var cards = Soft(cv + @"\NetworkCards");
            var withIps = InterfaceAddresses.Read(ifaces, out int skipped);
            foreach (var iface in withIps)
            {
                var k = iface.Key;
                var rel = InterfaceAddresses.InterfacesKey + "\\" + k.Name;
                string S(string v) => SysSrc(rel, v);
                bool dhcp = Num(k, "EnableDHCP") == 1;
                var staticIps = iface.StaticIps;
                var dhcpIp = iface.DhcpIp;
                string? desc = null, descSrc = null;
                if (cards != null)
                    foreach (var c in cards.SubKeys)
                        if (string.Equals(Str(c, "ServiceName"), k.Name, StringComparison.OrdinalIgnoreCase))
                        { desc = Str(c, "Description"); descSrc = $"SOFTWARE\\{cv}\\NetworkCards\\{c.Name}\\Description"; break; }
                var connRel = $@"Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{k.Name}\Connection";
                var connName = Str(Sys(connRel), "Name");
                var cat = $"{CatNetwork}: " + (desc ?? connName ?? k.Name);
                Add(f, cat, "Interface GUID", k.Name, SysSrc(@"Services\Tcpip\Parameters\Interfaces", k.Name));
                if (desc != null) Add(f, cat, "Adapter", desc, descSrc!);
                if (connName != null) Add(f, cat, "Connection name", connName, SysSrc(connRel, "Name"));
                Add(f, cat, "DHCP enabled", dhcp ? "Yes" : "No", S("EnableDHCP"));
                if (staticIps.Count > 0)
                {
                    Add(f, cat, "IP address (static)", Join(staticIps), S("IPAddress"));
                    Add(f, cat, "Subnet mask", Join(k.Value("SubnetMask")?.AsMulti() ?? Array.Empty<string>()), S("SubnetMask"));
                    var gw = k.Value("DefaultGateway")?.AsMulti() ?? Array.Empty<string>();
                    if (gw.Length > 0) Add(f, cat, "Default gateway", Join(gw), S("DefaultGateway"));
                    ips.AddRange(staticIps);
                }
                if (dhcpIp != null)
                {
                    Add(f, cat, "IP address (DHCP)", dhcpIp, S("DhcpIPAddress"));
                    Add(f, cat, "Subnet mask (DHCP)", Str(k, "DhcpSubnetMask"), S("DhcpSubnetMask"));
                    var gw = k.Value("DhcpDefaultGateway")?.AsMulti() ?? Array.Empty<string>();
                    if (gw.Length > 0) Add(f, cat, "Default gateway (DHCP)", Join(gw), S("DhcpDefaultGateway"));
                    Add(f, cat, "DHCP server", Str(k, "DhcpServer"), S("DhcpServer"));
                    Add(f, cat, "Lease obtained (UTC)", Unix(Num(k, "LeaseObtainedTime")), S("LeaseObtainedTime"));
                    Add(f, cat, "Lease terminates (UTC)", Unix(Num(k, "LeaseTerminatesTime")), S("LeaseTerminatesTime"));
                    ips.Add(dhcpIp);
                }
                var dns = Str(k, "NameServer");
                var dnsSrc = "NameServer";
                if (string.IsNullOrWhiteSpace(dns)) { dns = Str(k, "DhcpNameServer"); dnsSrc = "DhcpNameServer"; }
                if (!string.IsNullOrWhiteSpace(dns)) Add(f, cat, "DNS servers", Join(dns!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)), S(dnsSrc));
                var dom = Str(k, "Domain");
                if (string.IsNullOrWhiteSpace(dom)) dom = Str(k, "DhcpDomain");
                if (!string.IsNullOrWhiteSpace(dom)) Add(f, cat, "Domain", dom, S(string.IsNullOrWhiteSpace(Str(k, "Domain")) ? "DhcpDomain" : "Domain"));
            }
            if (skipped > 0) d.Notes.Add($"{skipped} network interface(s) without an IPv4 address were not listed ({SysSrc(@"Services\Tcpip\Parameters", "Interfaces")}).");
        }
        else Add(f, CatNetwork, "Interfaces", null, SysSrc(@"Services\Tcpip\Parameters", "Interfaces"));

        // ---- Known networks (SOFTWARE NetworkList profiles; times are local time of the system)
        var profiles = Soft(cv + @"\NetworkList\Profiles");
        if (profiles != null)
            foreach (var p in profiles.SubKeys)
            {
                var name = Str(p, "ProfileName") ?? p.Name;
                var created = SystemTime(p.Value("DateCreated")?.Raw);
                var last = SystemTime(p.Value("DateLastConnected")?.Raw);
                var parts = new List<string>();
                if (created != null) parts.Add("first connected " + created);
                if (last != null) parts.Add("last connected " + last);
                if (Str(p, "Description") is { } desc && desc != name) parts.Add(desc);
                Add(f, CatKnownNetworks, name, parts.Count == 0 ? null : string.Join(" · ", parts) + (created != null || last != null ? " (local time)" : ""),
                    $"SOFTWARE\\{cv}\\NetworkList\\Profiles\\{p.Name}\\ProfileName, DateCreated, DateLastConnected");
            }

        // ---- Disks and volumes
        if (files.VolumeSerial != null) Add(f, CatDisks, "Volume serial (system volume)", files.VolumeSerial, files.BootPath + " (offset 0x48)");
        var mounted = system?.GetKey("MountedDevices");
        if (mounted != null)
        {
            int volumes = 0;
            foreach (var v in mounted.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (v.Name.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase))
                    Add(f, CatDisks, v.Name[@"\DosDevices\".Length..], DecodeMountedDevice(v.Raw), @"SYSTEM\MountedDevices\" + v.Name);
                else volumes++;
            }
            if (volumes > 0) Add(f, CatDisks, "Volume GUIDs seen", volumes.ToString(CultureInfo.InvariantCulture), @"SYSTEM\MountedDevices\\??\Volume{...}");
        }
        foreach (var (bus, label) in new[] { ("USBSTOR", "USB"), ("SCSI", "SCSI/SATA/NVMe"), ("IDE", "IDE") })
        {
            var en = Sys(@"Enum\" + bus);
            if (en == null) continue;
            foreach (var dev in en.SubKeys)
            {
                if (bus != "USBSTOR" && !dev.Name.StartsWith("Disk", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var inst in dev.SubKeys)
                {
                    var fn = Str(inst, "FriendlyName") ?? dev.Name;
                    Add(f, CatStorage, $"{label}: {fn}", $"{dev.Name} · serial/instance {inst.Name}" + (inst.LastWriteUtc is { } lw ? $" · key last written {Fmt(lw)}" : ""),
                        SysSrc($@"Enum\{bus}\{dev.Name}\{inst.Name}", "FriendlyName"));
                }
            }
        }

        // ---- Users
        var plist = Soft(cv + @"\ProfileList");
        if (plist != null)
            foreach (var p in plist.SubKeys)
                Add(f, CatProfiles, p.Name, Str(p, "ProfileImagePath"), $"SOFTWARE\\{cv}\\ProfileList\\{p.Name}\\ProfileImagePath");
        AddSamUsers(f, sam);

        // ---- summary
        var title = computerName ?? hostname ?? Path.GetFileName(systemRoot.TrimEnd('\\', '/'));
        d.Title = string.IsNullOrEmpty(title) ? systemRoot : title;
        var os = osName ?? product;
        var ver = displayVersion ?? releaseId;
        var osPart = os == null ? null
            : os + (ver != null ? " " + ver : csd != null ? " " + ShortSp(csd) : "") + (build != null ? $" ({build}{(ubr != null ? "." + ubr : "")})" : "");
        var ipList = ips.Distinct().ToList();
        var ipPart = ipList.Count == 0 ? null : string.Join(", ", ipList.Take(2)) + (ipList.Count > 2 ? $" +{ipList.Count - 2}" : "");
        d.Summary = string.Join(" · ", new[] { computerName ?? hostname ?? Unknown, osPart ?? Unknown, ipPart ?? Unknown });
        return d;
    }

    /// <summary>Manufacturer / model / BIOS (Control\SystemInformation, else HardwareConfig\Current) and the machine type classification.</summary>
    private static void AddHardwareIdentity(List<DetailField> f, IRegistryHive? system, string? cs)
    {
        if (system == null) return;
        var input = BuildMachineTypeInput(system, cs);
        var src = input.HardwareSource;
        Add(f, CatHardware, "Manufacturer", input.Manufacturer, src + @"\SystemManufacturer");
        Add(f, CatHardware, "Model", input.Model, src + @"\SystemProductName");
        Add(f, CatHardware, "BIOS version", input.BiosVersion, src + @"\BIOSVersion");
        Add(f, CatHardware, "BIOS date", input.BiosDate, src + @"\BIOSReleaseDate");
        var r = MachineTypeClassifier.Classify(input);
        const string clsSrc = @"classification of SYSTEM Control\SystemInformation, Control\ProductOptions, Services, Enum (see evidence)";
        Add(f, CatHardware, "Machine type", r.Display, clsSrc);
        Add(f, CatHardware, "Hypervisor", r.Hypervisor ?? (r.Type == MachineType.VirtualMachine ? "unidentified" : "none detected"), clsSrc);
        if (r.OsRole != null) Add(f, CatHardware, "OS role", r.OsRole, $@"SYSTEM\{cs ?? "ControlSet00x"}\Control\ProductOptions\ProductType");
        Add(f, CatHardware, "Classification confidence", r.Confidence.ToString(), clsSrc);
        Add(f, CatHardware, "Classification evidence", string.Join("; ", r.Evidence), clsSrc);
    }

    /// <summary>Reads the facts <see cref="MachineTypeClassifier"/> needs from an offline SYSTEM hive (<paramref name="cs"/> = control set key name).</summary>
    public static MachineTypeInput BuildMachineTypeInput(IRegistryHive system, string? cs)
    {
        RegKey? Sys(string rel) => cs == null ? null : system.GetKey(cs + "\\" + rel);
        var csName = cs ?? "ControlSet00x";
        var hw = Sys(@"Control\SystemInformation");
        var hwSrc = $@"SYSTEM\{csName}\Control\SystemInformation";
        if (Str(hw, "SystemManufacturer") == null && Str(hw, "SystemProductName") == null && Str(hw, "BIOSVersion") == null &&
            system.GetKey(@"HardwareConfig\Current") is { } hc)
        { hw = hc; hwSrc = @"SYSTEM\HardwareConfig\Current"; }

        var services = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var svcKey = Sys("Services");
        if (svcKey != null)
            foreach (var k in svcKey.SubKeys) services[k.Name] = Num(k, "Start");

        var devices = new List<string>();
        var en = Sys("Enum");
        if (en != null)
        {
            foreach (var bus in new[] { "PCI", "VMBUS", "XENBUS", "XEN", "SCSI", "IDE" })
                if (en.SubKey(bus) is { } b)
                    foreach (var dev in b.SubKeys) devices.Add(bus + "\\" + dev.Name);
            if (en.SubKey("ACPI") is { } acpi)
                foreach (var dev in acpi.SubKeys)
                {
                    if (dev.SubKeys.Count == 0) devices.Add(@"ACPI\" + dev.Name);
                    foreach (var inst in dev.SubKeys) devices.Add($@"ACPI\{dev.Name}\{inst.Name}");
                }
        }

        var hints = new List<string>();
        const string batteryClass = "{72631E54-78A4-11D0-BCF7-00AA00B7B32A}";
        if (Sys($@"Control\Class\{batteryClass}") is { } bc)
            foreach (var k in bc.SubKeys)
                if (Str(k, "DriverDesc") is { } dd && dd.Contains("Control Method Battery", StringComparison.OrdinalIgnoreCase))
                { hints.Add($@"Battery driver installed: {dd} [SYSTEM\{csName}\Control\Class\{batteryClass}\{k.Name}\DriverDesc]"); break; }
        if (Num(Sys(@"Services\CmBatt\Enum"), "Count") is > 0 and var n)
            hints.Add($@"CmBatt bound to {n} battery device(s) [SYSTEM\{csName}\Services\CmBatt\Enum\Count]");

        return new MachineTypeInput
        {
            Manufacturer = Str(hw, "SystemManufacturer"),
            Model = Str(hw, "SystemProductName"),
            BiosVersion = Str(hw, "BIOSVersion"),
            BiosDate = Str(hw, "BIOSReleaseDate"),
            ProductType = Str(Sys(@"Control\ProductOptions"), "ProductType"),
            Services = services,
            DeviceIds = devices,
            BatteryHints = hints,
            ControlSet = csName,
            HardwareSource = hwSrc,
        };
    }

    /// <summary>The control set Windows booted with last (<c>Select\Current</c>), else ControlSet001 / CurrentControlSet; null when none exists.</summary>
    public static string? ResolveControlSet(IRegistryHive system)
    {
        var cur = system.GetKey("Select")?.Value("Current")?.AsNumber();
        if (cur is > 0 and < 1000 && system.GetKey($"ControlSet{cur:000}") != null) return $"ControlSet{cur:000}";
        if (system.GetKey("ControlSet001") != null) return "ControlSet001";
        if (system.GetKey("CurrentControlSet") != null) return "CurrentControlSet";
        return null;
    }

    private static void AddSamUsers(List<DetailField> f, IRegistryHive? sam)
    {
        var names = sam?.GetKey(@"SAM\Domains\Account\Users\Names");
        if (names == null) return;
        var users = sam!.GetKey(@"SAM\Domains\Account\Users");
        foreach (var n in names.SubKeys)
        {
            uint rid = n.DefaultValue?.Type ?? 0;
            var parts = new List<string> { $"RID {rid}" };
            if (n.LastWriteUtc is { } created) parts.Add("created " + Fmt(created));
            var fk = users?.SubKey(rid.ToString("X8"));
            var fv = fk?.Value("F")?.Raw;
            if (fv is { Length: >= 0x44 })
            {
                var lastLogin = FileTime(BitConverter.ToInt64(fv, 8));
                var pwdSet = FileTime(BitConverter.ToInt64(fv, 24));
                ushort acb = BitConverter.ToUInt16(fv, 56);
                ushort logins = BitConverter.ToUInt16(fv, 66);
                parts.Add("last login " + (lastLogin == Unknown ? "never" : lastLogin));
                if (pwdSet != Unknown) parts.Add("password set " + pwdSet);
                parts.Add($"logins {logins}");
                if ((acb & 0x0001) != 0) parts.Add("disabled");
            }
            f.Add(new DetailField(CatUsers, n.Name, string.Join(" · ", parts),
                $@"SAM\SAM\Domains\Account\Users\Names\{n.Name} (RID = value type; created = key last write)" + (fv != null ? $@" + Users\{rid:X8}\F" : "")));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void Add(List<DetailField> f, string cat, string field, string? value, string source)
        => f.Add(new DetailField(cat, field, string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim(), source));

    private static string? Str(RegKey? k, string name)
    {
        var s = k?.Value(name)?.AsString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static long? Num(RegKey? k, string name) => k?.Value(name)?.AsNumber();

    private static string Join(IEnumerable<string> items)
    {
        var s = string.Join(", ", items.Where(i => !string.IsNullOrWhiteSpace(i)));
        return s.Length == 0 ? Unknown : s;
    }


    public static string Fmt(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Unix(long? seconds)
    {
        if (seconds is not > 0) return Unknown;
        try { return Fmt(DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime); }
        catch (ArgumentOutOfRangeException) { return Unknown; }
    }

    public static string FileTime(long? ft)
    {
        if (ft is not > 0 || ft == long.MaxValue) return Unknown;
        try
        {
            var dt = DateTime.FromFileTimeUtc(ft.Value);
            return dt.Year < 1970 || dt.Year > 2200 ? Unknown : Fmt(dt);
        }
        catch (ArgumentOutOfRangeException) { return Unknown; }
    }

    /// <summary>SYSTEMTIME (16 bytes: year, month, day of week, day, hour, minute, second, ms).</summary>
    public static string? SystemTime(byte[]? b)
    {
        if (b is not { Length: >= 16 }) return null;
        int y = BitConverter.ToUInt16(b, 0), mo = BitConverter.ToUInt16(b, 2), day = BitConverter.ToUInt16(b, 6),
            h = BitConverter.ToUInt16(b, 8), mi = BitConverter.ToUInt16(b, 10), s = BitConverter.ToUInt16(b, 12);
        try { return new DateTime(y, mo, day, h, mi, s).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Bias in minutes west of UTC (registry DWORD, may be negative as unsigned) to "UTC-07:00".</summary>
    public static string? Bias(long? bias)
    {
        if (bias == null) return null;
        int b = unchecked((int)(uint)bias.Value);
        int offset = -b;
        var sign = offset < 0 ? "-" : "+";
        offset = Math.Abs(offset);
        return $"UTC{sign}{offset / 60:00}:{offset % 60:00} (bias {b} min)";
    }

    private static string? Arch(string? a) => a?.ToUpperInvariant() switch
    {
        null => null,
        "AMD64" => "x64 (AMD64)",
        "X86" => "x86",
        "ARM64" => "ARM64",
        "IA64" => "Itanium (IA64)",
        _ => a,
    };

    /// <summary>The registry still says "Windows 10" on Windows 11 (build 22000+).</summary>
    public static string? DeriveOsName(string? product, string? build)
    {
        if (product == null) return null;
        if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b >= 22000 &&
            product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            return "Windows 11" + product["Windows 10".Length..];
        return product;
    }

    private static string ShortSp(string csd) => csd.StartsWith("Service Pack ", StringComparison.OrdinalIgnoreCase) ? "SP" + csd["Service Pack ".Length..] : csd;

    public static string FormatBytes(long n)
    {
        string[] u = { "bytes", "KB", "MB", "GB", "TB" };
        double v = n; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{n.ToString("N0", CultureInfo.InvariantCulture)} bytes" : $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {u[i]} ({n.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
    }

    /// <summary>MountedDevices data: 12 bytes = MBR disk signature + partition offset; "DMIO:ID:" + GUID = GPT/dynamic; else a UTF-16 device path.</summary>
    public static string DecodeMountedDevice(byte[] raw)
    {
        if (raw.Length == 12)
        {
            uint sig = BitConverter.ToUInt32(raw, 0);
            ulong off = BitConverter.ToUInt64(raw, 4);
            return $"MBR disk signature {sig:X8}, partition offset {off.ToString("N0", CultureInfo.InvariantCulture)} bytes";
        }
        if (raw.Length >= 24 && Encoding.ASCII.GetString(raw, 0, 8) == "DMIO:ID:")
            return $"GPT partition GUID {new Guid(raw.AsSpan(8, 16)):B}";
        if (raw.Length >= 4 && raw.Length % 2 == 0)
        {
            var s = Encoding.Unicode.GetString(raw).TrimEnd('\0');
            if (s.Length > 0 && s.All(c => c >= 0x20 && c < 0x7F)) return s;
        }
        return $"{raw.Length} bytes: {Convert.ToHexString(raw.AsSpan(0, Math.Min(raw.Length, 32)))}{(raw.Length > 32 ? "…" : "")}";
    }
}
