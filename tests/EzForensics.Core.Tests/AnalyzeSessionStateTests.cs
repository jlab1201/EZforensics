using EzForensics.Core.Results;
using Xunit;

namespace EzForensics.Core.Tests;

public class AnalyzeSessionStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezas_" + Guid.NewGuid().ToString("N"));
    public AnalyzeSessionStateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Csv(string name, string text = "a,b\n1,2\n")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static AnalyzeFileState For(string path)
    {
        var fp = CsvFingerprint.Compute(path);
        return new AnalyzeFileState { Path = path, Size = fp.Size, LastWriteUtc = fp.LastWriteUtc, HeadSha256 = fp.HeadSha256 };
    }

    [Fact]
    public void RoundTrip_keeps_everything()
    {
        var csv = Csv("x.csv");
        var f = For(csv);
        f.GlobalText = "powershell"; f.FiltersEnabled = false; f.FlagFilter = true; f.FlagIds.Add("malicious");
        f.Cells.Add(new AnalyzeCellFilter { Column = "b", Op = ColumnFilterMode.Between, Value = "1..9",
            Header = AnalyzeFilterNode.From(new FilterDateSet("b", new[] { new DateOnly(2026, 1, 2) }, true)) });
        f.Extra = AnalyzeFilterNode.From(FilterGroup.Or(new FilterCondition("a", ColumnFilterMode.Regex, "x.*"), new FilterValueSet("b", new[] { "q" })));
        f.SortColumn = "a"; f.SortDescending = true; f.ColumnOrder.AddRange(new[] { 1, 0 });
        f.SelectedPosition = 3; f.SelectedSource = 7; f.ScrollOffset = 120;
        var s = new AnalyzeSessionState { CaseFolder = @"C:\case", SelectedFile = csv, FocusMode = true };
        s.Put(f);
        var path = Path.Combine(_dir, "s.json");
        AnalyzeSessionStore.Save(s, path);

        var r = AnalyzeSessionStore.Load(path);
        Assert.Equal(@"C:\case", r.CaseFolder);
        Assert.True(r.FocusMode);
        var g = r.TryGet(csv, CsvFingerprint.Compute(csv), out var why);
        Assert.Null(why);
        Assert.NotNull(g);
        Assert.Equal("powershell", g!.GlobalText);
        Assert.False(g.FiltersEnabled);
        Assert.True(g.FlagFilter);
        Assert.Equal(new[] { "malicious" }, g.FlagIds);
        Assert.Equal(ColumnFilterMode.Between, g.Cells[0].Op);
        var d = Assert.IsType<FilterDateSet>(g.Cells[0].Header!.ToNode());
        Assert.Contains(new DateOnly(2026, 1, 2), d.Days);
        Assert.True(d.IncludeBlanks);
        var grp = Assert.IsType<FilterGroup>(g.Extra!.ToNode());
        Assert.Equal(FilterGroupKind.Or, grp.Kind);
        Assert.Equal(2, grp.Children.Count);
        Assert.Equal("a", g.SortColumn);
        Assert.True(g.SortDescending);
        Assert.Equal(new[] { 1, 0 }, g.ColumnOrder);
        Assert.Equal((3, 7L, 120), (g.SelectedPosition, g.SelectedSource, g.ScrollOffset));
    }

    [Fact]
    public void Changed_file_drops_state_with_reason()
    {
        var csv = Csv("x.csv");
        var s = new AnalyzeSessionState();
        s.Put(For(csv));
        File.WriteAllText(csv, "a,b\n1,2\n3,4\n");
        Assert.Null(s.TryGet(csv, CsvFingerprint.Compute(csv), out var why));
        Assert.NotNull(why);
        Assert.Empty(s.Files);
    }

    [Fact]
    public void Missing_files_are_pruned()
    {
        var csv = Csv("x.csv");
        var s = new AnalyzeSessionState();
        s.Put(For(csv));
        File.Delete(csv);
        Assert.Single(s.PruneMissing());
        Assert.Empty(s.Files);
    }

    [Fact]
    public void Cap_evicts_oldest_and_put_moves_to_front()
    {
        var s = new AnalyzeSessionState();
        for (int i = 0; i < AnalyzeSessionState.MaxFiles + 5; i++) s.Put(new AnalyzeFileState { Path = $@"C:\f{i}.csv" });
        Assert.Equal(AnalyzeSessionState.MaxFiles, s.Files.Count);
        Assert.Equal($@"C:\f{AnalyzeSessionState.MaxFiles + 4}.csv", s.Files[0].Path);
        Assert.DoesNotContain(s.Files, f => f.Path == @"C:\f0.csv");
        s.Put(new AnalyzeFileState { Path = @"C:\F10.CSV" });
        Assert.Equal(@"C:\F10.CSV", s.Files[0].Path);
        Assert.Equal(AnalyzeSessionState.MaxFiles, s.Files.Count);
    }

    [Fact]
    public void Corrupt_or_missing_file_gives_empty_state()
    {
        var p = Path.Combine(_dir, "bad.json");
        File.WriteAllText(p, "{ not json ,,");
        Assert.Empty(AnalyzeSessionStore.Load(p).Files);
        Assert.Null(AnalyzeSessionStore.Load(p).CaseFolder);
        Assert.Empty(AnalyzeSessionStore.Load(Path.Combine(_dir, "none.json")).Files);
        File.WriteAllText(p, "[1,2]");
        Assert.Empty(AnalyzeSessionStore.Load(p).Files);
    }

    [Fact]
    public void Save_leaves_no_temp_file()
    {
        var p = Path.Combine(_dir, "s.json");
        AnalyzeSessionStore.Save(new AnalyzeSessionState(), p);
        Assert.True(File.Exists(p));
        Assert.False(File.Exists(p + ".tmp"));
    }
}
