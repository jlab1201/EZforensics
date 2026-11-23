using EzForensics.Core.Diagnostics;
using EzForensics.Core.Results;
using Xunit;

namespace EzForensics.Core.Tests;

[Collection("CoreLog")]
public class CoreLogTests
{
    [Fact]
    public void Corrupt_flag_sidecar_is_logged_not_silently_ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-corelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var seen = new List<string>();
        var old = CoreLog.Sink;
        CoreLog.Sink = m => { lock (seen) seen.Add(m); };
        try
        {
            var csv = Path.Combine(dir, "a.csv");
            File.WriteAllText(csv, "A,B\n1,2\n");
            File.WriteAllText(RowFlagStore.SidecarPathFor(csv), "{ not json");
            using var store = RowFlagStore.Open(csv, fallbackDir: Path.Combine(dir, "fb"));
            Assert.Equal(0, store.Count);
            lock (seen) Assert.Contains(seen, m => m.Contains("Could not read row flags") && m.Contains(".ezflags.json"));
        }
        finally { CoreLog.Sink = old; try { Directory.Delete(dir, true); } catch { } }
    }
}
