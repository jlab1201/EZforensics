using System.Xml.Linq;
using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class NetFxTlsConfigTests
{
    private const string Strong = "Switch.System.Net.DontEnableSchUseStrongCrypto";
    private const string SysDefault = "Switch.System.Net.DontEnableSystemDefaultTlsVersions";

    [Fact]
    public void Creates_the_config_for_a_net4_exe()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "RECmd.exe");
        File.Copy(FakeToolPaths.Dll, exe); // managed = Net4
        NetFxTlsConfig.EnsureStrongTls(exe).Should().BeTrue();
        var v = XDocument.Load(exe + ".config").Root!.Element("runtime")!.Element("AppContextSwitchOverrides")!.Attribute("value")!.Value;
        v.Should().Contain(Strong + "=false").And.Contain(SysDefault + "=false");
    }

    [Fact]
    public void Merges_into_an_existing_config_and_keeps_other_content()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "RECmd.exe");
        File.Copy(FakeToolPaths.Dll, exe);
        File.WriteAllText(exe + ".config", """
            <?xml version="1.0"?>
            <configuration>
              <startup><supportedRuntime version="v4.0" /></startup>
              <runtime>
                <gcServer enabled="true" />
                <AppContextSwitchOverrides value="Switch.Other.Thing=true;Switch.System.Net.DontEnableSchUseStrongCrypto=true" />
              </runtime>
            </configuration>
            """);
        NetFxTlsConfig.EnsureStrongTls(exe);
        var root = XDocument.Load(exe + ".config").Root!;
        root.Element("startup")!.Element("supportedRuntime").Should().NotBeNull();
        root.Element("runtime")!.Element("gcServer").Should().NotBeNull();
        var v = root.Element("runtime")!.Elements("AppContextSwitchOverrides").Single().Attribute("value")!.Value;
        v.Should().Contain("Switch.Other.Thing=true").And.Contain(Strong + "=false").And.Contain(SysDefault + "=false").And.NotContain(Strong + "=true");
    }

    [Fact]
    public void Is_a_no_op_for_modern_tools()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "net", "RECmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(FakeToolPaths.Exe, exe);
        NetFxTlsConfig.EnsureStrongTls(exe).Should().BeFalse();
        File.Exists(exe + ".config").Should().BeFalse();
    }
}
