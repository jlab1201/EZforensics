using System.Text.Json;
using EzForensics.Core.Advisories;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using EzForensics.Core.SystemInfo;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Memory computer details (windows.info + manifest) and the advisory context taken from the manifest (archive coverage, memory format).</summary>
public sealed class MemoryDetailsProviderTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _case;
    private readonly string _mem;

    private const string InfoCsv =
        "TreeDepth,Variable,Value\r\n" +
        "0,Kernel Base,0xf80000000000\r\n0,DTB,0x1ad000\r\n0,Is64Bit,True\r\n0,KeNumberProcessors,4\r\n" +
        "0,SystemTime,2026-10-01 12:00:00.000000\r\n0,NtSystemRoot,C:\\Windows\r\n0,NtProductType,NtProductWorkstation\r\n" +
        "0,NtMajorVersion,10\r\n0,NtMinorVersion,0\r\n0,NtBuildLab,19041.1.amd64fre.vb_release.191206-1406\r\n";

    public MemoryDetailsProviderTests()
    {
        _case = Path.Combine(_t.Path, "case");
        _mem = Path.Combine(_case, "H", "Memory");
        Directory.CreateDirectory(_mem);
    }

    public void Dispose() => _t.Dispose();

    private string WriteInfo() { var p = Path.Combine(_mem, "Memory_Info.csv"); File.WriteAllText(p, InfoCsv); return p; }

    private void WriteManifest(string imagePath, string format, string? kind, string plugin = "windows.info", string artifact = "memory-info",
        string? csv = null, ManifestSource? source = null)
    {
        var d = new Dictionary<string, string>
        {
            ["volatility.plugin"] = plugin, ["input.path"] = imagePath, ["image.format"] = format, ["input.sha256"] = "AB12", ["volatility.version"] = "2.7.0",
        };
        if (kind != null) d["dump.kind"] = kind;
        var m = new RunManifest
        {
            CaseDir = _case, LayoutVersion = 2,
            Sources = source == null ? null : new List<ManifestSource> { source },
            Jobs = { new ManifestJob
            {
                JobId = Guid.NewGuid(), ToolId = "Volatility3", Label = plugin, State = "Succeeded", OutputDir = _mem, Host = "H", Details = d,
                ArtifactId = artifact, EndUtc = DateTime.UtcNow.ToString("o"),
                OutputFiles = csv == null ? new() : new() { new ManifestOutputFile { Path = csv, Size = new FileInfo(csv).Length, ArtifactId = artifact, ToolId = "Volatility3", Host = "H" } },
            } },
        };
        File.WriteAllText(Path.Combine(_case, $"EzHub_manifest_{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));
    }

    [Fact]
    public void Adds_memory_image_category_and_keeps_memory_unknown_without_pages()
    {
        WriteManifest(@"D:\ev\mem.raw", "Raw", null);
        WriteInfo();
        var sys = new SystemDetails();
        sys.Fields.Add(new DetailField(ComputerDetailsCollector.CatMemory, "Installed memory", ComputerDetailsCollector.MemoryUnknown, "x"));
        new MemoryDetailsProvider(_case, "H").Contribute(sys, "", default);

        sys.Fields.Single(f => f.Field == "Installed memory").Value.Should().Be(ComputerDetailsCollector.MemoryUnknown);
        var cat = sys.Fields.Where(f => f.Category == "Memory image").ToDictionary(f => f.Field, f => f.Value);
        cat["Image path"].Should().Be(@"D:\ev\mem.raw");
        cat["Image format"].Should().Be("Raw");
        cat["SHA-256"].Should().Be("AB12");
        cat["Processors"].Should().Be("4");
        cat["Kernel build"].Should().StartWith("19041.1.amd64fre");
        cat["Acquisition system time (UTC)"].Should().Be("2026-10-01 12:00:00.000000 UTC");
    }

    [Fact]
    public void Usable_memory_replaces_the_placeholder()
    {
        MemoryDetailsProvider.FormatUsable(2_097_152L * 4096).Should().Be("≈8 GB usable (from memory image)");
        MemoryDetailsProvider.FormatUsable(1_572_864L * 4096).Should().Be("≈6 GB usable (from memory image)");
        MemoryDetailsProvider.FormatUsable(4_194_304L * 4096 * 2).Should().Be("≈32 GB usable (from memory image)");

        var sys = new SystemDetails();
        sys.Fields.Add(new DetailField("Memory", "Installed memory", ComputerDetailsCollector.MemoryUnknown, "x"));
        MemoryDetailsProvider.Apply(sys, new MemoryDetailsProvider.MemoryFacts(null, new Dictionary<string, string>(), null, null, null, null, 2_097_152, null));
        sys.Fields.Single(f => f.Field == "Installed memory").Value.Should().Be("≈8 GB usable (from memory image)");
    }

    [Fact]
    public void Memory_only_host_gets_a_report_but_registry_details_win()
    {
        WriteManifest(@"D:\ev\mem.raw", "Raw", null);
        WriteInfo();
        var json = MemoryDetailsProvider.WriteForMemoryOnlyHost(_case, "H");
        json.Should().NotBeNull();
        var rep = ComputerDetailsStore.Load(_case)!;
        rep.Systems.Should().ContainSingle().Which.Fields.Should().Contain(f => f.Category == "Memory image" && f.Field == "Image format");
        MemoryDetailsProvider.WriteForMemoryOnlyHost(_case, "H").Should().NotBeNull("a memory-only report is refreshed");

        // A report from the registry is never overwritten.
        var registry = new ComputerDetailsReport();
        registry.Systems.Add(new SystemDetails { Title = "PC", Host = "H" });
        ComputerDetailsStore.Write(registry, ComputerDetailsStore.HostFolderFor(_case, "H"));
        MemoryDetailsProvider.WriteForMemoryOnlyHost(_case, "H").Should().BeNull();
        MemoryDetailsProvider.WriteForMemoryOnlyHost(_case, "NoMemory").Should().BeNull();
    }

    // ---------- advisory context from the manifest ----------

    private ReliabilityAdvisor Advisor => new();

    private AdvisoryContext Context(CsvProvenance p, string headers) => new()
    {
        ArtifactId = p.AdvisoryArtifactId, Headers = headers.Split(','), CsvFileName = Path.GetFileName(p.Path),
        Acquisition = p.EvidenceAcquisition, InputTimestampFacts = p.InputTimestampFacts,
        ArchiveTimestampCoverage = p.ArchiveTimestampCoverage, MemoryFormat = p.MemoryFormat, MemoryDumpKind = p.MemoryDumpKind,
    };

    private string WritePsList()
    {
        var p = Path.Combine(_mem, "Memory_PsList.csv");
        File.WriteAllText(p, "PID,PPID,ImageFileName\r\n4,0,System\r\n");
        return p;
    }

    [Theory]
    [InlineData("VMware", false)]
    [InlineData("Raw", true)]
    public void Memory_format_comes_from_the_info_job_and_drives_smear(string format, bool smear)
    {
        var ps = WritePsList();
        // pslist job has no format of its own; the info job on the same image has.
        var m = new RunManifest { CaseDir = _case, LayoutVersion = 2 };
        m.Jobs.Add(new ManifestJob { JobId = Guid.NewGuid(), ToolId = "Volatility3", Host = "H", OutputDir = _mem, EndUtc = DateTime.UtcNow.ToString("o"),
            Details = new() { ["volatility.plugin"] = "windows.info", ["input.path"] = @"D:\ev\m.img", ["image.format"] = format } });
        m.Jobs.Add(new ManifestJob { JobId = Guid.NewGuid(), ToolId = "Volatility3", Host = "H", OutputDir = _mem, EndUtc = DateTime.UtcNow.ToString("o"),
            Details = new() { ["volatility.plugin"] = "windows.pslist", ["input.path"] = @"D:\ev\m.img" }, ArtifactId = "memory-pslist",
            OutputFiles = { new ManifestOutputFile { Path = ps, Size = new FileInfo(ps).Length, ArtifactId = "memory-pslist", ToolId = "Volatility3", Host = "H" } } });
        File.WriteAllText(Path.Combine(_case, "EzHub_manifest_x.json"), JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));

        var prov = CaseIndex.Load(_case).Describe(ps);
        prov.MemoryFormat.Should().Be(format);
        var ids = Advisor.GetAdvisories(Context(prov, "PID,PPID,ImageFileName")).Select(a => a.Id).ToList();
        ids.Any(i => i.Contains("smear", StringComparison.OrdinalIgnoreCase)).Should().Be(smear);
    }

    [Theory]
    [InlineData("false", false, false)]   // halted bug check dump (samples\C\memory.dmp, 0x80): no smear warning
    [InlineData("true", true, true)]
    [InlineData(null, null, true)]        // older manifests without memory.live stay cautious
    public void Crash_dump_live_flag_comes_from_the_info_job_and_drives_smear(string? live, bool? expectedLive, bool smear)
    {
        var ps = WritePsList();
        var infoDetails = new Dictionary<string, string> { ["volatility.plugin"] = "windows.info", ["input.path"] = @"D:\ev\m.dmp", ["image.format"] = "WindowsCrashDump", ["dump.kind"] = "Full" };
        if (live != null) infoDetails["memory.live"] = live;
        var m = new RunManifest { CaseDir = _case, LayoutVersion = 2 };
        m.Jobs.Add(new ManifestJob { JobId = Guid.NewGuid(), ToolId = "Volatility3", Host = "H", OutputDir = _mem, EndUtc = DateTime.UtcNow.ToString("o"), Details = infoDetails });
        m.Jobs.Add(new ManifestJob { JobId = Guid.NewGuid(), ToolId = "Volatility3", Host = "H", OutputDir = _mem, EndUtc = DateTime.UtcNow.ToString("o"),
            Details = new() { ["volatility.plugin"] = "windows.pslist", ["input.path"] = @"D:\ev\m.dmp" }, ArtifactId = "memory-pslist",
            OutputFiles = { new ManifestOutputFile { Path = ps, Size = new FileInfo(ps).Length, ArtifactId = "memory-pslist", ToolId = "Volatility3", Host = "H" } } });
        File.WriteAllText(Path.Combine(_case, "EzHub_manifest_x.json"), JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));

        var prov = CaseIndex.Load(_case).Describe(ps);
        prov.MemoryLiveDump.Should().Be(expectedLive);
        var ids = Advisor.GetAdvisories(Context(prov, "PID,PPID,ImageFileName") with { MemoryLiveDump = prov.MemoryLiveDump }).Select(a => a.Id).ToList();
        ids.Contains("memory-smear").Should().Be(smear);
    }

    [Fact]
    public void Kernel_dump_kind_flows_to_the_context()
    {
        var ps = WritePsList();
        WriteManifest(@"D:\ev\k.dmp", "WindowsCrashDump", "Kernel", plugin: "windows.pslist", artifact: "memory-pslist", csv: ps);
        var prov = CaseIndex.Load(_case).Describe(ps);
        (prov.MemoryFormat, prov.MemoryDumpKind).Should().Be(("WindowsCrashDump", "Kernel"));
        Advisor.GetAdvisories(Context(prov, "PID,PPID,ImageFileName")).Select(a => a.Id)
            .Should().Contain(i => i.Contains("kernel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Archive_coverage_from_the_manifest_source_is_exposed()
    {
        var pf = Path.Combine(_case, "H", "ProgramExecution", "Prefetch.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(pf)!);
        File.WriteAllText(pf, "A,B\r\n1,2\r\n");
        var m = new RunManifest
        {
            CaseDir = _case, LayoutVersion = 2,
            Sources = new() { new ManifestSource { RootPath = @"D:\x", Host = "H", AcquisitionKind = "ExtractedArchive", TimestampCoverage = "All", ArchivePath = @"D:\ev\c.zip", ArchiveSha256 = "FF" } },
            Jobs = { new ManifestJob { JobId = Guid.NewGuid(), ToolId = "PECmd", Host = "H", OutputDir = Path.GetDirectoryName(pf)!, ArtifactId = "prefetch", SystemRoot = @"D:\x",
                EndUtc = DateTime.UtcNow.ToString("o"),
                OutputFiles = { new ManifestOutputFile { Path = pf, Size = new FileInfo(pf).Length, ArtifactId = "prefetch", ToolId = "PECmd", Host = "H" } } } },
        };
        File.WriteAllText(Path.Combine(_case, "EzHub_manifest_a.json"), JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));

        var prov = CaseIndex.Load(_case).Describe(pf);
        (prov.ArchiveTimestampCoverage, prov.ArchivePath, prov.ArchiveSha256).Should().Be(("All", @"D:\ev\c.zip", "FF"));
        prov.EvidenceAcquisition.Should().Be(EvidenceAcquisition.ExtractedArchive);
        var ids = Advisor.GetAdvisories(Context(prov, "SourceFilename,SourceCreated,SourceModified,SourceAccessed,ExecutableName,Hash,Size,Version,RunCount,LastRun")).Select(a => a.Id).ToList();
        ids.Should().Contain(i => i.Contains("restored", StringComparison.OrdinalIgnoreCase));
    }
}
