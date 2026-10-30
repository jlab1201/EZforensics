using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Zeek;

/// <summary>
/// argv for <c>zeek -C -r &lt;pcap&gt; [local] LogAscii::use_json=F</c> on each engine. Zeek writes its logs into the working
/// directory, so the output folder is the working directory (native), <c>-w /data/out</c> (Docker, input mounted read-only)
/// or <c>--cd /mnt/...</c> (WSL, paths translated). Every value is one argv element.
/// </summary>
public static class ZeekCommandBuilder
{
    public const string ContainerIn = "/data/in";
    public const string ContainerOut = "/data/out";

    /// <summary>
    /// Zeek's own arguments: <c>-C</c> ignores bad checksums (captures taken on the sending host have offloaded, "wrong"
    /// checksums and Zeek would otherwise drop those packets), <c>-r</c> reads the file, <c>local</c> loads the site policy,
    /// and <c>LogAscii::use_json=F</c> forces the TSV format the converter expects even when a site policy turns JSON on.
    /// </summary>
    public static IReadOnlyList<string> ZeekArgs(string pcapForEngine, bool loadLocal, IEnumerable<string>? extra = null)
    {
        var a = new List<string> { "-C", "-r", pcapForEngine };
        if (loadLocal) a.Add("local");
        if (extra != null) a.AddRange(extra.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        a.Add("LogAscii::use_json=F");
        return a;
    }

    public static ZeekInvocation Build(ZeekInstall install, ZeekRunOptions o, string? containerName = null)
    {
        if (string.IsNullOrWhiteSpace(o.Pcap)) throw new ArgumentException("No packet capture was given.");
        if (string.IsNullOrWhiteSpace(o.OutputDir)) throw new ArgumentException("The output folder is empty.");
        var pcap = Path.GetFullPath(o.Pcap);
        var outDir = Path.GetFullPath(o.OutputDir);
        bool local = o.IncludeLocal && install.HasLocalScript;

        switch (install.Engine)
        {
            case ZeekEngine.Docker:
            {
                var name = containerName ?? DockerRuntime.NewContainerName("zeek");
                var args = new List<string>
                {
                    "run", "--rm", "--name", name,
                    "--network", "none", // offline analysis: the container needs no network
                    "--mount", DockerRuntime.MountSpec(Path.GetDirectoryName(pcap)!, ContainerIn, readOnly: true),
                    "--mount", DockerRuntime.MountSpec(outDir, ContainerOut, readOnly: false),
                    "-w", ContainerOut,
                    install.DockerImage ?? ZeekLocator.DockerImage, install.ZeekPath,
                };
                args.AddRange(ZeekArgs(ContainerIn + "/" + Path.GetFileName(pcap), local, o.AllExtra));
                return new ZeekInvocation(install.Exe, args, outDir, name, new[] { install.Exe }.Concat(DockerRuntime.StopArgs(name)).ToList(), null);
            }
            case ZeekEngine.Wsl:
            {
                var distro = install.WslDistro ?? throw new ArgumentException("No WSL distro.");
                var inPath = WslRuntime.ToWslPath(pcap, install.WslMountRoot)
                             ?? throw new ArgumentException($"WSL cannot read {pcap} (network shares are not mounted in WSL). Copy the capture to a local drive.");
                var outPath = WslRuntime.ToWslPath(outDir, install.WslMountRoot)
                              ?? throw new ArgumentException($"WSL cannot write to {outDir}. Use a case folder on a local drive.");
                var args = WslRuntime.Args(distro, new[] { install.ZeekPath }.Concat(ZeekArgs(inPath, local, o.AllExtra)), outPath);
                // Killing wsl.exe does not always stop the Linux process: stop it by its (unique) capture path.
                var cancel = new[] { install.Exe }.Concat(WslRuntime.Args(distro, new[] { "pkill", "-f", "--", "zeek -C -r " + RegexEscape(inPath) }, asRoot: true)).ToList();
                return new ZeekInvocation(install.Exe, args, outDir, null, cancel, WslRuntime.Utf8Env);
            }
            default:
                return new ZeekInvocation(install.Exe, ZeekArgs(pcap, local, o.AllExtra), outDir, null, null, null);
        }
    }

    /// <summary>Escapes a literal for pkill's extended regex.</summary>
    public static string RegexEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if ("\\.^$|?*+()[]{}".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
