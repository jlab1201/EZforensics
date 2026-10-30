using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Memory;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class VolatilityEngineTests
{
    private static readonly VolatilityInstall Install =
        new(@"C:\ez\volatility\venv\Scripts\vol.exe", @"C:\ez\volatility\venv\Scripts\python.exe", "2.28.2", @"C:\ez\volatility",
            @"C:\ez\volatility\symbols", @"C:\ez\volatility\cache");

    private static MemoryPluginDef P(string id) => MemoryPlugins.Find(id)!;

    // ------------------------------------------------------------------ plugin catalog

    [Fact]
    public void Catalog_has_the_default_and_optional_plugins()
    {
        var all = MemoryPlugins.All;
        all.Where(p => p.Os == "windows" && p.DefaultSelected).Select(p => p.Plugin).Should().BeEquivalentTo(
            "windows.info", "windows.pslist", "windows.pstree", "windows.psscan", "windows.cmdline", "windows.netscan",
            "windows.malware.malfind", "windows.svcscan", "windows.dlllist", "windows.registry.printkey",
            "windows.cmdscan", "windows.consoles", "windows.verinfo", "windows.malware.hollowprocesses", "windows.malware.processghosting",
            "windows.malware.suspicious_threads", "windows.malware.pebmasquerade", "windows.getsids");
        all.Where(p => p.Os == "windows" && !p.DefaultSelected).Select(p => p.Id).Should().BeEquivalentTo(
            "netstat", "handles", "filescan", "privileges", "envars", "ldrmodules", "sessions", "hivelist", "userassist",
            "amcache", "modules", "driverscan", "threads", "getservicesids", "joblinks", "timeliner");
        all.Should().OnlyContain(p => p.ArtifactId == "memory-" + p.Id);
        all.Select(p => p.Variant).Should().OnlyHaveUniqueItems();
        all.Should().NotContain(p => p.Plugin.Contains("hibernation"));
        all[0].IsInfo.Should().BeTrue("info runs first");
        P("computername").Args.Should().Equal("--key", @"ControlSet001\Control\ComputerName\ComputerName");
        MemoryPlugins.ForOs(MemoryOs.Linux).Should().OnlyContain(p => p.Plugin.StartsWith("linux."));
        MemoryJobs.LoadPlugins().Should().BeSameAs(MemoryPlugins.All);
    }

    // ------------------------------------------------------------------ command builder

    [Fact]
    public void Command_is_csv_with_symbols_and_cache_and_plugin_last()
    {
        var inv = VolatilityCommandBuilder.Build(Install, @"D:\ev\mem dump.raw", P("pslist"), offline: false);
        inv.Exe.Should().Be(Install.Exe);
        inv.Args.Should().Equal("-r", "csv", "-f", @"D:\ev\mem dump.raw", "--symbol-dirs", @"C:\ez\volatility\symbols",
            "--cache-path", @"C:\ez\volatility\cache", "windows.pslist");
        inv.Args.Should().NotContain("-q");
        inv.Environment["PYTHONNOUSERSITE"].Should().Be("1");
    }

    [Fact]
    public void Command_offline_plugin_args_extra_args_and_pagefile()
    {
        var inv = VolatilityCommandBuilder.Build(Install, @"D:\ev\m.raw", P("computername"), offline: true, extraArgs: "--recurse \"a b\"",
            includePagefile: true, pagefilePath: @"D:\ev\pagefile.sys");
        inv.Args.Should().Equal("-r", "csv", "-f", @"D:\ev\m.raw", "--symbol-dirs", Install.SymbolsDir, "--cache-path", Install.CacheDir,
            "--offline", "--single-swap-locations", @"D:\ev\pagefile.sys", "windows.registry.printkey",
            "--key", @"ControlSet001\Control\ComputerName\ComputerName", "--recurse", "a b");
    }

    [Fact]
    public void Pagefile_is_skipped_when_none_is_found()
    {
        using var t = new TempDir();
        var img = Path.Combine(t.Path, "m.raw");
        VolatilityCommandBuilder.Build(Install, img, P("pslist"), false, includePagefile: true).Args.Should().NotContain("--single-swap-locations");
        File.WriteAllText(Path.Combine(t.Path, "pagefile.sys"), "x");
        VolatilityCommandBuilder.Build(Install, img, P("pslist"), false, includePagefile: true).Args
            .Should().ContainInOrder("--single-swap-locations", Path.Combine(t.Path, "pagefile.sys"));
    }

    // ------------------------------------------------------------------ progress

    [Theory]
    [InlineData("Progress:   45.12\t\tScanning memory_layer using PdbSignatureScanner", 45.12)]
    [InlineData("Progress:  100.00\t\tPDB scanning finished", 100.0)]
    [InlineData("  Progress: 3\t\tx", 3.0)]
    public void Progress_lines_parse(string line, double expected) =>
        VolatilityProgressParser.Parse(line).Should().BeApproximately(expected, 0.001);

    [Theory]
    [InlineData("Unsatisfied requirement plugins.Info.kernel.symbol_table_name: ")]
    [InlineData("Volatility 3 Framework 2.28.2")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_progress_lines_give_null(string? line) => VolatilityProgressParser.Parse(line).Should().BeNull();

    [Fact]
    public void Progress_status_text() =>
        VolatilityProgressParser.Status("Progress:   45.12\t\tScanning layer").Should().Be("Scanning layer");

    // ------------------------------------------------------------------ CSV normalise

    private static (VolatilityCsvResult R, string Text) Norm(string raw)
    {
        var sw = new StringWriter();
        var r = VolatilityCsv.Normalize(new StringReader(raw), sw);
        return (r, sw.ToString());
    }

    [Fact]
    public void Banner_is_stripped_and_timestamps_converted()
    {
        var (r, text) = Norm("Volatility 3 Framework 2.28.2\n\nTreeDepth,PID,ImageFileName,CreateTime,ExitTime\n" +
                             "0,4,System,2024-03-01 09:00:00.000000 UTC,N/A\n" +
                             "1,1234,\"evil, inc.exe\",2024-03-01 09:30:15.500000 UTC,2024-03-01 09:31:00.000000 UTC\n");
        r.BannerLinesStripped.Should().Be(2);
        r.Rows.Should().Be(2);
        r.Headers.Should().Equal("TreeDepth", "PID", "ImageFileName", "CreateTime", "ExitTime");
        r.TimestampsConverted.Should().Be(3);
        text.Should().Be("TreeDepth,PID,ImageFileName,CreateTime,ExitTime\r\n" +
                         "0,4,System,2024-03-01 09:00:00.0000000,\r\n" +
                         "1,1234,\"evil, inc.exe\",2024-03-01 09:30:15.5000000,2024-03-01 09:31:00.0000000\r\n");
    }

    [Fact]
    public void Python_escapes_are_undone_and_quotes_rewritten_rfc4180()
    {
        var (_, text) = Norm("TreeDepth,PID,Args\n0,1,\"C:\\\\Windows\\\\cmd.exe /c \"\"echo hi\"\"\"\n0,2,\"line1\nline2\"\n");
        text.Should().Be("TreeDepth,PID,Args\r\n0,1,\"C:\\Windows\\cmd.exe /c \"\"echo hi\"\"\"\r\n0,2,\"line1\nline2\"\r\n");
    }

    [Fact]
    public void Offset_timestamps_convert_in_any_column_bare_ones_only_in_time_columns()
    {
        var (_, text) = Norm("TreeDepth,Variable,Value\n0,SystemTime,2024-03-01 10:20:30.123456+00:00\n0,Other,2024-03-01 10:20:30\n0,Local,2024-03-01 12:00:00+02:00\n");
        text.Should().Contain("0,SystemTime,2024-03-01 10:20:30.1234560\r\n");
        text.Should().Contain("0,Other,2024-03-01 10:20:30\r\n");
        text.Should().Contain("0,Local,2024-03-01 10:00:00.0000000\r\n");
    }

    [Fact]
    public void Missing_header_fails()
    {
        var act = () => Norm("Volatility 3 Framework 2.28.2\nSomething went wrong\n");
        act.Should().Throw<InvalidDataException>().WithMessage("*no CSV header*Volatility 3 Framework*");
        var empty = () => Norm("");
        empty.Should().Throw<InvalidDataException>().WithMessage("*no output*");
    }

    [Fact]
    public void Normalize_in_place_replaces_the_file()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "PsList.csv");
        File.WriteAllText(f, "Volatility 3 Framework 2.28.2\n\n\"TreeDepth\",\"PID\"\n0,4\n", new UTF8Encoding(false));
        var r = VolatilityCsv.Normalize(f, f, P("pslist"));
        r.Rows.Should().Be(1);
        File.ReadAllText(f).Should().Be("TreeDepth,PID\r\n0,4\r\n");
        Directory.GetFiles(t.Path).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ naming

    [Fact]
    public void Rename_row_names_memory_outputs_with_the_plugin_artifact_id()
    {
        ArtifactNaming.Map(MemoryJobs.ToolId, new OutputPlan("", "Memory", "memory-pslist"), "PsList.csv")
            .Should().Be(("Memory_PsList", "memory-pslist"));
        ArtifactNaming.FinalFileName(MemoryJobs.ToolId, new OutputPlan("", "Memory", "memory-netscan", "laptop2"), "NetScan.csv")
            .Should().Be("Memory_NetScan_laptop2.csv");
        ArtifactNaming.Map(MemoryJobs.ToolId, new OutputPlan("", "Memory", "memory-computername"), "ComputerName.csv").ArtifactId
            .Should().Be("memory-computername");
    }

    [Fact]
    public void Timeout_is_base_plus_per_gb()
    {
        var p = P("psscan");
        MemoryJobs.TimeoutFor(p, 4L << 30).Should().Be(TimeSpan.FromMinutes(p.TimeoutBaseMin + 4 * p.TimeoutPerGbMin));
        MemoryJobs.TimeoutFor(p, 0).Should().Be(TimeSpan.FromMinutes(p.TimeoutBaseMin));
    }

    // ------------------------------------------------------------------ gate

    [Fact]
    public async Task Gate_open_lets_waiters_through_and_fail_propagates_the_message()
    {
        var g = new MemorySymbolGate();
        var w = g.WaitAsync(CancellationToken.None);
        w.IsCompleted.Should().BeFalse();
        g.Open();
        await w;
        g.Fail("late");   // already decided: no effect
        g.Failed.Should().BeFalse();

        var f = new MemorySymbolGate();
        var w2 = f.WaitAsync(CancellationToken.None);
        f.Fail(MemorySymbolGate.SymbolsMissingMessage);
        await w2.Invoking(t => t).Should().ThrowAsync<MemorySymbolsMissingException>().WithMessage("*symbols*");
        MemorySymbolGate.IsSymbolError("Unsatisfied requirement plugins.PsList.kernel").Should().BeTrue();
        MemorySymbolGate.IsSymbolError("plugins.Info.kernel.symbol_table_name: ").Should().BeTrue();
        MemorySymbolGate.IsSymbolError("Progress: 3").Should().BeFalse();
    }
}
