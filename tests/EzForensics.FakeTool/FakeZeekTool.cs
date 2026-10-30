using System.Text;

/// <summary>
/// Fake zeek for tests: <c>--version</c> prints "zeek version 9.0.0"; <c>-C -r &lt;pcap&gt; [scripts] [var=value]</c>
/// checks that the capture exists and writes conn.log / dns.log (Zeek TSV) into the working directory, like Zeek does.
/// Options in the environment: FAKE_ZEEK_HANG=1 (sleep forever, for cancel tests), FAKE_ZEEK_EXIT=n.
/// </summary>
internal static class FakeZeekTool
{
    public static bool Handles(string[] args) =>
        (args.Length == 1 && args[0] == "--version") || (args.Length >= 3 && args[0] == "-C" && args[1] == "-r");

    public static int Run(string[] args)
    {
        if (args[0] == "--version")
        {
            Console.WriteLine("zeek version 9.0.0");
            return 0;
        }
        var pcap = args[2];
        if (!File.Exists(pcap))
        {
            Console.Error.WriteLine($"fatal error: problem with trace file {pcap} (No such file or directory)");
            return 1;
        }
        Console.WriteLine($"fake zeek reading {pcap} with {string.Join(" ", args.Skip(3))}");
        if (Environment.GetEnvironmentVariable("FAKE_ZEEK_HANG") == "1")
        {
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        var enc = new UTF8Encoding(false);
        File.WriteAllText("conn.log", string.Join("\n", Header("conn",
            "ts\tuid\tid.orig_h\tid.orig_p\tid.resp_h\tid.resp_p\tproto\tservice\tduration\torig_bytes\tresp_bytes\tconn_state\ttunnel_parents",
            "time\tstring\taddr\tport\taddr\tport\tenum\tstring\tinterval\tcount\tcount\tstring\tset[string]"))
            + "\n1258535653.087137\tCabc1\t192.168.1.103\t49203\t192.150.187.43\t80\ttcp\thttp\t1.5\t512\t4096\tSF\t(empty)\n"
            + "1258535654.500000\tCabc2\t192.168.1.103\t53111\t192.168.1.1\t53\tudp\tdns\t-\t-\t-\tS0\t-\n#close\t2026-10-08-00-00-01\n", enc);
        File.WriteAllText("dns.log", string.Join("\n", Header("dns",
            "ts\tuid\tid.orig_h\tid.orig_p\tid.resp_h\tid.resp_p\tproto\tquery\tqtype_name\trcode_name\tanswers\tTTLs",
            "time\tstring\taddr\tport\taddr\tport\tenum\tstring\tstring\tstring\tvector[string]\tvector[interval]"))
            + "\n1258535654.500000\tCabc2\t192.168.1.103\t53111\t192.168.1.1\t53\tudp\twww.bro.org\tA\tNOERROR\t192.150.187.43,10.0.0.1\t300.0,300.0\n#close\t2026-10-08-00-00-01\n", enc);
        var exit = Environment.GetEnvironmentVariable("FAKE_ZEEK_EXIT");
        return int.TryParse(exit, out var e) ? e : 0;
    }

    private static IEnumerable<string> Header(string path, string fields, string types) => new[]
    {
        "#separator \\x09", "#set_separator\t,", "#empty_field\t(empty)", "#unset_field\t-", "#path\t" + path,
        "#open\t2026-10-08-00-00-00", "#fields\t" + fields, "#types\t" + types,
    };
}
