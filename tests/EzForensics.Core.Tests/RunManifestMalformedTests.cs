using EzForensics.Core.Execution;
using Xunit;

namespace EzForensics.Core.Tests;

public class RunManifestMalformedTests
{
    private static string Write(string json)
    {
        var p = Path.Combine(Path.GetTempPath(), "ezf-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    [Theory]
    [InlineData("{\"jobs\": null}", 0)]
    [InlineData("{\"jobs\": [null, {\"toolId\":\"x\",\"outputFiles\":null,\"argv\":null}]}", 1)]
    [InlineData("{\"jobs\": [{\"toolId\":\"x\",\"outputFiles\":[null]}]}", 1)]
    public void Load_tolerates_null_jobs_and_entries(string json, int jobs)
    {
        var p = Write(json);
        try
        {
            var m = RunManifest.TryLoad(p);
            Assert.NotNull(m);
            Assert.Equal(jobs, m!.Jobs.Count);
            Assert.All(m.Jobs, j => { Assert.NotNull(j.OutputFiles); Assert.NotNull(j.Argv); });
        }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("{\"jobs\": 5}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void TryLoad_returns_null_for_wrong_shapes(string json)
    {
        var p = Write(json);
        try { Assert.Null(RunManifest.TryLoad(p)); }
        finally { File.Delete(p); }
    }
}
