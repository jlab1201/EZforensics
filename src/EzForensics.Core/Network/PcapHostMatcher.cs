using EzForensics.Core.Triage;

namespace EzForensics.Core.Network;

/// <summary>A host a capture may belong to: its case host folder name, other known names, system root (null for a host of an earlier ingest) and IPs.</summary>
public sealed record PcapHostCandidate(string HostName, string? RootPath, IReadOnlyList<string> IpAddresses)
{
    /// <summary>More names of the host (computer name, TCP/IP host name) besides <see cref="HostName"/>.</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    /// <summary>The discovered system of the source (null for a host of an earlier ingest).</summary>
    public DiscoveredSystem? System { get; init; }

    public static PcapHostCandidate From(DiscoveredSystem s) => new(s.HostName, s.RootPath, s.IpAddresses) { System = s };
}

/// <summary>Which host a capture was collected on (null = none identified: case-wide) and how that was decided.</summary>
public sealed record PcapHostMatch(PcapHostCandidate? Host, string Method)
{
    public const string NotIdentified = "source host not identified";
}

/// <summary>
/// Assigns a packet capture to the host it was collected on, from its location and the evidence inside it, in this order:
/// (1) the capture sits under the host's system root; (2) the dominant IPv4 address (share ≥ 0.5, clearly ahead of the runner-up) is an IP
/// of exactly one host; (3) a DHCP / NBNS / LLMNR / mDNS / NTLM workstation name the capture's host announced equals exactly one host's name
/// (case-insensitive, NetBIOS 15-character truncation aware); (4) an ARP sender or DHCP client IP is an IP of exactly one host. A step that
/// matches several hosts is ambiguous and moves on to the next; when nothing matches the result has no host (case-wide).
/// </summary>
public static class PcapHostMatcher
{
    public const double MinDominantShare = 0.5;
    /// <summary>The runner-up may have at most this fraction of the dominant address's share.</summary>
    public const double MaxRunnerUpRatio = 0.75;
    /// <summary>An address in nearly every packet is the capture host even when one peer is busy too (e.g. most traffic to one C2), unless that peer is also in nearly every packet.</summary>
    public const double NearlyAll = 0.9;

    public static PcapHostMatch Match(string capturePath, PcapEvidence? evidence, IReadOnlyList<PcapHostCandidate> hosts)
    {
        if (hosts.Count == 0) return new PcapHostMatch(null, PcapHostMatch.NotIdentified);

        // 1. location
        var under = hosts.Where(h => !string.IsNullOrEmpty(h.RootPath) && IsUnder(capturePath, h.RootPath!))
            .OrderByDescending(h => h.RootPath!.Length).FirstOrDefault();
        if (under != null) return new PcapHostMatch(under, "matched by capture location");
        if (evidence == null) return new PcapHostMatch(null, PcapHostMatch.NotIdentified);

        // 2. dominant IP
        if (evidence.DominantIp is { } dom && evidence.DominantShare >= MinDominantShare &&
            (evidence.SecondShare <= evidence.DominantShare * MaxRunnerUpRatio || (evidence.DominantShare >= NearlyAll && evidence.SecondShare < NearlyAll)))
        {
            var byIp = hosts.Where(h => HasIp(h, dom)).ToList();
            if (byIp.Count == 1) return new PcapHostMatch(byIp[0], $"matched by IP {dom}");
        }

        // 3. announced host names
        var named = new List<(PcapHostCandidate Host, PcapNameEvidence Name)>();
        foreach (var n in evidence.Names.Where(n => n.Owned))
            foreach (var h in hosts)
                if (NameMatches(h, n.Name)) named.Add((h, n));
        var namedHosts = named.Select(x => x.Host).Distinct().ToList();
        if (namedHosts.Count == 1)
        {
            var best = named.OrderBy(x => KindRank(x.Name.Kind)).First().Name;
            return new PcapHostMatch(namedHosts[0], $"matched by {best.Kind} {best.Name}");
        }

        // 4. ARP sender / DHCP client IPs
        var seen = evidence.ArpSenderIps.Select(ip => (Ip: ip, Kind: "ARP")).Concat(evidence.DhcpIps.Select(ip => (Ip: ip, Kind: "DHCP"))).ToList();
        var byArp = seen.SelectMany(s => hosts.Where(h => HasIp(h, s.Ip)).Select(h => (Host: h, s.Ip, s.Kind))).ToList();
        var arpHosts = byArp.Select(x => x.Host).Distinct().ToList();
        if (arpHosts.Count == 1) return new PcapHostMatch(arpHosts[0], $"matched by {byArp[0].Kind} IP {byArp[0].Ip}");

        return new PcapHostMatch(null, PcapHostMatch.NotIdentified);
    }

    private static int KindRank(string kind) => kind.StartsWith("NTLM", StringComparison.Ordinal) ? 0
        : kind.StartsWith("DHCP", StringComparison.Ordinal) ? 1 : kind.StartsWith("NBNS", StringComparison.Ordinal) ? 2 : 3;

    public static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasIp(PcapHostCandidate h, string ip) => h.IpAddresses.Any(i => string.Equals(i, ip, StringComparison.OrdinalIgnoreCase));

    private static bool NameMatches(PcapHostCandidate h, string seen) =>
        SameHostName(h.HostName, seen) || h.Names.Any(n => SameHostName(n, seen));

    /// <summary>
    /// Case-insensitive host name equality on the first DNS label; a 15-character NetBIOS name also equals a longer name that starts with it.
    /// </summary>
    public static bool SameHostName(string a, string b)
    {
        a = FirstLabel(a); b = FirstLabel(b);
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var (shortName, longName) = a.Length <= b.Length ? (a, b) : (b, a);
        return shortName.Length == 15 && longName.Length > 15 && longName.StartsWith(shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLabel(string n)
    {
        n = n.Trim();
        int dot = n.IndexOf('.');
        return dot > 0 ? n[..dot] : n;
    }
}
