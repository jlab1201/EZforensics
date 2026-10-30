using System.Text;
using EzForensics.Core.Archives;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using EzForensics.Core.Platforms;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class MemoryTriageTests
{
    private static MemoryImage Img(string path, MemoryOs os = MemoryOs.Windows, long size = 4096, string? note = null) =>
        new(path, size, MemoryFormat.WindowsCrashDump, os, DumpKind.Full, 1, note);

    private static VolatilityInstall Install(string root) =>
        new(FakeToolPaths.Exe, "python.exe", "2.28.2", root, Path.Combine(root, "symbols"), Path.Combine(root, "cache"));

    private static IReadOnlyList<PlannedJob> Plan(IReadOnlyList<MemoryImage> images, string root, IReadOnlyList<DiscoveredSystem>? systems = null,
        IReadOnlyCollection<string>? sel = null, Func<string, CancellationToken, string?>? hint = null, IEnumerable<string>? existing = null,
        ICollection<string>? notes = null) =>
        MemoryTriage.Plan(images, Path.Combine(root, "case"), Install(root), null, systems, sel, existingHosts: existing, notes: notes, hostHint: hint ?? ((_, _) => null));

    private static string WriteDump(string path)
    {
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
        return path;
    }

    [Fact]
    public void Default_selection_plans_info_first_and_every_default_windows_plugin()
    {
        using var t = new TempDir();
        var plan = Plan(new[] { Img(Path.Combine(t.Path, "memory.dmp")) }, t.Path);
        plan[0].Spec.Provenance!.ArtifactId.Should().Be("memory-info");
        var expected = MemoryPlugins.ForOs(MemoryOs.Windows).Where(p => p.DefaultSelected).Select(p => p.ArtifactId);
        plan.Select(p => p.Spec.Provenance!.ArtifactId!).Should().BeEquivalentTo(expected);
        plan.Should().OnlyContain(p => p.Spec.Category == "Memory" && p.SelectedByDefault);
        plan[0].Spec.OutputDir.Should().Be(Path.Combine(t.Path, "case", "Host_memory", "Memory"));   // host = file stem (a category name gets Host_)
        plan[0].Spec.Provenance!.HostNameSource.Should().Be(MemoryTriage.FromFileName);
    }

    [Fact]
    public async Task Without_info_the_gate_is_open_and_plugins_run()
    {
        using var t = new TempDir();
        var img = WriteDump(Path.Combine(t.Path, "m.dmp"));
        var plan = Plan(new[] { Img(img) }, t.Path, sel: new[] { "pslist" });
        plan.Should().ContainSingle();
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(2));
        var job = q.Enqueue(plan[0].Spec, Path.Combine(t.Path, "case"));
        await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(60));
        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
    }

    [Fact]
    public void Image_under_a_system_root_takes_that_host_and_provenance()
    {
        using var t = new TempDir();
        var root = Path.Combine(t.Path, "src", "C");
        var origin = new ArchiveOrigin("evidence.zip", "00", ArchiveFormat.Zip, false, null, root, new[] { "evidence.zip" }, TimestampCoverage.All);
        var sys = new DiscoveredSystem
        {
            RootPath = root, HostName = "SRV-A", HostNameSource = HostDiscovery.FromSystemHive, OsFamily = OsFamily.Windows,
            AcquisitionKind = AcquisitionKind.ExtractedArchive, Archive = origin,
        };
        var plan = Plan(new[] { Img(Path.Combine(root, "Windows", "MEMORY.DMP")) }, t.Path, new[] { sys }, hint: (_, _) => "OTHER");
        var prov = plan[0].Spec.Provenance!;
        prov.Host.Should().Be("SRV-A");
        prov.HostNameSource.Should().Be(HostDiscovery.FromSystemHive);
        prov.Archive.Should().BeSameAs(origin);
        prov.AcquisitionKind.Should().Be("ExtractedArchive");
        prov.SystemRoot.Should().Be(root);
        plan[0].Spec.OutputDir.Should().Be(Path.Combine(t.Path, "case", "SRV-A", "Memory"));
    }

    [Fact]
    public void Loose_images_use_the_hint_then_the_file_name_and_hosts_stay_unique()
    {
        using var t = new TempDir();
        var a = Img(Path.Combine(t.Path, "a.raw"));
        var b = Img(Path.Combine(t.Path, "b.raw"));
        var c = Img(Path.Combine(t.Path, "wks-9.raw"));
        var plan = Plan(new[] { a, b, c }, t.Path, sel: new[] { "pslist" }, existing: new[] { "WKS-01" },
            hint: (p, _) => p.EndsWith("wks-9.raw") ? null : "WKS-01");
        var hosts = plan.Select(p => p.Spec.Provenance!.Host).ToList();
        hosts[0].Should().Be("WKS-01 (2)");   // WKS-01 already exists in the case
        hosts[1].Should().Be("WKS-01 (2)");   // same machine: shared host
        hosts[2].Should().Be("wks-9");
        plan[0].Spec.Output!.Qualifier.Should().BeNull();
        plan[1].Spec.Output!.Qualifier.Should().Be("b");
        plan[0].Spec.Provenance!.HostNameSource.Should().Be(MemoryHostHint.FromMemoryStrings);
        plan[2].Spec.Provenance!.HostNameSource.Should().Be(MemoryTriage.FromFileName);
        plan[0].Spec.Output!.StagingDir.Should().NotBe(plan[1].Spec.Output!.StagingDir);
    }

    [Fact]
    public void Memory_hint_equal_to_a_scanned_system_shares_that_host()
    {
        using var t = new TempDir();
        var sys = new DiscoveredSystem { RootPath = Path.Combine(t.Path, "src", "C"), HostName = "DC01", HostNameSource = "x" };
        var plan = Plan(new[] { Img(Path.Combine(t.Path, "loose.raw")) }, t.Path, new[] { sys }, sel: new[] { "pslist" }, hint: (_, _) => "dc01");
        plan[0].Spec.Provenance!.Host.Should().Be("DC01");
    }

    [Fact]
    public void Linux_needs_an_isf_and_macos_plans_nothing()
    {
        using var t = new TempDir();
        var notes = new List<string>();
        var linux = Img(Path.Combine(t.Path, "l.lime"), MemoryOs.Linux);
        var mac = Img(Path.Combine(t.Path, "m.raw"), MemoryOs.Mac);
        Plan(new[] { linux, mac }, t.Path, notes: notes).Should().BeEmpty();
        notes.Should().Contain(n => n.Contains("ISF")).And.Contain(n => n.Contains("macOS"));

        Directory.CreateDirectory(Path.Combine(t.Path, "symbols", "linux"));
        File.WriteAllText(Path.Combine(t.Path, "symbols", "linux", "k.json.xz"), "x");
        var plan = Plan(new[] { linux }, t.Path);
        plan.Should().NotBeEmpty().And.OnlyContain(p => p.Spec.Provenance!.ArtifactId!.StartsWith("memory-linux-"));
        plan[0].Spec.Provenance!.OsFamily.Should().Be("Linux");
    }

    [Fact]
    public void Kernel_dump_note_is_reported_and_pagefile_flag_is_applied()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "pagefile.sys"), "x");
        var notes = new List<string>();
        var img = Img(Path.Combine(t.Path, "k.dmp"), note: "user-mode pages missing");
        var plan = MemoryTriage.Plan(new[] { img }, Path.Combine(t.Path, "case"), Install(t.Path), notes: notes, selectedPluginIds: new[] { "pslist" },
            includePagefile: true, hostHint: (_, _) => null);
        notes.Should().Contain(n => n.Contains("user-mode pages missing"));
        plan[0].Spec.Args.Should().Contain("--single-swap-locations");
        MemoryTriage.Plan(new[] { img }, Path.Combine(t.Path, "case"), Install(t.Path), selectedPluginIds: new[] { "pslist" }, hostHint: (_, _) => null)[0]
            .Spec.Args.Should().NotContain("--single-swap-locations");
    }

    [Fact]
    public void Without_an_install_the_jobs_are_placeholders()
    {
        using var t = new TempDir();
        var plan = MemoryTriage.Plan(new[] { Img(Path.Combine(t.Path, "m.dmp")) }, Path.Combine(t.Path, "case"), null, hostHint: (_, _) => null);
        plan.Should().NotBeEmpty().And.OnlyContain(p => MemoryTriage.IsPlaceholder(p.Spec));
    }

    [Theory]
    [InlineData("WKS-01", false)]
    [InlineData("OTHER-PC", true)]
    public async Task Computer_name_job_records_the_registry_name_and_notes_a_mismatch(string registryName, bool mismatch)
    {
        using var t = new TempDir();
        var img = WriteDump(Path.Combine(t.Path, "m.dmp"));
        var plan = Plan(new[] { Img(img) }, t.Path, sel: new[] { "computername" }, hint: (_, _) => "WKS-01");
        var spec = plan.Single().Spec;
        spec = spec with { Environment = new Dictionary<string, string>(spec.Environment!) { ["FAKE_VOL_COMPUTERNAME"] = registryName } };
        var q = new JobQueue(new FakeLocator(null), 2, TimeSpan.FromMinutes(2));
        var job = q.Enqueue(spec, Path.Combine(t.Path, "case"));
        await q.WhenAllAsync().WaitAsync(TimeSpan.FromSeconds(60));
        job.State.Should().Be(JobState.Succeeded, job.StatusReason);
        job.Details["computername.registry"].Should().Be(registryName);
        if (mismatch) job.Details["computername.note"].Should().Contain("not renamed");
        else job.Details.Should().NotContainKey("computername.note");
        Directory.Exists(Path.Combine(t.Path, "case", "WKS-01", "Memory")).Should().BeTrue();
    }

    [Fact]
    public void Single_memory_file_is_a_scan_result_and_unchecked_systems_are_filtered()
    {
        using var t = new TempDir();
        var img = WriteDump(Path.Combine(t.Path, "x.dmp"));
        var r = TriageScanResult.ForFile(img, "note");
        r.MemoryImages.Should().ContainSingle().Which.Path.Should().Be(img);
        r.Systems.Should().BeEmpty();
        TriageScanResult.ForFile(Path.Combine(t.Path, "none.txt"), "n").MemoryImages.Should().BeEmpty();

        var sys = new DiscoveredSystem { RootPath = Path.Combine(t.Path, "S"), HostName = "S", HostNameSource = "x" };
        var inside = Img(Path.Combine(sys.RootPath, "MEMORY.DMP"));
        var choices = new[] { new SystemChoice(sys, false, "S") };
        SystemSelection.FilterMemoryImages(new[] { inside, Img(img) }, choices).Select(m => m.Path).Should().Equal(img);
    }
}
