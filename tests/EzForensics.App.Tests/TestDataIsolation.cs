using System.IO;
using System.Runtime.CompilerServices;

namespace EzForensics.App.Tests;

/// <summary>Points the data root at a per-run temp folder so tests never write to the user's real EzForensicsData (logs, flags, cache).</summary>
internal static class TestDataIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EZFORENSICS_DATA"))) return;
        Environment.SetEnvironmentVariable("EZFORENSICS_DATA",
            Path.Combine(Path.GetTempPath(), "ezf-tests-data-" + Guid.NewGuid().ToString("N")));
    }
}
