using System.IO;
using System.Runtime.InteropServices;
using EzForensics.Core.Case;

namespace EzForensics.App.Services;

/// <summary>
/// Deletes through the Windows Recycle Bin (never a silent hard delete). Uses <c>SHFileOperation</c> with
/// <c>FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING</c>: the app has already asked, but if Windows cannot recycle an item
/// (too large for the bin...) it warns before destroying it, and declining aborts the delete. Network / removable volumes are refused
/// up front (<see cref="CaseFileOperations.RecycleRefusal"/>): Windows has no Recycle Bin there.
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>A recycle callback for <see cref="CaseFileOperations"/> whose Windows prompts are owned by <paramref name="owner"/>.</summary>
    public static Action<string, bool> For(IntPtr owner) => (path, isDirectory) => Send(path, isDirectory, owner);

    public static void Send(string path, bool isDirectory) => Send(path, isDirectory, IntPtr.Zero);

    /// <summary>Sends one file / folder to the Recycle Bin. Throws when refused, failed or aborted (the item is then still there).</summary>
    public static void Send(string path, bool isDirectory, IntPtr owner)
    {
        var full = Path.GetFullPath(path);
        if (CaseFileOperations.RecycleRefusal(full) is { } why)
            throw new CaseFileOperationException("Nothing was deleted: " + why);
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = full + "\0\0",   // double-null-terminated list
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
        };
        int rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted)
            throw new CaseFileOperationException($"Delete was cancelled; {Path.GetFileName(full)} was not deleted.");
        if (rc != 0)
            throw new IOException($"Windows could not send {Path.GetFileName(full)} to the Recycle Bin (error 0x{rc:X}).");
        if (isDirectory ? Directory.Exists(full) : File.Exists(full))
            throw new IOException($"{Path.GetFileName(full)} is still there after sending it to the Recycle Bin.");
    }
}
