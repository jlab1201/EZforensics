using EzForensics.Core.Commands;
using EzForensics.Core.Memory;
using EzForensics.Core.Plaso;
using EzForensics.Core.Rita;
using EzForensics.Core.Settings;
using EzForensics.Core.Triage;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>"Additional arguments" of the app-built commands: parsing, managed flags, placement in each builder, saved values.</summary>
public sealed class ExtraArgsTests
{
    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Split_groups_quotes_inside_words_and_keeps_backslashes()
    {
        ExtraArgsStore.Split(@"--a 1  --path ""C:\My Dir\x"" --x=""a b"" """"", out var err).Should().Equal("--a", "1", "--path", @"C:\My Dir\x", "--x=a b", "");
        err.Should().BeNull();
        ExtraArgsStore.Split("   ", out err).Should().BeEmpty();
    }

    [Fact]
    public void Unbalanced_quote_is_an_error()
    {
        var p = ExtraArgsStore.Parse(ExtraArgsStore.Zeek, @"-e ""redef x");
        p.IsValid.Should().BeFalse();
        p.Errors.Should().ContainSingle().Which.Should().Contain("Unbalanced quote");
    }

    [Theory]
    [InlineData(ExtraArgsStore.PlasoLog2Timeline, "--storage_file x.plaso")]
    [InlineData(ExtraArgsStore.PlasoLog2Timeline, "--storage=x.plaso")]          // argparse abbreviation
    [InlineData(ExtraArgsStore.PlasoLog2Timeline, "--parsers win7")]
    [InlineData(ExtraArgsStore.PlasoLog2Timeline, "-zUTC")]
    [InlineData(ExtraArgsStore.PlasoPsort, "-o json")]
    [InlineData(ExtraArgsStore.PlasoPsort, "--write out.csv")]
    [InlineData(ExtraArgsStore.Zeek, "-r other.pcap")]
    [InlineData(ExtraArgsStore.Zeek, "-i eth0")]
    [InlineData(ExtraArgsStore.Zeek, "LogAscii::use_json=T")]
    [InlineData(ExtraArgsStore.RitaImport, "--database other")]
    [InlineData(ExtraArgsStore.RitaImport, "-l /tmp/logs")]
    [InlineData("volatility.pslist", "-f other.raw")]
    [InlineData("volatility.pslist", "--offline")]
    [InlineData("volatility.pslist", "--symbol-dirs=C:\\s")]
    public void Managed_flags_are_rejected(string id, string text)
    {
        var p = ExtraArgsStore.Parse(id, text);
        p.IsValid.Should().BeFalse();
        p.Errors.Should().ContainSingle().Which.Should().Contain("remove it");
    }

    [Theory]
    [InlineData(ExtraArgsStore.PlasoLog2Timeline, "--hashers md5 --yara_rules C:\\rules.yar")]
    [InlineData(ExtraArgsStore.PlasoPsort, "--additional_fields hostname")]
    [InlineData(ExtraArgsStore.Zeek, "frameworks/files/hash-all-files -e \"redef Log::default_rotation_interval=0secs;\"")]
    [InlineData(ExtraArgsStore.RitaImport, "--rolling")]
    [InlineData(ExtraArgsStore.RitaImport, "-rolling")]                          // urfave/cli: one flag, not -r grouped
    [InlineData("volatility.pslist", "--pid 4 --physical")]
    public void Other_flags_are_accepted(string id, string text) =>
        ExtraArgsStore.Parse(id, text).IsValid.Should().BeTrue();

    [Fact]
    public void Saved_args_are_used_only_when_valid()
    {
        var s = new AppSettings();
        ExtraArgsStore.Set(s, ExtraArgsStore.RitaImport, "  --rolling  ");
        ExtraArgsStore.Get(s, ExtraArgsStore.RitaImport).Should().Be("--rolling");
        ExtraArgsStore.Args(s, ExtraArgsStore.RitaImport).Should().Equal("--rolling");
        ExtraArgsStore.Set(s, ExtraArgsStore.Zeek, "-r x.pcap");
        ExtraArgsStore.Args(s, ExtraArgsStore.Zeek).Should().BeEmpty();
        ExtraArgsStore.ValidText(s, ExtraArgsStore.Zeek).Should().BeNull();
        ExtraArgsStore.Set(s, ExtraArgsStore.RitaImport, " ");
        s.ToolExtraArgs.Should().NotContainKey(ExtraArgsStore.RitaImport);
    }

    // ------------------------------------------------------------------ placement

    private static PlasoInstall PlasoExe() => new(PlasoOrigin.ManagedVenv, PlasoEngineKind.Executable,
        PlasoCommand.Of(@"C:\v\Scripts\log2timeline.exe"), PlasoCommand.Of(@"C:\v\Scripts\psort.exe"), "20260720", @"C:\v\Scripts");

    [Fact]
    public void Plaso_extra_args_go_before_the_source_and_before_psort_output()
    {
        var o = new PlasoRunOptions
        {
            Source = @"C:\nowhere\evidence", CaseDir = @"C:\nowhere\case", Stamp = "20261007T120000Z",
            ExtraLog2TimelineArgs = new[] { "--hashers", "md5" }, ExtraPsortArgs = new[] { "--additional_fields", "hostname" },
        };
        var l2t = PlasoCommandBuilder.BuildLog2Timeline(PlasoExe(), o).Args;
        l2t[^1].Should().Be(o.Source);
        l2t.TakeLast(3).Should().Equal("--hashers", "md5", o.Source);
        var psort = PlasoCommandBuilder.BuildPsort(PlasoExe(), o).Args.ToList();
        psort.Should().ContainInConsecutiveOrder("--additional_fields", "hostname", "-w");
        psort[^1].Should().Be(o.StorageFile);
    }

    [Fact]
    public void Zeek_extra_args_go_before_the_scripts_and_the_tsv_redef()
    {
        var install = new ZeekInstall(ZeekEngine.Native, ZeekOrigin.Configured, @"C:\Zeek\bin\zeek.exe", @"C:\Zeek\bin\zeek.exe", "9.0.0", @"C:\Zeek\bin", true);
        var o = new ZeekRunOptions { Pcap = @"C:\c\a.pcap", OutputDir = @"C:\c\out", ExtraArgs = new[] { "-e", "redef x=1;" }, ExtraScripts = new() { "s.zeek" } };
        ZeekCommandBuilder.Build(install, o).Args.Should().Equal("-C", "-r", @"C:\c\a.pcap", "local", "-e", "redef x=1;", "s.zeek", "LogAscii::use_json=F");
        ZeekJobs.CreateSpec(install, @"C:\c\a.pcap", @"C:\c\out", new ZeekRunOptions { ExtraArgs = new[] { "-e", "redef x=1;" } }).Args
            .Should().ContainInConsecutiveOrder("local", "-e", "redef x=1;", "LogAscii::use_json=F");
    }

    [Fact]
    public void Rita_extra_import_args_are_shell_quoted_after_rebuild()
    {
        var s = RitaCommandBuilder.PipelineScript("/mnt/d/case/x", "ez_case_ws1_abcdef", new RitaOptions { ExtraImportArgs = new[] { "--rolling", "it's" } });
        s.Should().Contain("--rebuild '--rolling' 'it'\"'\"'s' > \"$RUN/import.log\"");
        RitaCommandBuilder.PipelineScript("/x", "ez_case_ws1_abcdef").Should().Contain("--rebuild > \"$RUN/import.log\"");
    }

    [Fact]
    public void Rita_options_from_settings_carry_the_saved_import_args()
    {
        var st = new AppSettings();
        ExtraArgsStore.Set(st, ExtraArgsStore.RitaImport, "--rolling");
        RitaOptions.FromSettings(st).ExtraImportArgs.Should().Equal("--rolling");
        ExtraArgsStore.Set(st, ExtraArgsStore.RitaImport, "--database x");
        RitaOptions.FromSettings(st).ExtraImportArgs.Should().BeEmpty();
    }

    [Fact]
    public void Rita_captures_in_a_folder_group_by_folder_and_v2_capture_name()
    {
        using var t = new TempDir();
        var a = Directory.CreateDirectory(Path.Combine(t.Path, "Zeek", "cap1")).FullName;
        File.WriteAllText(Path.Combine(a, "conn.log"), "");
        File.WriteAllText(Path.Combine(a, "dns.log"), "");
        File.WriteAllText(Path.Combine(a, "files.log"), "");   // not read by RITA
        var net = Directory.CreateDirectory(Path.Combine(t.Path, "WS1", "Network")).FullName;
        File.WriteAllText(Path.Combine(net, "Zeek_Conn_lab.log"), "");
        File.WriteAllText(Path.Combine(net, "Zeek_Dns_lab.log"), "");
        File.WriteAllText(Path.Combine(net, "Zeek_Conn_other.log"), "");
        var caps = RitaJobs.CapturesIn(t.Path);
        caps.Select(c => (c.Capture, c.LogFiles.Count)).Should().BeEquivalentTo(new[] { ("cap1", 2), ("lab", 2), ("other", 1) });
        RitaJobs.CapturesIn(Path.Combine(t.Path, "missing")).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Ingest planning

    [Fact]
    public void Memory_plan_passes_the_saved_plugin_args_to_that_plugin_only()
    {
        using var t = new TempDir();
        var img = Path.Combine(t.Path, "m.dmp");
        File.WriteAllBytes(img, System.Text.Encoding.ASCII.GetBytes("PAGEDU64" + new string('\0', 4088)));
        var image = new MemoryImage(img, 4096, MemoryFormat.WindowsCrashDump, MemoryOs.Windows, DumpKind.Full, 1, null);
        var install = new VolatilityInstall(FakeToolPaths.Exe, "python.exe", "2.28.2", t.Path, Path.Combine(t.Path, "symbols"), Path.Combine(t.Path, "cache"));
        var plan = MemoryTriage.Plan(new[] { image }, Path.Combine(t.Path, "case"), install, selectedPluginIds: new[] { "info", "pslist" },
            hostHint: (_, _) => null, pluginExtraArgs: new Dictionary<string, string> { ["pslist"] = "--pid 4" });
        plan.Single(p => p.Spec.Provenance!.ArtifactId == "memory-pslist").Spec.Args.TakeLast(2).Should().Equal("--pid", "4");
        plan.Single(p => p.Spec.Provenance!.ArtifactId != "memory-pslist").Spec.Args.Should().NotContain("--pid");
    }

    [Fact]
    public void Zeek_plan_and_resolve_pass_the_options()
    {
        using var t = new TempDir();
        var pcap = Path.Combine(t.Path, "a.pcap");
        File.WriteAllBytes(pcap, new byte[16]);
        var install = new ZeekInstall(ZeekEngine.Native, ZeekOrigin.Configured, @"C:\Zeek\bin\zeek.exe", @"C:\Zeek\bin\zeek.exe", "9.0.0", @"C:\Zeek\bin", true);
        var options = new ZeekRunOptions { ExtraArgs = new[] { "-e", "redef x=1;" } };
        var caseDir = Path.Combine(t.Path, "case");
        var planned = NetworkTriage.Plan(new[] { new Network.PcapFile(pcap, 16, Network.PcapFormat.Pcap) }, caseDir, install, options: options).Single();
        planned.Spec.Args.Should().ContainInConsecutiveOrder("-e", "redef x=1;");
        NetworkTriage.Resolve(planned, install, options).Args.Should().ContainInConsecutiveOrder("-e", "redef x=1;");
        NetworkTriage.Resolve(planned, install).Args.Should().NotContain("-e");
    }

    [Fact]
    public void Volatility_extra_args_follow_the_plugin()
    {
        var install = new VolatilityInstall(@"C:\v\vol.exe", "python.exe", "2.28.2", @"C:\v", @"C:\v\symbols", @"C:\v\cache");
        var plugin = MemoryPlugins.All.First(p => !p.IsInfo && p.Args.Count == 0);
        var inv = VolatilityCommandBuilder.Build(install, @"C:\m\mem.raw", plugin, offline: false, extraArgs: @"--pid 4 --dump-dir ""C:\a b""");
        inv.Args.TakeLast(5).Should().Equal(plugin.Plugin, "--pid", "4", "--dump-dir", @"C:\a b");
    }
}
