using System.IO;
using EzForensics.App.Views.Explore;
using FluentAssertions;

namespace EzForensics.App.Tests;

public sealed class CyberChefLogicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-cc-" + Guid.NewGuid().ToString("N"));

    public CyberChefLogicTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("https://cyberchef.local/CyberChef_v10.19.4.html", true)]
    [InlineData("https://CYBERCHEF.LOCAL/assets/x.js", true)]
    [InlineData("blob:https://cyberchef.local/1234", true)]
    [InlineData("http://cyberchef.local/x.html", false)]
    [InlineData("https://cyberchef.local:8443/x", false)]
    [InlineData("https://evil.com/", false)]
    [InlineData("https://cyberchef.local.evil.com/", false)]
    [InlineData("https://user@cyberchef.local/", false)]
    [InlineData("https://evil.com/?https://cyberchef.local/", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("ftp://cyberchef.local/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void Uri_allowlist(string? uri, bool allowed) => CyberChefLogic.IsAllowedUri(uri).Should().Be(allowed);

    [Fact]
    public void FindHtml_prefers_highest_version()
    {
        foreach (var n in new[] { "CyberChef_v9.55.0.html", "CyberChef_v10.19.4.html", "CyberChef_v10.4.0.html", "other.html" })
            File.WriteAllText(Path.Combine(_dir, n), "x");
        CyberChefLogic.FindHtml(_dir).Should().Be("CyberChef_v10.19.4.html");
    }

    [Fact]
    public void FindHtml_falls_back_and_handles_missing()
    {
        CyberChefLogic.FindHtml(_dir).Should().BeNull();
        CyberChefLogic.FindHtml(Path.Combine(_dir, "nope")).Should().BeNull();
        CyberChefLogic.FindHtml(null).Should().BeNull();
        File.WriteAllText(Path.Combine(_dir, "index.html"), "x");
        CyberChefLogic.FindHtml(_dir).Should().Be("index.html");
        File.WriteAllText(Path.Combine(_dir, "CyberChef.html"), "x");
        CyberChefLogic.FindHtml(_dir).Should().Be("CyberChef.html");
    }

    [Fact]
    public void Fragment_roundtrip_keeps_recipe_and_replaces_input()
    {
        var bytes = new byte[] { 0, 1, 2, 250, 251, 252, 253 };
        var f = CyberChefLogic.WithInput("#recipe=From_Base64('A-Za-z0-9%2B/%3D',true)&input=AAAA", bytes);
        f.Should().StartWith("recipe=From_Base64('A-Za-z0-9%2B/%3D',true)&input=");
        f.Should().NotContain("=="); // padding stripped
        CyberChefLogic.TryGetInput(f).Should().Equal(bytes);
        CyberChefLogic.ParseFragment(f).Select(p => p.Key).Should().Equal("recipe", "input");
    }

    [Fact]
    public void Fragment_parse_and_build()
    {
        var pairs = CyberChefLogic.ParseFragment("#a=1&b=2=3&c");
        CyberChefLogic.BuildFragment(pairs).Should().Be("a=1&b=2=3&c=");
        CyberChefLogic.TryGetInput("recipe=x").Should().BeNull();
        CyberChefLogic.TryGetInput("input=!!!").Should().BeNull();
    }

    [Fact]
    public void PageUrl_includes_fragment()
    {
        CyberChefLogic.PageUrl("CyberChef_v1.0.0.html").Should().Be("https://cyberchef.local/CyberChef_v1.0.0.html");
        CyberChefLogic.PageUrl("a.html", "#input=QQ").Should().Be("https://cyberchef.local/a.html#input=QQ");
    }

    [Fact]
    public void Input_cap_truncates_above_10MB()
    {
        CyberChefLogic.IsWithinCap(CyberChefLogic.MaxInputBytes).Should().BeTrue();
        CyberChefLogic.IsWithinCap(CyberChefLogic.MaxInputBytes + 1).Should().BeFalse();
        var small = new byte[10];
        CyberChefLogic.ApplyCap(small, out var t1).Should().BeSameAs(small);
        t1.Should().BeFalse();
        var big = new byte[CyberChefLogic.MaxInputBytes + 5];
        CyberChefLogic.ApplyCap(big, out var t2).Length.Should().Be((int)CyberChefLogic.MaxInputBytes);
        t2.Should().BeTrue();
    }

    [Fact]
    public void Script_helpers_escape_and_unwrap()
    {
        CyberChefLogic.SetFragmentScript("#a=\"b\"").Should().NotContain("\"b\"").And.Contain("window.location.hash = \"a=");
        CyberChefLogic.UnwrapJsonString("\"#recipe=x\"").Should().Be("#recipe=x");
        CyberChefLogic.UnwrapJsonString("null").Should().Be("");
        CyberChefLogic.UnwrapJsonString(null).Should().Be("");
    }

    [Fact]
    public void Layout_persists()
    {
        var path = Path.Combine(_dir, "layout.json");
        CyberChefLayout.Load(path).Width.Should().Be(1100);
        new CyberChefLayout { Left = 10, Top = 20, Width = 800, Height = 600 }.Save(path);
        var l = CyberChefLayout.Load(path);
        l.Width.Should().Be(800);
    }

    private static CyberChefLogic.ThemeColors Colors(bool dark) => new(dark, "#101112", "#202122", "#303132", "#EEEEEE", "#999999",
        "#3B82F6", "#FFFFFF", "#444444", "#2C4A7A", "#0A0B0C", "#050607", "Segoe UI Variable Text, Segoe UI", 13, "Cascadia Mono, Consolas");

    [Fact]
    public void Theme_css_overrides_variables_with_the_app_colours_and_fonts()
    {
        var css = CyberChefLogic.BuildThemeCss(Colors(true));
        css.Should().Contain("--primary-background-colour:#101112;").And.Contain("--secondary-background-colour:#202122;")
            .And.Contain("--primary-font-colour:#EEEEEE;").And.Contain("--selected-operation-bg-colour:#2C4A7A;")
            .And.Contain("--btn-success-bg-colour:#3B82F6;").And.Contain("--primary-border-colour:#444444;");
        css.Should().Contain("--primary-font-family:\"Segoe UI Variable Text\", \"Segoe UI\", sans-serif;")
            .And.Contain("--fixed-width-font-family:\"Cascadia Mono\", \"Consolas\", monospace;")
            .And.Contain("--primary-font-size:13px;");
        css.Should().StartWith(":root:root:root{");
    }

    [Fact]
    public void Theme_css_hides_download_notice_and_about_but_keeps_options()
    {
        var css = CyberChefLogic.BuildThemeCss(Colors(false));
        css.Should().Contain("#notice-wrapper").And.Contain("#support").And.Contain("[data-target=\"#download-modal\"]").And.Contain("display:none");
        css.Should().NotContain("#options{");
    }

    [Fact]
    public void Theme_script_picks_dark_or_classic_base_theme_and_color_scheme()
    {
        CyberChefLogic.BaseTheme(true).Should().Be("dark");
        CyberChefLogic.BaseTheme(false).Should().Be("classic");
        CyberChefLogic.ApplyThemeScript(Colors(true)).Should().Contain("\"dark\"").And.Contain(CyberChefLogic.ThemeStyleId);
        CyberChefLogic.ApplyThemeScript(Colors(false)).Should().Contain("\"classic\"");
        CyberChefLogic.BuildThemeCss(Colors(true)).Should().Contain("color-scheme:dark");
        CyberChefLogic.BuildThemeCss(Colors(false)).Should().Contain("color-scheme:light");
    }
}
