using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class SavedFilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezsf_" + Guid.NewGuid().ToString("N"));
    public SavedFilterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static SavedFilter F(string sig, string name, string text = "") =>
        new() { CategorySignature = sig, Name = name, GlobalText = text, SavedUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Corrupt_file_is_kept_as_bad_copy_before_it_can_be_overwritten()
    {
        var path = Path.Combine(_dir, "saved-filters.json");
        File.WriteAllText(path, "{ not json");
        SavedFilterStore.Load(path).Filters.Should().BeEmpty();
        File.ReadAllText(path + ".bad").Should().Be("{ not json");
    }

    [Fact]
    public void RoundTrip_through_file()
    {
        var path = Path.Combine(_dir, "sub", "saved-filters.json");
        var lib = new SavedFilterLibrary();
        lib.EnsureCategory("sig1", "Event logs", new[] { "EventId", "Channel" });
        var expr = FilterGroup.And(new FilterCondition("EventId", ColumnFilterMode.Equals, "4688"),
            new FilterValueSet("Channel", new[] { "Security" }));
        var f = F("sig1", "Filter 1", "psexec OR wmic");
        f.Expression = AnalyzeFilterNode.From(expr);
        lib.Put(f);
        SavedFilterStore.Save(lib, path);

        var back = SavedFilterStore.Load(path);
        back.Categories.Should().ContainSingle().Which.Columns.Should().Equal("EventId", "Channel");
        var g = back.Find("SIG1", "filter 1");
        g.Should().NotBeNull();
        g!.GlobalText.Should().Be("psexec OR wmic");
        g.SavedUtc.Should().Be(f.SavedUtc);
        g.Expression!.Type.Should().Be("group");
        g.Expression.Children.Should().HaveCount(2);
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Missing_corrupt_or_newer_file_gives_empty_library()
    {
        SavedFilterStore.Load(Path.Combine(_dir, "none.json")).Filters.Should().BeEmpty();
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        SavedFilterStore.Load(bad).Categories.Should().BeEmpty();
        var newer = Path.Combine(_dir, "newer.json");
        File.WriteAllText(newer, "{\"version\":99,\"filters\":[{\"name\":\"x\",\"categorySignature\":\"s\"}]}");
        SavedFilterStore.Load(newer).Filters.Should().BeEmpty();
        var nulls = Path.Combine(_dir, "nulls.json");
        File.WriteAllText(nulls, "{\"version\":1,\"categories\":null,\"filters\":[null,{\"name\":\"\",\"categorySignature\":\"s\"}]}");
        var l = SavedFilterStore.Load(nulls);
        l.Categories.Should().BeEmpty();
        l.Filters.Should().BeEmpty();
    }

    [Fact]
    public void NextNumberedName_is_one_above_the_highest_per_category()
    {
        var lib = new SavedFilterLibrary();
        lib.NextNumberedName("a").Should().Be("Filter 1");
        lib.Put(F("a", "Filter 2"));
        lib.NextNumberedName("a").Should().Be("Filter 3");
        lib.Put(F("a", "Filter 1")); lib.Put(F("a", "filter 7")); lib.Put(F("a", "Logons")); lib.Put(F("b", "Filter 2"));
        lib.NextNumberedName("a").Should().Be("Filter 8");
        lib.NextNumberedName("b").Should().Be("Filter 3");
        lib.NextNumberedName("c").Should().Be("Filter 1");
    }

    [Fact]
    public void Names_are_unique_per_category_case_insensitive()
    {
        var lib = new SavedFilterLibrary();
        lib.Put(F("a", "Hunt", "one"));
        lib.Put(F("a", "HUNT", "two"));
        lib.Put(F("b", "hunt", "other"));
        lib.ForSignature("a").Should().ContainSingle().Which.GlobalText.Should().Be("two");
        lib.ForSignature("b").Should().ContainSingle();
        lib.Find("a", "hunt")!.GlobalText.Should().Be("two");
    }

    [Fact]
    public void Rename_fails_on_clash()
    {
        var lib = new SavedFilterLibrary();
        lib.Put(F("a", "One")); lib.Put(F("a", "Two")); lib.Put(F("b", "Three"));
        lib.Rename("a", "One", "two").Should().BeFalse();
        lib.Rename("a", "One", "Three").Should().BeTrue();
        lib.Rename("a", "One", "x").Should().BeFalse();
        lib.Rename("a", "Three", "THREE").Should().BeTrue();
        lib.Find("a", "Three")!.Name.Should().Be("THREE");
        lib.Find("b", "Three").Should().NotBeNull();
    }

    [Fact]
    public void RemoveCategory_removes_its_filters()
    {
        var lib = new SavedFilterLibrary();
        lib.EnsureCategory("a", "A", new[] { "x" }); lib.EnsureCategory("b", "B", new[] { "y" });
        lib.Put(F("a", "One")); lib.Put(F("b", "Two"));
        lib.RenameCategory("a", "Renamed").Should().BeTrue();
        lib.Category("a")!.Name.Should().Be("Renamed");
        lib.RemoveCategory("A").Should().BeTrue();
        lib.Category("a").Should().BeNull();
        lib.Filters.Should().ContainSingle().Which.Name.Should().Be("Two");
        lib.Remove("b", "TWO").Should().BeTrue();
        lib.Filters.Should().BeEmpty();
    }

    [Fact]
    public void EnsureCategory_keeps_existing()
    {
        var lib = new SavedFilterLibrary();
        lib.EnsureCategory("a", "First", new[] { "x" });
        lib.EnsureCategory("a", "Second", new[] { "y" }).Name.Should().Be("First");
        lib.Categories.Should().ContainSingle();
    }

    [Fact]
    public void ForSignature_uses_natural_order()
    {
        var lib = new SavedFilterLibrary();
        foreach (var n in new[] { "Filter 10", "Filter 2", "alpha", "Filter 1", "Filter 02b" }) lib.Put(F("a", n));
        lib.ForSignature("a").Select(f => f.Name).Should().Equal("alpha", "Filter 1", "Filter 2", "Filter 02b", "Filter 10");
    }

    [Fact]
    public void Columns_walks_the_tree()
    {
        var tree = FilterGroup.Or(
            new FilterCondition("Message", ColumnFilterMode.Contains, "x"),
            FilterGroup.Not(new FilterValueSet("source", new[] { "a" })),
            new FilterDateSet("Time", new[] { new DateOnly(2026, 1, 1) }),
            new FilterCondition("MESSAGE", ColumnFilterMode.Equals, "y"));
        SavedFilterStore.Columns(tree).Should().Equal("Message", "source", "Time");
    }

    private SavedFilterLibrary Source()
    {
        var lib = new SavedFilterLibrary();
        lib.EnsureCategory("sigA", "Layout A", new[] { "C1", "C2" });
        lib.EnsureCategory("sigB", "Layout B", new[] { "D1" });
        lib.Put(F("sigA", "One", "x"));
        lib.Put(F("sigA", "Two", "y"));
        lib.Put(F("sigB", "Three", "z"));
        return lib;
    }

    [Fact]
    public void Export_import_round_trip_only_selected_categories()
    {
        var src = Source();
        var path = Path.Combine(_dir, "x.ezfilters.json");
        SavedFilterStore.Export(src, src.ForSignature("sigA"), path);
        SavedFilterStore.TryImport(path, out var back, out var err).Should().BeTrue(err);
        back.Categories.Should().ContainSingle().Which.Columns.Should().Equal("C1", "C2");
        back.Filters.Select(f => f.Name).Should().BeEquivalentTo("One", "Two");
    }

    [Fact]
    public void Import_rejects_wrong_kind_newer_version_and_corrupt()
    {
        var p = Path.Combine(_dir, "bad.json");
        File.WriteAllText(p, "{\"version\":1,\"kind\":\"other\",\"categories\":[],\"filters\":[]}");
        SavedFilterStore.TryImport(p, out _, out var e1).Should().BeFalse();
        e1.Should().NotBeEmpty();
        File.WriteAllText(p, "{\"version\":99,\"kind\":\"ezforensics-saved-filters\",\"categories\":[],\"filters\":[]}");
        SavedFilterStore.TryImport(p, out _, out var e2).Should().BeFalse();
        e2.Should().Contain("99");
        File.WriteAllText(p, "{ not json");
        SavedFilterStore.TryImport(p, out _, out var e3).Should().BeFalse();
        e3.Should().NotBeEmpty();
        SavedFilterStore.TryImport(Path.Combine(_dir, "missing.json"), out _, out var e4).Should().BeFalse();
        e4.Should().NotBeEmpty();
    }

    [Fact]
    public void Merge_adds_missing_category_and_keeps_local_category_name()
    {
        var local = new SavedFilterLibrary();
        local.EnsureCategory("sigA", "My name", new[] { "C1" });
        var src = Source();
        var r = local.Merge(src, src.Filters, ConflictMode.KeepBoth);
        r.Should().Be(new MergeResult(3, 0, 0));
        local.Category("sigA")!.Name.Should().Be("My name");
        local.Category("sigB")!.Name.Should().Be("Layout B");
        local.Category("sigB")!.Columns.Should().Equal("D1");
    }

    [Fact]
    public void Merge_conflict_modes()
    {
        var src = Source();
        SavedFilterLibrary Local()
        {
            var l = new SavedFilterLibrary();
            l.EnsureCategory("sigA", "Layout A", new[] { "C1" });
            l.Put(F("sigA", "One", "old"));
            l.Put(F("sigA", "One (2)", "old2"));
            return l;
        }
        var sel = src.ForSignature("sigA");

        var l1 = Local();
        l1.Merge(src, sel, ConflictMode.Replace).Should().Be(new MergeResult(1, 1, 0));
        l1.Find("sigA", "One")!.GlobalText.Should().Be("x");

        var l2 = Local();
        l2.Merge(src, sel, ConflictMode.Skip).Should().Be(new MergeResult(1, 0, 1));
        l2.Find("sigA", "One")!.GlobalText.Should().Be("old");

        var l3 = Local();
        l3.Merge(src, sel, ConflictMode.KeepBoth).Should().Be(new MergeResult(2, 0, 0));
        l3.Find("sigA", "One")!.GlobalText.Should().Be("old");
        l3.Find("sigA", "One (3)")!.GlobalText.Should().Be("x");
    }
}

public class ArtifactCategoryNamerTests
{
    [Fact]
    public void Evtx_headers_give_event_logs()
    {
        ArtifactCategoryNamer.Name(new[] { "EventId", "Channel", "Computer", "Payload" }, "20261020123456_EvtxECmd_Output.csv")
            .Should().Be("Event logs");
    }

    [Fact]
    public void Artifact_id_wins()
    {
        ArtifactCategoryNamer.Name(new[] { "a" }, "whatever.csv", "eventlog").Should().Be("Event logs");
    }

    [Theory]
    [InlineData("netscan.csv")]
    [InlineData("windows.netscan.csv")]
    [InlineData("20261020123456_Memory_NetScan_image01.csv")]
    public void Memory_plugin_from_file_name(string file)
    {
        ArtifactCategoryNamer.Name(new[] { "Offset", "Proto" }, file, "memory-carved").Should().Be("Memory: Netscan");
    }

    [Fact]
    public void Memory_file_without_known_plugin_uses_short_label()
    {
        ArtifactCategoryNamer.Name(new[] { "a" }, "mystery.csv", "memory-process").Should().Be("Memory process list");
    }

    [Theory]
    [InlineData("20261020123456_my_custom-export_Output.csv", "My Custom Export")]
    [InlineData("hosts_2.csv", "Hosts")]
    [InlineData("inventory.csv", "Inventory")]
    public void Unknown_uses_file_stem(string file, string expected)
    {
        ArtifactCategoryNamer.Name(new[] { "zzz1", "zzz2" }, file).Should().Be(expected);
    }

    [Fact]
    public void Nothing_usable_is_custom_layout()
    {
        ArtifactCategoryNamer.Name(new[] { "zzz1" }, null).Should().Be("Custom layout");
        ArtifactCategoryNamer.Name(new[] { "zzz1" }, "20261020123456_Output.csv").Should().Be("Custom layout");
    }
}
