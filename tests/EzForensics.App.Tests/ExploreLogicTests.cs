using System.IO;
using System.Text;
using EzForensics.App.ViewModels.Explore;
using EzForensics.Core.Explore;
using EzForensics.Core.Sysinternals;
using FluentAssertions;

namespace EzForensics.App.Tests;

public class ExploreLogicTests
{
    [Fact]
    public void Strings_finds_ascii_and_utf16_with_offsets()
    {
        var data = new List<byte> { 0, 1, 2 };
        data.AddRange(Encoding.ASCII.GetBytes("hello world"));
        data.AddRange(new byte[] { 0, 0xFF });
        data.AddRange(Encoding.Unicode.GetBytes("wide text"));
        data.AddRange(new byte[] { 0, 0, 0xFF, 0xFE });
        data.AddRange(Encoding.ASCII.GetBytes("ab"));   // below min length
        var r = StringsExtractor.Extract(data.ToArray(), 4);
        r.Items.Should().HaveCount(2);
        r.Items[0].Should().Be(new ExtractedString(3, false, "hello world"));
        r.Items[1].Utf16.Should().BeTrue();
        r.Items[1].Text.Should().Be("wide text");
        r.Items[1].Offset.Should().Be(3 + 11 + 2);
    }

    [Fact]
    public void Strings_wide_at_odd_offset_and_min_length_respected()
    {
        var data = new List<byte> { 0x01 };
        data.AddRange(Encoding.Unicode.GetBytes("abcde"));
        StringsExtractor.Extract(data.ToArray(), 6).Items.Should().BeEmpty();
        var r = StringsExtractor.Extract(data.ToArray(), 5);
        r.Items.Should().ContainSingle().Which.Offset.Should().Be(1);
    }

    [Fact]
    public void Strings_caps_count()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++) sb.Append("word").Append(i).Append('\0');
        var r = StringsExtractor.Extract(Encoding.ASCII.GetBytes(sb.ToString()), 4, maxCount: 10);
        r.Items.Should().HaveCount(10);
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Inspector_decodes_little_endian_filetime_and_unix()
    {
        // 2020-01-01 00:00:00 UTC as FILETIME
        ulong ft = (ulong)(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks;
        var vals = SelectionInspector.Describe(BitConverter.GetBytes(ft));
        vals.Single(v => v.Label == "FILETIME").Value.Should().Be("2020-01-01 00:00:00 UTC");
        vals.Single(v => v.Label == "int64 LE").Value.Should().StartWith(((long)ft).ToString());
        SelectionInspector.Describe(BitConverter.GetBytes(1577836800u)).Single(v => v.Label.StartsWith("Unix")).Value
            .Should().Be("2020-01-01 00:00:00 UTC");
        SelectionInspector.Describe(new byte[] { 0xFE, 0xFF }).Single(v => v.Label == "int16 LE").Value.Should().StartWith("-2");
        SelectionInspector.Describe(Array.Empty<byte>()).Should().BeEmpty();
    }

    private static FileFacts Facts(string name, string typeId = "pe", bool mismatch = false, SignatureCheck? sig = null, ZoneIdentifier? zone = null)
    {
        var entry = new FileEntry(@"C:\x\" + name, name, false, 10, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.Normal);
        return new FileFacts(entry, new FileHashes("a", "b", "c", 10), 5.0, new MagicType(typeId, "t", Array.Empty<string>(), true),
            Path.GetExtension(name), mismatch, NameChecks.Analyze(name), sig, Array.Empty<AdsStream>(), zone);
    }

    [Fact]
    public void Chips_for_unsigned_packed_pe_from_internet()
    {
        var die = new DieResult(new[] { "PE" }, new[] { new DieDetection("PE", "packer", "UPX", "3.9", null, "Packer: UPX", false) });
        var zone = ZoneIdentifier.Parse("[ZoneTransfer]\nZoneId=3\n");
        var chips = VerdictChips.From(Facts("a.exe", zone: zone), die);
        chips.Select(c => c.Text).Should().Equal("Packed: UPX", "Unsigned", "From internet");
    }

    [Fact]
    public void Chips_for_name_tricks_and_mismatch()
    {
        var chips = VerdictChips.From(Facts("invoice.pdf.exe", mismatch: true, sig: new SignatureCheck(true, false, "X", null, "ok")));
        chips.Select(c => c.Text).Should().Contain(new[] { "Ext mismatch", "Double ext" }).And.NotContain("Unsigned");
        VerdictChips.From(Facts("a.txt", typeId: "text")).Should().BeEmpty();
    }

    [Fact]
    public void History_back_forward()
    {
        var h = new NavHistory();
        h.Push(@"C:\a"); h.Push(@"C:\a\b"); h.Push(@"C:\a\b\c");
        h.Back().Should().Be(@"C:\a\b");
        h.CanForward.Should().BeTrue();
        h.Push(@"C:\a\x");
        h.CanForward.Should().BeFalse();
        h.Back().Should().Be(@"C:\a\b");
        h.Back().Should().Be(@"C:\a");
        h.Back().Should().BeNull();
        h.Forward().Should().Be(@"C:\a\b");
    }

    [Fact]
    public void Breadcrumb_segments_and_parent()
    {
        var s = Breadcrumbs.Build(@"C:\case\Host1\C\Users", @"C:\case", "Case");
        s.Select(x => x.Label).Should().Equal("Case", "Host1", "C", "Users");
        s[2].Path.Should().Be(@"C:\case\Host1\C");
        Breadcrumbs.Parent(@"C:\case\Host1", @"C:\case").Should().Be(@"C:\case");
        Breadcrumbs.Parent(@"C:\case", @"C:\case").Should().BeNull();
    }

    [Fact]
    public void Size_formatting()
    {
        Fmt.Size(0).Should().Be("0 B");
        Fmt.Size(1536).Should().Be("1.5 KB");
        Fmt.Size(5L * 1024 * 1024).Should().Be("5 MB");
    }
}
