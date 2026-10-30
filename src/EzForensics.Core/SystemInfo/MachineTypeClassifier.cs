namespace EzForensics.Core.SystemInfo;

/// <summary>What kind of machine the evidence came from.</summary>
public enum MachineType { Unknown, VirtualMachine, Laptop, Desktop, Server }

public enum ClassificationConfidence { Low, Medium, High }

/// <summary>
/// Facts read offline from the registry that hint at the machine type (all optional). <see cref="Services"/> maps a service name
/// (<c>SYSTEM\&lt;ControlSet&gt;\Services\&lt;name&gt;</c>) to its <c>Start</c> value (null when the value is missing).
/// <see cref="DeviceIds"/> are Enum paths relative to <c>Enum</c> (e.g. <c>ACPI\PNP0C0A\1</c>, <c>PCI\VEN_15AD&amp;DEV_0405</c>,
/// <c>VMBUS\{...}</c>, <c>SCSI\Disk&amp;Ven_VMware&amp;Prod_Virtual_disk</c>). <see cref="BatteryHints"/> are free-form, already cited
/// observations that a battery was configured (battery-class drivers, CmBatt bound, power scheme DC settings...).
/// </summary>
public sealed record MachineTypeInput
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? BiosVersion { get; init; }
    public string? BiosDate { get; init; }
    /// <summary><c>Control\ProductOptions\ProductType</c>: WinNT (workstation), ServerNT (server), LanmanNT (domain controller).</summary>
    public string? ProductType { get; init; }
    public IReadOnlyDictionary<string, long?> Services { get; init; } = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> DeviceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BatteryHints { get; init; } = Array.Empty<string>();

    /// <summary>Control set used in evidence citations (e.g. <c>ControlSet001</c>).</summary>
    public string ControlSet { get; init; } = "CurrentControlSet";
    /// <summary>Where Manufacturer / Model / BIOS were read (evidence citation).</summary>
    public string HardwareSource { get; init; } = @"SYSTEM\CurrentControlSet\Control\SystemInformation";
}

/// <summary>
/// Result of <see cref="MachineTypeClassifier.Classify"/>. A virtual machine running a server OS is reported as
/// <see cref="MachineType.VirtualMachine"/> with <see cref="IsServerOs"/> = true (the hypervisor is the stronger fact for triage).
/// </summary>
public sealed record MachineTypeResult(
    MachineType Type,
    string? Hypervisor,
    ClassificationConfidence Confidence,
    bool IsServerOs,
    string? OsRole,
    IReadOnlyList<string> Evidence)
{
    /// <summary>"Virtual machine (VMware, server OS)", "Laptop", "Server (domain controller)", "Unknown".</summary>
    public string Display
    {
        get
        {
            var name = Type switch
            {
                MachineType.VirtualMachine => "Virtual machine",
                MachineType.Laptop => "Laptop",
                MachineType.Desktop => "Desktop",
                MachineType.Server => "Server",
                _ => "Unknown",
            };
            var extra = new List<string>();
            if (Type == MachineType.VirtualMachine && Hypervisor != null) extra.Add(Hypervisor);
            if (IsServerOs && Type != MachineType.Server) extra.Add(OsRole == "Domain controller" ? "domain controller OS" : "server OS");
            if (Type == MachineType.Server && OsRole == "Domain controller") extra.Add("domain controller");
            return extra.Count == 0 ? name : $"{name} ({string.Join(", ", extra)})";
        }
    }
}

/// <summary>
/// Classifies a Windows installation as VM / laptop / desktop / server from offline registry facts. Pure and deterministic.
/// Order: hypervisor signals (strings, guest services, virtual devices) win over everything; then server OS / server hardware;
/// then battery or lid devices (laptop); otherwise desktop (low or medium confidence) or unknown when nothing was readable.
/// </summary>
public static class MachineTypeClassifier
{
    public const string VMware = "VMware", VirtualBox = "VirtualBox", HyperV = "Hyper-V", QemuKvm = "QEMU-KVM", Xen = "Xen", Parallels = "Parallels";

    // (hypervisor, substring) matched case-insensitively in "manufacturer | model | BIOS version".
    private static readonly (string Hv, string Needle)[] HardwareStrings =
    {
        (VMware, "VMware"),
        (VirtualBox, "VirtualBox"), (VirtualBox, "innotek"), (VirtualBox, "VBOX"),
        (QemuKvm, "QEMU"), (QemuKvm, "KVM"), (QemuKvm, "Bochs"), (QemuKvm, "Standard PC (i440FX"), (QemuKvm, "Standard PC (Q35"),
        (QemuKvm, "Google Compute Engine"), (QemuKvm, "Amazon EC2"),
        (Xen, "Xen"), (Xen, "HVM domU"),
        (Parallels, "Parallels"),
    };

    // Guest-only drivers / services (installed by guest tools or used only inside a guest). Start 4 = disabled → ignored.
    private static readonly (string Hv, string Service, bool Strong)[] GuestServices =
    {
        (VMware, "vmhgfs", true), (VMware, "vm3dmp", true), (VMware, "VMTools", true), (VMware, "vmmouse", true), (VMware, "vmusbmouse", true),
        (VMware, "vmvss", true), (VMware, "vmrawdsk", true), (VMware, "vmci", false) /* also on VMware Workstation hosts */,
        (VirtualBox, "VBoxGuest", true), (VirtualBox, "VBoxSF", true), (VirtualBox, "VBoxMouse", true), (VirtualBox, "VBoxVideo", true),
        (VirtualBox, "VBoxWddm", true), (VirtualBox, "VBoxService", true),
        (Xen, "xenvbd", true), (Xen, "xenbus", true), (Xen, "xennet", true), (Xen, "xenfilt", true), (Xen, "xeniface", true),
        (QemuKvm, "viostor", true), (QemuKvm, "vioscsi", true), (QemuKvm, "netkvm", true), (QemuKvm, "vioser", true), (QemuKvm, "QEMU-GA", true),
        (QemuKvm, "BALLOON", false),
        // Hyper-V synthetic drivers ship in-box on every Windows: only boot-start (0) counts, and only as weak evidence.
        (HyperV, "vmbus", false), (HyperV, "storvsc", false), (HyperV, "hyperkbd", false), (HyperV, "netvsc", false),
    };

    // Device id prefixes (relative to Enum) of virtual hardware.
    private static readonly (string Hv, string Prefix)[] VirtualDevices =
    {
        (VMware, @"PCI\VEN_15AD"), (VMware, "Ven_VMware"),
        (VirtualBox, @"PCI\VEN_80EE"), (VirtualBox, "VBOX_HARDDISK"), (VirtualBox, "Ven_VBOX"),
        (QemuKvm, @"PCI\VEN_1AF4"), (QemuKvm, "QEMU_HARDDISK"), (QemuKvm, "Ven_QEMU"),
        (Xen, @"PCI\VEN_5853"), (Xen, @"XENBUS\"), (Xen, @"XEN\"),
        (Parallels, @"PCI\VEN_1AB8"),
        (HyperV, @"VMBUS\"), (HyperV, "Ven_Msft&Prod_Virtual_Disk"),
    };

    private static readonly string[] LaptopModels =
        { "Latitude", "ThinkPad", "EliteBook", "ProBook", "ZBook", "XPS 13", "XPS 15", "XPS 17", "Inspiron 13", "Inspiron 14", "Inspiron 15", "Vostro 14", "Vostro 15",
          "MacBook", "Surface Laptop", "Surface Book", "Surface Pro", "Surface Go", "IdeaPad", "Yoga", "Spectre", "Envy x360", "Pavilion x360", "ZenBook", "VivoBook",
          "Notebook", "Laptop" };
    private static readonly string[] DesktopModels =
        { "OptiPlex", "ThinkCentre", "ProDesk", "EliteDesk", "Precision Tower", "Precision 3", "Precision 5", "Precision 7", "Vostro 3", "Inspiron 3", "Mac mini", "iMac",
          "ThinkStation", "Z4 G", "Z6 G", "Z8 G", "Desktop", "Tower", "All-in-One" };
    private static readonly string[] ServerModels = { "PowerEdge", "ProLiant", "ThinkSystem", "System x", "PRIMERGY", "UCS ", "SuperServer" };

    public static MachineTypeResult Classify(MachineTypeInput input)
    {
        var evidence = new List<string>();
        var cs = input.ControlSet;
        string Svc(string name) => $@"SYSTEM\{cs}\Services\{name}\Start";

        // ---- OS role
        bool serverOs = false;
        string? role = null;
        var pt = input.ProductType?.Trim();
        if (!string.IsNullOrEmpty(pt))
        {
            if (pt.Equals("ServerNT", StringComparison.OrdinalIgnoreCase)) { serverOs = true; role = "Server"; }
            else if (pt.Equals("LanmanNT", StringComparison.OrdinalIgnoreCase)) { serverOs = true; role = "Domain controller"; }
            else if (pt.Equals("WinNT", StringComparison.OrdinalIgnoreCase)) role = "Workstation";
            evidence.Add($@"ProductType = {pt} ({role ?? "unrecognised"}) [SYSTEM\{cs}\Control\ProductOptions\ProductType]");
        }

        // ---- hypervisor scoring
        var score = new Dictionary<string, int>(StringComparer.Ordinal);
        var hvEvidence = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Hit(string hv, int pts, string ev)
        {
            score[hv] = score.GetValueOrDefault(hv) + pts;
            if (!hvEvidence.TryGetValue(hv, out var l)) hvEvidence[hv] = l = new List<string>();
            l.Add(ev);
        }

        var hwText = string.Join(" | ", new[] { input.Manufacturer, input.Model, input.BiosVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string? stringHv = null;
        if (hwText.Length > 0)
        {
            if (Contains(input.Manufacturer, "Microsoft") && Contains(input.Model, "Virtual Machine")) stringHv = HyperV;
            else foreach (var (hv, needle) in HardwareStrings)
                if (Contains(hwText, needle)) { stringHv = hv; break; }
            if (stringHv != null)
                Hit(stringHv, 10, $"Hardware strings \"{hwText}\" identify {stringHv} [{input.HardwareSource}]");
        }

        foreach (var (hv, name, strong) in GuestServices)
        {
            if (!input.Services.TryGetValue(name, out var start)) continue;
            if (start == 4) continue;                                      // disabled
            if (hv == HyperV && start != 0) continue;                      // in-box driver, only boot-start is notable
            Hit(hv, strong ? 2 : 1, $"Guest service {name} (Start = {(start?.ToString() ?? "?")}) [{Svc(name)}]");
        }

        var devHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in input.DeviceIds)
            foreach (var (hv, prefix) in VirtualDevices)
            {
                bool match = prefix.Contains('\\') ? id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : Contains(id, prefix);
                if (!match || !devHits.Add(hv + "|" + prefix)) continue;
                Hit(hv, 2, $"Virtual device {id} [SYSTEM\\{cs}\\Enum\\{id}]");
            }

        // Best hypervisor: a hardware-string match is decisive; otherwise highest score (needs at least 2 points).
        string? hypervisor = stringHv;
        if (hypervisor == null && score.Count > 0)
        {
            var best = score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            if (best.Value >= 2) hypervisor = best.Key;
        }

        if (hypervisor != null)
        {
            evidence.AddRange(hvEvidence[hypervisor]);
            foreach (var other in hvEvidence.Where(kv => kv.Key != hypervisor).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                evidence.AddRange(other.Value.Select(e => "Also seen: " + e));
            var s = score[hypervisor];
            var conf = stringHv != null || s >= 4 ? ClassificationConfidence.High : ClassificationConfidence.Medium;
            if (hypervisor == HyperV && stringHv == null)
                evidence.Add("Note: Hyper-V synthetic devices are also present on a Hyper-V host's root partition and on Windows with VBS/WSL2.");
            return new MachineTypeResult(MachineType.VirtualMachine, hypervisor, conf, serverOs, role, evidence);
        }
        // weak hits that did not decide anything are still worth showing
        foreach (var kv in hvEvidence.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            evidence.AddRange(kv.Value.Select(e => "Weak (not decisive): " + e));

        // ---- server
        var serverHw = ServerModels.FirstOrDefault(m => Contains(input.Model, m));
        if (serverOs || serverHw != null)
        {
            if (serverHw != null) evidence.Add($"Model \"{input.Model}\" is server hardware [{input.HardwareSource}]");
            var conf = serverOs ? ClassificationConfidence.High : ClassificationConfidence.Medium;
            return new MachineTypeResult(MachineType.Server, null, conf, serverOs, role, evidence);
        }

        // ---- laptop
        bool battery = false, lid = false;
        foreach (var id in input.DeviceIds)
        {
            if (!battery && id.StartsWith(@"ACPI\PNP0C0A", StringComparison.OrdinalIgnoreCase))
            { battery = true; evidence.Add($@"ACPI control-method battery device {id} [SYSTEM\{cs}\Enum\{id}]"); }
            if (!lid && id.StartsWith(@"ACPI\PNP0C0D", StringComparison.OrdinalIgnoreCase))
            { lid = true; evidence.Add($@"ACPI lid switch device {id} [SYSTEM\{cs}\Enum\{id}]"); }
        }
        foreach (var h in input.BatteryHints) evidence.Add(h);
        var laptopModel = LaptopModels.FirstOrDefault(m => Contains(input.Model, m));
        if (battery || lid || input.BatteryHints.Count > 0 || laptopModel != null)
        {
            if (laptopModel != null) evidence.Add($"Model \"{input.Model}\" is a portable product line [{input.HardwareSource}]");
            int pts = (battery ? 2 : 0) + (lid ? 2 : 0) + (input.BatteryHints.Count > 0 ? 1 : 0) + (laptopModel != null ? 1 : 0);
            var conf = pts >= 4 || (battery && lid) ? ClassificationConfidence.High : pts >= 2 ? ClassificationConfidence.Medium : ClassificationConfidence.Low;
            return new MachineTypeResult(MachineType.Laptop, null, conf, serverOs, role, evidence);
        }

        // ---- desktop / unknown
        var desktopModel = DesktopModels.FirstOrDefault(m => Contains(input.Model, m));
        bool anyFacts = hwText.Length > 0 || input.DeviceIds.Count > 0 || input.Services.Count > 0 || pt != null;
        if (!anyFacts)
            return new MachineTypeResult(MachineType.Unknown, null, ClassificationConfidence.Low, serverOs, role,
                evidence.Count > 0 ? evidence : new List<string> { "No hardware, service or device information was readable." });
        if (desktopModel != null)
        {
            evidence.Add($"Model \"{input.Model}\" is a desktop product line [{input.HardwareSource}]");
            evidence.Add("No battery or lid device and no hypervisor signals");
            return new MachineTypeResult(MachineType.Desktop, null, ClassificationConfidence.Medium, serverOs, role, evidence);
        }
        evidence.Add(input.DeviceIds.Count > 0
            ? $@"No battery (ACPI\PNP0C0A) or lid (ACPI\PNP0C0D) device and no hypervisor signals [SYSTEM\{cs}\Enum\ACPI]"
            : "No hypervisor signals; device enumeration unavailable, so a missing battery is not conclusive");
        return new MachineTypeResult(MachineType.Desktop, null, input.DeviceIds.Count > 0 ? ClassificationConfidence.Medium : ClassificationConfidence.Low,
            serverOs, role, evidence);
    }

    private static bool Contains(string? hay, string needle) => hay != null && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
