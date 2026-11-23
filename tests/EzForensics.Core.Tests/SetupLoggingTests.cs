using System.Text;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The elevated helper's live log file, the tailer that forwards it, and the "still working" heartbeat.</summary>
public class SetupLoggingTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ezf-setuplog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Helper_writes_every_log_line_to_the_log_file_next_to_the_result()
    {
        var dir = TempDir();
        try
        {
            var result = Path.Combine(dir, "enable-wsl.result.json");
            var log = Path.Combine(dir, "enable-wsl.log");
            var args = ElevatedSetupHelper.BuildArgs(new[] { ElevatedSetupHelper.EnableWslTask }, result, log);
            args.Should().ContainInOrder("--result", result, "--log", log);
            var seen = new List<string>();
            int Run(string exe, IReadOnlyList<string> a, Action<string> l) { l("output of " + Path.GetFileName(exe)); return 0; }
            ElevatedSetupHelper.RunCommandLine(args.Skip(1).ToList(), seen.Add, Run).Should().Be(0);

            var lines = File.ReadAllLines(log);
            lines.Should().Equal(seen, "every line also goes to the host log");
            lines.Should().Contain(l => l.StartsWith("> ") && l.Contains("wsl.exe --install --no-distribution"));
            lines.Should().Contain("output of wsl.exe");
            ElevatedSetupHelper.ReadResult(result)!.Ok.Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Tailer_returns_only_complete_new_lines()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.log");
            var t = new LogFileTailer(path);
            t.ReadNew().Should().BeEmpty("the file does not exist yet");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            void W(string s) { var b = Encoding.UTF8.GetBytes(s); fs.Write(b); fs.Flush(); }
            W("first\nsec");
            t.ReadNew().Should().Equal("first");
            W("ond – ü\r\nthird\n");
            t.ReadNew().Should().Equal("second – ü", "third");
            t.ReadNew().Should().BeEmpty();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Tail_forwards_lines_while_the_writer_is_running_and_drains_at_the_end()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "t.log");
            var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var stop = new CancellationTokenSource();
            var tail = LogFileTailer.TailAsync(path, got.Enqueue, stop.Token);
            using (var f = LineLogFile.TryOpen(path)!)
            {
                f.WriteLine("one");
                var until = DateTime.UtcNow.AddSeconds(10);
                while (got.IsEmpty && DateTime.UtcNow < until) await Task.Delay(20);
                got.Should().Equal("one");
                f.WriteLine("two");
                stop.Cancel();
            }
            (await tail).Should().Be(2);
            got.Should().Equal("one", "two");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Heartbeat_logs_a_still_line_when_the_step_is_quiet()
    {
        var step = new SetupStep { Id = "s", Kind = SetupStepKind.EnableWsl, Title = "Enable WSL" };
        using (new StepHeartbeat(step, DefaultSetupExecutor.WslHeartbeat, TimeSpan.FromMilliseconds(100)))
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!step.LogText.Contains("Still enabling WSL") && DateTime.UtcNow < until) await Task.Delay(20);
        }
        step.LogText.Should().Contain("Still enabling WSL… Windows is installing components; this can take several minutes (elapsed 0:0");
        await Task.Delay(150);                               // a tick already past the disposed check may still land
        var after = step.LogText;
        await Task.Delay(300);
        step.LogText.Should().Be(after, "a disposed heartbeat stays quiet");
    }

    [Fact, Trait("Category", "Flaky")] // timing-sensitive under full parallel load; passes alone
    public async Task Heartbeat_waits_while_the_step_keeps_logging()
    {
        var step = new SetupStep { Id = "s", Kind = SetupStepKind.InstallPlaso, Title = "pip" };
        using (new StepHeartbeat(step, "Still installing", TimeSpan.FromSeconds(3)))
        {
            for (int i = 0; i < 10; i++) { step.Log("Collecting package " + i); await Task.Delay(20); }
        }
        step.LogText.Should().NotContain("Still installing");
        StepHeartbeat.Format("Still x", TimeSpan.FromSeconds(160)).Should().Be("Still x (elapsed 2:40).");
    }
}
