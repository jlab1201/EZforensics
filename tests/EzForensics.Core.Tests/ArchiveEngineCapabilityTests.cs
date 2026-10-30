using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Xunit.Abstractions;

namespace EzForensics.Core.Tests;

/// <summary>
/// Capability spike: what SharpCompress can do with the checked-in encrypted fixtures
/// (tests/TestData/archives). Results are written to the test output as a matrix.
/// </summary>
public class ArchiveEngineCapabilityTests
{
    private const string Pw = "EzTest-123!";
    private const string RarPw = "test";
    private readonly ITestOutputHelper _out;
    public ArchiveEngineCapabilityTests(ITestOutputHelper o) => _out = o;

    private static string Fx(string rel) => Path.Combine(TriageTestData.Root, "archives", rel);

    public static TheoryData<string, string?, bool> Fixtures => new()
    {
        { "zip-zipcrypto/sample.zip", Pw, true },
        { "zip-aes256/sample.zip", Pw, true },
        { "7z-aes-plainhdr/sample.7z", Pw, true },
        { "7z-aes-enchdr/sample.7z", Pw, true },
        { "7z-plain/sample.7z", null, false },
        { "rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar", RarPw, true },
        { "rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar", RarPw, true },
        { "rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar", RarPw, true },
    };

    private static IArchive Open(string rel, string? pw) =>
        ArchiveFactory.OpenArchive(Fx(rel), new ReaderOptions { Password = pw });

    private static byte[] ReadAll(IArchiveEntry e)
    {
        using var s = e.OpenEntryStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Lists_entries_with_password_and_flags_encryption(string rel, string? pw, bool encrypted)
    {
        using var a = Open(rel, pw);
        var files = a.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.NotEmpty(files);
        foreach (var e in files)
            _out.WriteLine($"{rel} :: {e.Key} size={e.Size} enc={e.IsEncrypted}");
        Assert.All(files, e => Assert.Equal(encrypted, e.IsEncrypted));
    }

    [Theory]
    [InlineData("7z-aes-enchdr/sample.7z")]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar")]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar")]
    public void Encrypted_headers_listing_without_password_throws(string rel)
    {
        var ex = Record.Exception(() =>
        {
            using var a = Open(rel, null);
            _ = a.Entries.ToList();
        });
        _out.WriteLine($"{rel} no-password listing => {ex?.GetType().FullName}: {ex?.Message}");
        Assert.NotNull(ex);
    }

    [Theory]
    [InlineData("7z-aes-enchdr/sample.7z")]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar")]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar")]
    public void Encrypted_headers_listing_with_wrong_password_throws(string rel)
    {
        var ex = Record.Exception(() =>
        {
            using var a = Open(rel, "definitely-wrong");
            _ = a.Entries.ToList();
        });
        _out.WriteLine($"{rel} wrong-password listing => {ex?.GetType().FullName}: {ex?.Message}");
        Assert.NotNull(ex);
    }

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip", Pw)]
    [InlineData("zip-aes256/sample.zip", Pw)]
    [InlineData("7z-aes-plainhdr/sample.7z", Pw)]
    [InlineData("7z-aes-enchdr/sample.7z", Pw)]
    [InlineData("7z-plain/sample.7z", null)]
    public void Correct_password_extracts_expected_bytes(string rel, string? pw)
    {
        using var a = Open(rel, pw);
        var map = a.Entries.Where(e => !e.IsDirectory)
            .ToDictionary(e => e.Key!.Replace('\\', '/'), ReadAll);
        Assert.Equal("hello secret one\n", System.Text.Encoding.ASCII.GetString(map["a.txt"]).Replace("\r\n", "\n"));
        Assert.Equal("second file contents 12345\n", System.Text.Encoding.ASCII.GetString(map["sub/b.txt"]).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar")]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar")]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar")]
    public void Rar_correct_password_extracts_with_matching_size(string rel)
    {
        using var a = Open(rel, RarPw);
        var files = a.Entries.Where(e => !e.IsDirectory).ToList();
        Assert.NotEmpty(files);
        foreach (var e in files)
        {
            var bytes = ReadAll(e);
            _out.WriteLine($"{rel} :: {e.Key} read={bytes.Length} size={e.Size} crc={e.Crc:X8}");
            Assert.Equal(e.Size, bytes.Length);
        }
    }

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip")]
    [InlineData("zip-aes256/sample.zip")]
    [InlineData("7z-aes-plainhdr/sample.7z")]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar")]
    public void Wrong_password_on_data_is_detectable(string rel)
    {
        var ex = Record.Exception(() =>
        {
            using var a = Open(rel, "wrong-password");
            foreach (var e in a.Entries.Where(x => !x.IsDirectory))
                ReadAll(e);
        });
        _out.WriteLine($"{rel} wrong-password extract => {ex?.GetType().FullName}: {ex?.Message}");
        Assert.NotNull(ex);
    }

    [Theory]
    [InlineData("zip-zipcrypto/sample.zip")]
    [InlineData("zip-aes256/sample.zip")]
    [InlineData("7z-aes-plainhdr/sample.7z")]
    [InlineData("7z-plain/sample.7z")]
    [InlineData("zip-ntfs-times/sample.zip")]
    [InlineData("7z-all-times/sample.7z")]
    public void Reports_which_timestamps_are_exposed(string rel)
    {
        using var a = Open(rel, Pw);
        foreach (var e in a.Entries.Where(x => !x.IsDirectory))
            _out.WriteLine($"{rel} :: {e.Key} mtime={e.LastModifiedTime:O} ctime={e.CreatedTime:O} atime={e.LastAccessedTime:O} archived={e.ArchivedTime:O}");
    }

    // 7-Zip stores ctime/atime in these fixtures (verified with `7z l -slt`) and the zip carries an NTFS 0x000a
    // extra field, yet SharpCompress 0.50.5 reports them as null: ingestion must NOT rely on them (phase 2 would
    // need its own extra-field parser / SharpSevenZip). This test pins the current behaviour and the mtime.
    [Theory]
    [InlineData("7z-all-times/sample.7z")]
    [InlineData("zip-ntfs-times/sample.zip")]
    public void Created_and_accessed_times_are_not_exposed_but_mtime_is(string rel)
    {
        using var a = Open(rel, null);
        foreach (var e in a.Entries.Where(x => !x.IsDirectory))
        {
            _out.WriteLine($"{rel} {e.Key}: mtime={e.LastModifiedTime} ctime={e.CreatedTime} atime={e.LastAccessedTime}");
            Assert.NotNull(e.LastModifiedTime);
            Assert.Null(e.CreatedTime);
            Assert.Null(e.LastAccessedTime);
        }
    }
}
