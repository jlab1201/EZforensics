using System.Diagnostics;

namespace EzForensics.Core.Tools;

public static class GuiLauncher
{
    /// <summary>Verifier used before elevating; tests replace it.</summary>
    public static IPeSignatureVerifier Verifier { get; set; } = WinTrustSignatureVerifier.Instance;

    /// <summary>Thrown instead of elevating an exe whose Authenticode signature is missing or invalid.</summary>
    public sealed class UntrustedToolException(string message) : Exception(message);

    public static Process? Launch(string exePath, IEnumerable<string>? args = null, bool runAsAdmin = false, Action<string>? log = null)
    {
        if (runAsAdmin && ToolSignaturePolicy.ElevationProblem(Verifier, exePath, Path.GetFileNameWithoutExtension(exePath), log) is { } problem)
            throw new UntrustedToolException(problem);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        if (args != null) foreach (var a in args) psi.ArgumentList.Add(a);
        if (runAsAdmin) psi.Verb = "runas";
        return Process.Start(psi);
    }
}
