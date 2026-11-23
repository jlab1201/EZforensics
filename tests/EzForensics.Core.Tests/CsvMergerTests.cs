using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CsvMergerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_merge_" + Guid.NewGuid().ToString("N"));

    public CsvMergerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void HaveSameSchema_Detects()
    {
        var a = Write("a.csv", "A,B\r\n1,2\r\n");
        var b = Write("b.csv", "a,b\r\n3,4\r\n");
        var c = Write("c.csv", "A,C\r\n3,4\r\n");
        var d = Write("d.csv", "B,A\r\n3,4\r\n");
        CsvMerger.HaveSameSchema(new[] { a, b }).Should().BeTrue();
        CsvMerger.HaveSameSchema(new[] { a, c }).Should().BeFalse();
        CsvMerger.HaveSameSchema(new[] { a, d }).Should().BeFalse();
        CsvMerger.HaveSameSchema(new[] { a }).Should().BeTrue();
        CsvMerger.HaveSameSchema(Array.Empty<string>()).Should().BeFalse();
        CsvMerger.HaveSameSchema(new[] { a, Path.Combine(_dir, "missing.csv") }).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_SameSchema_AddsSourceAndHost()
    {
        var a = Write("a.csv", "A,B\r\n1,2\r\n");
        var b = Write("b.csv", "A,B\r\n3,4\r\n5,6\r\n");
        var o = Path.Combine(_dir, "out.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a, "H1"), new CsvMergeInput(b, "H2") }, o);
        r.RowsWritten.Should().Be(3);
        r.Headers.Should().Equal("SourceFile", "Host", "A", "B");
        r.SourceFileColumnName.Should().Be("SourceFile");
        File.ReadAllText(o).Should().Be("SourceFile,Host,A,B\r\na.csv,H1,1,2\r\nb.csv,H2,3,4\r\nb.csv,H2,5,6\r\n");
    }

    [Fact]
    public async Task Merge_DifferentSchemas_UnionInFirstSeenOrder()
    {
        var a = Write("a.csv", "A,B\r\n1,2\r\n");
        var b = Write("b.csv", "C,a\r\n3,4\r\n");
        var o = Path.Combine(_dir, "out.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a), new CsvMergeInput(b, "X") }, o,
            new CsvMergeOptions { AddHostColumn = false });
        r.Headers.Should().Equal("SourceFile", "A", "B", "C");
        File.ReadAllText(o).Should().Be("SourceFile,A,B,C\r\na.csv,1,2,\r\nb.csv,4,,3\r\n");
    }

    [Fact]
    public async Task Merge_NoExtraColumns()
    {
        var a = Write("a.csv", "A\r\n1\r\n");
        var o = Path.Combine(_dir, "out.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, o, new CsvMergeOptions { AddSourceFileColumn = false, AddHostColumn = false });
        File.ReadAllText(o).Should().Be("A\r\n1\r\n");
        r.Headers.Should().Equal("A");
    }

    [Fact]
    public async Task Merge_Quoting_RoundTrips()
    {
        var a = Write("a.csv", "A,B\r\n\"he said \"\"hi\"\"\",\"line1\r\nline2\"\r\n\"x,y\",plain\r\n");
        var o = Path.Combine(_dir, "out.csv");
        await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, o, new CsvMergeOptions { AddSourceFileColumn = false, AddHostColumn = false });
        File.ReadAllText(o).Should().Be("A,B\r\n\"he said \"\"hi\"\"\",\"line1\r\nline2\"\r\n\"x,y\",plain\r\n");
        // and the merged file is itself valid CSV with 2 data rows
        var again = Path.Combine(_dir, "out2.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(o) }, again);
        r.RowsWritten.Should().Be(2);
    }

    [Fact]
    public async Task Merge_Hashes_AreCorrect()
    {
        var a = Write("a.csv", "A,B\r\n1,2\r\n");
        var b = Write("b.csv", "A,B\r\n3,4\r\n");
        var o = Path.Combine(_dir, "out.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a), new CsvMergeInput(b) }, o);
        r.InputSha256[a].Should().Be(Sha(a));
        r.InputSha256[b].Should().Be(Sha(b));
        r.OutputSha256.Should().Be(Sha(o));
    }

    [Fact]
    public async Task Merge_Refuses_Overwrite_And_Input()
    {
        var a = Write("a.csv", "A\r\n1\r\n");
        var existing = Write("exists.csv", "keep");
        var act1 = async () => await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, existing);
        await act1.Should().ThrowAsync<IOException>();
        File.ReadAllText(existing).Should().Be("keep");
        var act2 = async () => await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, a);
        await act2.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(a).Should().Be("A\r\n1\r\n");
    }

    [Fact]
    public async Task Merge_Cancelled_DeletesPartialOutput()
    {
        var a = Write("a.csv", "A\r\n" + string.Concat(Enumerable.Repeat("v\r\n", 50000)));
        var o = Path.Combine(_dir, "out.csv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, o, null, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(o).Should().BeFalse();
    }

    [Fact]
    public async Task Merge_ColumnNamedSourceFile_KeepsItsNameAndValues_AddedColumnYields()
    {
        var a = Write("a.csv", "SourceFile,B\r\norig,2\r\n");
        var o = Path.Combine(_dir, "out.csv");
        var r = await CsvMerger.MergeAsync(new[] { new CsvMergeInput(a) }, o);
        r.Headers.Should().Equal("SourceCsv", "Host", "SourceFile", "B");
        r.SourceFileColumnName.Should().Be("SourceCsv");
        File.ReadAllText(o).Should().Be("SourceCsv,Host,SourceFile,B\r\na.csv,,orig,2\r\n");
    }
}
