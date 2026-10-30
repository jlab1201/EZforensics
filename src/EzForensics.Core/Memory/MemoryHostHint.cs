using System.Text;

namespace EzForensics.Core.Memory;

/// <summary>Guesses the host name of a memory image by stream-scanning for UTF-16LE "COMPUTERNAME=" environment strings.</summary>
public static class MemoryHostHint
{
    public const string FromMemoryStrings = "memory strings";

    private static readonly byte[] Needle = Encoding.Unicode.GetBytes("COMPUTERNAME=");

    public static string? TryGetComputerName(string imagePath, long maxBytes = 4L << 30, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            const int chunk = 8 << 20;
            const int overlap = 256; // needle (26 bytes) + longest name (30 bytes) with margin
            var buf = new byte[chunk + overlap];
            var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            int carry = 0;
            while (total < maxBytes)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(chunk, maxBytes - total);
                int got = 0;
                while (got < want)
                {
                    int r = fs.Read(buf, carry + got, want - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got == 0) break;
                bool last = got < want || total + got >= maxBytes;
                int len = carry + got;
                var span = buf.AsSpan(0, len);
                int pos = 0;
                while (pos < len)
                {
                    int i = span[pos..].IndexOf(Needle);
                    if (i < 0) break;
                    int start = pos + i;
                    pos = start + Needle.Length;
                    // A match in the tail is re-found (and counted) in the next window, where it sits after the carry boundary.
                    if (!last && start >= len - overlap) continue;
                    var sb = new StringBuilder();
                    for (int p = pos; p + 1 < len && sb.Length < 16; p += 2)
                    {
                        char c = (char)(span[p] | span[p + 1] << 8);
                        if (c < 0x20 || c > 0x7E) break;
                        sb.Append(c);
                    }
                    var name = sb.ToString();
                    if (IsPlausible(name)) votes[name] = votes.GetValueOrDefault(name) + 1;
                }
                total += got;
                if (last) break;
                carry = Math.Min(overlap, len);
                span[^carry..].CopyTo(buf);
            }
            if (votes.Count == 0) return null;
            return votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsPlausible(string s)
    {
        if (s.Length is < 1 or > 15) return false;
        foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }
}
