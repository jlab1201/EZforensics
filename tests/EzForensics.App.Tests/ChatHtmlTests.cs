using EzForensics.App.Services;
using Xunit;

namespace EzForensics.App.Tests;

public sealed class ChatHtmlTests
{
    [Fact]
    public void Bold_list_and_code_render()
    {
        var html = ChatHtml.RenderMarkdown("**bad** logon\n\n- one\n- two\n\nrun `whoami`\n\n```\nnet user\n```");
        Assert.Contains("<strong>bad</strong>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<code>whoami</code>", html);
        Assert.Contains("<pre><code>net user", html);
    }

    [Fact]
    public void Script_tags_are_escaped_not_emitted()
    {
        var html = ChatHtml.RenderMarkdown("<script>alert(1)</script>\n\nhello <b>x</b>");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void Img_onerror_is_escaped()
    {
        var html = ChatHtml.RenderMarkdown("<img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html);
    }

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](https://evil.example/x)")]
    [InlineData("![pic](https://evil.example/x.png)")]
    [InlineData("<https://evil.example/x>")]
    [InlineData("https://evil.example/x")]
    public void Links_and_images_never_become_anchors_or_sources(string md)
    {
        var html = ChatHtml.RenderMarkdown(md);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Code_fence_info_string_cannot_break_out_of_the_attribute()
    {
        var html = ChatHtml.RenderMarkdown("```\"><script>x</script>\ncode\n```");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_has_a_strict_csp_with_the_nonce_and_no_external_loads()
    {
        var theme = new ChatHtml.Theme(true, "#111111", "#222222", "#333333", "#eeeeee", "#999999", "#3b82f6", "#ffffff", "#444444", "#2c4a7a", "#1a1d24", "#0b0d11", "Segoe UI, Arial", "Consolas");
        var page = ChatHtml.BuildPage(theme, "NONCE123");
        Assert.Contains("default-src 'none'", page);
        Assert.Contains("script-src 'nonce-NONCE123'", page);
        Assert.Contains("<script nonce=\"NONCE123\">", page);
        Assert.Contains("--bg:#111111", page);
        Assert.DoesNotContain("http://", page);
        Assert.DoesNotContain("https://", page);
    }

    [Theory]
    [InlineData(true, "dark")]
    [InlineData(false, "light")]
    public void Page_and_theme_message_carry_the_colour_scheme(bool dark, string scheme)
    {
        var theme = new ChatHtml.Theme(dark, "#111111", "#222222", "#333333", "#eeeeee", "#999999", "#3b82f6", "#ffffff", "#444444", "#2c4a7a", "#1a1d24", "#0b0d11", "Segoe UI, Arial", "Consolas");
        Assert.Contains("color-scheme:" + scheme, ChatHtml.BuildPage(theme, "N"));
        using var doc = System.Text.Json.JsonDocument.Parse(ChatHtml.ThemeJson(theme));
        Assert.Equal(dark, doc.RootElement.GetProperty("dark").GetBoolean());
        Assert.Equal("theme", doc.RootElement.GetProperty("op").GetString());
    }

    [Fact]
    public void Page_has_typing_dots_callout_tails_and_scroll_following()
    {
        var page = ChatHtml.BuildPage(new ChatHtml.Theme(true, "#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9", "#a", "#b", "f", "m"), "N");
        Assert.Contains("@keyframes", page);
        Assert.Contains("::before", page);
        Assert.Contains("::-webkit-scrollbar", page);
        Assert.Contains("ResizeObserver", page);
        Assert.Contains("applyAll", page);
        using var doc = System.Text.Json.JsonDocument.Parse(ChatHtml.TypingJson(true, "Running x"));
        Assert.True(doc.RootElement.GetProperty("on").GetBoolean());
        Assert.Equal("Running x", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Theme_values_cannot_inject_css_or_markup()
    {
        var theme = new ChatHtml.Theme(true, "#111;}</style><script>x</script>", "#2", "#3", "#4", "#5", "#6", "#7", "#8", "#9", "#a", "#b", "f", "m");
        var page = ChatHtml.BuildPage(theme, "N");
        Assert.DoesNotContain("<script>x", page);
        Assert.DoesNotContain("</style><script", page);
    }

    [Fact]
    public void Message_json_keeps_markup_inert()
    {
        var json = ChatHtml.AddMessageJson("1", "spectre", html: ChatHtml.RenderMarkdown("<script>x</script>"));
        Assert.DoesNotContain("<script", json);   // < is escaped as < in JSON
    }

    [Theory]
    [InlineData("{\"type\":\"apply\",\"id\":\"abc\"}", "apply", "abc")]
    [InlineData("{\"type\":\"undo\",\"id\":\"abc\"}", "undo", "abc")]
    [InlineData("{\"type\":\"applyAll\",\"id\":\"abc\"}", "applyAll", "abc")]
    [InlineData("{\"type\":\"ready\"}", "ready", "")]
    public void Valid_page_actions_parse(string json, string type, string id) => Assert.Equal((type, id), ChatHtml.ParseAction(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{\"type\":\"delete\",\"id\":\"a\"}")]
    [InlineData("{\"type\":\"apply\"}")]
    [InlineData("{\"type\":\"applyAll\"}")]
    [InlineData("{\"type\":\"apply\",\"id\":5}")]
    public void Anything_else_from_the_page_is_ignored(string? json) => Assert.Null(ChatHtml.ParseAction(json));

    // the real theme values (Themes/Light.xaml, Themes/Dark.xaml)
    private static ChatHtml.Theme Light => new(false, "#F0F0F0", "#FCFCFC", "#F3F3F3", "#1F2329", "#5B6270", "#2563EB", "#FFFFFF", "#D0D0D0", "#CFE0FF", "#FFFFFF", "#E8E8E8", "Segoe UI", "Consolas");
    private static ChatHtml.Theme Dark => new(true, "#1E2128", "#272B34", "#313642", "#E6E8EE", "#9AA1B0", "#3B82F6", "#FFFFFF", "#3A404D", "#2C4A7A", "#1A1D24", "#0B0D11", "Segoe UI", "Consolas");

    private static double[] Rgb(string hex) => new[] { Convert.ToInt32(hex[1..3], 16) / 255.0, Convert.ToInt32(hex[3..5], 16) / 255.0, Convert.ToInt32(hex[5..7], 16) / 255.0 };
    private static double Lum(double[] c)
    {
        static double L(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c[0]) + 0.7152 * L(c[1]) + 0.0722 * L(c[2]);
    }
    private static double Contrast(double[] a, double[] b)
    {
        var (x, y) = (Lum(a), Lum(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
    private static double[] Mix(string accent, string surface, double pct) => Rgb(accent).Zip(Rgb(surface), (a, s) => a * pct + s * (1 - pct)).ToArray();

    [Fact]
    public void Spectre_bubble_is_tinted_with_a_border_and_the_name_sits_above_it()
    {
        var page = ChatHtml.BuildPage(Light, "N");
        Assert.Contains("--spFill:color-mix(in srgb,var(--accent) 12%,var(--surface))", page);
        Assert.Contains(".msg.spectre{background:var(--spFill);border:1px solid var(--spBorder);color:var(--text)}", page);
        // the name is a sibling above the bubble, outside it; the examiner's bubble has no name
        Assert.Contains("n=el('div','row');n.appendChild(el('div','who','Spectre'));var b=el('div','msg spectre')", page);
        Assert.Contains("n.appendChild(b)", page);
        Assert.Contains("el('div','msg you',m.text)", page);
        Assert.DoesNotContain("n.appendChild(el('div','who','Spectre'));var d=el('div','md')", page);   // the old name-inside-the-bubble markup
        // the tail, typing dots, Apply-all bar and stick-to-bottom are still there
        Assert.Contains(".msg.spectre::before", page);
        Assert.Contains(".dots i", page);
        Assert.Contains("Apply all (", page);
        Assert.Contains("function follow()", page);
        Assert.Contains("script-src 'nonce-N'", page);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Spectre_bubble_text_and_name_are_readable_in_both_themes(bool dark)
    {
        var t = dark ? Dark : Light;
        var fill = Mix(t.Accent, t.Surface, 0.12);
        Assert.True(Contrast(Rgb(t.Text), fill) >= 4.5, "body text on the bubble");
        Assert.True(Contrast(Rgb(t.TextSecondary), Rgb(t.Window)) >= 4.5, "the name above the bubble");
        Assert.True(Contrast(fill, Rgb(t.Surface)) > 1.0); // visibly tinted, not the plain surface
    }
}
