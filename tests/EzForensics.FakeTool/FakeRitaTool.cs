using System.Globalization;
using System.Net;
using System.Text;

/// <summary>
/// Fake RITA pipeline for tests (the Direct engine of RitaCommandBuilder):
/// <c>--fake-rita --out &lt;dir&gt; --db &lt;name&gt; --subnets &lt;cidr,...&gt;</c>. Reads <c>rita_inputs.tmp</c> from the output folder
/// (folder, name, capture, path per line), parses the Zeek conn / dns logs and writes the same work files as the real pipeline:
/// <c>rita_view.csv.tmp</c> (RITA v5 view columns: one beacon row per internal → external pair, one C2-over-DNS row per queried domain),
/// <c>rita_pairs.csv.tmp</c>, <c>rita_dns.csv.tmp</c>, <c>rita_span.csv.tmp</c>, <c>rita_info.tmp</c>.
/// Environment: FAKE_RITA_HANG=1 (sleep forever, for cancel tests), FAKE_RITA_EXIT=n, FAKE_RITA_NODATA=1 (no findings, nodata=1),
/// FAKE_RITA_NOSPANS=1 (no enrichment files).
/// </summary>
internal static class FakeRitaTool
{
    public static bool Handles(string[] args) => args.Length >= 1 && args[0] == "--fake-rita";

    private const string ViewHeader = "Severity,Source IP,Destination IP,FQDN,Beacon Score,Strobe,Total Duration,Long Connection Score,Subdomains,C2 Over DNS Score,Threat Intel,Prevalence,First Seen,Missing Host Header,Connection Count,Total Bytes,Port:Proto:Service,Modifiers";

    public static int Run(string[] args)
    {
        string? outDir = null, db = null, subnets = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--db": db = args[++i]; break;
                case "--subnets": subnets = args[++i]; break;
            }
        }
        if (outDir == null || db == null) { Console.Error.WriteLine("usage: --fake-rita --out <dir> --db <name> [--subnets a,b]"); return 2; }
        var inputs = Path.Combine(outDir, "rita_inputs.tmp");
        if (!File.Exists(inputs)) { Console.Error.WriteLine("EZFORENSICS: the input list is missing"); return 2; }
        Console.WriteLine($"EZFORENSICS: fake rita import --database {db}");
        if (Environment.GetEnvironmentVariable("FAKE_RITA_HANG") == "1")
        {
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        var exit = Environment.GetEnvironmentVariable("FAKE_RITA_EXIT");
        if (int.TryParse(exit, out var e) && e != 0) { Console.Error.WriteLine("EZFORENSICS: rita import failed (exit " + e + ")"); return e; }

        var nets = (subnets ?? "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => IPNetwork.TryParse(s.Trim(), out var n) ? n : (IPNetwork?)null).Where(n => n != null).Select(n => n!.Value).ToList();
        bool Internal(string ip) => IPAddress.TryParse(ip, out var a) && nets.Any(n => n.Contains(a));

        var pairs = new Dictionary<(string, string), (double First, double Last, int Count, double Dur, long Bytes, HashSet<string> Caps)>();
        var domains = new Dictionary<string, (double First, double Last, int Count, HashSet<string> Subs, HashSet<string> Caps)>(StringComparer.OrdinalIgnoreCase);
        var spans = new Dictionary<string, (double First, double Last)>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(inputs))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length < 4 || !File.Exists(p[3])) continue;
            var (name, cap, path) = (p[1], p[2], p[3]);
            foreach (var rec in Records(path))
            {
                if (!double.TryParse(rec.GetValueOrDefault("ts"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ts)) continue;
                spans[cap] = spans.TryGetValue(cap, out var sp) ? (Math.Min(sp.First, ts), Math.Max(sp.Last, ts)) : (ts, ts);
                var src = rec.GetValueOrDefault("id.orig_h") ?? "";
                var dst = rec.GetValueOrDefault("id.resp_h") ?? "";
                if (name == "conn.log" && Internal(src) && !Internal(dst))
                {
                    double.TryParse(rec.GetValueOrDefault("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
                    long.TryParse(rec.GetValueOrDefault("orig_bytes"), out var ob);
                    long.TryParse(rec.GetValueOrDefault("resp_bytes"), out var rb);
                    var k = (src, dst);
                    var v = pairs.TryGetValue(k, out var x) ? x : (ts, ts, 0, 0.0, 0L, new HashSet<string>());
                    v.Item6.Add(cap);
                    pairs[k] = (Math.Min(v.Item1, ts), Math.Max(v.Item2, ts), v.Item3 + 1, v.Item4 + dur, v.Item5 + ob + rb, v.Item6);
                }
                else if (name == "dns.log" && rec.GetValueOrDefault("query") is { Length: > 0 } q && q.Count(c => c == '.') >= 1)
                {
                    var labels = q.Split('.');
                    var domain = string.Join(".", labels.TakeLast(2));
                    var v = domains.TryGetValue(domain, out var x) ? x : (ts, ts, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>());
                    v.Item4.Add(q);
                    v.Item5.Add(cap);
                    domains[domain] = (Math.Min(v.Item1, ts), Math.Max(v.Item2, ts), v.Item3 + 1, v.Item4, v.Item5);
                }
            }
        }
        bool nodata = Environment.GetEnvironmentVariable("FAKE_RITA_NODATA") == "1";
        if (nodata) { pairs.Clear(); domains.Clear(); }

        var enc = new UTF8Encoding(false);
        var view = new StringBuilder("Viewing database: " + db + "\n" + ViewHeader + "\n");
        foreach (var ((src, dst), v) in pairs)
        {
            var lc = v.Dur >= 3600 ? "0.5" : "0";
            view.Append($"Low,{src},{dst},,0.75,false,{v.Dur.ToString(CultureInfo.InvariantCulture)},{lc},0,0,false,1,1 hour ago,false,{v.Count},{v.Bytes},\"80:tcp:http\",\"\"\n");
        }
        foreach (var (d, v) in domains)
            view.Append($"Medium,::,::,{d},0,false,0,0,{v.Subs.Count},0.6,false,0,1 hour ago,false,{v.Count},0,\"\",\"\"\n");
        File.WriteAllText(Path.Combine(outDir, "rita_view.csv.tmp"), nodata ? "" : view.ToString(), enc);

        if (!nodata && Environment.GetEnvironmentVariable("FAKE_RITA_NOSPANS") != "1")
        {
            var sb = new StringBuilder("\"src\",\"dst\",\"first_ts\",\"last_ts\",\"captures\"\n");
            foreach (var ((src, dst), v) in pairs) sb.Append($"\"{src}\",\"{dst}\",{(long)v.First},{(long)v.Last},\"{string.Join("|", v.Caps.OrderBy(c => c))}\"\n");
            File.WriteAllText(Path.Combine(outDir, "rita_pairs.csv.tmp"), sb.ToString(), enc);
            sb = new StringBuilder("\"fqdn\",\"first_ts\",\"last_ts\",\"captures\"\n");
            foreach (var (d, v) in domains) sb.Append($"\"{d}\",{(long)v.First},{(long)v.Last},\"{string.Join("|", v.Caps.OrderBy(c => c))}\"\n");
            File.WriteAllText(Path.Combine(outDir, "rita_dns.csv.tmp"), sb.ToString(), enc);
            File.WriteAllText(Path.Combine(outDir, "rita_sni.csv.tmp"), "\"src\",\"fqdn\",\"first_ts\",\"last_ts\",\"captures\"\n", enc);
            sb = new StringBuilder("\"capture\",\"first_ts\",\"last_ts\"\n");
            foreach (var (c, s) in spans) sb.Append($"\"{c}\",{(long)s.First},{(long)s.Last}\n");
            File.WriteAllText(Path.Combine(outDir, "rita_span.csv.tmp"), sb.ToString(), enc);
        }
        File.WriteAllText(Path.Combine(outDir, "rita_info.tmp"),
            $"rita.version=RITA version v5.1.2\nclickhouse.version=ClickHouse local version 24.1.6.52 (official build).\nrita.db={db}\nnodata={(nodata ? 1 : 0)}\n", enc);
        Console.WriteLine($"EZFORENSICS: done ({pairs.Count + domains.Count} finding(s))");
        return 0;
    }

    /// <summary>Zeek TSV records as field → value maps.</summary>
    private static IEnumerable<Dictionary<string, string>> Records(string path)
    {
        string[]? fields = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("#fields\t", StringComparison.Ordinal)) { fields = line.Split('\t').Skip(1).ToArray(); continue; }
            if (line.Length == 0 || line[0] == '#' || fields == null) continue;
            var v = line.Split('\t');
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Length && i < v.Length; i++) map[fields[i]] = v[i];
            yield return map;
        }
    }
}
