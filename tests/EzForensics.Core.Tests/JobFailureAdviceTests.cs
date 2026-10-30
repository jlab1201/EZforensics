using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class JobFailureAdviceTests
{
    [Fact] public void NotInstalled() =>
        JobFailureAdvice.Advise("MFTECmd", null, JobOutcomeClassifier.MissingToolReason, null).Should().Be("Install it in Tools → MFTECmd.");

    [Fact] public void Outdated() =>
        JobFailureAdvice.Advise("EvtxECmd", 1, "Exited with code 1", "Unrecognized command or argument '--foo'").Should().Be("Update it in Tools → EvtxECmd.");

    [Fact] public void NeedsAdmin() =>
        JobFailureAdvice.Advise("x", 0, JobOutcomeClassifier.AdminReason, null).Should().Be("Restart EZ Forensics as administrator.");

    [Fact] public void RitaImport() =>
        JobFailureAdvice.Advise("rita", 1, "Exited with code 1: rita import failed", null).Should().Contain("re-run Zeek");

    [Fact] public void ClickHouse() =>
        JobFailureAdvice.Advise("rita", 1, "x", "ClickHouse did not start").Should().Contain("wsl --shutdown");

    [Fact] public void Timeout() =>
        JobFailureAdvice.Advise("x", -1, "Timed out after 30 min", null).Should().Be("Increase the timeout in Settings or narrow the input.");

    [Fact] public void FileInUse() =>
        JobFailureAdvice.Advise("x", 1, "Exited with code 1", "The process cannot access the file because it is being used by another process").Should().StartWith("Close programs");

    [Fact] public void DiskFull() =>
        JobFailureAdvice.Advise("x", 1, "Exited with code 1", "No space left on device").Should().Be("Free disk space on the case drive.");

    [Fact] public void FallbackDoesNotRepeatError()
    {
        var a = JobFailureAdvice.Advise("x", 3, "Exited with code 3: boom", "boom");
        a.Should().Be(JobFailureAdvice.Generic).And.NotContain("boom");
    }

    [Fact] public void MultiLineCommandRoundTrips()
    {
        var cmd = "wsl.exe -d D sh -c \"a\nb\nc\"";
        var lines = JobLogFormat.CommandHeaderLines(cmd).ToList();
        lines.Should().OnlyContain(l => JobLogFormat.IsHeaderLine(l));
        var (c, body) = JobLogFormat.Split(string.Join("\n", lines.Prepend("# EzHub job 1")) + "\nout1\nout2\n# Exit code: 0\n");
        c.Should().Be(cmd);
        body.Should().Be("out1\nout2\n");
    }
}
