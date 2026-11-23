using System.Diagnostics;

namespace EzForensics.Core.Setup;

/// <summary>Authenticode result of a script: <c>Get-AuthenticodeSignature</c>'s Status (Valid, NotSigned, HashMismatch, ...) and signer subject.</summary>
public sealed record ScriptSignature(string Status, string? SignerSubject);

/// <summary>Checks the Authenticode signature of a downloaded script before it is run. Replaceable for tests.</summary>
public interface IScriptSignatureVerifier
{
    ScriptSignature Verify(string path);
}

/// <summary>Runs Windows PowerShell's <c>Get-AuthenticodeSignature</c> on the file (the path goes in through an environment variable, not the command line).</summary>
public sealed class PowerShellSignatureVerifier : IScriptSignatureVerifier
{
    public static readonly PowerShellSignatureVerifier Instance = new();

    private const string PathVar = "EZF_SIGNATURE_FILE";

    public ScriptSignature Verify(string path)
    {
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(ps)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$s = Get-AuthenticodeSignature -LiteralPath $env:" + PathVar + "; " +
                     "'STATUS=' + $s.Status; if ($s.SignerCertificate) { 'SUBJECT=' + $s.SignerCertificate.Subject }" })
            psi.ArgumentList.Add(a);
        psi.Environment[PathVar] = path;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new ScriptSignature("CouldNotRun", null);
            var errTask = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return new ScriptSignature("TimedOut", null); }
            _ = errTask.Result;
            string status = "Unknown", subject = "";
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("STATUS=", StringComparison.Ordinal)) status = line[7..];
                else if (line.StartsWith("SUBJECT=", StringComparison.Ordinal)) subject = line[8..];
            }
            return new ScriptSignature(status, subject.Length > 0 ? subject : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ScriptSignature("CouldNotRun: " + ex.Message, null);
        }
    }
}

public static class ScriptSignaturePolicy
{
    /// <summary>
    /// Null when <paramref name="sig"/> is a valid Authenticode signature by Microsoft Corporation, else the reason the script must not run.
    /// </summary>
    public static string? MicrosoftSignedProblem(ScriptSignature sig)
    {
        if (!string.Equals(sig.Status, "Valid", StringComparison.Ordinal))
            return $"its Authenticode signature is not valid (status: {sig.Status})";
        if (sig.SignerSubject is null || !HasMicrosoftOrganization(sig.SignerSubject))
            return $"it is not signed by Microsoft Corporation (signer: {sig.SignerSubject ?? "none"})";
        return null;
    }

    /// <summary>The subject has CN or O equal to "Microsoft Corporation" (not just containing it, e.g. "CN=Not Microsoft Corporation Ltd").</summary>
    private static bool HasMicrosoftOrganization(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var kv = part.Trim();
            if (kv.Equals("CN=Microsoft Corporation", StringComparison.Ordinal) || kv.Equals("O=Microsoft Corporation", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
