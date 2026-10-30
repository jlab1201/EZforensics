using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsGridHelpersTests
{
    [Fact]
    public void Tsv_HasHeaderVisibleColumnsAndCleansWhitespace()
    {
        var rows = new[] { new[] { "a", "b\tx", "c" }, new[] { "d", "e\r\nf", "g" } };
        TsvFormatter.Build(new[] { "H1", "H2", "H3" }, new[] { 0, 2 }, rows, 1000, out var text, out var n).Should().BeTrue();
        n.Should().Be(2);
        text.Should().Be("H1\tH3\r\na\tc\r\nd\tg\r\n");
        TsvFormatter.Build(new[] { "H1", "H2", "H3" }, new[] { 1 }, rows, 1000, out text, out _);
        text.Should().Be("H2\r\nb x\r\ne  f\r\n");
    }

    [Fact]
    public void Tsv_EscapesFormulaLikeCells_WhenAsked()
    {
        var rows = new[] { new[] { "=HYPERLINK(\"http://x\",\"y\")", "+cmd|' /C calc'!A0", "-2+3", "@SUM(A1)", "-5", "+1.5e3", "a=b", "" } };
        var cols = Enumerable.Range(0, 8).ToArray();
        var headers = new[] { "=H", "B", "C", "D", "E", "F", "G", "H" };
        TsvFormatter.Build(headers, cols, rows, 10_000, out var text, out _, escapeFormulas: true).Should().BeTrue();
        text.Should().Be("'=H\tB\tC\tD\tE\tF\tG\tH\r\n'=HYPERLINK(\"http://x\",\"y\")\t'+cmd|' /C calc'!A0\t'-2+3\t'@SUM(A1)\t-5\t+1.5e3\ta=b\t\r\n");
        TsvFormatter.Build(headers, cols, rows, 10_000, out var raw, out _).Should().BeTrue();
        raw.Should().StartWith("=H\t").And.Contain("\t-2+3\t");
        TsvFormatter.Clean("\t=1+1", escapeFormulas: true).Should().Be(" =1+1", "a tab becomes a space first; Excel does not evaluate a leading space");
        new AppSettings().EscapeFormulasOnCopy.Should().BeTrue();
    }

    [Fact]
    public void Tsv_StopsAtCap()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new[] { "row" + i });
        TsvFormatter.Build(new[] { "H" }, new[] { 0 }, rows, 40, out var text, out var n).Should().BeFalse();
        n.Should().BeGreaterThan(0).And.BeLessThan(100);
        text.Length.Should().BeLessOrEqualTo(40);
    }

    [Fact]
    public void ColumnLayout_SignatureIsStableAndOrderSensitive()
    {
        ColumnLayoutStore.Signature(new[] { "A", "B" }).Should().Be(ColumnLayoutStore.Signature(new[] { "A", "B" }));
        ColumnLayoutStore.Signature(new[] { "A", "B" }).Should().NotBe(ColumnLayoutStore.Signature(new[] { "B", "A" }));
        ColumnLayoutStore.Signature(new[] { "AB", "" }).Should().NotBe(ColumnLayoutStore.Signature(new[] { "A", "B" }));
    }

    [Fact]
    public void ColumnLayout_PutCapsEntriesAndDropsEmpty()
    {
        var all = new Dictionary<string, ColumnLayout>();
        for (int i = 0; i < 250; i++) ColumnLayoutStore.Put(all, "k" + i, new ColumnLayout { Hidden = { 1 }, LastUsedTicks = 0 });
        all.Count.Should().Be(ColumnLayoutStore.MaxEntries);
        all.Should().ContainKey("k249");
        ColumnLayoutStore.Put(all, "k249", new ColumnLayout());
        all.Should().NotContainKey("k249");
    }

    [Fact]
    public void ColumnLayout_RoundTripsThroughSettingsJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezforensics_cl_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonSettingsStore(path);
            var s = new AppSettings();
            ColumnLayoutStore.Put(s.ColumnLayouts, "sig", new ColumnLayout { Hidden = { 2, 3 }, Widths = { [1] = 222.5 } });
            store.Save(s);
            var l = store.Load().ColumnLayouts["sig"];
            l.Hidden.Should().Equal(2, 3);
            l.Widths[1].Should().Be(222.5);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ColumnLayout_ClampWidth()
    {
        ColumnLayoutStore.ClampWidth(5).Should().Be(40);
        ColumnLayoutStore.ClampWidth(5000).Should().Be(800);
        ColumnLayoutStore.ClampWidth(100).Should().Be(100);
    }
}
