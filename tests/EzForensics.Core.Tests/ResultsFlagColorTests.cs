using System.Text;
using System.Text.Json;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsFlagColorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _csv;

    public ResultsFlagColorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ezforensics_flagcolor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _csv = Path.Combine(_dir, "Sample.csv");
        File.WriteAllText(_csv,
            "Source,Message\r\n" +
            "MFT,powershell.exe\r\n" +   // 0
            "EVTX,logon\r\n" +           // 1
            "MFT,cmd.exe\r\n" +          // 2
            "EVTX,powershell -enc\r\n" + // 3
            "LNK,notes.txt\r\n",         // 4
            new UTF8Encoding(false));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Fallback => Path.Combine(_dir, "_fallback");

    [Fact]
    public void Colours_set_clear_count_and_round_trip()
    {
        using (var s = RowFlagStore.Open(_csv, Fallback, TimeSpan.FromMilliseconds(10)))
        {
            s.SetFlag(new long[] { 0, 2 }, FlagCatalog.Malicious).Should().Be(2);
            s.SetFlag(new long[] { 1, 2, 3 }, FlagCatalog.Informational).Should().Be(3);   // 2 is recoloured
            s.SetFlag(new long[] { 1 }, FlagCatalog.Informational).Should().Be(0);         // unchanged
            s.SetFlag(new long[] { 4 }, FlagCatalog.Interesting).Should().Be(1);
            s.GetFlagId(2).Should().Be(FlagCatalog.Informational);
            s.GetFlagId(0).Should().Be(FlagCatalog.Malicious);
            s.GetFlagId(9).Should().BeNull();
            s.Count.Should().Be(5);
            var counts = s.Counts();
            counts[FlagCatalog.Malicious].Should().Be(1);
            counts[FlagCatalog.Informational].Should().Be(3);
            counts[FlagCatalog.Interesting].Should().Be(1);
            counts.ContainsKey(FlagCatalog.Benign).Should().BeFalse();
            counts.Keys.Should().BeEquivalentTo(new[] { FlagCatalog.Malicious, FlagCatalog.Informational, FlagCatalog.Interesting });
            s.CountOf(FlagCatalog.Informational).Should().Be(3);
            s.ToSortedArray(new[] { FlagCatalog.Informational, FlagCatalog.Interesting }).Should().Equal(1, 2, 3, 4);
            s.SetFlag(new long[] { 3, 4, 8 }, null).Should().Be(2);           // clear
            s.ToSortedArray().Should().Equal(0, 1, 2);
        }

        var file = Path.Combine(_dir, "Sample.ezflags.json");
        File.Exists(file + ".tmp").Should().BeFalse();
        using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
        {
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(3);
            doc.RootElement.GetProperty("rows").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(0, 1, 2);   // v1 readers see all flags
            doc.RootElement.GetProperty("colors").GetProperty("malicious").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(0);
            doc.RootElement.GetProperty("colors").GetProperty("informational").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(1, 2);
        }

        using var again = RowFlagStore.Open(_csv, Fallback);
        again.GetFlagId(0).Should().Be(FlagCatalog.Malicious);
        again.GetFlagId(1).Should().Be(FlagCatalog.Informational);
        again.GetFlagId(2).Should().Be(FlagCatalog.Informational);
        again.Count.Should().Be(3);
    }

    [Fact]
    public void Version_1_sidecar_migrates_to_malicious_and_is_rewritten_as_version_3()
    {
        var fp = CsvFingerprint.Compute(_csv);
        var v1 = new
        {
            version = 1, csvFile = "Sample.csv", csvSize = fp.Size, csvLastWriteUtc = fp.LastWriteUtc, csvHeadSha256 = fp.HeadSha256,
            rows = new long[] { 1, 3 },
        };
        var file = Path.Combine(_dir, "Sample.ezflags.json");
        File.WriteAllText(file, JsonSerializer.Serialize(v1));

        using (var s = RowFlagStore.Open(_csv, Fallback, TimeSpan.FromMilliseconds(10)))
        {
            s.StaleWarning.Should().BeNull();
            s.Count.Should().Be(2);
            s.GetFlagId(1).Should().Be(FlagCatalog.Malicious);
            s.GetFlagId(3).Should().Be(FlagCatalog.Malicious);
            s.Counts()[FlagCatalog.Malicious].Should().Be(2);
            File.ReadAllText(file).Should().Contain("\"version\":1");   // opening alone does not rewrite the file
            s.SetFlag(new long[] { 4 }, FlagCatalog.Benign);
        }
        var json = File.ReadAllText(file);
        json.Should().Contain("\"version\":3").And.Contain("\"benign\":[4]").And.Contain("\"malicious\":[1,3]");
    }

    [Fact]
    public void Plain_flagging_uses_the_default_colour_and_keeps_existing_colours()
    {
        using var s = RowFlagStore.Open(_csv, Fallback, TimeSpan.FromMilliseconds(10));
        s.SetFlag(new long[] { 0 }, FlagCatalog.Interesting);
        s.DefaultFlagId = FlagCatalog.Suspicious;
        s.SetMany(new long[] { 0, 1 }, true).Should().Be(1);
        s.GetFlagId(0).Should().Be(FlagCatalog.Interesting);
        s.GetFlagId(1).Should().Be(FlagCatalog.Suspicious);
        s.Toggle(2).Should().BeTrue();
        s.GetFlagId(2).Should().Be(FlagCatalog.Suspicious);
        s.Snapshot().Should().BeEquivalentTo(new long[] { 0, 1, 2 });
        s.SnapshotFlags().Should().HaveCount(3);
        FluentActions.Invoking(() => s.SetFlag(new long[] { 1 }, " ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Version_2_colour_names_map_to_ids_by_colour_and_unknown_names_are_kept()
    {
        var fp = CsvFingerprint.Compute(_csv);
        File.WriteAllText(Path.Combine(_dir, "Sample.ezflags.json"), JsonSerializer.Serialize(new
        {
            version = 2, csvFile = "Sample.csv", csvSize = fp.Size, csvLastWriteUtc = fp.LastWriteUtc, csvHeadSha256 = fp.HeadSha256,
            rows = new long[] { 0, 1, 2 },
            colors = new Dictionary<string, long[]> { ["blue"] = new long[] { 1 }, ["magenta"] = new long[] { 2 }, ["3"] = new long[] { 0 } },
        }));
        using var s = RowFlagStore.Open(_csv, Fallback);
        s.GetFlagId(0).Should().Be(FlagCatalog.Suspicious);        // "3" = old enum number of Yellow
        s.GetFlagId(1).Should().Be(FlagCatalog.Informational);     // blue
        s.GetFlagId(2).Should().Be("magenta");                     // unknown names keep working (shown as a placeholder)
    }

    private async Task<int[]> Run(FilterSpec spec)
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        return await FilterEngine.FilterAsync(idx, spec, null, default);
    }

    [Fact]
    public async Task Filter_by_flag_colour_set_combines_with_other_filters()
    {
        var colors = new Dictionary<long, string> { [0] = FlagCatalog.Malicious, [1] = FlagCatalog.Informational, [3] = FlagCatalog.Malicious, [4] = FlagCatalog.Benign, [77] = FlagCatalog.Malicious };

        (await Run(new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColorRows = colors })).Should().Equal(0, 1, 3, 4);
        (await Run(new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColorRows = colors, FlagColors = new HashSet<string> { FlagCatalog.Malicious } }))
            .Should().Equal(0, 3);
        (await Run(new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColorRows = colors, FlagColors = new HashSet<string> { FlagCatalog.Malicious, FlagCatalog.Benign } }))
            .Should().Equal(0, 3, 4);
        (await Run(new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColorRows = colors, FlagColors = new HashSet<string> { FlagCatalog.Interesting } }))
            .Should().BeEmpty();
        (await Run(new FilterSpec { Flag = FlagFilterMode.Unflagged, FlagColorRows = colors })).Should().Equal(2);

        // combined with a text filter and a column condition
        var spec = new FilterSpec
        {
            Flag = FlagFilterMode.Flagged, FlagColorRows = colors, FlagColors = new HashSet<string> { FlagCatalog.Malicious, FlagCatalog.Informational },
            GlobalText = "power",
        };
        (await Run(spec)).Should().Equal(0, 3);
        spec.Expression = new FilterCondition("Source", ColumnFilterMode.Equals, "evtx");
        (await Run(spec)).Should().Equal(3);
    }

    [Fact]
    public void Display_string_lists_the_selected_colours()
    {
        new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColors = new HashSet<string> { FlagCatalog.Informational, FlagCatalog.Malicious } }
            .ToDisplayString().Should().Be("[Flag] In ('informational', 'malicious')");
        new FilterSpec { Flag = FlagFilterMode.Flagged }.ToDisplayString().Should().Be("[Flag]");
        new FilterSpec { Flag = FlagFilterMode.Flagged, FlagColors = new HashSet<string>() }.ToDisplayString().Should().Be("[Flag]");
        new FilterSpec
        {
            GlobalText = "x", Flag = FlagFilterMode.Flagged, FlagColors = new HashSet<string> { FlagCatalog.Benign },
        }.ToDisplayString().Should().Be("Contains(*, 'x') And [Flag] In ('benign')");
    }

    [Fact]
    public async Task Export_adds_the_flag_label_column()
    {
        var colors = new Dictionary<long, string> { [1] = FlagCatalog.Informational, [3] = FlagCatalog.Malicious };
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        var outPath = Path.Combine(_dir, "out.csv");
        await CsvExporter.ExportAsync(idx, new[] { 3, 2, 1 }, outPath, extra: ExportExtraColumn.FlagColumn(colors, idx.Headers, id => FlagCatalog.LabelOf(FlagCatalog.Defaults(), id)));
        File.ReadAllText(outPath).Should().Be("Flag,Source,Message\r\nMalicious,EVTX,powershell -enc\r\n,MFT,cmd.exe\r\nInformational,EVTX,logon\r\n");

        ExportExtraColumn.FlagColumn(colors, new[] { "flag", "x" }, id => id).Header.Should().Be("EzForensics_Flag");
    }

    [Theory]
    [InlineData("red", FlagCatalog.Malicious)]
    [InlineData("Orange", FlagCatalog.Suspicious)]
    [InlineData("yellow", FlagCatalog.Suspicious)]
    [InlineData("green", FlagCatalog.Benign)]
    [InlineData("BLUE", FlagCatalog.Informational)]
    [InlineData("purple", FlagCatalog.Interesting)]
    [InlineData("malicious", FlagCatalog.Malicious)]
    [InlineData("5", FlagCatalog.Informational)]
    public void Legacy_colours_map_to_flag_ids(string key, string id) => FlagCatalog.MigrateLegacy(key).Should().Be(id);

    [Theory]
    [InlineData("pink")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("9")]
    public void Unknown_legacy_keys_do_not_map(string? key) => FlagCatalog.MigrateLegacy(key).Should().BeNull();

    [Fact]
    public void Defaults_are_ordered_by_investigation_progression()
    {
        var d = FlagCatalog.Defaults();
        d.Select(f => f.Id).Should().Equal(FlagCatalog.Informational, FlagCatalog.Interesting, FlagCatalog.Suspicious, FlagCatalog.Malicious, FlagCatalog.Benign);
        d.Select(f => f.Label).Should().Equal("Informational", "Interesting", "Suspicious", "Malicious", "Benign / Ruled out");
        d.Should().OnlyContain(f => f.IsBuiltIn);
        d.Select(f => f.Color).Should().OnlyHaveUniqueItems().And.OnlyContain(c => c.StartsWith("#") && c.Length == 7);
    }

    [Fact]
    public void Cycle_follows_list_order_and_a_reordered_list()
    {
        var flags = FlagCatalog.Defaults();
        string? c = null;
        var seen = new List<string?>();
        for (int i = 0; i < 6; i++) { c = FlagCatalog.Next(flags, c); seen.Add(c); }
        seen.Should().Equal(FlagCatalog.Informational, FlagCatalog.Interesting, FlagCatalog.Suspicious, FlagCatalog.Malicious, FlagCatalog.Benign, null);
        FlagCatalog.Next(flags, "gone").Should().Be(FlagCatalog.Informational);

        flags.Reverse();
        FlagCatalog.Next(flags, null).Should().Be(FlagCatalog.Benign);
        FlagCatalog.Next(flags, FlagCatalog.Benign).Should().Be(FlagCatalog.Malicious);
        FlagCatalog.Order(flags, FlagCatalog.Benign).Should().Be(0);
        FlagCatalog.Order(flags, "gone").Should().Be(int.MaxValue);
    }

    [Fact]
    public void Normalize_keeps_order_repairs_entries_and_never_returns_empty()
    {
        var custom = new FlagDefinition("custom-1", "Lateral movement", "#00ff00");
        var list = FlagCatalog.Normalize(new[]
        {
            custom, new FlagDefinition("malicious", "", "not-a-colour"), new FlagDefinition("custom-1", "dup", "#000000"), new FlagDefinition("", "x", "#111111"),
        });
        list.Select(f => f.Id).Should().Equal("custom-1", "malicious");
        list[0].Color.Should().Be("#00FF00");
        list[1].Label.Should().Be("Malicious");
        list[1].Color.Should().Be(FlagCatalog.Defaults().Single(f => f.Id == "malicious").Color);
        FlagCatalog.Normalize(null).Select(f => f.Id).Should().Equal(FlagCatalog.Defaults().Select(f => f.Id));
        FlagCatalog.Normalize(new List<FlagDefinition>()).Should().HaveCount(5);
    }

    [Fact]
    public void Resolve_uses_label_from_list_and_a_placeholder_for_removed_ids()
    {
        var flags = FlagCatalog.Defaults();
        flags[3].Label = "Confirmed bad";
        FlagCatalog.LabelOf(flags, FlagCatalog.Malicious).Should().Be("Confirmed bad");   // renaming does not change the id
        FlagCatalog.Resolve(flags, "custom-gone").Color.Should().Be(FlagCatalog.FallbackColor);
    }

    [Fact]
    public void Renaming_a_flag_does_not_break_stored_rows()
    {
        using (var s = RowFlagStore.Open(_csv, Fallback, TimeSpan.FromMilliseconds(10))) s.SetFlag(new long[] { 1 }, "custom-ab12cd34");
        using var again = RowFlagStore.Open(_csv, Fallback);
        again.GetFlagId(1).Should().Be("custom-ab12cd34");
    }

    [Fact]
    public void Settings_legacy_last_colour_and_default_flags_load()
    {
        var st = System.Text.Json.JsonSerializer.Deserialize<EzForensics.Core.Settings.AppSettings>("{\"flagLastColor\":\"green\"}")!;
        st.FlagLastId.Should().Be(FlagCatalog.Benign);
        st.Flags.Select(f => f.Id).Should().Equal(FlagCatalog.Defaults().Select(f => f.Id));
        var round = System.Text.Json.JsonSerializer.Deserialize<EzForensics.Core.Settings.AppSettings>(System.Text.Json.JsonSerializer.Serialize(st))!;
        round.FlagLastId.Should().Be(FlagCatalog.Benign);
    }

    [Fact]
    public void RotateNext_starts_at_last_flag_visits_every_flag_then_clears()
    {
        var flags = FlagCatalog.Defaults();
        var last = FlagCatalog.Suspicious;
        var seen = new List<string?>();
        string? c = null;
        for (int i = 0; i < 7; i++) { c = FlagCatalog.RotateNext(flags, c, last); seen.Add(c); }
        seen.Should().Equal(FlagCatalog.Suspicious, FlagCatalog.Malicious, FlagCatalog.Benign, FlagCatalog.Informational, FlagCatalog.Interesting, null, FlagCatalog.Suspicious);
    }

    [Fact]
    public void RotateNext_unknown_ids_fall_back_to_last_or_first()
    {
        var flags = FlagCatalog.Defaults();
        FlagCatalog.RotateNext(flags, "gone", FlagCatalog.Malicious).Should().Be(FlagCatalog.Malicious);
        FlagCatalog.RotateNext(flags, null, "gone").Should().Be(FlagCatalog.Informational);
        FlagCatalog.RotateNext(new List<FlagDefinition>(), null, FlagCatalog.Malicious).Should().BeNull();
    }
}
