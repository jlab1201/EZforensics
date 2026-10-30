using EzForensics.Core.Results;
using EzForensics.Core.Zeek;
using FluentAssertions;

namespace EzForensics.Core.Tests;

internal static class ZeekTestData
{
    public static string Dir => Path.Combine(TriageTestData.Root, "zeek");

    /// <summary>Copies a fixture log into a temp folder (the converter writes next to the log).</summary>
    public static string CopyTo(TempDir t, string name)
    {
        var p = Path.Combine(t.Path, name);
        File.Copy(Path.Combine(Dir, name), p, overwrite: true);
        return p;
    }

    public static async Task<(IReadOnlyList<string> Headers, List<string[]> Rows)> ReadCsvAsync(string path)
    {
        using var idx = await CsvIndex.OpenAsync(path, null, CancellationToken.None);
        await idx.IndexingCompleted;
        var rows = new List<string[]>();
        for (long i = 0; i < idx.RowCount; i++) rows.Add(idx.ReadRow(i));
        return (idx.Headers.ToList(), rows);
    }
}

public class ZeekConverterTests
{
    private static string Cell(IReadOnlyList<string> headers, string[] row, string col) => row[headers.ToList().IndexOf(col)];

    [Fact]
    public async Task Conn_log_gets_DateTimeUtc_keeps_all_fields_and_blanks_unset_and_empty()
    {
        using var t = new TempDir();
        var r = ZeekLogConverter.Convert(ZeekTestData.CopyTo(t, "conn.log"));
        r.Succeeded.Should().BeTrue(r.Error);
        r.Format.Should().Be("tsv");
        r.Rows.Should().Be(3);
        r.CsvPath.Should().EndWith("conn.csv");
        File.Exists(r.CsvPath + ".tmp").Should().BeFalse();

        var (h, rows) = await ZeekTestData.ReadCsvAsync(r.CsvPath);
        h.Should().Equal("DateTimeUtc", "ts", "uid", "id.orig_h", "id.orig_p", "id.resp_h", "id.resp_p", "proto", "service", "duration",
            "orig_bytes", "resp_bytes", "conn_state", "local_orig", "history", "tunnel_parents");
        r.Columns.Should().Be(16);
        Cell(h, rows[0], "DateTimeUtc").Should().Be("2009-11-18 09:14:13.0871370");
        Cell(h, rows[0], "ts").Should().Be("1258535653.087137", "the raw epoch is kept");
        Cell(h, rows[0], "tunnel_parents").Should().BeEmpty("(empty) is an empty set");
        Cell(h, rows[1], "duration").Should().BeEmpty("- is unset");
        Cell(h, rows[1], "service").Should().Be("dns");
        Cell(h, rows[1], "tunnel_parents").Should().Be("CuJGNz1yGa5FDHKOl4,Cx9n8F2bfXvG3BxAkh", "set elements keep Zeek's , separator");
        Cell(h, rows[1], "DateTimeUtc").Should().Be("2009-11-18 09:14:14.5000000");
        Cell(h, rows[2], "id.orig_h").Should().Be("fe80::1");
        Cell(h, rows[2], "service").Should().BeEmpty();
    }

    [Fact]
    public async Task Dns_log_vectors_utf8_escapes_and_escaped_separators()
    {
        using var t = new TempDir();
        var r = ZeekLogConverter.Convert(ZeekTestData.CopyTo(t, "dns.log"));
        r.Succeeded.Should().BeTrue(r.Error);
        var (h, rows) = await ZeekTestData.ReadCsvAsync(r.CsvPath);
        Cell(h, rows[0], "answers").Should().Be("192.150.187.43,10.0.0.1");
        Cell(h, rows[0], "TTLs").Should().Be("300.000000,300.000000");
        Cell(h, rows[1], "query").Should().Be("münchen.example", "\\xc3\\xbc is UTF-8 for ü");
        Cell(h, rows[1], "answers").Should().BeEmpty();
        Cell(h, rows[1], "TTLs").Should().BeEmpty();
        Cell(h, rows[2], "query").Should().Be("a,b.example", "\\x2c is an escaped comma");
        Cell(h, rows[2], "answers").Should().Be("v,1,", "an unset element inside a vector becomes empty");
        Cell(h, rows[2], "rejected").Should().Be("F");
    }

    [Fact]
    public async Task Http_log_tabs_quotes_and_control_bytes_survive_the_csv_round_trip()
    {
        using var t = new TempDir();
        var r = ZeekLogConverter.Convert(ZeekTestData.CopyTo(t, "http.log"));
        r.Succeeded.Should().BeTrue(r.Error);
        r.Rows.Should().Be(2);
        var (h, rows) = await ZeekTestData.ReadCsvAsync(r.CsvPath);
        Cell(h, rows[0], "user_agent").Should().Be("Mozilla/5.0\t(X11)");
        Cell(h, rows[0], "username").Should().Be("-", "\\x2d is a literal dash, not the unset marker");
        Cell(h, rows[0], "tags").Should().BeEmpty();
        Cell(h, rows[1], "uri").Should().Be("/a,\"quoted\"\\x01", "control bytes stay escaped");
        Cell(h, rows[1], "user_agent").Should().BeEmpty();
        Cell(h, rows[1], "tags").Should().Be("HTTP::URI_SQLI,HTTP::POST_BODY");
        Cell(h, rows[1], "status_code").Should().Be("404");
    }

    [Fact]
    public async Task Other_time_fields_become_iso_utc()
    {
        using var t = new TempDir();
        var r = ZeekLogConverter.Convert(ZeekTestData.CopyTo(t, "x509.log"));
        var (h, rows) = await ZeekTestData.ReadCsvAsync(r.CsvPath);
        Cell(h, rows[0], "certificate.not_valid_before").Should().Be("2009-01-01 00:00:00.0000000");
        Cell(h, rows[0], "certificate.not_valid_after").Should().Be("2010-12-31 23:59:59.0000000");
        Cell(h, rows[0], "san.dns").Should().Be("www.bro.org,bro.org");
    }

    [Fact]
    public async Task Json_logs_are_converted_with_the_union_of_keys()
    {
        using var t = new TempDir();
        var r = ZeekLogConverter.Convert(ZeekTestData.CopyTo(t, "files_json.log"));
        r.Succeeded.Should().BeTrue(r.Error);
        r.Format.Should().Be("json");
        var (h, rows) = await ZeekTestData.ReadCsvAsync(r.CsvPath);
        h.Should().Equal("DateTimeUtc", "ts", "fuid", "uid", "source", "analyzers", "mime_type", "seen_bytes", "md5", "filename", "is_orig");
        Cell(h, rows[0], "DateTimeUtc").Should().Be("2009-11-18 09:14:13.5000000");
        Cell(h, rows[0], "analyzers").Should().Be("MD5,SHA1");
        Cell(h, rows[0], "filename").Should().BeEmpty();
        Cell(h, rows[1], "filename").Should().Be("a,b.exe");
        Cell(h, rows[1], "is_orig").Should().Be("F");
        Cell(h, rows[1], "uid").Should().BeEmpty();
    }

    [Fact]
    public void Header_only_log_gives_a_header_only_csv()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "weird.log");
        File.WriteAllText(p, "#separator \\x09\n#set_separator\t,\n#empty_field\t(empty)\n#unset_field\t-\n#path\tweird\n#fields\tts\tuid\tname\n#types\ttime\tstring\tstring\n#close\tx\n");
        var r = ZeekLogConverter.Convert(p);
        r.Succeeded.Should().BeTrue();
        r.Rows.Should().Be(0);
        File.ReadAllText(r.CsvPath).Should().Be("DateTimeUtc,ts,uid,name\r\n");
    }

    [Fact]
    public void Text_that_is_not_a_zeek_log_is_reported_not_thrown()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "notes.log");
        File.WriteAllText(p, "hello world\n");
        var r = ZeekLogConverter.Convert(p);
        r.Succeeded.Should().BeFalse();
        r.Error.Should().Contain("#fields");
        File.Exists(Path.Combine(t.Path, "notes.csv")).Should().BeFalse();
        File.Exists(Path.Combine(t.Path, "notes.csv.tmp")).Should().BeFalse();
    }

    [Fact]
    public void ConvertFolder_converts_every_log_and_reports()
    {
        using var t = new TempDir();
        foreach (var f in new[] { "conn.log", "dns.log", "http.log", "x509.log" }) ZeekTestData.CopyTo(t, f);
        var lines = new List<string>();
        var res = ZeekLogConverter.ConvertFolder(t.Path, lines.Add);
        res.Should().HaveCount(4).And.OnlyContain(r => r.Succeeded);
        res.Select(r => Path.GetFileName(r.CsvPath)).Should().Equal("conn.csv", "dns.csv", "http.csv", "x509.csv");
        lines.Should().HaveCount(4).And.OnlyContain(l => l.StartsWith("# Converted"));
    }

    [Theory]
    [InlineData("1258535653.087137", "2009-11-18 09:14:13.0871370")]
    [InlineData("0", "1970-01-01 00:00:00.0000000")]
    [InlineData("1700000000", "2023-11-14 22:13:20.0000000")]
    [InlineData("1700000000.123456789", "2023-11-14 22:13:20.1234567")]
    [InlineData("-", "")]
    [InlineData("", "")]
    [InlineData("abc", "")]
    [InlineData("-5", "")]
    [InlineData("17.٣", "")]   // non-ASCII digits must not throw
    public void FormatEpoch(string input, string expected) => ZeekLogConverter.FormatEpoch(input).Should().Be(expected);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\\x2cb", "a,b")]
    [InlineData("tab\\x09x", "tab\tx")]
    [InlineData("m\\xc3\\xbcnchen", "münchen")]
    [InlineData("bad\\xff\\xfe", "bad\\xff\\xfe")]
    [InlineData("ctl\\x00\\x1b", "ctl\\x00\\x1b")]
    [InlineData("back\\x5cslash", "back\\slash")]
    [InlineData("\\xzz", "\\xzz")]
    public void Unescape(string input, string expected) => ZeekLogConverter.Unescape(input).Should().Be(expected);
}
