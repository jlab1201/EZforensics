using System.Text;

/// <summary>
/// Minimal stand-in for plaso's log2timeline / psort (argv as EZ Forensics builds it), used by the Core tests:
/// prints linear status lines, writes a storage file / a dynamic or l2tcsv CSV, and hangs when the source path
/// contains "hang" (cancellation tests).
/// </summary>
internal static class FakePlasoTool
{
    public static int Run(string tool, string[] a)
    {
        string? Opt(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        if (a.Contains("--version")) { Console.WriteLine($"plaso - {tool} version 20990101"); return 0; }
        var pid = Environment.ProcessId;
        if (tool == "log2timeline")
        {
            var storage = Opt("--storage_file");
            var source = a.Length > 0 ? a[^1] : "";
            Console.WriteLine("Source path\t\t: " + source);
            if (storage == null || (!Directory.Exists(source) && !File.Exists(source)))
            {
                Console.Error.WriteLine("ERROR: Source path does not exist");
                return 1;
            }
            Console.WriteLine("Processing started.");
            Console.WriteLine("Processing time: 00:00:01");
            Console.WriteLine($"Main (PID: {pid}) status: running, event data produced: 10, events produced: 12, file: OS:{Path.Combine(source, "a.evtx")}");
            Console.WriteLine($"Worker_00 (PID: {pid}) status: idle, event data produced: 0, file: OS:{Path.Combine(source, "b.lnk")}");
            Console.Out.Flush();
            if (source.Contains("hang", StringComparison.OrdinalIgnoreCase))
            {
                // Like plaso: a task-storage temp folder and a partial storage file exist while processing.
                var dir = Path.GetDirectoryName(Path.GetFullPath(storage))!;
                Directory.CreateDirectory(Path.Combine(dir, "tmpfake01", "processed"));
                File.WriteAllBytes(storage, new byte[] { 1 });
                Console.WriteLine("partial storage written");
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storage))!);
            // Like plaso: an existing storage file is opened for appending (one more session per run).
            using (var fs = new FileStream(storage, FileMode.Append, FileAccess.Write))
                fs.Write(new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65 });
            Console.WriteLine("Processing time: 00:00:02");
            Console.WriteLine($"Main (PID: {pid}) status: completed, event data produced: 20, events produced: 24, file: ");
            Console.WriteLine("Processing completed.");
            Console.WriteLine("Number of warnings generated while extracting events: 3.");
            return 0;
        }
        if (tool == "psort")
        {
            var output = Opt("-w");
            var format = Opt("-o") ?? "dynamic";
            int si = output == null ? -1 : Array.IndexOf(a, output) + 1;
            var storage = si > 0 && si < a.Length ? a[si] : null;
            if (output == null || storage == null || !File.Exists(storage)) { Console.Error.WriteLine("ERROR: storage file missing"); return 1; }
            bool filtered = si + 1 < a.Length;
            var lines = new List<string>();
            if (format == "l2tcsv")
            {
                lines.Add("date,time,timezone,MACB,source,sourcetype,type,user,host,short,desc,version,filename,inode,notes,format,extra");
                lines.Add("01/02/2020,10:00:00,UTC,MACB,FILE,NTFS $MFT,$SI [MACB] time,-,PC,short,C:/a.txt,2,OS:C:/a.txt,5,-,mft,-");
                lines.Add("03/04/2021,11:30:00,UTC,M...,EVT,WinEVTX,Content Modification Time,-,PC,short,[4624] logon,2,OS:C:/Security.evtx,-,-,winevtx,-");
                if (!filtered) lines.Add("05/06/2022,12:00:00,UTC,.A..,LNK,Windows Shortcut,Last Access Time,-,PC,short,b.lnk,2,OS:C:/b.lnk,-,-,lnk,-");
            }
            else
            {
                lines.Add("datetime,timestamp_desc,macb,source,source_long,message,parser,display_name,tag");
                lines.Add("2020-01-02T10:00:00.123456+00:00,Creation Time,...B,FILE,NTFS $MFT,C:/a.txt,mft,OS:C:/$MFT,-");
                lines.Add("2021-03-04T11:30:00.000000+00:00,Content Modification Time,M...,EVT,WinEVTX,\"[4624] logon, ok\",winevtx,OS:C:/Security.evtx,-");
                if (!filtered) lines.Add("2022-05-06T12:00:00.000000+00:00,Last Access Time,.A..,LNK,Windows Shortcut,b.lnk,lnk,OS:C:/b.lnk,-");
            }
            File.WriteAllText(output, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            Console.WriteLine("Processing time: 00:00:00");
            Console.WriteLine($"Main (PID: {pid}) status: exporting, events consumed: {(filtered ? 2 : 3)}");
            return 0;
        }
        Console.Error.WriteLine("unknown fake plaso tool " + tool);
        return 2;
    }
}
