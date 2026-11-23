using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EzForensics.Core.Tools;

/// <summary>Authenticode result of an executable: whether Windows trusts the signature, the signer subject, and why not when it is not trusted.</summary>
public sealed record PeSignature(bool Trusted, string? SignerSubject, string? Problem);

/// <summary>Checks the Authenticode signature of an .exe/.dll. Replaceable for tests.</summary>
public interface IPeSignatureVerifier
{
    PeSignature Verify(string path);
}

/// <summary>WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2) for validity, plus the signer certificate subject.</summary>
public sealed class WinTrustSignatureVerifier : IPeSignatureVerifier
{
    public static readonly WinTrustSignatureVerifier Instance = new();

    public PeSignature Verify(string path)
    {
        if (!OperatingSystem.IsWindows()) return new PeSignature(false, null, "signature checks are only available on Windows");
        if (!File.Exists(path)) return new PeSignature(false, null, "the file does not exist");
        int rc = WinVerifyTrustFile(path);
        string? subject = null;
        try { subject = X509Certificate.CreateFromSignedFile(path).Subject; } catch { }
        return rc == 0
            ? new PeSignature(true, subject, null)
            : new PeSignature(false, subject, rc == unchecked((int)0x800B0100) ? "it is not signed" : $"WinVerifyTrust returned 0x{rc:X8}");
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static int WinVerifyTrustFile(string path)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (no network lookups; offline forensic machines)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000,  // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }
}

/// <summary>Decision logic around signatures (testable without Windows).</summary>
public static class ToolSignaturePolicy
{
    /// <summary>
    /// Signer organizations accepted for Eric Zimmerman's tools. Checked against real files on Nov 22 2026: the exes are signed
    /// "CN=ZimTech LLC, O=ZimTech LLC, S=Indiana, C=US". "Eric Zimmerman" is kept for older builds signed in his own name.
    /// </summary>
    public static readonly string[] EzSignerNames = { "ZimTech LLC", "Eric Zimmerman" };

    /// <summary>Null when the exe may be elevated (valid trusted signature), else the error text to show.</summary>
    public static string? ElevationProblem(IPeSignatureVerifier v, string exePath, string toolName, Action<string>? log = null)
    {
        var sig = v.Verify(exePath);
        if (!sig.Trusted)
        {
            log?.Invoke($"{toolName}: signature check failed ({sig.Problem}); not elevated.");
            return $"{toolName} is not signed or its signature is invalid; refusing to run it as administrator.";
        }
        log?.Invoke($"{toolName}: signed by {sig.SignerSubject ?? "(unknown signer)"}; elevating.");
        return null;
    }

    /// <summary>Null when the signature is valid and the signer is an Eric Zimmerman one, else the reason it is not accepted.</summary>
    public static string? EzSignerProblem(PeSignature sig)
    {
        if (!sig.Trusted) return $"signature not valid ({sig.Problem ?? "unknown"})";
        if (sig.SignerSubject is null || !HasEzSigner(sig.SignerSubject))
            return $"unexpected signer ({sig.SignerSubject ?? "none"})";
        return null;
    }

    private static bool HasEzSigner(string subject)
    {
        foreach (var part in subject.Split(','))
        {
            var kv = part.Trim();
            foreach (var n in EzSignerNames)
                if (kv.Equals("CN=" + n, StringComparison.Ordinal) || kv.Equals("O=" + n, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
