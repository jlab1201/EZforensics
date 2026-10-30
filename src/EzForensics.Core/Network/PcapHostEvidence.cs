using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace EzForensics.Core.Network;

/// <summary>A host name seen in a capture. <see cref="Owned"/> = announced by the host itself (registration, answer, DHCP client, NTLM client).</summary>
public sealed record PcapNameEvidence(string Name, string Kind, string? Ip, bool Owned);

/// <summary>An NTLMSSP AUTHENTICATE (type 3) message: the client workstation / domain / user, sent from <see cref="ClientIp"/>.</summary>
public sealed record PcapNtlmEvidence(string? Workstation, string? Domain, string? User, string ClientIp);

/// <summary>What a sample of a packet capture says about the host it was collected on (<see cref="PcapHostEvidence.Read(string, PcapEvidenceOptions?, CancellationToken)"/>).</summary>
public sealed class PcapEvidence
{
    public string Format { get; set; } = "";
    /// <summary>IPv4 packets per unicast address (counted once per packet as source or destination).</summary>
    public Dictionary<string, int> IpPackets { get; } = new(StringComparer.Ordinal);
    /// <summary>Sampled IPv4 packets.</summary>
    public int IpPacketCount { get; set; }
    public int PacketsRead { get; set; }
    public long BytesRead { get; set; }
    /// <summary>The address in the most sampled IPv4 packets (null without IPv4 traffic).</summary>
    public string? DominantIp { get; set; }
    /// <summary>Share of sampled IPv4 packets that involve <see cref="DominantIp"/> (0..1).</summary>
    public double DominantShare { get; set; }
    /// <summary>Same share for the runner-up address.</summary>
    public double SecondShare { get; set; }
    /// <summary>ARP sender IPs (requests, replies, gratuitous; probes from 0.0.0.0 are left out).</summary>
    public HashSet<string> ArpSenderIps { get; } = new(StringComparer.Ordinal);
    /// <summary>DHCP client IPs: option 50 requested IP, yiaddr, ciaddr.</summary>
    public HashSet<string> DhcpIps { get; } = new(StringComparer.Ordinal);
    public List<PcapNameEvidence> Names { get; } = new();
    public List<PcapNtlmEvidence> Ntlm { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>Sampling limits for <see cref="PcapHostEvidence"/>.</summary>
public sealed record PcapEvidenceOptions
{
    public int HeadPackets { get; init; } = 20_000;
    public int Windows { get; init; } = 8;
    public int WindowPackets { get; init; } = 500;
    public long MaxBytes { get; init; } = 64L << 20;
    public TimeSpan MaxTime { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Pure, streaming reader of host evidence in a pcap / pcapng file (no external libraries): samples the first packets plus a few windows
/// spread through the file (resynchronising on a plausible record / block header) and collects IPv4 address tallies, ARP senders, DHCP,
/// NBNS / LLMNR / mDNS names and NTLMSSP AUTHENTICATE workstation names. IPv4 only; link types Ethernet (VLAN), Linux SLL / SLL2 and raw IPv4.
/// Bounded memory and time; never throws on a malformed file (what was collected is returned, with a note).
/// </summary>
public static class PcapHostEvidence
{
    private const int MaxPacket = 262_144;
    private const int MaxBlock = 1 << 20;
    private const int ResyncBuffer = 512 * 1024;
    private const int MaxTallyAddresses = 200_000;
    private const int MaxNames = 2_000;
    private static readonly byte[] NtlmSig = Encoding.ASCII.GetBytes("NTLMSSP\0");

    public static PcapEvidence Read(string path, PcapEvidenceOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Read(fs, options, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var e = new PcapEvidence();
            e.Notes.Add("Capture could not be opened: " + ex.Message);
            return e;
        }
    }

    public static PcapEvidence Read(Stream stream, PcapEvidenceOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new PcapEvidenceOptions();
        var st = new State(stream, o, ct);
        try
        {
            var head = new byte[24];
            if (!st.Fill(head, 24)) { st.E.Notes.Add("File too short for a capture header."); return st.Finish(); }
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (m is 0xA1B2C3D4 or 0xA1B23C4D or 0xD4C3B2A1 or 0x4D3CB2A1) ReadPcap(st, head);
            else if (m == 0x0A0D0D0A) ReadPcapNg(st, head);
            else st.E.Notes.Add("Not a pcap / pcapng file (unknown magic).");
        }
        catch (OperationCanceledException) { st.E.Notes.Add("Stopped: cancelled."); }
        catch (Exception ex) { st.E.Notes.Add("Stopped on a malformed capture: " + ex.GetType().Name); }
        return st.Finish();
    }

    // ------------------------------------------------------------------ state

    private sealed class State
    {
        public State(Stream s, PcapEvidenceOptions o, CancellationToken ct) { S = s; O = o; Ct = ct; Clock = Stopwatch.StartNew(); }
        public readonly Stream S;
        public readonly PcapEvidenceOptions O;
        public readonly CancellationToken Ct;
        public readonly Stopwatch Clock;
        public readonly PcapEvidence E = new();
        public readonly Dictionary<uint, int> Tally = new();
        public readonly HashSet<string> NameKeys = new(StringComparer.OrdinalIgnoreCase);
        public bool Exhausted;

        public long Length { get { try { return S.CanSeek ? S.Length : -1; } catch (Exception) { return -1; } } }

        public bool OverBudget()
        {
            if (Exhausted) return true;
            if (E.BytesRead >= O.MaxBytes) { Exhausted = true; E.Notes.Add($"Stopped after reading {O.MaxBytes >> 20} MB (sample limit)."); }
            else if (Clock.Elapsed >= O.MaxTime) { Exhausted = true; E.Notes.Add($"Stopped after {O.MaxTime.TotalSeconds:0} s (time limit)."); }
            Ct.ThrowIfCancellationRequested();
            return Exhausted;
        }

        /// <summary>Reads exactly <paramref name="n"/> bytes; false at end of file (truncated).</summary>
        public bool Fill(byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = S.Read(buf, got, n - got);
                if (r <= 0) break;
                got += r;
            }
            E.BytesRead += got;
            return got == n;
        }

        public int ReadUpTo(byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = S.Read(buf, got, n - got);
                if (r <= 0) break;
                got += r;
            }
            E.BytesRead += got;
            return got;
        }

        public PcapEvidence Finish()
        {
            var e = E;
            foreach (var (ip, n) in Tally) e.IpPackets[Ip(ip)] = n;
            if (e.IpPacketCount > 0 && Tally.Count > 0)
            {
                var top = Tally.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(2).ToList();
                e.DominantIp = Ip(top[0].Key);
                e.DominantShare = top[0].Value / (double)e.IpPacketCount;
                e.SecondShare = top.Count > 1 ? top[1].Value / (double)e.IpPacketCount : 0;
            }
            return e;
        }

        public void AddName(string name, string kind, string? ip, bool owned)
        {
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0 || name.Length > 255 || E.Names.Count >= MaxNames) return;
            if (NameKeys.Add($"{name}|{kind}|{ip}|{owned}")) E.Names.Add(new PcapNameEvidence(name, kind, ip, owned));
        }
    }

    // ------------------------------------------------------------------ classic pcap

    private sealed class PcapHeader { public bool Big; public bool Nano; public uint Snap; public uint Link; public uint FirstTs; }

    private static void ReadPcap(State st, byte[] head)
    {
        uint le = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var h = new PcapHeader { Big = le is 0xD4C3B2A1 or 0x4D3CB2A1, Nano = le is 0xA1B23C4D or 0x4D3CB2A1 };
        h.Snap = U32(head, 16, h.Big);
        h.Link = U32(head, 20, h.Big) & 0x0FFFFFFF;
        st.E.Format = h.Nano ? "pcap (ns)" : "pcap";
        var buf = new byte[MaxPacket];
        var rec = new byte[16];
        bool first = true;

        bool ReadRecords(int max)
        {
            for (int i = 0; i < max; i++)
            {
                if ((i & 255) == 0 && st.OverBudget()) return false;
                int got = st.ReadUpTo(rec, 16);
                if (got == 0) return false;                                // clean end of file
                if (got < 16) { Note(st, TruncatedNote); return false; }
                uint ts = U32(rec, 0, h.Big), sub = U32(rec, 4, h.Big), cap = U32(rec, 8, h.Big), orig = U32(rec, 12, h.Big);
                if (!PlausiblePcap(h, ts, sub, cap, orig, first ? null : h.FirstTs)) { Note(st, "Implausible record header; resynchronised further on."); return false; }
                if (first) { h.FirstTs = ts; first = false; }
                if (!st.Fill(buf, (int)cap)) { Note(st, TruncatedNote); return false; }
                st.E.PacketsRead++;
                Packet(st, (int)h.Link, buf.AsSpan(0, (int)cap));
            }
            return true;
        }

        ReadRecords(st.O.HeadPackets);
        Windows(st, 24, (offset, window) =>
        {
            int n = window.Length;
            for (int p = 0; p + 16 <= n; p++)
            {
                uint ts = U32(window, p, h.Big), sub = U32(window, p + 4, h.Big), cap = U32(window, p + 8, h.Big), orig = U32(window, p + 12, h.Big);
                if (!PlausiblePcap(h, ts, sub, cap, orig, first ? null : h.FirstTs)) continue;
                long next = p + 16L + cap;
                if (next + 16 > n)
                {
                    if (offset + next != st.Length) continue;       // only the very last record may end at EOF
                }
                else
                {
                    uint ts2 = U32(window, (int)next, h.Big), sub2 = U32(window, (int)next + 4, h.Big), cap2 = U32(window, (int)next + 8, h.Big),
                        orig2 = U32(window, (int)next + 12, h.Big);
                    if (!PlausiblePcap(h, ts2, sub2, cap2, orig2, ts)) continue;
                }
                return p;
            }
            return -1;
        }, () => ReadRecords(st.O.WindowPackets));
    }

    private static bool PlausiblePcap(PcapHeader h, uint ts, uint sub, uint cap, uint orig, uint? near)
    {
        uint snap = h.Snap is > 0 and <= MaxPacket ? h.Snap : MaxPacket;
        if (cap == 0 || cap > snap || orig < cap || orig > 1u << 20) return false;
        if (sub >= (h.Nano ? 1_000_000_000u : 1_000_000u)) return false;
        if (near is { } n && (ts + 86_400L * 31 < n || ts > n + 86_400L * 366 * 5)) return false;
        return true;
    }

    private static void Note(State st, string note) { if (!st.E.Notes.Contains(note)) st.E.Notes.Add(note); }
    private static void Truncated(State st, int _) => Note(st, TruncatedNote);
    private const string TruncatedNote = "Capture is truncated (last record incomplete).";

    /// <summary>
    /// Sampling windows after the head: seeks to evenly spread offsets past what was read, finds a record start with <paramref name="sync"/>
    /// (index in the window buffer or -1) and reads from there with <paramref name="read"/>.
    /// </summary>
    private static void Windows(State st, long dataStart, Func<long, byte[], int> sync, Action read)
    {
        long len = st.Length;
        if (len <= 0 || st.Exhausted || st.O.Windows <= 0) return;
        long done;
        try { done = st.S.Position; } catch (Exception) { return; }
        var buf = new byte[ResyncBuffer];
        int found = 0;
        for (int i = 0; i < st.O.Windows; i++)
        {
            if (st.OverBudget()) return;
            long off = dataStart + (len - dataStart) * (i + 1) / (st.O.Windows + 1);
            if (off <= done) continue;
            st.S.Seek(off, SeekOrigin.Begin);
            int n = st.ReadUpTo(buf, buf.Length);
            if (n < 16) break;
            int p = sync(off, n == buf.Length ? buf : buf[..n]);
            if (p < 0) continue;
            st.S.Seek(off + p, SeekOrigin.Begin);
            found++;
            read();
            try { done = st.S.Position; } catch (Exception) { return; }
        }
        if (found > 0) st.E.Notes.Add($"Sampled {found} window(s) through the file.");
    }

    // ------------------------------------------------------------------ pcapng

    private static readonly HashSet<uint> KnownBlocks = new() { 0x0A0D0D0A, 1, 2, 3, 4, 5, 6, 9, 10, 0x00000BAD, 0x40000BAD };

    private static void ReadPcapNg(State st, byte[] head)
    {
        st.E.Format = "pcapng";
        uint bom = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        bool big = bom == 0x4D3C2B1A;
        if (!big && bom != 0x1A2B3C4D) { st.E.Notes.Add("pcapng section header has no byte-order magic."); return; }
        var links = new List<int>();
        var body = new byte[MaxBlock];
        // the first SHB: skip its remaining bytes
        uint shbLen = U32(head, 4, big);
        if (shbLen < 28 || shbLen > MaxBlock || shbLen % 4 != 0) { st.E.Notes.Add("pcapng section header length is invalid."); return; }
        if (!st.Fill(body, (int)shbLen - 24)) { Truncated(st, 0); return; }

        bool ReadBlocks(int maxPackets)
        {
            int packets = 0, blocks = 0;
            var bh = new byte[8];
            while (packets < maxPackets)
            {
                if ((blocks++ & 255) == 0 && st.OverBudget()) return false;
                int got = st.ReadUpTo(bh, 8);
                if (got == 0) return false;                               // clean end of file
                if (got < 8) { Truncated(st, 0); return false; }
                uint type = U32(bh, 0, big), len = U32(bh, 4, big);
                if (type == 0x0A0D0D0A)
                {
                    if (!st.Fill(body, 4)) { Truncated(st, 0); return false; }
                    uint b = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    if (b == 0x4D3C2B1A) big = !big; else if (b != 0x1A2B3C4D) { st.E.Notes.Add("Bad pcapng section header."); return false; }
                    len = U32(bh, 4, big);
                    if (len < 28 || len > MaxBlock || len % 4 != 0) { st.E.Notes.Add("Bad pcapng block length."); return false; }
                    if (!st.Fill(body, (int)len - 12)) { Truncated(st, 0); return false; }
                    links.Clear();
                    continue;
                }
                if (len < 12 || len % 4 != 0 || len > MaxBlock) { st.E.Notes.Add("Bad pcapng block length; skipping ahead."); return false; }
                int bodyLen = (int)len - 8;
                if (!st.Fill(body, bodyLen)) { Truncated(st, 0); return false; }
                var span = body.AsSpan(0, bodyLen - 4);
                switch (type)
                {
                    case 1 when span.Length >= 8:
                        links.Add(big ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span));
                        break;
                    case 6 when span.Length >= 20:
                    {
                        int ifc = (int)U32(span, 0, big);
                        uint cap = U32(span, 12, big);
                        if (cap > span.Length - 20) cap = (uint)(span.Length - 20);
                        st.E.PacketsRead++; packets++;
                        if (ifc >= 0 && ifc < links.Count) Packet(st, links[ifc], span.Slice(20, (int)cap));
                        break;
                    }
                    case 3 when span.Length >= 4:
                    {
                        uint orig = U32(span, 0, big);
                        int cap = (int)Math.Min(orig, (uint)(span.Length - 4));
                        st.E.PacketsRead++; packets++;
                        if (links.Count > 0) Packet(st, links[0], span.Slice(4, cap));
                        break;
                    }
                }
            }
            return true;
        }

        ReadBlocks(st.O.HeadPackets);
        Windows(st, 0, (offset, w) =>
        {
            int n = w.Length;
            int start = (int)((4 - offset % 4) % 4);
            for (int p = start; p + 12 <= n; p += 4)
            {
                uint type = U32(w, p, big), len = U32(w, p + 4, big);
                if (type == 0x0A0D0D0A || !KnownBlocks.Contains(type) || len < 12 || len % 4 != 0 || len > MaxBlock) continue;
                if (p + (long)len > n) continue;
                if (U32(w, p + (int)len - 4, big) != len) continue;
                if (type is 6 or 3 && links.Count == 0) continue;
                return p;
            }
            return -1;
        }, () => ReadBlocks(st.O.WindowPackets));
    }

    // ------------------------------------------------------------------ packets

    private static void Packet(State st, int link, ReadOnlySpan<byte> d)
    {
        try
        {
            switch (link)
            {
                case 1: Ethernet(st, d); break;
                case 113:
                    if (d.Length >= 16) L3(st, BinaryPrimitives.ReadUInt16BigEndian(d[14..]), d[16..]);
                    break;
                case 276:
                    if (d.Length >= 20) L3(st, BinaryPrimitives.ReadUInt16BigEndian(d), d[20..]);
                    break;
                case 101 or 228 or 12:
                    if (d.Length > 0 && d[0] >> 4 == 4) Ipv4(st, d);
                    break;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException) { /* malformed packet */ }
    }

    private static void Ethernet(State st, ReadOnlySpan<byte> d)
    {
        if (d.Length < 14) return;
        int off = 12;
        ushort et = BinaryPrimitives.ReadUInt16BigEndian(d[off..]);
        for (int v = 0; v < 2 && et is 0x8100 or 0x88A8 or 0x9100; v++)
        {
            off += 4;
            if (d.Length < off + 2) return;
            et = BinaryPrimitives.ReadUInt16BigEndian(d[off..]);
        }
        L3(st, et, d[(off + 2)..]);
    }

    private static void L3(State st, ushort ethertype, ReadOnlySpan<byte> d)
    {
        if (ethertype == 0x0800) Ipv4(st, d);
        else if (ethertype == 0x0806) Arp(st, d);
    }

    private static void Arp(State st, ReadOnlySpan<byte> d)
    {
        if (d.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(d[2..]) != 0x0800 || d[4] != 6 || d[5] != 4) return;
        uint spa = BinaryPrimitives.ReadUInt32BigEndian(d[14..]);
        if (Unicast(spa)) st.E.ArpSenderIps.Add(Ip(spa));
    }

    private static void Ipv4(State st, ReadOnlySpan<byte> d)
    {
        if (d.Length < 20 || d[0] >> 4 != 4) return;
        int ihl = (d[0] & 0x0F) * 4;
        if (ihl < 20 || d.Length < ihl) return;
        int total = BinaryPrimitives.ReadUInt16BigEndian(d[2..]);
        if (total >= ihl && total < d.Length) d = d[..total];
        uint src = BinaryPrimitives.ReadUInt32BigEndian(d[12..]), dst = BinaryPrimitives.ReadUInt32BigEndian(d[16..]);
        st.E.IpPacketCount++;
        Count(st, src);
        if (dst != src) Count(st, dst);
        bool firstFragment = (BinaryPrimitives.ReadUInt16BigEndian(d[6..]) & 0x1FFF) == 0;
        if (!firstFragment) return;
        var l4 = d[ihl..];
        if (d[9] == 17 && l4.Length >= 8)
        {
            int sp = BinaryPrimitives.ReadUInt16BigEndian(l4), dp = BinaryPrimitives.ReadUInt16BigEndian(l4[2..]);
            var pl = l4[8..];
            if (sp is 67 or 68 && dp is 67 or 68) Dhcp(st, pl);
            else if (sp == 137 || dp == 137) Nbns(st, pl, src);
            else if (sp == 5355) Dns(st, pl, src, "LLMNR");
            else if (sp == 5353 || dp == 5353) Dns(st, pl, src, "mDNS");
        }
        else if (d[9] == 6 && l4.Length >= 20)
        {
            int off = (l4[12] >> 4) * 4;
            if (off >= 20 && off < l4.Length) Ntlm(st, l4[off..], src);
        }
    }

    private static void Count(State st, uint ip)
    {
        if (!Unicast(ip)) return;
        if (st.Tally.TryGetValue(ip, out var n)) st.Tally[ip] = n + 1;
        else if (st.Tally.Count < MaxTallyAddresses) st.Tally[ip] = 1;
    }

    /// <summary>Not 0.0.0.0/8, loopback, multicast/reserved (224+) or broadcast.</summary>
    private static bool Unicast(uint ip) => ip >> 24 is not (0 or 127) && ip < 0xE0000000u;

    // ---- DHCP

    private static void Dhcp(State st, ReadOnlySpan<byte> b)
    {
        if (b.Length < 240 || b[236] != 0x63 || b[237] != 0x82 || b[238] != 0x53 || b[239] != 0x63) return;
        byte op = b[0];
        uint ciaddr = BinaryPrimitives.ReadUInt32BigEndian(b[12..]), yiaddr = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        if (Unicast(ciaddr)) st.E.DhcpIps.Add(Ip(ciaddr));
        if (Unicast(yiaddr)) st.E.DhcpIps.Add(Ip(yiaddr));
        string? host = null;
        uint? requested = null;
        for (int i = 240; i < b.Length;)
        {
            byte code = b[i++];
            if (code == 0) continue;
            if (code == 255 || i >= b.Length) break;
            int len = b[i++];
            if (i + len > b.Length) break;
            var v = b.Slice(i, len);
            if (code == 12 && len > 0) host = Latin1(v);
            else if (code == 50 && len == 4) requested = BinaryPrimitives.ReadUInt32BigEndian(v);
            i += len;
        }
        if (requested is { } r && Unicast(r)) st.E.DhcpIps.Add(Ip(r));
        if (host != null)
        {
            uint ip = requested ?? (Unicast(ciaddr) ? ciaddr : yiaddr);
            st.AddName(host, op == 1 ? "DHCP hostname" : "DHCP hostname (server reply)", Unicast(ip) ? Ip(ip) : null, true);   // option 12 is the client's own name
        }
    }

    // ---- NBNS (RFC 1002)

    private static void Nbns(State st, ReadOnlySpan<byte> b, uint src)
    {
        if (b.Length < 12 + 34) return;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        bool response = (flags & 0x8000) != 0;
        int opcode = (flags >> 11) & 0x0F;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(b[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(b[10..]);
        int off = 12;
        var name = NbName(b, ref off);
        if (name == null) return;
        var (n, suffix) = name.Value;
        bool registration = opcode is 5 or 8 && !response;
        if (qd > 0 && !registration) { if (!response) st.AddName(n, "NBNS query", Ip(src), false); return; }
        // the record (answer of a response, additional record of a registration) holds NB flags + IP
        if ((registration && ar > 0) || (response && an > 0 && opcode == 0 && (flags & 0x000F) == 0))
        {
            if (qd > 0)
            {
                off += 4;                                                      // QTYPE, QCLASS; then the record name (usually a pointer)
                if (off + 2 <= b.Length && (b[off] & 0xC0) == 0xC0) off += 2;
                else if (NbName(b, ref off) == null) return;
            }
            if (off + 10 + 6 > b.Length) return;
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(b[(off + 8)..]);
            off += 10;
            if (rdlen < 6 || off + 6 > b.Length) return;
            bool group = (b[off] & 0x80) != 0;
            uint ip = BinaryPrimitives.ReadUInt32BigEndian(b[(off + 2)..]);
            if (group || suffix is not (0x00 or 0x20)) return;               // workgroup / service names are not the host name
            st.AddName(n, registration ? "NBNS registration" : "NBNS answer", Unicast(ip) ? Ip(ip) : Ip(src), ip == src || !Unicast(ip));
        }
    }

    /// <summary>First-level encoded NetBIOS name (0x20 length + 32 'A'..'P' characters + root label): the 15-character name and suffix byte.</summary>
    private static (string Name, byte Suffix)? NbName(ReadOnlySpan<byte> b, ref int off)
    {
        if (off + 34 > b.Length || b[off] != 0x20) return null;
        Span<byte> raw = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int hi = b[off + 1 + 2 * i] - 'A', lo = b[off + 2 + 2 * i] - 'A';
            if (hi is < 0 or > 15 || lo is < 0 or > 15) return null;
            raw[i] = (byte)(hi << 4 | lo);
        }
        int p = off + 33;
        while (p < b.Length && b[p] != 0) { if ((b[p] & 0xC0) == 0xC0) { p++; break; } p += b[p] + 1; }   // scope id
        off = p + 1;
        return (Latin1(raw[..15]).TrimEnd(' ', '\0'), raw[15]);
    }

    // ---- LLMNR / mDNS answers (A records)

    private static void Dns(State st, ReadOnlySpan<byte> b, uint src, string kind)
    {
        if (b.Length < 12) return;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        if ((flags & 0x8000) == 0) return;                                     // only responses / announcements
        int qd = BinaryPrimitives.ReadUInt16BigEndian(b[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
        int off = 12;
        for (int i = 0; i < qd && i < 16; i++)
        {
            if (DnsName(b, ref off) == null) return;
            off += 4;
        }
        for (int i = 0; i < an && i < 32; i++)
        {
            var name = DnsName(b, ref off);
            if (name == null || off + 10 > b.Length) return;
            int type = BinaryPrimitives.ReadUInt16BigEndian(b[off..]);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(b[(off + 8)..]);
            off += 10;
            if (off + rdlen > b.Length) return;
            if (type == 1 && rdlen == 4)
            {
                uint ip = BinaryPrimitives.ReadUInt32BigEndian(b[off..]);
                var n = name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
                if (Unicast(ip)) st.AddName(n, kind + " answer", Ip(ip), ip == src);
            }
            off += rdlen;
        }
    }

    private static string? DnsName(ReadOnlySpan<byte> b, ref int off)
    {
        var sb = new StringBuilder();
        int p = off, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (p >= b.Length) return null;
            int l = b[p];
            if (l == 0) { if (!jumped) off = p + 1; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (p + 1 >= b.Length || ++jumps > 10) return null;
                if (!jumped) off = p + 2;
                jumped = true;
                p = (l & 0x3F) << 8 | b[p + 1];
                continue;
            }
            if (l > 63 || p + 1 + l > b.Length) return null;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Latin1(b.Slice(p + 1, l)));
            if (sb.Length > 255) return null;
            p += l + 1;
        }
        return sb.ToString();
    }

    // ---- NTLMSSP AUTHENTICATE

    private static readonly byte[] AuthNtlm = Encoding.ASCII.GetBytes("authorization: ntlm ");
    private static readonly byte[] AuthNegotiate = Encoding.ASCII.GetBytes("authorization: negotiate ");

    private static void Ntlm(State st, ReadOnlySpan<byte> p, uint src)
    {
        if (p.Length < 24) return;
        int i = p.IndexOf(NtlmSig);
        if (i >= 0) { NtlmAuth(st, p[i..], src); return; }
        // HTTP: "Authorization: NTLM <base64>" / "Negotiate <base64>"
        foreach (var prefix in new[] { AuthNtlm, AuthNegotiate })
        {
            int h = IndexOfAsciiIgnoreCase(p, prefix);
            if (h < 0) continue;
            var rest = p[(h + prefix.Length)..];
            int end = rest.IndexOfAny((byte)'\r', (byte)'\n');
            var token = Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]).Trim();
            try
            {
                var raw = Convert.FromBase64String(token);
                int j = raw.AsSpan().IndexOf(NtlmSig);
                if (j >= 0) NtlmAuth(st, raw.AsSpan(j), src);
            }
            catch (FormatException) { }
            return;
        }
    }

    private static void NtlmAuth(State st, ReadOnlySpan<byte> m, uint src)
    {
        if (m.Length < 64 || BinaryPrimitives.ReadUInt32LittleEndian(m[8..]) != 3) return;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(m[60..]);
        bool unicode = (flags & 1) != 0;
        string? domain = NtlmField(m, 28, unicode), user = NtlmField(m, 36, unicode), ws = NtlmField(m, 44, unicode);
        if (ws == null && domain == null && user == null) return;
        if (st.E.Ntlm.Count < MaxNames && !st.E.Ntlm.Any(n => n.Workstation == ws && n.Domain == domain && n.User == user && n.ClientIp == Ip(src)))
            st.E.Ntlm.Add(new PcapNtlmEvidence(ws, domain, user, Ip(src)));
        if (ws != null) st.AddName(ws, "NTLM workstation", Ip(src), true);
    }

    /// <summary>A security buffer (length u16, max length u16, offset u32 from the signature) of an NTLMSSP message, Unicode or OEM.</summary>
    private static string? NtlmField(ReadOnlySpan<byte> m, int at, bool unicode)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(m[at..]);
        long off = BinaryPrimitives.ReadUInt32LittleEndian(m[(at + 4)..]);
        if (len == 0 || len > 512 || off + len > m.Length) return null;
        var v = m.Slice((int)off, len);
        var s = unicode ? Encoding.Unicode.GetString(v[..(len & ~1)]) : Latin1(v);
        s = s.Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> hay, byte[] lowerNeedle)
    {
        for (int i = 0; i + lowerNeedle.Length <= hay.Length; i++)
        {
            int k = 0;
            for (; k < lowerNeedle.Length; k++)
            {
                byte c = hay[i + k];
                if (c is >= (byte)'A' and <= (byte)'Z') c = (byte)(c + 32);
                if (c != lowerNeedle[k]) break;
            }
            if (k == lowerNeedle.Length) return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ helpers

    private static uint U32(ReadOnlySpan<byte> b, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(b[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    private static uint U32(byte[] b, int at, bool big) => U32(b.AsSpan(), at, big);

    public static string Ip(uint ip) => $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";

    private static string Latin1(ReadOnlySpan<byte> b) => Encoding.Latin1.GetString(b).TrimEnd('\0');
}
