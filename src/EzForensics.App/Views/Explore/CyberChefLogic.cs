using System.Text.Json;

namespace EzForensics.App.Views.Explore;

/// <summary>Pure helpers for the CyberChef panel: URL allowlist, html discovery, fragment build/parse, input cap.</summary>
public static class CyberChefLogic
{
    public const string Host = "cyberchef.local";
    public const string Origin = "https://cyberchef.local/";
    public const long MaxInputBytes = 10L * 1024 * 1024;
    public const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>True only for https://cyberchef.local/... (plus blob:/data: URIs generated inside the page, which touch no network).</summary>
    public static bool IsAllowedUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == "blob" || u.Scheme == "data") return true;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        return string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo);
    }

    /// <summary>Finds the CyberChef html in a folder: CyberChef_vX.Y.Z.html (highest version), else CyberChef*.html, else index.html.</summary>
    public static string? FindHtml(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var files = Directory.EnumerateFiles(folder, "CyberChef*.html", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).OfType<string>().ToList();
        var versioned = files
            .Select(f => (f, v: ParseVersion(f)))
            .Where(x => x.v != null)
            .OrderByDescending(x => x.v)
            .Select(x => x.f)
            .FirstOrDefault();
        if (versioned != null) return versioned;
        var plain = files.FirstOrDefault(f => f.Equals("CyberChef.html", StringComparison.OrdinalIgnoreCase))
                    ?? files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (plain != null) return plain;
        return File.Exists(Path.Combine(folder, "index.html")) ? "index.html" : null;
    }

    private static Version? ParseVersion(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        const string prefix = "CyberChef_v";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return Version.TryParse(name[prefix.Length..], out var v) ? v : null;
    }

    public static string PageUrl(string htmlFile, string? fragment = null)
    {
        var url = Origin + Uri.EscapeDataString(htmlFile);
        var f = NormalizeFragment(fragment);
        return f.Length == 0 ? url : url + "#" + f;
    }

    public static string NormalizeFragment(string? fragment) => (fragment ?? "").TrimStart('#');

    /// <summary>Parses "recipe=...&amp;input=..." into pairs (values stay URL-encoded exactly as CyberChef wrote them).</summary>
    public static List<KeyValuePair<string, string>> ParseFragment(string? fragment)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var part in NormalizeFragment(fragment).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            list.Add(i < 0 ? new(part, "") : new(part[..i], part[(i + 1)..]));
        }
        return list;
    }

    public static string BuildFragment(IEnumerable<KeyValuePair<string, string>> pairs)
        => string.Join('&', pairs.Select(p => $"{p.Key}={p.Value}"));

    /// <summary>Base64 as CyberChef expects in <c>input=</c>: standard alphabet, no padding.</summary>
    public static string EncodeInput(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');

    /// <summary>Replaces (or adds) the input= pair of a fragment, keeping the recipe and other params.</summary>
    public static string WithInput(string? fragment, byte[] data)
    {
        var pairs = ParseFragment(fragment).Where(p => !p.Key.Equals("input", StringComparison.OrdinalIgnoreCase)).ToList();
        pairs.Add(new("input", EncodeInput(data)));
        return BuildFragment(pairs);
    }

    public static byte[]? TryGetInput(string? fragment)
    {
        var v = ParseFragment(fragment).FirstOrDefault(p => p.Key.Equals("input", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(v)) return null;
        try
        {
            v = Uri.UnescapeDataString(v).Replace('-', '+').Replace('_', '/');
            v = v.PadRight(v.Length + (4 - v.Length % 4) % 4, '=');
            return Convert.FromBase64String(v);
        }
        catch (FormatException) { return null; }
    }

    public static bool IsWithinCap(long length) => length <= MaxInputBytes;

    /// <summary>The data to send, cut to the cap. <paramref name="truncated"/> tells the caller to warn.</summary>
    public static byte[] ApplyCap(byte[] data, out bool truncated)
    {
        truncated = !IsWithinCap(data.LongLength);
        return truncated ? data.AsSpan(0, (int)MaxInputBytes).ToArray() : data;
    }

    /// <summary>JS string literal for ExecuteScriptAsync.</summary>
    public static string JsString(string s) => JsonSerializer.Serialize(s);

    /// <summary>Sets location.hash; CyberChef's popstate/hashchange handler then loads recipe and input from it.</summary>
    public static string SetFragmentScript(string fragment)
        => $"(function(){{ window.location.hash = {JsString(NormalizeFragment(fragment))}; return 'ok'; }})()";

    /// <summary>Dev/default location: EZFORENSICS_CYBERCHEF_DIR (the Core locator will replace this).</summary>
    public static string? FolderFromEnvironment() => EzForensics.Core.Settings.AppPaths.Env("CYBERCHEF_DIR");

    public const string GetFragmentScript = "window.location.hash";

    /// <summary>ExecuteScriptAsync returns a JSON-encoded value; this unwraps a JSON string result.</summary>
    public static string UnwrapJsonString(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return "";
        try { return JsonSerializer.Deserialize<string>(json) ?? ""; } catch (JsonException) { return json; }
    }

    // ---- Theme: CyberChef (v11) themes are CSS custom properties on :root.<theme>; we override them at higher specificity. ----

    public const string ThemeStyleId = "ezf-theme";

    /// <summary>Colours (#RRGGBB) and fonts taken from the app theme.</summary>
    public sealed record ThemeColors(
        bool Dark, string Background, string Surface, string SurfaceAlt, string Text, string TextSecondary,
        string Accent, string AccentText, string Border, string Selection, string Input, string Code,
        string UiFont, double UiFontSize, string MonoFont);

    /// <summary>CSS font-family list from a WPF-style comma list ("Segoe UI Variable Text, Segoe UI"), quoting each name.</summary>
    public static string CssFontFamily(string wpfList, string generic)
    {
        var names = (wpfList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => "\"" + n.Replace("\"", "").Replace("\\", "") + "\"");
        return string.Join(", ", names.Append(generic));
    }

    /// <summary>CyberChef's own base theme to start from, so anything we do not override still fits.</summary>
    public static string BaseTheme(bool dark) => dark ? "dark" : "classic";

    /// <summary>The override stylesheet: variables, the app fonts and the hidden download/notice/about chrome.</summary>
    public static string BuildThemeCss(ThemeColors t)
    {
        var ui = CssFontFamily(t.UiFont, "sans-serif");
        var mono = CssFontFamily(t.MonoFont, "monospace");
        var size = t.UiFontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";
        var v = new (string, string)[]
        {
            ("--primary-font-family", ui), ("--primary-font-colour", t.Text), ("--primary-font-size", size),
            ("--fixed-width-font-family", mono), ("--fixed-width-font-colour", t.Text), ("--fixed-width-font-size", size),
            ("--subtext-font-colour", t.TextSecondary),
            ("--primary-background-colour", t.Background), ("--secondary-background-colour", t.Surface),
            ("--primary-border-colour", t.Border), ("--secondary-border-colour", t.Border),
            ("--title-colour", t.Text), ("--title-background-colour", t.SurfaceAlt),
            ("--banner-font-colour", t.TextSecondary), ("--banner-bg-colour", t.Surface), ("--banner-url-colour", t.Accent),
            ("--category-list-font-colour", t.Text),
            ("--loader-background-colour", t.Background), ("--loader-outer-colour", t.Accent), ("--loader-middle-colour", t.Selection), ("--loader-inner-colour", t.Accent),
            ("--op-list-operation-font-colour", t.Text), ("--op-list-operation-bg-colour", t.Surface), ("--op-list-operation-border-colour", t.Border),
            ("--rec-list-operation-font-colour", t.Text), ("--rec-list-operation-bg-colour", t.SurfaceAlt), ("--rec-list-operation-border-colour", t.Border),
            ("--selected-operation-font-color", t.AccentText), ("--selected-operation-bg-colour", t.Selection), ("--selected-operation-border-colour", t.Accent),
            ("--disabled-font-colour", t.TextSecondary), ("--disabled-bg-colour", t.Surface), ("--disabled-border-colour", t.Border),
            ("--fc-operation-font-colour", t.Text), ("--fc-operation-bg-colour", t.SurfaceAlt), ("--fc-operation-border-colour", t.Border),
            ("--arg-font-colour", t.Text), ("--arg-background", t.Input), ("--arg-border-colour", t.Border),
            ("--arg-disabled-background", t.Surface), ("--arg-label-colour", t.TextSecondary),
            ("--btn-default-font-colour", t.Text), ("--btn-default-bg-colour", t.SurfaceAlt), ("--btn-default-border-colour", t.Border),
            ("--btn-default-hover-font-colour", t.Text), ("--btn-default-hover-bg-colour", t.Selection), ("--btn-default-hover-border-colour", t.Accent),
            ("--btn-success-font-colour", t.AccentText), ("--btn-success-bg-colour", t.Accent), ("--btn-success-border-colour", t.Accent),
            ("--btn-success-hover-font-colour", t.AccentText), ("--btn-success-hover-bg-colour", t.Accent), ("--btn-success-hover-border-colour", t.Accent),
            ("--scrollbar-track", t.Surface), ("--scrollbar-thumb", t.Border), ("--scrollbar-hover", t.TextSecondary),
            ("--drop-file-border-colour", t.Accent), ("--table-border-colour", t.Border),
            ("--popover-background", t.Surface), ("--popover-border-colour", t.Border),
            ("--code-background", t.Code), ("--code-font-colour", t.Text),
            ("--input-highlight-colour", t.Accent), ("--input-border-colour", t.Border),
        };
        var sb = new System.Text.StringBuilder();
        sb.Append(":root:root:root{");
        foreach (var (k, val) in v) sb.Append(k).Append(':').Append(val).Append(';');
        sb.Append("color-scheme:").Append(t.Dark ? "dark" : "light").Append(";}");
        sb.Append("body,input,select,textarea,button,.modal-content{font-family:").Append(ui).Append(";}");
        sb.Append("pre,code,#input-text,#output-text,.cm-editor,.cm-content,.cm-scroller,.arg-input textarea,textarea.form-control{font-family:").Append(mono).Append(";}");
        sb.Append("::selection{background:").Append(t.Selection).Append(";color:").Append(t.Text).Append(";}");
        // Hidden chrome: Download link and modal, "Last build" / notice area, About/Support (all advertising or online links). Options stays.
        sb.Append("#banner a[data-target=\"#download-modal\"],#download-modal,#notice-wrapper,#notice,#support,#support-modal{display:none !important;}");
        return sb.ToString();
    }

    /// <summary>JS that installs/updates the stylesheet and switches CyberChef's base theme class; safe to run repeatedly.</summary>
    public static string ApplyThemeScript(ThemeColors t)
    {
        var css = JsString(BuildThemeCss(t));
        var theme = JsString(BaseTheme(t.Dark));
        return "(function(){var id='" + ThemeStyleId + "';var s=document.getElementById(id);if(!s){s=document.createElement('style');s.id=id;(document.head||document.documentElement).appendChild(s);}"
             + "s.textContent=" + css + ";var r=document.documentElement;if(r.className!==" + theme + "){r.className=" + theme + ";}return 'ok';})()";
    }
}
