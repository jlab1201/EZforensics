using EzForensics.Core.SystemInfo;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class SystemInfoMachineTypeTests
{
    private static Dictionary<string, long?> Svc(params (string Name, long? Start)[] s) =>
        s.ToDictionary(x => x.Name, x => x.Start, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void VMware_from_hardware_strings()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "VMware, Inc.", Model = "VMware7,1", BiosVersion = "INTEL  - 6040000",
            Services = Svc(("vmhgfs", 1), ("vm3dmp", 3)), ProductType = "WinNT", ControlSet = "ControlSet001",
        });
        r.Type.Should().Be(MachineType.VirtualMachine);
        r.Hypervisor.Should().Be(MachineTypeClassifier.VMware);
        r.Confidence.Should().Be(ClassificationConfidence.High);
        r.IsServerOs.Should().BeFalse();
        r.Evidence.Should().Contain(e => e.Contains(@"SYSTEM\ControlSet001\Services\vmhgfs\Start"));
        r.Display.Should().Be("Virtual machine (VMware)");
    }

    [Fact]
    public void VirtualBox_from_guest_services_and_devices_without_strings()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Services = Svc(("VBoxGuest", 0), ("VBoxSF", 1), ("VBoxDrv", 3)),
            DeviceIds = new[] { @"PCI\VEN_80EE&DEV_CAFE&SUBSYS_00000000&REV_00", @"ACPI\PNP0C0A\1" },
        });
        r.Type.Should().Be(MachineType.VirtualMachine, "a VM wins over an emulated battery");
        r.Hypervisor.Should().Be(MachineTypeClassifier.VirtualBox);
        r.Confidence.Should().Be(ClassificationConfidence.High);
    }

    [Fact]
    public void VirtualBox_innotek_strings()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput { Manufacturer = "innotek GmbH", Model = "VirtualBox" });
        r.Hypervisor.Should().Be(MachineTypeClassifier.VirtualBox);
        r.Confidence.Should().Be(ClassificationConfidence.High);
    }

    [Fact]
    public void Disabled_guest_service_is_ignored()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "Dell Inc.", Model = "OptiPlex 7090", Services = Svc(("VBoxGuest", 4)), DeviceIds = new[] { @"ACPI\PNP0A08\0" },
        });
        r.Type.Should().Be(MachineType.Desktop);
    }

    [Fact]
    public void HyperV_server_vm_is_vm_with_server_flag()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "Microsoft Corporation", Model = "Virtual Machine", BiosVersion = "Hyper-V UEFI Release v4.1",
            ProductType = "ServerNT", DeviceIds = new[] { @"VMBUS\{f8615163-df3e-46c5-913f-f2d2f965ed0e}" },
        });
        r.Type.Should().Be(MachineType.VirtualMachine);
        r.Hypervisor.Should().Be(MachineTypeClassifier.HyperV);
        r.IsServerOs.Should().BeTrue();
        r.OsRole.Should().Be("Server");
        r.Display.Should().Be("Virtual machine (Hyper-V, server OS)");
    }

    [Fact]
    public void Surface_laptop_is_not_HyperV()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "Microsoft Corporation", Model = "Surface Laptop 5",
            Services = Svc(("vmbus", 3), ("storvsc", 3)),
            DeviceIds = new[] { @"ACPI\PNP0C0A\1", @"ACPI\PNP0C0D\2" },
        });
        r.Type.Should().Be(MachineType.Laptop);
        r.Hypervisor.Should().BeNull();
        r.Confidence.Should().Be(ClassificationConfidence.High);
    }

    [Fact]
    public void Weak_HyperV_service_alone_does_not_make_a_vm()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "ASUS", Model = "System Product Name", Services = Svc(("vmbus", 0)), DeviceIds = new[] { @"ACPI\PNP0A08\0" },
        });
        r.Type.Should().Be(MachineType.Desktop);
        r.Evidence.Should().Contain(e => e.StartsWith("Weak"));
    }

    [Fact]
    public void QEMU_and_Xen_and_Parallels()
    {
        MachineTypeClassifier.Classify(new MachineTypeInput { Manufacturer = "QEMU", Model = "Standard PC (Q35 + ICH9, 2009)" })
            .Hypervisor.Should().Be(MachineTypeClassifier.QemuKvm);
        MachineTypeClassifier.Classify(new MachineTypeInput { Manufacturer = "Xen", Model = "HVM domU" })
            .Hypervisor.Should().Be(MachineTypeClassifier.Xen);
        MachineTypeClassifier.Classify(new MachineTypeInput { Manufacturer = "Parallels Software International Inc.", Model = "Parallels Virtual Platform" })
            .Hypervisor.Should().Be(MachineTypeClassifier.Parallels);
        MachineTypeClassifier.Classify(new MachineTypeInput { Services = Svc(("xenvbd", 0), ("xenbus", 0)) })
            .Hypervisor.Should().Be(MachineTypeClassifier.Xen);
    }

    [Fact]
    public void Laptop_from_battery_device()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "LENOVO", Model = "20XW0026US", ProductType = "WinNT", ControlSet = "ControlSet001",
            DeviceIds = new[] { @"ACPI\PNP0C0A\1", @"ACPI\PNP0A08\0" },
        });
        r.Type.Should().Be(MachineType.Laptop);
        r.Confidence.Should().Be(ClassificationConfidence.Medium);
        r.Evidence.Should().Contain(e => e.Contains(@"SYSTEM\ControlSet001\Enum\ACPI\PNP0C0A\1"));
    }

    [Fact]
    public void Desktop_when_no_battery_and_no_vm()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "Gigabyte Technology Co., Ltd.", Model = "B550 AORUS ELITE", ProductType = "WinNT",
            DeviceIds = new[] { @"ACPI\PNP0A08\0", @"PCI\VEN_10DE&DEV_2484" },
        });
        r.Type.Should().Be(MachineType.Desktop);
        r.Confidence.Should().Be(ClassificationConfidence.Medium);
        r.Hypervisor.Should().BeNull();
    }

    [Fact]
    public void Server_from_product_type()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput
        {
            Manufacturer = "Dell Inc.", Model = "PowerEdge R740", ProductType = "LanmanNT", DeviceIds = new[] { @"ACPI\PNP0A08\0" },
        });
        r.Type.Should().Be(MachineType.Server);
        r.Confidence.Should().Be(ClassificationConfidence.High);
        r.IsServerOs.Should().BeTrue();
        r.OsRole.Should().Be("Domain controller");
        r.Display.Should().Be("Server (domain controller)");
    }

    [Fact]
    public void Unknown_when_nothing_is_readable()
    {
        var r = MachineTypeClassifier.Classify(new MachineTypeInput());
        r.Type.Should().Be(MachineType.Unknown);
        r.Confidence.Should().Be(ClassificationConfidence.Low);
        r.Evidence.Should().NotBeEmpty();
    }

    [Fact]
    public void Collector_adds_hardware_fields_from_SystemInformation()
    {
        var sys = new InMemoryHive("SYSTEM");
        sys.Key("Select").With(RegValue.Dword("Current", 1));
        sys.Key(@"ControlSet001\Control\SystemInformation").With(RegValue.Sz("SystemManufacturer", "VMware, Inc."),
            RegValue.Sz("SystemProductName", "VMware20,1"), RegValue.Sz("BIOSVersion", "VMW201.00V.21805430.B64.2305221830"),
            RegValue.Sz("BIOSReleaseDate", "05/22/2023"));
        sys.Key(@"ControlSet001\Control\ProductOptions").With(RegValue.Sz("ProductType", "ServerNT"));
        sys.Key(@"ControlSet001\Services\vmhgfs").With(RegValue.Dword("Start", 1));
        sys.Key(@"ControlSet001\Enum\PCI\VEN_15AD&DEV_0405&SUBSYS_040515AD&REV_00\3&1").With(RegValue.Sz("DeviceDesc", "VMware SVGA 3D"));

        var d = ComputerDetailsCollector.Extract(@"X:\C", sys, null, null);
        string V(string field) => d.Fields.Single(f => f.Category == ComputerDetailsCollector.CatHardware && f.Field == field).Value;
        V("Manufacturer").Should().Be("VMware, Inc.");
        V("Model").Should().Be("VMware20,1");
        V("BIOS version").Should().StartWith("VMW201");
        V("BIOS date").Should().Be("05/22/2023");
        V("Machine type").Should().Be("Virtual machine (VMware, server OS)");
        V("Hypervisor").Should().Be("VMware");
        V("OS role").Should().Be("Server");
        V("Classification confidence").Should().Be("High");
        V("Classification evidence").Should().Contain(@"SYSTEM\ControlSet001\Control\SystemInformation");
        d.Fields.Single(f => f.Field == "Manufacturer").Source.Should().Be(@"SYSTEM\ControlSet001\Control\SystemInformation\SystemManufacturer");
    }

    [Fact]
    public void Collector_falls_back_to_HardwareConfig_and_detects_laptop()
    {
        var sys = new InMemoryHive("SYSTEM");
        sys.Key("Select").With(RegValue.Dword("Current", 1));
        sys.Key(@"HardwareConfig\Current").With(RegValue.Sz("SystemManufacturer", "Dell Inc."), RegValue.Sz("SystemProductName", "Latitude 7420"));
        sys.Key(@"ControlSet001\Enum\ACPI\PNP0C0A\1");
        sys.Key(@"ControlSet001\Enum\ACPI\PNP0C0D\2");

        var d = ComputerDetailsCollector.Extract(@"X:\C", sys, null, null);
        string V(string field) => d.Fields.Single(f => f.Category == ComputerDetailsCollector.CatHardware && f.Field == field).Value;
        V("Model").Should().Be("Latitude 7420");
        V("BIOS version").Should().Be(ComputerDetailsCollector.Unknown);
        V("Machine type").Should().Be("Laptop");
        V("Hypervisor").Should().Be("none detected");
        V("Classification confidence").Should().Be("High");
        d.Fields.Single(f => f.Field == "Model").Source.Should().Be(@"SYSTEM\HardwareConfig\Current\SystemProductName");
    }
}
