using System.Globalization;
using System.Net;
using EzForensics.Core.Settings;

namespace EzForensics.Core.Rita;

/// <summary>
/// Where RITA runs. <see cref="Wsl"/>: inside the private EZ Forensics distro (the default). <see cref="Docker"/>: reserved
/// (two containers on a private network; not implemented, see <see cref="RitaSetup.DockerNotSupportedMessage"/>).
/// <see cref="Direct"/>: <see cref="RitaInstall.Exe"/> is run directly with Windows paths (the fake tool in tests).
/// </summary>
public enum RitaEngine { Wsl, Docker, Direct }

/// <summary>A usable RITA installation.</summary>
/// <param name="Exe">wsl.exe (Wsl), docker.exe (Docker) or the program itself (Direct).</param>
/// <param name="Distro">WSL distro name (Wsl); null otherwise.</param>
public sealed record RitaInstall(RitaEngine Engine, string Exe, string? RitaVersion, string? ClickHouseVersion, string? Distro = null)
{
    public IReadOnlyDictionary<string, string> ManifestDetails() => new Dictionary<string, string>
    {
        ["rita.version"] = RitaVersion ?? "unknown",
        ["clickhouse.version"] = ClickHouseVersion ?? "unknown",
        ["rita.engine"] = Engine.ToString().ToLowerInvariant(),
        ["rita.image"] = RitaRelease.ImageReference,
    };

    public string Display => Engine switch
    {
        RitaEngine.Wsl => $"RITA {RitaVersion} in WSL {Distro} (ClickHouse {ClickHouseVersion})",
        RitaEngine.Docker => $"RITA {RitaVersion} in Docker",
        _ => $"RITA {RitaVersion} ({Exe})",
    };
}

/// <summary>Analysis options (from Settings: <see cref="AppSettings.RitaInternalSubnets"/>, <see cref="AppSettings.RitaOnlineThreatIntel"/>).</summary>
public sealed record RitaOptions
{
    /// <summary>RFC 1918 + the IPv6 ULA prefix (RITA's own defaults).</summary>
    public static readonly IReadOnlyList<string> DefaultInternalSubnets = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fd00::/8" };

    /// <summary>abuse.ch Feodo Tracker (RITA's default online feed), only used when <see cref="OnlineThreatIntel"/> is on.</summary>
    public static readonly IReadOnlyList<string> DefaultOnlineFeeds = new[] { "https://feodotracker.abuse.ch/downloads/ipblocklist.txt" };

    public IReadOnlyList<string> InternalSubnets { get; init; } = DefaultInternalSubnets;
    public bool OnlineThreatIntel { get; init; }
    /// <summary>The user's additional <c>rita import</c> arguments (Tools page, <see cref="Commands.ExtraArgsStore.RitaImport"/>), after ours.</summary>
    public IReadOnlyList<string> ExtraImportArgs { get; init; } = Array.Empty<string>();

    public static RitaOptions Default { get; } = new();

    public static RitaOptions FromSettings(AppSettings s) => new()
    {
        InternalSubnets = s.RitaInternalSubnets is { Count: > 0 } l ? NormalizeSubnets(l) : DefaultInternalSubnets,
        OnlineThreatIntel = s.RitaOnlineThreatIntel,
        ExtraImportArgs = Commands.ExtraArgsStore.Args(s, Commands.ExtraArgsStore.RitaImport),
    };

    /// <summary>Trimmed, valid CIDRs (an address alone becomes /32 or /128), duplicates removed; invalid entries dropped.</summary>
    public static IReadOnlyList<string> NormalizeSubnets(IEnumerable<string?> values)
    {
        var list = new List<string>();
        foreach (var raw in values)
        {
            foreach (var part in (raw ?? "").Split(new[] { ',', ';', '\n', '\r', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var c = NormalizeCidr(part);
                if (c != null && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
            }
        }
        return list;
    }

    /// <summary>"10.0.0.0/8" → itself; "10.1.2.3" → "10.1.2.3/32"; invalid → null.</summary>
    public static string? NormalizeCidr(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var slash = s.IndexOf('/');
        var ipText = slash < 0 ? s : s[..slash];
        if (!IPAddress.TryParse(ipText, out var ip)) return null;
        // IPAddress also accepts legacy short forms ("10.0.0" = 10.0.0.0, "10" = 0.0.0.10): only dotted quads count as IPv4
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ipText.Count(c => c == '.') != 3) return null;
        int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        int prefix = max;
        if (slash >= 0 && (!int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max)) return null;
        return $"{ip}/{prefix.ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>The Zeek logs of one capture (Windows paths; any naming: <c>Zeek_Conn_cap.log</c> or <c>conn.log</c>).</summary>
public sealed record RitaCaptureLogs(string Capture, IReadOnlyList<string> LogFiles);

/// <summary>One staged log: RITA's sub-folder (<c>c01</c>...), its original Zeek name (<c>conn.log</c>) and the source file.</summary>
public sealed record RitaStagedLog(string Folder, string Name, string Capture, string SourcePath);
