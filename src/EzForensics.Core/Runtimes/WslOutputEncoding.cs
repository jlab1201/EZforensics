using System.Text;

namespace EzForensics.Core.Runtimes;

/// <summary>
/// Decodes wsl.exe output. With <c>WSL_UTF8=1</c> (and for the Linux programs it runs) the output is UTF-8, but wsl.exe builds that
/// ignore the variable (inbox / older WSL) write their own messages as UTF-16LE without a BOM, which read as UTF-8 come out as
/// "W\0S\0L\0" and hide the real error. The decoder looks at the first bytes of the stream: ASCII-range UTF-16LE (a NUL as every
/// second byte; UTF-8 text never contains NUL) is decoded as UTF-16LE, anything else as UTF-8. Encoding (stdin) is UTF-8.
/// </summary>
public sealed class WslOutputEncoding : Encoding
{
    public static readonly WslOutputEncoding Instance = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly Encoding Utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    /// <summary>True for the wsl.exe launcher (any folder, any case).</summary>
    public static bool IsWslExe(string? exe) =>
        !string.IsNullOrEmpty(exe) && Path.GetFileName(exe).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UTF-16LE when the bytes start with its BOM or with an ASCII character followed by a NUL (two bytes are enough: UTF-8 has
    /// no NUL bytes in text). Null when there are not enough bytes to tell.
    /// </summary>
    public static bool? LooksLikeUtf16Le(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) return b.Length == 0 ? null : false;
        if (b[0] == 0xFF && b[1] == 0xFE) return true;
        return b[0] != 0 && b[1] == 0;
    }

    /// <summary>Whole-buffer decode with the same detection (tests, short probe outputs).</summary>
    public static string Decode(byte[] bytes) => LooksLikeUtf16Le(bytes) == true ? Utf16.GetString(bytes).TrimStart('﻿') : Utf8.GetString(bytes);

    public override string EncodingName => "wsl.exe output (UTF-8 or UTF-16LE)";
    public override int GetByteCount(char[] chars, int index, int count) => Utf8.GetByteCount(chars, index, count);
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => Utf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
    public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes[index..(index + count)]).Length;
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        var s = Decode(bytes[byteIndex..(byteIndex + byteCount)]);
        s.CopyTo(0, chars, charIndex, s.Length);
        return s.Length;
    }
    public override int GetMaxByteCount(int charCount) => Utf8.GetMaxByteCount(charCount);
    // UTF-8's bound (one char per byte, plus a pending char) is also enough for UTF-16LE (one char per two bytes).
    public override int GetMaxCharCount(int byteCount) => Utf8.GetMaxCharCount(byteCount) + 1;
    public override byte[] GetPreamble() => Array.Empty<byte>();
    public override Decoder GetDecoder() => new SniffingDecoder();

    /// <summary>Buffers the first byte until the stream's encoding is known, then hands everything to the chosen decoder.</summary>
    private sealed class SniffingDecoder : Decoder
    {
        private Decoder? _inner;
        private byte[] _pending = Array.Empty<byte>();

        private Decoder? Choose(ReadOnlySpan<byte> start, bool flush)
        {
            if (_inner != null) return _inner;
            var utf16 = LooksLikeUtf16Le(start);
            if (utf16 == null) return flush ? (_inner = Utf8.GetDecoder()) : null;
            if (start.Length < 2 && !flush) return null; // one byte: wait for the second one to tell
            return _inner = (utf16 == true ? Utf16 : Utf8).GetDecoder();
        }

        private byte[] Join(byte[] bytes, int index, int count)
        {
            var all = new byte[_pending.Length + count];
            _pending.CopyTo(all, 0);
            Array.Copy(bytes, index, all, _pending.Length, count);
            return all;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => GetCharCount(bytes, index, count, false);

        public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
        {
            var all = Join(bytes, index, count);
            if (_inner != null) return _inner.GetCharCount(all, 0, all.Length, flush);
            var probe = (LooksLikeUtf16Le(all) == true ? Utf16 : Utf8);
            return all.Length < 2 && !flush ? 0 : probe.GetCharCount(all);
        }

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            GetChars(bytes, byteIndex, byteCount, chars, charIndex, false);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
        {
            var all = Join(bytes, byteIndex, byteCount);
            var d = Choose(all, flush);
            if (d == null)
            {
                _pending = all;
                return 0;
            }
            _pending = Array.Empty<byte>();
            return d.GetChars(all, 0, all.Length, chars, charIndex, flush);
        }

        public override void Reset()
        {
            _inner = null;
            _pending = Array.Empty<byte>();
        }
    }
}
