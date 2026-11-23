using System.Text;

namespace EzForensics.Core.Explore.Search;

/// <summary>Result of sniffing a file's first bytes.</summary>
/// <param name="Encoding">Decoder to use; null when the file is binary and binaries are skipped.</param>
/// <param name="PreambleLength">BOM bytes to skip before decoding.</param>
internal sealed record DetectedEncoding(Encoding? Encoding, int PreambleLength, string Name, bool IsBinary);

/// <summary>Encoding detection and the Windows-1252 helpers for content search.</summary>
public static class TextDecoding
{
    public const int SniffBytes = 8192;

    private static readonly char[] Cp1252High =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    /// <summary>The Windows-1252 character for a byte (undefined bytes map to the same-numbered C1 control).</summary>
    public static char Cp1252(byte b) => b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;

    /// <summary>Windows-1252 (code page provider when present, else an equivalent built-in decoder).</summary>
    public static Encoding Windows1252 { get; } = CreateWindows1252();

    /// <summary>UTF-8 that decodes each invalid byte as its Windows-1252 character instead of U+FFFD.</summary>
    public static Encoding Utf8With1252Fallback { get; } = CreateUtf8Fallback();

    private static Encoding CreateWindows1252()
    {
        try
        {
            if (CodePagesEncodingProvider.Instance.GetEncoding(1252) is { } e) return e;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { }
        // No code page provider: Latin-1 differs from 1252 only in 0x80-0x9F.
        return Encoding.Latin1;
    }

    private static Encoding CreateUtf8Fallback()
    {
        var e = (Encoding)new UTF8Encoding(false).Clone();
        e.DecoderFallback = new Cp1252DecoderFallback();
        return e;
    }

    internal static DetectedEncoding Detect(ReadOnlySpan<byte> head, ContentEncoding mode, bool skipBinary)
    {
        int bom = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) bom = 3;
        else if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) bom = -2;      // LE
        else if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) bom = -3;      // BE

        switch (mode)
        {
            case ContentEncoding.Utf16LE: return new(new UnicodeEncoding(false, false), bom == -2 ? 2 : 0, "UTF-16LE", false);
            case ContentEncoding.Utf16BE: return new(new UnicodeEncoding(true, false), bom == -3 ? 2 : 0, "UTF-16BE", false);
            case ContentEncoding.Utf8:
                if (skipBinary && bom != 3 && head.IndexOf((byte)0) >= 0) return Binary();
                return new(new UTF8Encoding(false), bom == 3 ? 3 : 0, "UTF-8", false);
            case ContentEncoding.Ansi:
                if (skipBinary && head.IndexOf((byte)0) >= 0) return Binary();
                return new(Windows1252, 0, "Windows-1252", false);
        }

        // Auto
        if (bom == 3) return new(Utf8With1252Fallback, 3, "UTF-8", false);
        if (bom == -2) return new(new UnicodeEncoding(false, false), 2, "UTF-16LE", false);
        if (bom == -3) return new(new UnicodeEncoding(true, false), 2, "UTF-16BE", false);
        var utf16 = GuessUtf16(head);
        if (utf16 == 1) return new(new UnicodeEncoding(false, false), 0, "UTF-16LE", false);
        if (utf16 == 2) return new(new UnicodeEncoding(true, false), 0, "UTF-16BE", false);
        if (head.IndexOf((byte)0) >= 0)
            return skipBinary ? Binary() : new(Windows1252, 0, "Windows-1252 (binary)", true);
        return new(Utf8With1252Fallback, 0, "UTF-8", false);

        static DetectedEncoding Binary() => new(null, 0, "binary", true);
    }

    /// <summary>1 = UTF-16LE, 2 = UTF-16BE, 0 = neither: at least 30% of the pairs have a NUL on one side and at most 5% on the other.</summary>
    internal static int GuessUtf16(ReadOnlySpan<byte> head)
    {
        int pairs = head.Length / 2;
        if (pairs == 0) return 0;
        int even = 0, odd = 0;
        for (int k = 0; k < pairs; k++)
        {
            if (head[2 * k] == 0) even++;
            if (head[2 * k + 1] == 0) odd++;
        }
        if (odd >= pairs * 0.3 && even <= pairs * 0.05) return 1;
        if (even >= pairs * 0.3 && odd <= pairs * 0.05) return 2;
        return 0;
    }

    private sealed class Cp1252DecoderFallback : DecoderFallback
    {
        public override int MaxCharCount => 4;
        public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer();

        private sealed class Buffer : DecoderFallbackBuffer
        {
            private char[] _chars = Array.Empty<char>();
            private int _pos;

            public override bool Fallback(byte[] bytesUnknown, int index)
            {
                _chars = new char[bytesUnknown.Length];
                for (int i = 0; i < bytesUnknown.Length; i++) _chars[i] = Cp1252(bytesUnknown[i]);
                _pos = 0;
                return _chars.Length > 0;
            }

            public override char GetNextChar() => _pos < _chars.Length ? _chars[_pos++] : '\0';

            public override bool MovePrevious()
            {
                if (_pos == 0) return false;
                _pos--;
                return true;
            }

            public override int Remaining => _chars.Length - _pos;

            public override void Reset() { _chars = Array.Empty<char>(); _pos = 0; }
        }
    }
}

/// <summary>
/// Reads text lines with a bounded buffer: <c>\r\n</c>, <c>\n</c> and <c>\r</c> end a line; a line longer than <see cref="MaxLineChars"/>
/// comes back in segments (<c>endsLine</c> false for all but the last), so one huge line never fills memory.
/// </summary>
internal sealed class BoundedLineReader
{
    public const int MaxLineChars = 1 << 20;

    private readonly TextReader _r;
    private readonly char[] _buf = new char[1 << 15];
    private readonly StringBuilder _sb = new();
    private readonly int _max;
    private int _pos, _len;
    private bool _eof, _skipLf;

    public BoundedLineReader(TextReader r, int maxLineChars = MaxLineChars) { _r = r; _max = maxLineChars; }

    public bool TryRead(out string line, out bool endsLine)
    {
        _sb.Clear();
        while (true)
        {
            if (_pos == _len)
            {
                if (_eof || !Fill())
                {
                    line = _sb.ToString();
                    endsLine = true;
                    return _sb.Length > 0;
                }
            }
            if (_skipLf)
            {
                _skipLf = false;
                if (_buf[_pos] == '\n') { _pos++; continue; }
            }
            var span = _buf.AsSpan(_pos, _len - _pos);
            int room = _max - _sb.Length;
            int nl = span.IndexOfAny('\r', '\n');
            if (nl >= 0 && nl <= room)
            {
                _sb.Append(span[..nl]);
                _pos += nl + 1;
                if (span[nl] == '\r') _skipLf = true;
                line = _sb.ToString();
                endsLine = true;
                return true;
            }
            int take = Math.Min(span.Length, room);
            _sb.Append(span[..take]);
            _pos += take;
            if (_sb.Length >= _max)
            {
                line = _sb.ToString();
                endsLine = false;
                return true;
            }
        }
    }

    private bool Fill()
    {
        _len = _r.Read(_buf, 0, _buf.Length);
        _pos = 0;
        if (_len == 0) { _eof = true; return false; }
        return true;
    }
}
