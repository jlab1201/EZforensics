using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessRowLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_rowloc_" + Guid.NewGuid().ToString("N"));

    public ProcessRowLocatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task FindsRowByPid_NameAndCreateTime()
    {
        var path = Path.Combine(_dir, "Memory_PsList.csv");
        File.WriteAllText(path, "PID,PPID,ImageFileName,CreateTime\r\n" +
            "4,0,System,2024-01-01T00:00:00+00:00\r\n" +
            "900,4,old.exe,2024-01-01T00:01:00+00:00\r\n" +
            "900,4,new.exe,2024-01-01T00:09:00+00:00\r\n", new UTF8Encoding(false));
        (await ProcessRowLocator.FindRowsAsync(path, 4, "System", null, default)).Should().Equal(0L);
        (await ProcessRowLocator.FindRowsAsync(path, 900, "new.exe", null, default)).Should().Equal(2L);
        var t = new DateTime(2024, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        (await ProcessRowLocator.FindRowsAsync(path, 900, null, t, default)).Should().Equal(1L);
        (await ProcessRowLocator.FindRowsAsync(path, 77, null, null, default)).Should().BeEmpty();
    }
}
