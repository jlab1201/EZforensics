using EzForensics.Core.Assistant;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class LocalAiAdvisorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static GpuInfo Gpu(string n, double gb) => new(n, (long)(gb * Gb));

    [Theory]
    [InlineData(8, 16, LocalAiChoice.Local, "qwen3:8b")]
    [InlineData(7.9, 8, LocalAiChoice.Local, "qwen3:8b")]
    [InlineData(12, 32, LocalAiChoice.Local, "qwen3:8b")]
    [InlineData(6, 16, LocalAiChoice.Local, "qwen3:4b")]
    [InlineData(7, 8, LocalAiChoice.Local, "qwen3:4b")]
    [InlineData(4, 16, LocalAiChoice.LocalSlow, "qwen3:4b")]
    [InlineData(2, 8, LocalAiChoice.Cloud, null)]
    public void Recommend_follows_the_table(double vramGb, double ramGb, LocalAiChoice choice, string? model)
    {
        var r = LocalAiAdvisor.Recommend((long)(ramGb * Gb), new[] { Gpu("NVIDIA RTX 3070", vramGb) });
        r.Choice.Should().Be(choice);
        r.Model.Should().Be(model);
        r.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void No_gpu_uses_ram()
    {
        LocalAiAdvisor.Recommend(32 * Gb, Array.Empty<GpuInfo>()).Choice.Should().Be(LocalAiChoice.LocalSlow);
        LocalAiAdvisor.Recommend(8 * Gb, Array.Empty<GpuInfo>()).Choice.Should().Be(LocalAiChoice.Cloud);
    }

    [Fact]
    public void Reason_names_the_gpu()
    {
        var r = LocalAiAdvisor.Recommend(16 * Gb, new[] { Gpu("NVIDIA RTX 3070", 8) });
        r.Reason.Should().Be("NVIDIA RTX 3070, 8 GB: qwen3:8b runs on the GPU.");
        r.RecommendLocal.Should().BeTrue();
    }

    [Fact]
    public void ParseGpus_reads_qword_falls_back_to_dword_and_skips_virtual()
    {
        var gpus = AiHardware.ParseGpus(new[]
        {
            new AiHardware.RegistryGpu("Microsoft Basic Display Adapter", 1L * Gb, null),
            new AiHardware.RegistryGpu("Intel(R) UHD Graphics", null, 128 * 1024 * 1024),
            new AiHardware.RegistryGpu("NVIDIA GeForce RTX 3070", BitConverter.GetBytes(8L * Gb), null),
            new AiHardware.RegistryGpu("Hyper-V Video", 1L * Gb, null),
            new AiHardware.RegistryGpu("No size", null, null),
        });
        gpus.Select(g => g.Name).Should().Equal("NVIDIA GeForce RTX 3070", "Intel(R) UHD Graphics");
        gpus[0].VramBytes.Should().Be(8L * Gb);
        gpus[1].VramBytes.Should().Be(128L * 1024 * 1024);
    }

    [Fact]
    public void FindOllamaExe_checks_the_user_folder_then_path()
    {
        AiHardware.FindOllamaExe(p => p.Contains("Programs"), @"C:\U\AppData\Local", "").Should().Contain("Programs");
        AiHardware.FindOllamaExe(p => p.StartsWith(@"D:\bin"), @"C:\U", @"C:\x;D:\bin").Should().Be(@"D:\bin\ollama.exe");
        AiHardware.FindOllamaExe(_ => false, @"C:\U", @"C:\x").Should().BeNull();
    }

    [Fact]
    public void OllamaState_HasModel_matches_untagged_names()
    {
        var s = new OllamaState(true, true, new[] { "qwen3:8b" });
        s.HasModel("qwen3:8b").Should().BeTrue();
        s.HasModel("qwen3").Should().BeTrue();
        s.HasModel("qwen3:4b").Should().BeFalse();
    }
}
