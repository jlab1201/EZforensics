using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EzForensics.Core.Sysinternals;

/// <param name="Valid">Authenticode signature present, chain to a trusted root valid, file not modified.</param>
/// <param name="Signer">Leaf certificate subject (e.g. <c>CN=Microsoft Corporation, O=Microsoft Corporation, ...</c>).</param>
public sealed record SignatureCheck(bool Valid, bool IsMicrosoft, string? Signer, string? Thumbprint, string Detail)
{
    public bool Trusted => Valid && IsMicrosoft;
}

/// <summary>Authenticode verification (Windows-only; other OSes get a verifier that reports "not supported").</summary>
public interface ISignatureVerifier
{
    SignatureCheck Verify(string file);
}

public static class SignatureVerifiers
{
    /// <summary>WinVerifyTrust on Windows; a stub elsewhere.</summary>
    public static ISignatureVerifier Default { get; } = OperatingSystem.IsWindows() ? new AuthenticodeVerifier() : new UnsupportedVerifier();

    private sealed class UnsupportedVerifier : ISignatureVerifier
    {
        public SignatureCheck Verify(string file) => new(false, false, null, null, "Authenticode verification is only available on Windows.");
    }
}

/// <summary>
/// WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2: file hash, signature and certificate chain to a trusted root; revocation is
/// not checked so it never blocks on the network) plus a check that the signing certificate belongs to Microsoft Corporation.
/// </summary>
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    public SignatureCheck Verify(string file)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, null, null, "Not Windows.");
        if (!File.Exists(file)) return new(false, false, null, null, "File not found.");
        string? subject = null, thumb = null;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile reads the Authenticode signer; there is no non-obsolete equivalent in .NET 8
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
            thumb = cert.Thumbprint;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return new(false, false, null, null, "Not signed (" + ex.Message.Trim() + ").");
        }
        int hr = WinVerify(file);
        bool ms = subject != null && subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal)
                  && subject.Contains("CN=Microsoft Corporation", StringComparison.Ordinal);
        var detail = hr == 0 ? "Signature valid" : $"WinVerifyTrust 0x{hr:X8}";
        return new(hr == 0, ms, subject, thumb, detail + (ms ? ", signed by Microsoft Corporation" : ", signer is not Microsoft Corporation"));
    }

    private static int WinVerify(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE: chain + hash verified; revocation is not checked (works offline)
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
