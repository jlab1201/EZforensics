using System.Xml.Linq;

namespace EzForensics.Core.Tools;

/// <summary>
/// .NET Framework 4.x tools default to TLS 1.0/1.1 unless told otherwise, which breaks <c>--sync</c> against GitHub
/// ("Could not create SSL/TLS secure channel"). This writes the two AppContext switches that make them use the system's TLS defaults
/// into <c>&lt;exe&gt;.config</c>, keeping any other content.
/// </summary>
public static class NetFxTlsConfig
{
    private const string SwitchName = "AppContextSwitchOverrides";
    private static readonly (string Key, string Value)[] Switches =
    {
        ("Switch.System.Net.DontEnableSchUseStrongCrypto", "false"),
        ("Switch.System.Net.DontEnableSystemDefaultTlsVersions", "false"),
    };

    /// <summary>Creates or updates the .config next to a .NET Framework exe. No-op for other tools. Returns true when the file was written.</summary>
    public static bool EnsureStrongTls(string exePath)
    {
        try
        {
            if (!File.Exists(exePath) || ToolLocator.Classify(exePath) != BuildFlavor.Net4) return false;
            return WriteConfig(exePath + ".config");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return false; }
    }

    /// <summary>Applies <see cref="EnsureStrongTls"/> to every .NET Framework tool the locator knows.</summary>
    public static int EnsureAll(IEnumerable<InstalledTool> tools)
    {
        int n = 0;
        foreach (var t in tools) if (t.Flavor == BuildFlavor.Net4 && EnsureStrongTls(t.Path)) n++;
        return n;
    }

    public static bool WriteConfig(string configPath)
    {
        XDocument doc;
        try { doc = File.Exists(configPath) ? XDocument.Load(configPath) : new XDocument(); }
        catch (System.Xml.XmlException) { doc = new XDocument(); } // unreadable: replace with a clean file
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "configuration") { root = new XElement("configuration"); doc = new XDocument(root); }
        var runtime = root.Element("runtime");
        if (runtime == null) { runtime = new XElement("runtime"); root.Add(runtime); }
        var ov = runtime.Elements().FirstOrDefault(e => e.Name.LocalName == SwitchName);
        if (ov == null) { ov = new XElement(SwitchName); runtime.Add(ov); }

        var map = new List<KeyValuePair<string, string>>();
        foreach (var part in (ov.Attribute("value")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map.Add(new(part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }
        foreach (var (k, v) in Switches)
        {
            map.RemoveAll(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            map.Add(new(k, v));
        }
        var value = string.Join(";", map.Select(p => $"{p.Key}={p.Value}"));
        if (File.Exists(configPath) && ov.Attribute("value")?.Value == value && !NeedsRewrite(configPath, value)) return false;
        ov.SetAttributeValue("value", value);
        doc.Declaration ??= new XDeclaration("1.0", "utf-8", null);
        doc.Save(configPath);
        return true;
    }

    private static bool NeedsRewrite(string path, string value) => !File.ReadAllText(path).Contains(value, StringComparison.Ordinal);
}
