using System.Text;
using System.Text.Json;

/// <summary>
/// Fake Explore analyzers. DIE: "-j -d -u &lt;file&gt;" prints diec-style JSON. ExifTool: "-@ &lt;argfile&gt;" prints exiftool-style JSON
/// with the file named on the argfile's last line. A target whose name contains "hang" never finishes (timeout tests).
/// The target is only read (its length), never run.
/// </summary>
internal static class FakeExploreTool
{
    public static bool Handles(string[] args) =>
        (args.Length == 4 && args[0] == "-j" && args[1] == "-d" && args[2] == "-u") || (args.Length == 2 && args[0] == "-@");

    public static int Run(string[] args)
    {
        string file = args[0] == "-@" ? File.ReadAllLines(args[1], Encoding.UTF8).Last() : args[3];
        if (Path.GetFileName(file).Contains("hang", StringComparison.OrdinalIgnoreCase)) Thread.Sleep(Timeout.Infinite);
        long len = File.Exists(file) ? new FileInfo(file).Length : -1;
        string json = args[0] == "-@"
            ? JsonSerializer.Serialize(new[] { new Dictionary<string, object> {
                ["SourceFile"] = file.Replace('\\', '/'), ["ExifTool:ExifToolVersion"] = 13.6, ["System:FileName"] = Path.GetFileName(file),
                ["System:FileSize"] = $"{len} bytes", ["File:FileType"] = "TXT" } })
            : JsonSerializer.Serialize(new { detects = new[] { new { filetype = "Binary", info = "", offset = "0", parentfilepart = "Header",
                size = len.ToString(), values = new[] { new { info = "", name = "UPX", @string = "Packer: UPX(4.24)[NRV]", type = "packer", version = "4.24" } } } } });
        var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(json));
        stdout.Flush();
        return 0;
    }
}
