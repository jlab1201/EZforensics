using EzForensics.Core.Tools;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ToolsLocatorTests
{
    [Fact]
    public void Classifies_flavors_and_resolves()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "net9"));
        Directory.CreateDirectory(Path.Combine(t.Path, "Sub", "Deep"));
        // net9 folder -> Net9
        File.Copy(FakeToolPaths.Exe, Path.Combine(t.Path, "net9", "PECmd.exe"));
        // managed assembly outside net9 -> Net4
        File.Copy(FakeToolPaths.Dll, Path.Combine(t.Path, "PECmd.exe"));
        // native/apphost outside net9 -> Net9
        File.Copy(FakeToolPaths.Exe, Path.Combine(t.Path, "Sub", "Deep", "AppHostTool.exe"));
        // junk
        Directory.CreateDirectory(Path.Combine(t.Path, "Maps"));
        File.Copy(FakeToolPaths.Exe, Path.Combine(t.Path, "Maps", "ignored.exe"));

        var loc = new ToolLocator();
        loc.Rescan(t.Path);

        loc.All.Should().HaveCount(3);
        loc.All.Single(x => x.Path.EndsWith(@"net9\PECmd.exe")).Flavor.Should().Be(BuildFlavor.Net9);
        loc.All.Single(x => x.Path == Path.Combine(t.Path, "PECmd.exe")).Flavor.Should().Be(BuildFlavor.Net4);
        loc.All.Single(x => x.ExeName == "AppHostTool.exe").Flavor.Should().Be(BuildFlavor.Net9);

        loc.Resolve("pecmd.exe", BuildFlavor.Net4)!.Flavor.Should().Be(BuildFlavor.Net4);
        loc.Resolve("PECmd", BuildFlavor.Net9)!.Flavor.Should().Be(RuntimeProbe.HasNet9() ? BuildFlavor.Net9 : BuildFlavor.Net4);
        loc.Resolve("apphosttool")!.Flavor.Should().Be(BuildFlavor.Net9);
        loc.Resolve("nothing").Should().BeNull();
    }

    [Theory]
    [InlineData("net")]
    [InlineData("net9")]
    [InlineData("net10")]
    public void Modern_folders_classify_as_Net9(string folder)
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, folder));
        // a managed assembly would be Net4 anywhere else; the folder name wins
        File.Copy(FakeToolPaths.Dll, Path.Combine(t.Path, folder, "RECmd.exe"));
        ToolLocator.Classify(Path.Combine(t.Path, folder, "RECmd.exe")).Should().Be(BuildFlavor.Net9);
    }
}
