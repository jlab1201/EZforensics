namespace EzForensics.Core.SystemInfo;

/// <summary>The IPv4 addresses of one TCP/IP interface key (<c>Services\Tcpip\Parameters\Interfaces\{GUID}</c>).</summary>
public sealed record InterfaceIps(RegKey Key, IReadOnlyList<string> StaticIps, string? DhcpIp);

/// <summary>
/// Reads interface IPs from an offline SYSTEM hive: <c>IPAddress</c> (multi-string) and <c>DhcpIPAddress</c> of every
/// <c>&lt;ControlSet&gt;\Services\Tcpip\Parameters\Interfaces</c> subkey, without 0.0.0.0 / 255.255.255.255. Shared by
/// <see cref="ComputerDetailsCollector"/> and host discovery.
/// </summary>
public static class InterfaceAddresses
{
    public const string InterfacesKey = @"Services\Tcpip\Parameters\Interfaces";

    /// <summary>The interfaces that hold at least one usable IP; <paramref name="skipped"/> = interfaces without one.</summary>
    public static IReadOnlyList<InterfaceIps> Read(RegKey? interfaces, out int skipped)
    {
        skipped = 0;
        var list = new List<InterfaceIps>();
        if (interfaces == null) return list;
        foreach (var k in interfaces.SubKeys)
        {
            var staticIps = (k.Value("IPAddress")?.AsMulti() ?? Array.Empty<string>()).Where(UsableIp).ToList();
            var dhcpIp = k.Value("DhcpIPAddress")?.AsString()?.Trim();
            if (string.IsNullOrWhiteSpace(dhcpIp) || !UsableIp(dhcpIp)) dhcpIp = null;
            if (staticIps.Count == 0 && dhcpIp == null) { skipped++; continue; }
            list.Add(new InterfaceIps(k, staticIps, dhcpIp));
        }
        return list;
    }

    /// <summary>Every usable IP of the hive's current control set (static first, then DHCP, per interface; distinct).</summary>
    public static IReadOnlyList<string> AllIps(IRegistryHive system)
    {
        var cs = ComputerDetailsCollector.ResolveControlSet(system);
        if (cs == null) return Array.Empty<string>();
        var ips = new List<string>();
        foreach (var i in Read(system.GetKey(cs + "\\" + InterfacesKey), out _))
        {
            ips.AddRange(i.StaticIps);
            if (i.DhcpIp != null) ips.Add(i.DhcpIp);
        }
        return ips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool UsableIp(string ip) => ip.Length > 0 && ip != "0.0.0.0" && ip != "255.255.255.255";
}
