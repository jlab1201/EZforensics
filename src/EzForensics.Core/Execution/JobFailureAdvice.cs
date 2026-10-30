namespace EzForensics.Core.Execution;

/// <summary>Turns a failed job (tool id, exit code, the raw reason and the log tail) into the likely next step. Never repeats the error itself.</summary>
public static class JobFailureAdvice
{
    public const string Generic = "Open the Log for details, then retry the job; if it keeps failing, re-install the tool in Tools.";

    public static string Advise(string toolId, int? exitCode, string? reason, string? logTail)
    {
        var text = (reason ?? "") + "\n" + (logTail ?? "");
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Tool not installed") || Has("was not found under the tools root") || Has("not recognized as an internal or external command"))
            return $"Install it in Tools → {toolId}.";
        if (Has("outdated") || Has("unknown option") || Has("unrecognized option") || Has("Unrecognized command or argument") || Has("unknown flag"))
            return $"Update it in Tools → {toolId}.";
        if (Has("rita import failed"))
            return "Check that the Zeek logs are valid (re-run Zeek), then retry; see Log for RITA's message.";
        if (Has("ClickHouse did not start"))
            return "Restart WSL (wsl --shutdown) and retry; update RITA in Tools if it persists.";
        if (Has("Administrator privileges") || Has("requires elevation") || Has("run as administrator") || Has("Access is denied") && Has("administrator"))
            return "Restart EZ Forensics as administrator.";
        if (Has("Timed out"))
            return "Increase the timeout in Settings or narrow the input.";
        if (Has("No space left on device") || Has("not enough space on the disk") || Has("disk full"))
            return "Free disk space on the case drive.";
        if (Has("being used by another process") || Has("Access to the path") || Has("Access is denied") || Has("sharing violation"))
            return "Close programs using the file or the output folder and retry.";
        return Generic;
    }
}
