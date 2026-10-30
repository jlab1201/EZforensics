using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ResultsRowFlagStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fallback;
    private readonly string _csv;

    public ResultsRowFlagStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ezforensics_flags_" + Guid.NewGuid().ToString("N"));
        _fallback = Path.Combine(_dir, "_fallback");
        Directory.CreateDirectory(_dir);
        _csv = Path.Combine(_dir, "Sample.csv");
        File.WriteAllText(_csv, "a,b\r\n1,2\r\n3,4\r\n5,6\r\n", new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    [Fact]
    public void Dispose_and_Flush_write_synchronously_despite_a_long_debounce()
    {
        var side = Path.Combine(_dir, "Sample.ezflags.json");
        var s = RowFlagStore.Open(_csv, _fallback, TimeSpan.FromHours(1));
        s.Set(1, true);
        File.Exists(side).Should().BeFalse();
        s.Flush();
        File.Exists(side).Should().BeTrue();
        s.Set(2, true);
        s.Dispose();
        using var again = RowFlagStore.Open(_csv, _fallback);
        again.ToSortedArray().Should().Equal(1, 2);
    }

    [Fact]
    public void Flags_round_trip_through_the_sidecar()
    {
        using (var s = RowFlagStore.Open(_csv, _fallback, TimeSpan.FromMilliseconds(20)))
        {
            s.SidecarPath.Should().Be(Path.Combine(_dir, "Sample.ezflags.json"));
            s.Set(2, true).Should().BeTrue();
            s.Set(2, true).Should().BeFalse();
            s.Toggle(0).Should().BeTrue();
            s.SetMany(new long[] { 1, 2, 7 }, true).Should().Be(2);
            s.SetMany(new long[] { 7 }, false).Should().Be(1);
            s.Count.Should().Be(3);
            s.IsFlagged(1).Should().BeTrue();
            s.ToSortedArray().Should().Equal(0, 1, 2);
        }
        File.Exists(Path.Combine(_dir, "Sample.ezflags.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "Sample.ezflags.json.tmp")).Should().BeFalse();

        using var again = RowFlagStore.Open(_csv, _fallback);
        again.StaleWarning.Should().BeNull();
        again.ToSortedArray().Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Debounced_save_writes_without_flush()
    {
        using var s = RowFlagStore.Open(_csv, _fallback, TimeSpan.FromMilliseconds(30));
        s.Set(1, true);
        var path = s.SidecarPath;
        for (int i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(20);
        File.Exists(path).Should().BeTrue();
        // The sidecar is replaced atomically (temp file + rename); a reader can still hit a transient sharing violation during the rename.
        string text = "";
        for (int i = 0; i < 50; i++)
        {
            try { text = File.ReadAllText(path); break; }
            catch (IOException) { await Task.Delay(20); }
        }
        text.Should().Contain("\"rows\":[1]").And.Contain("csvHeadSha256");
    }

    [Fact]
    public void Changed_csv_drops_stale_flags_with_a_warning()
    {
        using (var s = RowFlagStore.Open(_csv, _fallback)) s.SetMany(new long[] { 0, 2 }, true);
        File.AppendAllText(_csv, "7,8\r\n");
        using var after = RowFlagStore.Open(_csv, _fallback);
        after.Count.Should().Be(0);
        after.StaleWarning.Should().Contain("2 saved flag(s) were dropped");
    }

    [Fact]
    public void Same_size_rewrite_is_detected_by_hash_or_time()
    {
        using (var s = RowFlagStore.Open(_csv, _fallback)) s.Set(1, true);
        var t = File.GetLastWriteTimeUtc(_csv);
        File.WriteAllText(_csv, "a,b\r\n9,9\r\n3,4\r\n5,6\r\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(_csv, t); // same size and time, different content
        using var after = RowFlagStore.Open(_csv, _fallback);
        after.Count.Should().Be(0);
        after.StaleWarning.Should().NotBeNull();
    }

    [Fact]
    public void Read_only_folder_falls_back_to_local_app_data_path()
    {
        // A read-only sidecar file stands in for a read-only folder (the write is refused either way).
        var sidecar = RowFlagStore.SidecarPathFor(_csv);
        File.WriteAllText(sidecar, "not json");
        File.SetAttributes(sidecar, FileAttributes.ReadOnly);

        using (var s = RowFlagStore.Open(_csv, _fallback))
        {
            s.Set(2, true);
            s.Flush();
            s.UsingFallback.Should().BeTrue();
            s.SidecarPath.Should().Be(RowFlagStore.FallbackPathFor(_csv, _fallback));
            File.Exists(s.SidecarPath).Should().BeTrue();
        }
        using var again = RowFlagStore.Open(_csv, _fallback);
        again.UsingFallback.Should().BeTrue();
        again.ToSortedArray().Should().Equal(2);
    }

    [Fact]
    public void Nothing_is_written_when_nothing_was_flagged()
    {
        using (var s = RowFlagStore.Open(_csv, _fallback)) { s.IsFlagged(0).Should().BeFalse(); }
        File.Exists(RowFlagStore.SidecarPathFor(_csv)).Should().BeFalse();
    }
}
