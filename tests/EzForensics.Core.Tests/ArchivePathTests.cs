using EzForensics.Core.Archives;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ArchivePathTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a\\..\\b.txt")]
    [InlineData("..")]
    public void Traversal_is_refused(string name)
    {
        ArchivePaths.Sanitize(name, out var reason).Should().BeNull();
        reason.Should().Contain("traversal");
    }

    [Theory]
    [InlineData("/etc/passwd", @"etc\passwd")]
    [InlineData(@"C:\Windows\System32\x.dll", @"Windows\System32\x.dll")]
    [InlineData(@"\\srv\share\a.txt", @"share\a.txt")]
    [InlineData(@"\\?\C:\a.txt", "a.txt")]
    [InlineData(@"\\.\C:\$MFT", "$MFT")]
    public void Roots_are_stripped(string name, string expected)
    {
        ArchivePaths.Sanitize(name, out var reason).Should().Be(expected);
        reason.Should().Contain("root stripped");
    }

    [Theory]
    [InlineData("./a/./b.txt", @"a\b.txt")]
    [InlineData("a//b.txt", @"a\b.txt")]
    [InlineData("$MFT", "$MFT")]
    [InlineData("Users/x/NTUSER.DAT", @"Users\x\NTUSER.DAT")]
    public void Plain_names_are_kept(string name, string expected) =>
        ArchivePaths.Sanitize(name, out _).Should().Be(expected);

    [Theory]
    [InlineData("file.txt:Zone.Identifier", "file.txt%3AZone.Identifier")]
    [InlineData("NUL.txt", "%4EUL.txt")]
    [InlineData("con", "%63on")]
    [InlineData("COM1.log", "%43OM1.log")]
    [InlineData("name. ", "name%2E%20")]
    [InlineData("a?b*.txt", "a%3Fb%2A.txt")]
    [InlineData("50%:x", "50%25%3Ax")]
    public void Unsafe_components_are_percent_encoded_reversibly(string name, string expected)
    {
        var s = ArchivePaths.Sanitize(name, out var reason);
        s.Should().Be(expected);
        reason.Should().Contain("percent-encoded");
        ArchivePaths.DecodeSegment(s!).Should().Be(name);
    }

    [Fact]
    public void Case_only_duplicates_get_a_tilde_suffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezsan-" + Guid.NewGuid().ToString("N"));
        var s = new EntryPathSanitizer(root);
        s.Sanitize("Dir/File.txt", false).RelativePath.Should().Be(@"Dir\File.txt");
        var dup = s.Sanitize("Dir/file.TXT", false);
        dup.RelativePath.Should().Be(@"Dir\file~2.TXT");
        dup.Reason.Should().Contain("~N");
        s.Sanitize("dir/other.txt", false).RelativePath.Should().Be(@"dir~2\other.txt");
        s.Sanitize("Dir/again.txt", false).RelativePath.Should().Be(@"Dir\again.txt");
    }

    [Fact]
    public void Reserved_record_file_name_is_not_overwritten()
    {
        var s = new EntryPathSanitizer(Path.Combine(Path.GetTempPath(), "ezsan"), 240, new[] { ExtractionRecord.FileName });
        s.Sanitize(".ezextract.json", false).RelativePath.Should().Be(".ezextract~2.json");
    }

    [Fact]
    public void Long_paths_are_shortened_with_a_hash_and_stay_under_the_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezsan-long");
        var s = new EntryPathSanitizer(root, 240);
        var name = string.Join('/', Enumerable.Repeat(new string('d', 60), 4)) + "/" + new string('f', 120) + ".evtx";
        var r = s.Sanitize(name, false);
        r.RelativePath.Should().NotBeNull();
        Path.Combine(Path.GetFullPath(root), r.RelativePath!).Length.Should().BeLessThanOrEqualTo(244);
        r.RelativePath.Should().EndWith(".evtx");
        r.Reason.Should().Contain("shortened");
        // The same folder is reused (consistently shortened) for a sibling.
        var r2 = s.Sanitize(string.Join('/', Enumerable.Repeat(new string('d', 60), 4)) + "/x.txt", false);
        Path.GetDirectoryName(r2.RelativePath!).Should().Be(Path.GetDirectoryName(r.RelativePath!));
    }
}
