using EzForensics.Core.Runtimes;

namespace EzForensics.Core.Assistant;

/// <summary>How to start the Spectre MCP server. All paths live here.</summary>
public static class SpectreMcp
{
    public const string McpExe = "/opt/ezforensics/spectre/venv/bin/spectre-mcp";
    public const string CasesDir = "/var/lib/ezforensics/spectre/cases";
    /// <summary>pkill pattern: the bracket keeps pkill from matching its own command line.</summary>
    public const string PkillPattern = "[/]opt/ezforensics/spectre/venv/bin/spectre-mcp";

    /// <summary>The server inside the private EZ Forensics distro, as root, plus the command that kills it if wsl.exe dies first.</summary>
    public static (string Exe, IReadOnlyList<string> Args, IReadOnlyList<string> Cancel) Command(string wslExe) =>
        (wslExe,
         WslRuntime.Args(EzDistro.Name, new[] { McpExe, "--cases-dir", CasesDir, "--no-journal-anchor" }, asRoot: true),
         new[] { wslExe }.Concat(WslRuntime.Args(EzDistro.Name, new[] { "pkill", "-f", PkillPattern }, asRoot: true)).ToList());

    /// <summary>Development override: run any local server (no cancel command needed for a plain child process).</summary>
    public static (string Exe, IReadOnlyList<string> Args, IReadOnlyList<string> Cancel) DevCommand(string exe, IReadOnlyList<string> args) =>
        (exe, args, Array.Empty<string>());
}
