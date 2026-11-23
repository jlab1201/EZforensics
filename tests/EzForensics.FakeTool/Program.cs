using System.Diagnostics;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(false);

// Fake plaso: "--fake-plaso log2timeline|psort <plaso args>" (used as the prefix args of a PlasoCommand in tests).
if (args.Length >= 2 && args[0] == "--fake-plaso")
    return FakePlasoTool.Run(args[1], args.Skip(2).ToArray());

// Fake zeek: "--version" or "-C -r <pcap> ..." (a ZeekInstall whose exe is this fake).
if (FakeZeekTool.Handles(args))
    return FakeZeekTool.Run(args);

// Fake RITA pipeline: "--fake-rita --out <dir> --db <name> [--subnets a,b]" (RitaEngine.Direct).
if (FakeRitaTool.Handles(args))
    return FakeRitaTool.Run(args);

// Fake Volatility 3: "-r csv -f <image> ... <plugin> [args]".
if (FakeVolatilityTool.Handles(args))
    return FakeVolatilityTool.Run(args);

// Fake Detect It Easy ("-j -d -u <file>") and ExifTool ("-@ <argfile>") for the Explore runners.
if (FakeExploreTool.Handles(args))
    return FakeExploreTool.Run(args);

// Fake MCP server (stdio JSON-RPC) and the "--fake-touch <path>" marker mode.
if (FakeMcpTool.Handles(args))
    return FakeMcpTool.Run(args);

int lines = 0, delay = 0, exit = 0;
bool stderr = false, hang = false, spawnChild = false, unicode = false;
string? csvDir = null, csvf = null;
var emit = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--lines": lines = int.Parse(args[++i]); break;
        case "--delay": delay = int.Parse(args[++i]); break;
        case "--exit": exit = int.Parse(args[++i]); break;
        case "--stderr": stderr = true; break;
        case "--hang": hang = true; break;
        case "--spawn-child": spawnChild = true; break;
        case "--unicode": unicode = true; break;
        case "--csv": csvDir = args[++i]; break;
        case "--csvf": csvf = args[++i]; break;
        case "--emit": emit.Add(args[++i]); break; // EZ-style output name relative to --csv; {ts} = yyyyMMddHHmmss run time
    }
}

if (spawnChild)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("--hang");
    var child = Process.Start(psi)!;
    Console.WriteLine($"CHILD:{child.Id}");
    Console.Out.Flush();
    hang = true;
}

if (unicode)
    Console.WriteLine("ünïcødé ✓");

for (int n = 1; n <= lines; n++)
{
    if (stderr && n % 2 == 0)
        Console.Error.WriteLine($"err {n}");
    else
        Console.WriteLine($"line {n}");
    if (delay > 0) Thread.Sleep(delay);
}

if (csvDir != null)
{
    Directory.CreateDirectory(csvDir);
    var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
    if (emit.Count == 0)
        File.WriteAllText(Path.Combine(csvDir, csvf ?? "out.csv"), "Name,Value\nalpha,1\nbeta,2\n", new UTF8Encoding(false));
    foreach (var e in emit)
    {
        var p = Path.Combine(csvDir, e.Replace("{ts}", ts));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "Name,Value\n" + Path.GetFileName(p) + ",1\n", new UTF8Encoding(false));
    }
}

Console.Out.Flush();
if (hang) Thread.Sleep(Timeout.Infinite);
return exit;
