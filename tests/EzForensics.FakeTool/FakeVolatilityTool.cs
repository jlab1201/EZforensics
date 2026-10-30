using System.Text;

/// <summary>
/// Fake Volatility 3 for tests: <c>-r csv -f &lt;img&gt; [--symbol-dirs d] [--cache-path d] [--offline] [--single-swap-locations p] &lt;plugin&gt; [args]</c>.
/// Prints the banner and a fixture CSV (Python csv style: backslash escapes, <c>... UTC</c> timestamps) on stdout and <c>Progress:</c>
/// lines on stderr. Environment switches: FAKE_VOL_HANG=1 (writes part of the CSV, then sleeps for ever), FAKE_VOL_NOSYMBOLS=1
/// (exit 1 with "Unsatisfied requirement" on stderr, no CSV), FAKE_VOL_EXIT=n (exit code after the output),
/// FAKE_VOL_ARGS=&lt;file&gt; (writes the argv and PYTHONNOUSERSITE there, one per line).
/// </summary>
internal static class FakeVolatilityTool
{
    public static bool Handles(string[] args) => args.Length >= 5 && args[0] == "-r" && args[1] == "csv" && args[2] == "-f";

    public static int Run(string[] args)
    {
        var argsFile = Environment.GetEnvironmentVariable("FAKE_VOL_ARGS");
        if (!string.IsNullOrEmpty(argsFile))
            File.WriteAllLines(argsFile, args.Append("PYTHONNOUSERSITE=" + Environment.GetEnvironmentVariable("PYTHONNOUSERSITE")));

        var image = args[3];
        string? plugin = null;
        for (int i = 4; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--symbol-dirs": case "--cache-path": case "--single-swap-locations": i++; continue;
                case "--offline": continue;
            }
            plugin = args[i];
            break;
        }
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        stdout.WriteLine("Volatility 3 Framework 2.28.2");
        if (!File.Exists(image))
        {
            Console.Error.WriteLine($"Unable to read file: {image}");
            return 1;
        }
        Console.Error.Write("Progress:    0.00\t\tScanning memory_layer using PdbSignatureScanner\r");
        Console.Error.Write("Progress:   45.12\t\tScanning memory_layer using PdbSignatureScanner\r");
        if (Environment.GetEnvironmentVariable("FAKE_VOL_NOSYMBOLS") == "1")
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Unsatisfied requirement plugins.Info.kernel.symbol_table_name: ");
            Console.Error.WriteLine("A symbol table requirement was not fulfilled.  Please verify that:");
            return 1;
        }
        Console.Error.WriteLine("Progress:  100.00\t\tPDB scanning finished");
        stdout.WriteLine();
        foreach (var l in Fixture(plugin ?? "")) stdout.WriteLine(l);
        if (Environment.GetEnvironmentVariable("FAKE_VOL_HANG") == "1")
        {
            stdout.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        var exit = Environment.GetEnvironmentVariable("FAKE_VOL_EXIT");
        return int.TryParse(exit, out var e) ? e : 0;
    }

    private static IEnumerable<string> Fixture(string plugin) => plugin switch
    {
        "windows.info" => new[]
        {
            "TreeDepth,Variable,Value",
            "0,Kernel Base,0xf80001a00000",
            "0,NtBuildLab,19041.1.amd64fre.vb_release.191206-1406",
            "0,SystemTime,2024-03-01 10:20:30.123456+00:00",
        },
        "windows.pslist" => new[]
        {
            "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output",
            "0,4,0,System,0xfa8000c9e040,90,-,N/A,False,2024-03-01 09:00:00.000000 UTC,N/A,Disabled",
            "0,1234,4,\"evil, inc.exe\",0xfa8000c9e041,3,-,1,False,2024-03-01 09:30:15.500000 UTC,2024-03-01 09:31:00.000000 UTC,Disabled",
        },
        "windows.cmdline" => new[]
        {
            "TreeDepth,PID,Process,Args",
            "0,1234,cmd.exe,\"C:\\\\Windows\\\\System32\\\\cmd.exe /c \"\"echo hi\"\"\"",
        },
        "windows.registry.printkey" => new[]
        {
            "TreeDepth,Last Write Time,Hive Offset,Type,Key,Name,Data,Volatile",
            "0,2024-03-01 08:00:00.000000 UTC,0xf8a000024010,REG_SZ,\\REGISTRY\\MACHINE\\SYSTEM\\ControlSet001\\Control\\ComputerName\\ComputerName,ComputerName,\"" + (Environment.GetEnvironmentVariable("FAKE_VOL_COMPUTERNAME") ?? "WKS-01") + "\",False",
        },
        _ => new[]
        {
            "TreeDepth,Name,Value",
            $"0,{plugin},1",
        },
    };
}
