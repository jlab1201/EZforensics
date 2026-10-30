namespace EzForensics.Core.Tools;

/// <summary>Display helpers for the Tools overview table (UI text only; download names are never changed).</summary>
public static class ToolDisplay
{
    public const string NotInstalled = "Not installed";
    public const string Installed = "Installed";
    public const string UpdateAvailable = "Update available";

    // "Usage" text of the special tools (the catalog tools carry theirs in Resources/tools/*.json).
    public const string PlasoUsage = "Builds a super timeline from disk images, triage collections or event logs: log2timeline extracts events, psort sorts and exports them. Use it when you want every artifact on one timeline to review or filter by time.";
    public const string ZeekUsage = "Turns packet captures (pcap / pcapng) into Zeek logs such as conn, dns, http and ssl, plus CSV copies. Use it to review network activity from a capture, or as the input for RITA beaconing analysis.";
    public const string RitaUsage = "Analyses Zeek logs for command-and-control behaviour: beaconing, long connections, DNS tunnelling and connections to known threat intel. Use it after Zeek has processed a capture to find hosts worth investigating.";
    public const string VolatilityUsage = "Analyses memory images (processes, network connections, loaded modules, injected code) with Volatility 3. Use it on RAM captures to find running malware or evidence that never reached disk.";

    /// <summary>"JLECmd.zip" becomes "JLECmd"; other names are returned unchanged.</summary>
    public static string StripZip(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>"2026.5.0.0" becomes "2026.5.0", the way the EZ tools site and the tools' own --version print it.</summary>
    public static string FormatVersion(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return "";
        var v = fileVersion.Trim();
        var plus = v.IndexOf('+');
        if (plus > 0) v = v[..plus];
        var parts = v.Split('.');
        return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts.Take(3)) : v;
    }

    /// <summary>Status text for one catalog tool; unknown update info counts as Installed.</summary>
    public static string StatusOf(bool installed, bool updateAvailable) =>
        !installed ? NotInstalled : updateAvailable ? UpdateAvailable : Installed;
}
