using System.IO;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The Brain page ships offline: no web addresses in its own files, and the vendored 3D graph library with its licences.</summary>
public sealed class BrainAssetsTests
{
    private static string BrainFolder()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !d.GetFiles("*.sln").Any()) d = d.Parent;
        d.Should().NotBeNull("the tests run inside the repository");
        return Path.Combine(d!.FullName, "src", "EzForensics.App", "Assets", "brain");
    }

    [Fact]
    public void BrainAssets_Exist_NoHttpUrls()
    {
        var dir = BrainFolder();
        foreach (var name in new[] { "index.html", "brain.js", "brain.css", "boot.js" })
        {
            var text = File.ReadAllText(Path.Combine(dir, name));
            text.Should().NotContainEquivalentOf("http://", name);
            text.Should().NotContainEquivalentOf("https://", name);
        }
        File.ReadAllText(Path.Combine(dir, "brain.js")).Should().MatchRegex(@"3d-force-graph 1\.\d+\.\d+").And.MatchRegex(@"three\.js 0\.\d+\.\d+");
        File.ReadAllText(Path.Combine(dir, "index.html")).Should().Contain("script-src 'self';", "the page loads no inline or remote script");
        File.Exists(Path.Combine(dir, "force-graph.min.js")).Should().BeFalse("the 2D library was replaced");
    }

    [Fact]
    public void BrainVendor_HasLibrariesAndLicences()
    {
        var vendor = Path.Combine(BrainFolder(), "vendor");
        File.Exists(Path.Combine(vendor, "3d-force-graph", "dist", "3d-force-graph.js")).Should().BeTrue();
        File.Exists(Path.Combine(vendor, "three", "build", "three.module.js")).Should().BeTrue();
        File.Exists(Path.Combine(vendor, "three", "examples", "jsm", "postprocessing", "UnrealBloomPass.js")).Should().BeTrue();
        File.ReadAllText(Path.Combine(vendor, "VERSIONS.txt")).Should().Contain("3d-force-graph 1.").And.Contain("three 0.");
        Directory.GetFiles(vendor, "*.mjs", SearchOption.AllDirectories).Should().BeEmpty("module scripts are served as .js");
        // one three.js only: no bundled UMD copies
        Directory.GetFiles(vendor, "*.min.js", SearchOption.AllDirectories).Should().BeEmpty();
        foreach (var pkg in VendorPackages(vendor))
            File.Exists(Path.Combine(pkg, "LICENSE")).Should().BeTrue(pkg);
    }

    /// <summary>Package folders under vendor (scoped ones are one level deeper); the shims are our own code.</summary>
    private static IEnumerable<string> VendorPackages(string vendor)
    {
        foreach (var d in Directory.GetDirectories(vendor))
        {
            var name = Path.GetFileName(d);
            if (name == "shims") continue;
            if (name.StartsWith('@')) foreach (var s in Directory.GetDirectories(d)) yield return s;
            else yield return d;
        }
    }

    [Fact]
    public void BrainAssets_CopiedNextToTheApp()
    {
        // the App project copies Assets\brain to the output; the test project references it, so the files land here too
        var outDir = Path.Combine(AppContext.BaseDirectory, "Assets", "brain");
        File.Exists(Path.Combine(outDir, "index.html")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "boot.js")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "vendor", "3d-force-graph", "dist", "3d-force-graph.js")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "vendor", "three", "build", "three.module.js")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "vendor", "three", "build", "three.core.js")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "force-graph.min.js")).Should().BeFalse("the old library must not linger in the output");
    }
}
