using System.Text;
using System.Text.Json;
using EzForensics.Core.Results;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Findings evidence is a transient row set now; the old "Spectre evidence" flag definition and row flags are cleaned up.</summary>
public class RetiredEvidenceFlagTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _csv;
    public RetiredEvidenceFlagTests()
    {
        _csv = Path.Combine(_t.Path, "Sample.csv");
        File.WriteAllText(_csv, "Source,Message\r\nMFT,a\r\nEVTX,b\r\nMFT,c\r\nEVTX,d\r\nLNK,e\r\n", new UTF8Encoding(false));
    }
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Settings_load_removes_the_old_evidence_flag_and_saves()
    {
        var path = Path.Combine(_t.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        var s = new AppSettings();
        var flags = s.Flags.Select(f => f.Clone()).ToList();
        flags.Add(new FlagDefinition("spectre-evidence", "Spectre evidence", "#14B8A6"));
        flags.Add(new FlagDefinition("custom-1", "Mine", "#112233"));
        s.Flags = flags;
        store.Save(s);
        File.ReadAllText(path).Should().Contain("spectre-evidence");

        var loaded = store.Load();
        loaded.Flags.Select(f => f.Id).Should().NotContain("spectre-evidence").And.Contain("custom-1");
        loaded.Flags.Count.Should().Be(flags.Count - 1);
        File.ReadAllText(path).Should().NotContain("spectre-evidence");
    }

    [Fact]
    public void Row_flag_load_drops_only_the_old_evidence_flags()
    {
        var fallback = Path.Combine(_t.Path, "_fb");
        using (var s = RowFlagStore.Open(_csv, fallback, TimeSpan.FromMilliseconds(10)))
        {
            s.SetFlag(new long[] { 0, 1 }, "spectre-evidence").Should().Be(2);
            s.SetFlag(new long[] { 2 }, FlagCatalog.Malicious);
            s.SetFlag(new long[] { 3 }, "spectre-evidence");
            s.SetFlag(new long[] { 3 }, FlagCatalog.Interesting);   // recoloured: stays
            s.Flush();
        }
        File.ReadAllText(RowFlagStore.SidecarPathFor(_csv)).Should().Contain("spectre-evidence");
        using var again = RowFlagStore.Open(_csv, fallback);
        again.SnapshotFlags().Should().BeEquivalentTo(new Dictionary<long, string> { [2] = FlagCatalog.Malicious, [3] = FlagCatalog.Interesting });
        again.Flush();
        File.ReadAllText(RowFlagStore.SidecarPathFor(_csv)).Should().NotContain("spectre-evidence");
    }

    [Fact]
    public async Task Row_set_filter_keeps_only_those_rows_and_ands_with_the_rest()
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        var set = new HashSet<long> { 1, 2, 4 };
        (await FilterEngine.FilterAsync(idx, new FilterSpec { RowSet = set }, null, default)).Should().Equal(1, 2, 4);
        (await FilterEngine.FilterAsync(idx, new FilterSpec { RowSet = set, GlobalText = "MFT" }, null, default)).Should().Equal(2);
        (await FilterEngine.FilterAsync(idx, new FilterSpec(), null, default)).Should().HaveCount(5);
        new FilterSpec { RowSet = set }.IsEmpty.Should().BeFalse();
    }
}
