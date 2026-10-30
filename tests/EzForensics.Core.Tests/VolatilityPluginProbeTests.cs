using EzForensics.Core.Memory;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class VolatilityPluginProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ezforensics_volprobe_" + Guid.NewGuid().ToString("N"));

    public VolatilityPluginProbeTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        VolatilityPluginProbe.Reset();
        try { Directory.Delete(_root, true); } catch { }
    }

    // The shape of vol 2.28.2 -h: argparse usage, options, then the PLUGIN group (an older build without the malware.* and verinfo plugins).
    private const string Help = "Volatility 3 Framework 2.5.0\r\n" +
        "usage: vol.exe [-h] [-c CONFIG] [-f FILE]\r\n" +
        "               PLUGIN ...\r\n" +
        "\r\n" +
        "options:\r\n" +
        "  -h, --help            Show this help message and exit, for specific plugin\r\n" +
        "  -p PLUGIN_DIRS, --plugin-dirs PLUGIN_DIRS\r\n" +
        "\r\n" +
        "Plugins:\r\n" +
        "  For plugin specific options, run 'vol.exe <plugin> --help'\r\n" +
        "\r\n" +
        "  PLUGIN\r\n" +
        "    banners.Banners     Attempts to identify potential linux banners in an\r\n" +
        "                        image\r\n" +
        "    timeliner.Timeliner\r\n" +
        "                        Runs all relevant plugins that provide time related\r\n" +
        "    windows.cmdscan.CmdScan\r\n" +
        "                        Looks for Windows Command History lists\r\n" +
        "    windows.info.Info   Show OS & kernel details of the memory sample being\r\n" +
        "    windows.malfind.Malfind\r\n" +
        "                        Lists process memory ranges that potentially contain\r\n" +
        "    windows.pslist.PsList\r\n" +
        "    windows.registry.printkey.PrintKey\r\n";

    private VolatilityInstall Install(string version = "2.5.0") =>
        new(Path.Combine(_root, "vol.exe"), Path.Combine(_root, "python.exe"), version, _root, Path.Combine(_root, "symbols"), Path.Combine(_root, "cache"));

    [Fact]
    public void ParseHelp_reads_the_plugin_group_with_and_without_class_names()
    {
        var set = VolatilityPluginProbe.ParseHelp(Help);
        set.Should().Contain(new[] { "windows.pslist", "windows.pslist.PsList", "windows.registry.printkey", "timeliner", "windows.malfind", "windows.cmdscan" });
        set.Should().NotContain(new[] { "windows.verinfo", "windows.malware.malfind", "PLUGIN_DIRS", "image" });
        VolatilityPluginProbe.ParseHelp("garbage").Should().BeEmpty();
    }

    [Fact]
    public void Probe_hides_a_missing_plugin_and_is_cached_per_path_and_version()
    {
        var fake = new FakePlasoProbe();
        var inst = Install();
        fake.Files.Add(inst.Exe);
        fake.Outputs[inst.Exe + " -h"] = Help;

        var available = VolatilityPluginProbe.Get(inst, fake);
        available.Should().NotBeNull();
        VolatilityPluginProbe.IsListed(MemoryPlugins.Find("verinfo")!, available).Should().BeFalse("vol 2.5.0 has no windows.verinfo");
        VolatilityPluginProbe.IsListed(MemoryPlugins.Find("hollowprocesses")!, available).Should().BeFalse();
        VolatilityPluginProbe.IsListed(MemoryPlugins.Find("pslist")!, available).Should().BeTrue();
        VolatilityPluginProbe.IsListed(MemoryPlugins.Find("cmdscan")!, available).Should().BeTrue();
        VolatilityPluginProbe.IsListed(MemoryPlugins.Find("computername")!, available).Should().BeTrue("windows.registry.printkey is listed");
        VolatilityPluginProbe.Missing(MemoryPlugins.ForOs(MemoryOs.Windows), available).Select(p => p.Id).Should().Contain("malfind", "the old build has windows.malfind, not windows.malware.malfind");

        VolatilityPluginProbe.Get(inst, fake).Should().BeSameAs(available);
        fake.Calls.Should().ContainSingle("the second call is served from memory");

        VolatilityPluginProbe.Reset(inst);
        File.Exists(Path.Combine(inst.CacheDir, VolatilityPluginProbe.CacheFileName)).Should().BeFalse("Reset(install) also drops the disk cache");
    }

    [Fact]
    public void Disk_cache_is_keyed_by_path_and_version()
    {
        var fake = new FakePlasoProbe();
        var inst = Install();
        fake.Files.Add(inst.Exe);
        fake.Outputs[inst.Exe + " -h"] = Help;
        VolatilityPluginProbe.Get(inst, fake);
        File.Exists(Path.Combine(inst.CacheDir, VolatilityPluginProbe.CacheFileName)).Should().BeTrue();

        VolatilityPluginProbe.Reset();
        var again = new FakePlasoProbe();
        VolatilityPluginProbe.Get(inst, again)!.Should().Contain("windows.pslist");
        again.Calls.Should().BeEmpty("same path and version: the cache file answers");

        var upgraded = new FakePlasoProbe();
        upgraded.Files.Add(inst.Exe);
        upgraded.Outputs[inst.Exe + " -h"] = Help.Replace("    windows.pslist.PsList\r\n", "    windows.pslist.PsList\r\n    windows.verinfo.VerInfo\r\n");
        VolatilityPluginProbe.Get(Install("2.28.2"), upgraded)!.Should().Contain("windows.verinfo");
        upgraded.Calls.Should().ContainSingle("another version probes again");
    }

    [Fact]
    public void A_failed_probe_fails_open()
    {
        var fake = new FakePlasoProbe();
        var inst = Install();
        fake.Files.Add(inst.Exe);   // no output scripted: Run returns null
        var available = VolatilityPluginProbe.Get(inst, fake);
        available.Should().BeNull();
        MemoryPlugins.All.Should().OnlyContain(p => VolatilityPluginProbe.IsListed(p, available));
        File.Exists(Path.Combine(inst.CacheDir, VolatilityPluginProbe.CacheFileName)).Should().BeFalse();
    }
}
