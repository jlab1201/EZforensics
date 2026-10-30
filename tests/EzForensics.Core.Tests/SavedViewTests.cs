using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class SavedViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezsv_" + Guid.NewGuid().ToString("N"));
    public SavedViewTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SavedView V(string sig, string name) =>
        new() { CategorySignature = sig, Name = name, SavedUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void RoundTrip_through_file()
    {
        var path = Path.Combine(_dir, "sub", "saved-views.json");
        var lib = new SavedViewLibrary();
        lib.EnsureCategory("sig1", "Event logs", new[] { "EventId", "Channel" });
        var v = V("sig1", "View 1");
        v.Columns.Add(new SavedViewColumn { Name = "Channel", Visible = true, Width = 120.5 });
        v.Columns.Add(new SavedViewColumn { Name = "EventId", Visible = false });
        v.Groups = new() { new PivotGroupSpec { Column = "Channel", Sort = PivotSort.CountDesc } };
        v.CollapsedGroups = new() { "k1" };
        lib.Put(v);
        SavedViewStore.Save(lib, path);

        var back = SavedViewStore.Load(path);
        var g = back.Find("SIG1", "view 1")!;
        g.Columns.Select(c => c.Name).Should().Equal("Channel", "EventId");
        g.Columns[0].Width.Should().Be(120.5);
        g.Columns[1].Visible.Should().BeFalse();
        g.Groups!.Single().Sort.Should().Be(PivotSort.CountDesc);
        g.CollapsedGroups.Should().BeNull("collapse state is session-only, never stored");
        g.Summary().Should().Be("1 of 2 columns · grouped by Channel");
        File.Exists(path + ".tmp").Should().BeFalse();
        File.ReadAllText(path).Should().NotContain("k1");
    }

    [Fact]
    public void Missing_corrupt_or_newer_file_gives_empty_library()
    {
        SavedViewStore.Load(Path.Combine(_dir, "none.json")).Views.Should().BeEmpty();
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        SavedViewStore.Load(bad).Categories.Should().BeEmpty();
        var newer = Path.Combine(_dir, "newer.json");
        File.WriteAllText(newer, "{\"version\":99,\"views\":[{\"name\":\"x\",\"categorySignature\":\"s\"}]}");
        SavedViewStore.Load(newer).Views.Should().BeEmpty();
        var nulls = Path.Combine(_dir, "nulls.json");
        File.WriteAllText(nulls, "{\"version\":1,\"categories\":null,\"views\":[null,{\"name\":\"\",\"categorySignature\":\"s\"},{\"name\":\"a\",\"categorySignature\":\"s\",\"columns\":null}]}");
        var l = SavedViewStore.Load(nulls);
        l.Views.Should().ContainSingle().Which.Columns.Should().BeEmpty();
    }

    [Fact]
    public void NextNumberedName_is_one_above_the_highest_per_category()
    {
        var lib = new SavedViewLibrary();
        lib.NextNumberedName("a").Should().Be("View 1");
        lib.Put(V("a", "View 2"));
        lib.Put(V("a", "view 7")); lib.Put(V("a", "Triage")); lib.Put(V("b", "View 2"));
        lib.NextNumberedName("a").Should().Be("View 8");
        lib.NextNumberedName("b").Should().Be("View 3");
    }

    [Fact]
    public void Names_unique_per_category_rename_and_remove()
    {
        var lib = new SavedViewLibrary();
        lib.EnsureCategory("a", "A", new[] { "x" });
        lib.Put(V("a", "Hunt")); lib.Put(V("a", "HUNT")); lib.Put(V("b", "hunt")); lib.Put(V("a", "Other"));
        lib.ForSignature("a").Should().HaveCount(2);
        lib.Rename("a", "Hunt", "Other").Should().BeFalse();
        lib.Rename("a", "Hunt", "Hunt 2").Should().BeTrue();
        lib.ForSignature("a").Select(v => v.Name).Should().Equal("Hunt 2", "Other");
        lib.Remove("a", "other").Should().BeTrue();
        lib.RenameCategory("a", " New ").Should().BeTrue();
        lib.Category("a")!.Name.Should().Be("New");
        lib.RemoveCategory("a").Should().BeTrue();
        lib.Views.Should().ContainSingle().Which.CategorySignature.Should().Be("b");
    }

    private static SavedViewLibrary Source()
    {
        var lib = new SavedViewLibrary();
        lib.EnsureCategory("sigA", "Layout A", new[] { "C1", "C2" });
        lib.EnsureCategory("sigB", "Layout B", new[] { "D1" });
        var v = V("sigA", "One");
        v.Columns.Add(new SavedViewColumn { Name = "C1", Width = 50 });
        lib.Put(v);
        lib.Put(V("sigA", "Two"));
        lib.Put(V("sigB", "Three"));
        return lib;
    }

    [Fact]
    public void Export_import_round_trip_only_selected_categories()
    {
        var src = Source();
        var path = Path.Combine(_dir, "x.ezviews.json");
        SavedViewStore.Export(src, src.ForSignature("sigA"), path);
        SavedViewStore.TryImport(path, out var back, out var err).Should().BeTrue(err);
        back.Categories.Should().ContainSingle().Which.Columns.Should().Equal("C1", "C2");
        back.Views.Select(f => f.Name).Should().BeEquivalentTo("One", "Two");
        back.Find("sigA", "One")!.Columns.Single().Width.Should().Be(50);
    }

    [Fact]
    public void Import_rejects_wrong_kind_newer_version_and_corrupt()
    {
        var p = Path.Combine(_dir, "bad.json");
        File.WriteAllText(p, "{\"version\":1,\"kind\":\"ezforensics-saved-filters\",\"categories\":[],\"views\":[]}");
        SavedViewStore.TryImport(p, out _, out var e1).Should().BeFalse();
        e1.Should().NotBeEmpty();
        File.WriteAllText(p, "{\"version\":99,\"kind\":\"ezforensics-saved-views\",\"categories\":[],\"views\":[]}");
        SavedViewStore.TryImport(p, out _, out var e2).Should().BeFalse();
        e2.Should().Contain("99");
        File.WriteAllText(p, "{ not json");
        SavedViewStore.TryImport(p, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Merge_adds_missing_category_and_conflict_modes()
    {
        var src = Source();
        SavedViewLibrary Local()
        {
            var l = new SavedViewLibrary();
            l.EnsureCategory("sigA", "Mine", new[] { "C1" });
            l.Put(V("sigA", "One"));
            return l;
        }
        var sel = src.Views.ToList();

        var l1 = Local();
        l1.Merge(src, sel, ConflictMode.Replace).Should().Be(new MergeResult(2, 1, 0));
        l1.Category("sigA")!.Name.Should().Be("Mine");
        l1.Category("sigB")!.Name.Should().Be("Layout B");
        l1.Find("sigA", "One")!.Columns.Should().HaveCount(1);

        var l2 = Local();
        l2.Merge(src, sel, ConflictMode.Skip).Should().Be(new MergeResult(2, 0, 1));
        l2.Find("sigA", "One")!.Columns.Should().BeEmpty();

        var l3 = Local();
        l3.Merge(src, sel, ConflictMode.KeepBoth).Should().Be(new MergeResult(3, 0, 0));
        l3.Find("sigA", "One (2)").Should().NotBeNull();
    }
}
