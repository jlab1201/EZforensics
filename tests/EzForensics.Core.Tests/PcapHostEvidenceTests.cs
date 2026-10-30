using System.Buffers.Binary;
using System.Text;
using EzForensics.Core.Network;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Synthetic packet / capture builders for <see cref="PcapHostEvidence"/> and <see cref="PcapHostMatcher"/> tests.</summary>
internal static class PcapBuild
{
    public static byte[] Ip(string s) => s.Split('.').Select(byte.Parse).ToArray();

    public static byte[] Eth(ushort type, byte[] payload) =>
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0x11, 0x22, 0x33, 0x44, 0x55, (byte)(type >> 8), (byte)type }.Concat(payload).ToArray();

    public static byte[] Vlan(ushort type, byte[] payload) =>
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x81, 0x00, 0x00, 0x0A, (byte)(type >> 8), (byte)type }.Concat(payload).ToArray();

    public static byte[] Ipv4(byte proto, string src, string dst, byte[] l4)
    {
        var h = new byte[20];
        h[0] = 0x45; h[8] = 64; h[9] = proto;
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)(20 + l4.Length));
        Ip(src).CopyTo(h, 12); Ip(dst).CopyTo(h, 16);
        return h.Concat(l4).ToArray();
    }

    public static byte[] Udp(int sp, int dp, byte[] payload)
    {
        var h = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)sp); BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)dp);
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(4), (ushort)(8 + payload.Length));
        return h.Concat(payload).ToArray();
    }

    public static byte[] Tcp(int sp, int dp, byte[] payload)
    {
        var h = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)sp); BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), (ushort)dp);
        h[12] = 5 << 4; h[13] = 0x18;
        return h.Concat(payload).ToArray();
    }

    public static byte[] UdpPacket(string src, string dst, int sp, int dp, byte[] payload) => Eth(0x0800, Ipv4(17, src, dst, Udp(sp, dp, payload)));
    public static byte[] TcpPacket(string src, string dst, int sp, int dp, byte[] payload) => Eth(0x0800, Ipv4(6, src, dst, Tcp(sp, dp, payload)));

    public static byte[] Arp(string senderIp, string targetIp, ushort op = 1)
    {
        var a = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(a, 1); BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(2), 0x0800);
        a[4] = 6; a[5] = 4; BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(6), op);
        Ip(senderIp).CopyTo(a, 14); Ip(targetIp).CopyTo(a, 24);
        return Eth(0x0806, a);
    }

    public static byte[] Dhcp(string hostname, string requested)
    {
        var b = new byte[240];
        b[0] = 1; b[1] = 1; b[2] = 6;
        b[236] = 0x63; b[237] = 0x82; b[238] = 0x53; b[239] = 0x63;
        var opts = new List<byte> { 53, 1, 3, 50, 4 };
        opts.AddRange(Ip(requested));
        opts.Add(12); opts.Add((byte)hostname.Length); opts.AddRange(Encoding.ASCII.GetBytes(hostname));
        opts.Add(255);
        return UdpPacket("0.0.0.0", "255.255.255.255", 68, 67, b.Concat(opts).ToArray());
    }

    public static byte[] NbName(string name, byte suffix = 0)
    {
        var raw = Encoding.ASCII.GetBytes(name.ToUpperInvariant().PadRight(15)).Concat(new[] { suffix }).ToArray();
        var enc = new List<byte> { 0x20 };
        foreach (var c in raw) { enc.Add((byte)('A' + (c >> 4))); enc.Add((byte)('A' + (c & 0x0F))); }
        enc.Add(0);
        return enc.ToArray();
    }

    /// <summary>NBNS name registration request (opcode 5) broadcast from <paramref name="ip"/>.</summary>
    public static byte[] NbnsRegistration(string name, string ip, bool group = false)
    {
        var p = new List<byte> { 0x12, 0x34, 0x29, 0x10, 0, 1, 0, 0, 0, 0, 0, 1 };
        p.AddRange(NbName(name));
        p.AddRange(new byte[] { 0, 0x20, 0, 1, 0xC0, 0x0C, 0, 0x20, 0, 1, 0, 0, 0x0D, 0x80, 0, 6, (byte)(group ? 0x80 : 0), 0 });
        p.AddRange(Ip(ip));
        return UdpPacket(ip, "10.0.0.255", 137, 137, p.ToArray());
    }

    public static byte[] NbnsQuery(string name, string ip)
    {
        var p = new List<byte> { 0x12, 0x35, 0x01, 0x10, 0, 1, 0, 0, 0, 0, 0, 0 };
        p.AddRange(NbName(name));
        p.AddRange(new byte[] { 0, 0x20, 0, 1 });
        return UdpPacket(ip, "10.0.0.255", 137, 137, p.ToArray());
    }

    /// <summary>LLMNR / mDNS response with one A record.</summary>
    public static byte[] DnsAnswer(string src, int port, string name, string aIp)
    {
        var p = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
        foreach (var label in name.Split('.')) { p.Add((byte)label.Length); p.AddRange(Encoding.ASCII.GetBytes(label)); }
        p.Add(0);
        p.AddRange(new byte[] { 0, 1, 0, 1, 0, 0, 0, 30, 0, 4 });
        p.AddRange(Ip(aIp));
        return UdpPacket(src, port == 5353 ? "224.0.0.251" : "10.0.0.9", port, port == 5353 ? 5353 : 50000, p.ToArray());
    }

    /// <summary>NTLMSSP AUTHENTICATE message (Unicode) inside an SMB-like payload.</summary>
    public static byte[] NtlmAuthenticate(string workstation, string domain, string user)
    {
        var d = Encoding.Unicode.GetBytes(domain); var u = Encoding.Unicode.GetBytes(user); var w = Encoding.Unicode.GetBytes(workstation);
        var m = new byte[64];
        Encoding.ASCII.GetBytes("NTLMSSP\0").CopyTo(m, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 3);
        void Field(int at, int len, int off) { BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(at), (ushort)len); BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(at + 2), (ushort)len); BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(at + 4), (uint)off); }
        Field(28, d.Length, 64); Field(36, u.Length, 64 + d.Length); Field(44, w.Length, 64 + d.Length + u.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(60), 0xE2088235);   // NTLMSSP_NEGOTIATE_UNICODE set
        var prefix = new byte[] { 0, 0, 0, 0x90, 0xFE, (byte)'S', (byte)'M', (byte)'B', 0x40, 0, 1, 0, 0x30, 0x82 };
        return prefix.Concat(m).Concat(d).Concat(u).Concat(w).ToArray();
    }

    public static byte[] Pcap(IEnumerable<byte[]> packets, bool big = false, bool nano = false, uint link = 1, uint ts0 = 1_700_000_000)
    {
        var ms = new MemoryStream();
        void W(uint v) { var b = new byte[4]; if (big) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void W16(ushort v) { var b = new byte[2]; if (big) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        W(nano ? 0xA1B23C4D : 0xA1B2C3D4); W16(2); W16(4); W(0); W(0); W(65535); W(link);
        uint i = 0;
        foreach (var p in packets) { W(ts0 + i / 10); W((i % 10) * 1000); W((uint)p.Length); W((uint)p.Length); ms.Write(p); i++; }
        return ms.ToArray();
    }

    public static byte[] PcapNg(IEnumerable<byte[]> packets, ushort link = 1, bool simple = false)
    {
        var ms = new MemoryStream();
        void W(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void Block(uint type, byte[] body)
        {
            int pad = (4 - body.Length % 4) % 4;
            uint len = (uint)(12 + body.Length + pad);
            W(type); W(len); ms.Write(body); ms.Write(new byte[pad]); W(len);
        }
        var shb = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(shb, 0x1A2B3C4D); BinaryPrimitives.WriteUInt16LittleEndian(shb.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(shb.AsSpan(8), -1);
        Block(0x0A0D0D0A, shb);
        var idb = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(idb, link); BinaryPrimitives.WriteUInt32LittleEndian(idb.AsSpan(4), 65535);
        Block(1, idb);
        foreach (var p in packets)
        {
            if (simple)
            {
                var b = new byte[4 + p.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)p.Length); p.CopyTo(b, 4);
                Block(3, b);
            }
            else
            {
                var b = new byte[20 + p.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)p.Length); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)p.Length);
                p.CopyTo(b, 20);
                Block(6, b);
            }
        }
        return ms.ToArray();
    }

    /// <summary>The host evidence mix: traffic of 10.0.0.5 plus ARP, DHCP, NBNS, LLMNR and NTLM of WS01.</summary>
    public static List<byte[]> HostMix()
    {
        var list = new List<byte[]>();
        for (int i = 0; i < 10; i++) list.Add(TcpPacket("10.0.0.5", "8.8.8.8", 50000 + i, 443, new byte[] { 1, 2, 3 }));
        list.Add(UdpPacket("10.0.0.9", "1.1.1.1", 5000, 53, new byte[4]));
        list.Add(Arp("10.0.0.5", "10.0.0.1"));
        list.Add(Arp("0.0.0.0", "10.0.0.77"));                   // probe: no sender IP
        list.Add(Dhcp("WS01", "10.0.0.5"));
        list.Add(NbnsRegistration("WS01", "10.0.0.5"));
        list.Add(NbnsRegistration("CORP", "10.0.0.5", group: true));
        list.Add(NbnsQuery("FILESRV", "10.0.0.5"));
        list.Add(DnsAnswer("10.0.0.5", 5355, "WS01", "10.0.0.5"));
        list.Add(DnsAnswer("10.0.0.5", 5353, "ws01.local", "10.0.0.5"));
        list.Add(TcpPacket("10.0.0.5", "10.0.0.10", 50123, 445, NtlmAuthenticate("WS01", "CORP", "alice")));
        return list;
    }
}

public class PcapHostEvidenceTests
{
    private static PcapEvidence Read(byte[] file, PcapEvidenceOptions? o = null) => PcapHostEvidence.Read(new MemoryStream(file), o);

    private static void AssertHostMix(PcapEvidence e)
    {
        e.DominantIp.Should().Be("10.0.0.5");
        e.IpPackets["10.0.0.5"].Should().BeGreaterThan(10);
        e.IpPackets.Should().NotContainKey("255.255.255.255").And.NotContainKey("0.0.0.0").And.NotContainKey("224.0.0.251");
        e.DominantShare.Should().BeGreaterThan(0.8);
        e.ArpSenderIps.Should().BeEquivalentTo(new[] { "10.0.0.5" });
        e.DhcpIps.Should().Contain("10.0.0.5");
        e.Names.Should().Contain(n => n.Name == "WS01" && n.Kind == "DHCP hostname" && n.Owned);
        e.Names.Should().Contain(n => n.Name == "WS01" && n.Kind == "NBNS registration" && n.Ip == "10.0.0.5" && n.Owned);
        e.Names.Should().NotContain(n => n.Name == "CORP");                       // group name
        e.Names.Should().Contain(n => n.Name == "FILESRV" && !n.Owned);
        e.Names.Should().Contain(n => n.Name == "WS01" && n.Kind == "LLMNR answer" && n.Owned);
        e.Names.Should().Contain(n => n.Name == "ws01" && n.Kind == "mDNS answer" && n.Owned);
        e.Ntlm.Should().ContainSingle().Which.Should().Be(new PcapNtlmEvidence("WS01", "CORP", "alice", "10.0.0.5"));
        e.Names.Should().Contain(n => n.Name == "WS01" && n.Kind == "NTLM workstation");
    }

    [Fact]
    public void Classic_pcap_collects_tally_arp_dhcp_nbns_llmnr_mdns_and_ntlm()
    {
        var e = Read(PcapBuild.Pcap(PcapBuild.HostMix()));
        e.Format.Should().Be("pcap");
        e.PacketsRead.Should().Be(PcapBuild.HostMix().Count);
        AssertHostMix(e);
        e.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Big_endian_nanosecond_pcap_and_pcapng_give_the_same_evidence()
    {
        AssertHostMix(Read(PcapBuild.Pcap(PcapBuild.HostMix(), big: true, nano: true)));
        var ng = Read(PcapBuild.PcapNg(PcapBuild.HostMix()));
        ng.Format.Should().Be("pcapng");
        AssertHostMix(ng);
        AssertHostMix(Read(PcapBuild.PcapNg(PcapBuild.HostMix(), simple: true)));
    }

    [Fact]
    public void Vlan_raw_ipv4_and_sll2_link_types()
    {
        var vlan = PcapBuild.Vlan(0x0800, PcapBuild.Ipv4(6, "10.1.1.1", "10.1.1.2", PcapBuild.Tcp(1, 2, Array.Empty<byte>())));
        Read(PcapBuild.Pcap(new[] { vlan })).DominantIp.Should().NotBeNull();
        var raw = PcapBuild.Ipv4(17, "10.2.2.2", "10.2.2.2", PcapBuild.Udp(1, 2, new byte[2]));
        Read(PcapBuild.Pcap(new[] { raw }, link: 101)).DominantIp.Should().Be("10.2.2.2");
        var sll2 = new byte[] { 0x08, 0x00 }.Concat(new byte[18]).Concat(raw).ToArray();
        Read(PcapBuild.PcapNg(new[] { sll2 }, link: 276)).DominantIp.Should().Be("10.2.2.2");
        var sll = new byte[14].Concat(new byte[] { 0x08, 0x00 }).Concat(raw).ToArray();
        Read(PcapBuild.Pcap(new[] { sll }, link: 113)).DominantIp.Should().Be("10.2.2.2");
    }

    [Fact]
    public void Truncated_and_garbage_files_never_throw()
    {
        var full = PcapBuild.Pcap(PcapBuild.HostMix());
        var cut = Read(full[..^10]);
        cut.PacketsRead.Should().Be(PcapBuild.HostMix().Count - 1);
        cut.Notes.Should().Contain(n => n.Contains("truncated"));
        cut.DominantIp.Should().Be("10.0.0.5");

        var ng = PcapBuild.PcapNg(PcapBuild.HostMix());
        Read(ng[..^7]).Notes.Should().Contain(n => n.Contains("truncated"));

        Read(new byte[] { 1, 2, 3 }).Notes.Should().NotBeEmpty();
        Read(Encoding.ASCII.GetBytes("this is not a capture at all, just some text")).Notes.Should().Contain(n => n.Contains("unknown magic"));
        var rnd = new byte[5000]; new Random(4).NextBytes(rnd);
        BitConverter.GetBytes(0xA1B2C3D4u).CopyTo(rnd, 0);
        Read(rnd).Should().NotBeNull();
        PcapHostEvidence.Read(Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N") + ".pcap")).Notes.Should().ContainSingle();
    }

    private static List<byte[]> Traffic(int n)
    {
        var list = new List<byte[]>();
        for (int i = 0; i < n; i++) list.Add(PcapBuild.TcpPacket("10.0.0.5", $"8.8.{i % 200}.8", 1000 + i % 50000, 443, new byte[i % 300]));
        return list;
    }

    [Fact]
    public void Windows_resync_past_a_corrupt_region_in_pcap_and_pcapng()
    {
        var o = new PcapEvidenceOptions { HeadPackets = 100, Windows = 4, WindowPackets = 50 };
        foreach (var file in new[] { PcapBuild.Pcap(Traffic(4000)), PcapBuild.PcapNg(Traffic(4000)) })
        {
            var e = Read(file, o);
            e.PacketsRead.Should().Be(100 + 4 * 50);
            e.Notes.Should().Contain(n => n.Contains("4 window"));
            e.DominantIp.Should().Be("10.0.0.5");
            e.DominantShare.Should().Be(1.0);

            // garbage right after the head: the head stops there, the windows still resync further on
            var bad = (byte[])file.Clone();
            var junk = new byte[3000]; new Random(7).NextBytes(junk);
            junk.CopyTo(bad, file.Length / 20);
            var r = Read(bad, o with { HeadPackets = 20_000 });
            r.PacketsRead.Should().BeGreaterThan(50);
            r.DominantIp.Should().Be("10.0.0.5");
        }
    }

    [Fact]
    public void Byte_budget_stops_the_sample()
    {
        var e = Read(PcapBuild.Pcap(Traffic(3000)), new PcapEvidenceOptions { MaxBytes = 20_000 });
        e.Notes.Should().Contain(n => n.Contains("sample limit"));
        e.PacketsRead.Should().BeLessThan(3000);
    }
}
