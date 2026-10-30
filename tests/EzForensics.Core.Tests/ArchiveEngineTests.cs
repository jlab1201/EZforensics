using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using EzForensics.Core.Archives;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ArchiveEngineTests : IDisposable
{
    internal const string Pw = "EzTest-123!";
    internal const string RarPw = "test";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ezarch-eng-" + Guid.NewGuid().ToString("N"));
    private readonly SharpCompressArchiveEngine _engine = new();

    public ArchiveEngineTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    internal static string Fx(string rel) => Path.Combine(TriageTestData.Root, "archives", rel);

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip", ArchiveFormat.Zip)]
    [InlineData("7z-aes-enchdr/sample.7z", ArchiveFormat.SevenZip)]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar", ArchiveFormat.Rar)]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar", ArchiveFormat.Rar)]
    [InlineData("tar-gz/sample.tar.gz", ArchiveFormat.TarGz)]
    public void Detects_format_by_magic(string rel, ArchiveFormat expected) =>
        ArchiveFormatDetector.Detect(Fx(rel)).Should().Be(expected);

    [Fact]
    public void Detects_tar_gz_vhdx_vhd_and_unknown_from_bytes()
    {
        var tar = Path.Combine(_tmp, "x.bin");
        using (var fs = File.Create(tar)) using (var w = new TarWriter(fs)) w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a.txt") { DataStream = new MemoryStream(new byte[] { 1 }) });
        ArchiveFormatDetector.Detect(tar).Should().Be(ArchiveFormat.Tar);

        var gz = Path.Combine(_tmp, "y.dat");
        using (var fs = File.Create(gz)) using (var g = new GZipStream(fs, CompressionLevel.Fastest)) g.Write(Encoding.ASCII.GetBytes("hello"));
        ArchiveFormatDetector.Detect(gz).Should().Be(ArchiveFormat.GZip);

        var vhdx = Path.Combine(_tmp, "d.vhdx");
        File.WriteAllBytes(vhdx, Encoding.ASCII.GetBytes("vhdxfile").Concat(new byte[1024]).ToArray());
        ArchiveFormatDetector.Detect(vhdx).Should().Be(ArchiveFormat.Vhdx);

        var vhd = Path.Combine(_tmp, "d.vhd");
        File.WriteAllBytes(vhd, new byte[2048].Concat(Encoding.ASCII.GetBytes("conectix")).Concat(new byte[504]).ToArray());
        ArchiveFormatDetector.Detect(vhd).Should().Be(ArchiveFormat.Vhd);

        var txt = Path.Combine(_tmp, "fake.zip");
        File.WriteAllText(txt, "not an archive");
        ArchiveFormatDetector.Detect(txt).Should().Be(ArchiveFormat.Unknown);
    }

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip", false, "ZipCrypto")]
    [InlineData("zip-aes256/sample.zip", false, "AES-256")]
    [InlineData("7z-aes-plainhdr/sample.7z", false, "AES-256")]
    [InlineData("7z-aes-enchdr/sample.7z", true, "AES-256")]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar", true, "AES-128")]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar", true, "AES-256")]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar", false, "AES-256")]
    public void Inspect_flags_encryption_without_a_password(string rel, bool headersEncrypted, string method)
    {
        var insp = _engine.Inspect(Fx(rel));
        insp.IsEncrypted.Should().BeTrue();
        insp.HeadersEncrypted.Should().Be(headersEncrypted);
        insp.EncryptionMethod.Should().Be(method);
        if (headersEncrypted) insp.Entries.Should().BeEmpty();
        else insp.Entries.Should().Contain(e => !e.IsDirectory && e.IsEncrypted);
    }

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip", Pw)]
    [InlineData("zip-aes256/sample.zip", Pw)]
    [InlineData("7z-aes-plainhdr/sample.7z", Pw)]
    [InlineData("7z-aes-enchdr/sample.7z", Pw)]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar", RarPw)]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar", RarPw)]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar", RarPw)]
    public void VerifyPassword_accepts_the_right_password_and_rejects_a_wrong_one(string rel, string pw)
    {
        _engine.VerifyPassword(Fx(rel), pw).Should().BeTrue();
        _engine.VerifyPassword(Fx(rel), "definitely-wrong").Should().BeFalse();
    }

    [Fact]
    public void Encrypted_headers_with_a_wrong_password_throw_without_leaking_it()
    {
        var act = () => _engine.Inspect(Fx("7z-aes-enchdr/sample.7z"), "Wrong-Secret-42");
        act.Should().Throw<ArchiveWrongPasswordException>().Which.ToString().Should().NotContain("Wrong-Secret-42");
    }

    [Fact]
    public void Encrypted_headers_list_after_the_password()
    {
        var insp = _engine.Inspect(Fx("7z-aes-enchdr/sample.7z"), Pw);
        insp.HeadersEncrypted.Should().BeTrue();
        insp.Entries.Select(e => e.Name.Replace('\\', '/')).Should().Contain(new[] { "a.txt", "sub/b.txt" });
    }

    [Fact]
    public void Zip_ntfs_extra_field_gives_created_and_accessed_times()
    {
        var insp = _engine.Inspect(Fx("zip-ntfs-times/sample.zip"));
        var a = insp.Entries.Single(e => e.Name == "a.txt");
        a.TimeSource.Should().Be(EntryTimeSource.NtfsExtra);
        a.ModifiedUtc.Should().NotBeNull();
        a.CreatedUtc.Should().NotBeNull();
        a.AccessedUtc.Should().NotBeNull();
        a.ModifiedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Dos_only_zip_times_are_marked_dos_local()
    {
        var zip = Path.Combine(_tmp, "dos.zip");
        new ZipTestWriter().Add("a.txt", "x").Save(zip);
        var e = _engine.Inspect(zip).Entries.Single();
        e.TimeSource.Should().Be(EntryTimeSource.DosLocal);
        e.ModifiedUtc.Should().Be(DateTime.SpecifyKind(new DateTime(2020, 1, 2, 3, 4, 6), DateTimeKind.Local).ToUniversalTime());
    }

    [Fact]
    public void Generated_zipcrypto_entries_decode_with_the_password()
    {
        var zip = Path.Combine(_tmp, "zc.zip");
        new ZipTestWriter().Add("secret.txt", "top secret payload", e => e.ZipCryptoPassword = "Gen-Pass-1").Save(zip);
        var insp = _engine.Inspect(zip);
        insp.IsEncrypted.Should().BeTrue();
        insp.EncryptionMethod.Should().Be("ZipCrypto");
        _engine.VerifyPassword(zip, "Gen-Pass-1").Should().BeTrue();
        _engine.VerifyPassword(zip, "nope").Should().BeFalse();
    }

    [Fact]
    public void Tar_lists_symlinks_and_pax_access_time()
    {
        var tar = Path.Combine(_tmp, "t.tar");
        var atime = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        using (var fs = File.Create(tar)) using (var w = new TarWriter(fs, TarEntryFormat.Pax))
        {
            var attrs = new Dictionary<string, string> { ["atime"] = new DateTimeOffset(atime).ToUnixTimeSeconds().ToString() };
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir/a.txt", attrs) { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("abc")), ModificationTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) });
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
        }
        var insp = _engine.Inspect(tar);
        var a = insp.Entries.Single(e => e.Name == "dir/a.txt");
        a.AccessedUtc.Should().Be(atime);
        a.ModifiedUtc.Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        a.CreatedUtc.Should().BeNull();
        a.TimeSource.Should().Be(EntryTimeSource.TarPax);
        insp.Entries.Single(e => e.Name == "link").IsSymlink.Should().BeTrue();
    }

    [Fact]
    public void Password_probe_is_the_smallest_encrypted_entry_or_first_solid_entry()
    {
        var zipInsp = _engine.Inspect(Fx("zip-zipcrypto/sample.zip"));
        ArchivePasswordVerifier.SelectProbe(zipInsp)!.Name.Should().Be("a.txt");
        var sevenInsp = _engine.Inspect(Fx("7z-aes-plainhdr/sample.7z"));
        sevenInsp.IsSolid.Should().BeTrue();
        var probe = ArchivePasswordVerifier.SelectProbe(sevenInsp)!;
        probe.Should().Be(sevenInsp.Entries.Where(e => !e.IsDirectory && e.Size > 0).OrderBy(e => e.Index).First());
    }
}
