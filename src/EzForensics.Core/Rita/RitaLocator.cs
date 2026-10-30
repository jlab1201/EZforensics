using System.Globalization;
using System.Text.RegularExpressions;
using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Rita;

/// <summary>What the Tools / Ingest page should offer.</summary>
public enum RitaSetupOption
{
    /// <summary>Installed and current.</summary>
    None,
    /// <summary>The distro is ready: install RITA into it (<see cref="RitaSetup.InstallAsync"/>).</summary>
    Install,
    /// <summary>Installed, but an older pin / schema: re-run the install.</summary>
    Update,
    /// <summary>WSL is enabled but the EZ Forensics distro is missing or not provisioned: <see cref="EzDistro.EnsureAsync"/> first, then install.</summary>
    InstallDistroThenRita,
    /// <summary>WSL is not enabled (setup's admin step), or wsl.exe is missing.</summary>
    NeedsWsl,
}

/// <summary>Result of <see cref="RitaLocator.Discover"/>.</summary>
/// <param name="Marker">Content of <see cref="RitaSetup.MarkerPath"/> ("rita 5.1.2 / clickhouse 24.1.6.52 / schema 1").</param>
/// <param name="DataBytes">Size of the RITA databases (<see cref="RitaSetup.DataDir"/>).</param>
public sealed record RitaDiscovery(RitaInstall? Selected, EzDistroInfo Distro, string? Marker, string? RitaVersion, string? ClickHouseVersion,
    long? DataBytes, RitaSetupOption Setup, string? Note)
{
    public bool Installed => Selected != null;
    public bool UpdateAvailable => Setup == RitaSetupOption.Update;

    public string Summary => Setup switch
    {
        RitaSetupOption.NeedsWsl => "Needs WSL (run setup to enable it)",
        RitaSetupOption.InstallDistroThenRita => $"Not installed (the {EzDistro.Name} WSL distro is set up first)",
        RitaSetupOption.Install => "Not installed",
        _ => $"RITA {RitaVersion ?? "?"} (ClickHouse {ClickHouseVersion ?? "?"}){(DataBytes is { } b ? ", databases " + (b < 1L << 20 ? "under 1 MB" : EzDistro.FormatBytes(b)) : "")}"
             + (UpdateAvailable ? $" — update to {RitaRelease.Version} available" : ""),
    };
}

/// <summary>Finds RITA in the private EZ Forensics distro (the only place EZ Forensics installs it).</summary>
public static class RitaLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Blocking (one short <c>sh -c</c> in the distro when it exists; starts it when stopped).</summary>
    public static RitaDiscovery Discover(IRuntimeProbe? probe = null)
    {
        probe ??= SystemRuntimeProbe.Instance;
        var info = EzDistro.Inspect(probe);
        if (info.WslExe == null || !info.WslAvailable)
            return new RitaDiscovery(null, info, null, null, null, null, RitaSetupOption.NeedsWsl, info.WslExe == null ? "wsl.exe was not found" : "WSL is not enabled");
        if (!info.Ready) return new RitaDiscovery(null, info, null, null, null, null, RitaSetupOption.InstallDistroThenRita, info.Summary);
        var output = probe.Run(info.WslExe, EzDistro.ScriptArgs(RitaSetup.ProbeScript), ProbeTimeout, WslRuntime.Utf8Env);
        return FromProbe(info, output);
    }

    /// <summary>Pure: interprets <see cref="RitaSetup.ProbeScript"/> output for a ready distro.</summary>
    public static RitaDiscovery FromProbe(EzDistroInfo info, string? probeOutput)
    {
        var map = ParseProbe(probeOutput);
        map.TryGetValue("DATABYTES", out var db);
        long? bytes = long.TryParse(db, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
        if (!map.TryGetValue("EZRITA", out var marker) || marker.Length == 0)
            return new RitaDiscovery(null, info, null, null, null, bytes, RitaSetupOption.Install, null);
        var ritaVer = ParseRitaVersion(map.GetValueOrDefault("RITAVER")) ?? MarkerPart(marker, "rita");
        var chVer = ParseClickHouseVersion(map.GetValueOrDefault("CHVER")) ?? MarkerPart(marker, "clickhouse");
        var install = new RitaInstall(RitaEngine.Wsl, info.WslExe!, ritaVer, chVer, EzDistro.Name);
        var current = marker.Trim().Equals(RitaSetup.MarkerText, StringComparison.Ordinal);
        return new RitaDiscovery(install, info, marker, ritaVer, chVer, bytes, current ? RitaSetupOption.None : RitaSetupOption.Update, null);
    }

    public static Dictionary<string, string> ParseProbe(string? output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in RuntimeText.Clean(output).Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            if (key is "EZRITA" or "RITAVER" or "CHVER" or "DATABYTES") map[key] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>"rita version v5.1.2" / "v5.1.2" → "5.1.2"; null when no version.</summary>
    public static string? ParseRitaVersion(string? text)
    {
        var m = Regex.Match(text ?? "", @"v?(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"ClickHouse local version 24.1.6.52 (official build)." → "24.1.6.52".</summary>
    public static string? ParseClickHouseVersion(string? text)
    {
        var m = Regex.Match(text ?? "", @"version\s+(\d+(\.\d+){1,3})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Value after <paramref name="name"/> in the marker ("rita 5.1.2 / clickhouse 24.1.6.52 / schema 1").</summary>
    public static string? MarkerPart(string marker, string name)
    {
        foreach (var part in marker.Split('/'))
        {
            var p = part.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2 && p[0].Equals(name, StringComparison.OrdinalIgnoreCase)) return p[1].Trim();
        }
        return null;
    }
}
