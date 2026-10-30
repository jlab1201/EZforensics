using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ExecutionOutcomeClassifierTests
{
    private static readonly string[] None = Array.Empty<string>();

    [Fact]
    public void Succeeded_counts_files_and_csv_rows()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h1,h2\n1,2\n3,4\n5,6\n");
        var txt = Path.Combine(d.Path, "b.txt"); File.WriteAllText(txt, "x\ny\n");
        var o = JobOutcomeClassifier.Classify(0, false, false, None, new[] { csv, txt });
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().Be("Completed: 2 output file(s), 3 CSV rows");
    }

    [Fact]
    public void Csv_without_trailing_newline_counts_last_row()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h\r\n1\r\n2");
        JobOutcomeClassifier.CountCsvRows(csv).Should().Be(2);
    }

    [Fact]
    public void Empty_output_is_a_warning_but_still_succeeded()
    {
        var o = JobOutcomeClassifier.Classify(0, false, false, None, None);
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().Be("Completed but produced no output files");
    }

    [Fact]
    public void Dirty_hive_fails_even_with_exit_zero()
    {
        var lines = new[] { "Registry hive is dirty and no transaction logs were found in the same directory! LOGs should have same base name as the hive. Aborting!!" };
        var o = JobOutcomeClassifier.Classify(0, false, false, lines, None);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Be(JobOutcomeClassifier.DirtyHiveReason).And.Contain("--nl");
    }

    [Fact]
    public void Dirty_hive_with_logs_found_is_not_flagged()
    {
        var lines = new[] { "Registry hive is dirty and 2 transaction logs were found; replaying" };
        JobOutcomeClassifier.Classify(0, false, false, lines, None).State.Should().Be(JobState.Succeeded);
    }

    [Fact]
    public void Admin_required()
    {
        var o = JobOutcomeClassifier.Classify(0, false, false, new[] { "Administrator privileges not found! Exiting" }, None);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Be(JobOutcomeClassifier.AdminReason);
    }

    [Fact]
    public void Missing_tool()
    {
        var o = JobOutcomeClassifier.Classify(0, false, false, None, None, toolMissing: true);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Contain("Tools");
    }

    [Fact]
    public void Timed_out_reports_minutes()
    {
        var o = JobOutcomeClassifier.Classify(-1, true, false, None, None, TimeSpan.FromMinutes(30));
        o.State.Should().Be(JobState.TimedOut);
        o.Reason.Should().Be("Timed out after 30 min");
    }

    [Fact]
    public void Cancelled()
    {
        var o = JobOutcomeClassifier.Classify(-1, false, true, None, None);
        o.State.Should().Be(JobState.Cancelled);
        o.Reason.Should().Be("Cancelled by user");
    }

    [Fact]
    public void Nonzero_exit_includes_last_error_looking_line()
    {
        var lines = new[] { "starting", "Error: file locked", "cleaning up", "bye" };
        var o = JobOutcomeClassifier.Classify(3, false, false, lines, None);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Be("Exited with code 3: Error: file locked");
    }

    [Fact]
    public void Nonzero_exit_falls_back_to_last_stderr_line()
    {
        var o = JobOutcomeClassifier.Classify(2, false, false, new[] { "a", "boom" }, None, stdErrLines: new[] { "", "boom" });
        o.Reason.Should().Be("Exited with code 2: boom");
    }

    [Fact]
    public void Nonzero_exit_without_output_has_plain_reason()
    {
        JobOutcomeClassifier.Classify(1, false, false, None, None).Reason.Should().Be("Exited with code 1");
    }

    private const string AdminWarn = "Warning: Administrator privileges not found!";
    private const string DirtyLine = "Registry hive is dirty and no transaction logs were found in the same directory! LOGs should have same base name as the hive. Aborting!!";

    [Fact]
    public void Admin_warning_with_data_is_success_with_note()
    {
        // Real EvtxECmd / MFTECmd / PECmd output when run unelevated: warning at startup, exit 0, real rows.
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h\n1\n2\n");
        var lines = new[] { "Command line: -d X", "", AdminWarn, "", "Processing 40 logs" };
        var o = JobOutcomeClassifier.Classify(0, false, false, lines, new[] { csv });
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().Be("Completed (ran without admin; some data may be missing): 1 output file(s), 2 CSV rows");
    }

    [Fact]
    public void Admin_warning_and_exiting_with_no_data_fails()
    {
        var o = JobOutcomeClassifier.Classify(0, false, false, new[] { AdminWarn, "Exiting" }, None);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Be(JobOutcomeClassifier.AdminReason);
    }

    [Fact]
    public void Admin_warning_and_exiting_but_data_produced_is_not_failed()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h\n1\n");
        JobOutcomeClassifier.Classify(0, false, false, new[] { AdminWarn, "Exiting" }, new[] { csv }).State.Should().Be(JobState.Succeeded);
    }

    [Fact]
    public void Admin_warning_with_header_only_csv_and_no_abort_is_not_failed()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h\n");
        var o = JobOutcomeClassifier.Classify(0, false, false, new[] { AdminWarn }, new[] { csv });
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().StartWith("Completed (ran without admin");
    }

    [Fact]
    public void Dirty_hive_wins_over_admin_warning()
    {
        // Real AppCompatCacheParser output on the dirty SYSTEM sample hive when unelevated.
        var lines = new[] { AdminWarn, "Processing hive 'SYSTEM'", "Log files not found and no administrator access to look for them!", DirtyLine };
        var o = JobOutcomeClassifier.Classify(0, false, false, lines, None);
        o.State.Should().Be(JobState.Failed);
        o.Reason.Should().Be(JobOutcomeClassifier.DirtyHiveReason);
    }

    [Fact]
    public void No_output_includes_tool_error_line()
    {
        // Real RECmd output on the SAM sample hive (tool exception, exit 0, nothing written).
        var lines = new[] { "Processing hive X", "There was an error: Index and count must refer to a location within the buffer. (Parameter 'bytes')", "System.ArgumentOutOfRangeException: ..." };
        var o = JobOutcomeClassifier.Classify(0, false, false, lines, None);
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().StartWith("Completed but produced no output files: There was an error: Index and count");
    }

    [Theory]
    [InlineData("Found 0 files")]
    [InlineData("Processed 0 files in 0.12 seconds")]
    [InlineData("Processed 0 out of 0 files")]
    public void Zero_input_files_is_a_succeeded_no_input_warning(string line)
    {
        var o = JobOutcomeClassifier.Classify(0, false, false, new[] { line }, None);
        o.State.Should().Be(JobState.Succeeded);
        o.Reason.Should().Be(JobOutcomeClassifier.NoInputReason);
        JobOutcomeClassifier.IsNoInput(o.Reason).Should().BeTrue();
    }

    [Fact]
    public void Header_only_csv_reports_zero_rows()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h1,h2\n");
        var o = JobOutcomeClassifier.Classify(0, false, false, new[] { "Found 0 files" }, new[] { csv });
        o.Reason.Should().Be(JobOutcomeClassifier.NoInputReason);
        var o2 = JobOutcomeClassifier.Classify(0, false, false, None, new[] { csv });
        o2.State.Should().Be(JobState.Succeeded);
        o2.Reason.Should().Be("Completed: 1 file(s), 0 rows (no matching data)");
    }

    [Fact]
    public void Zero_input_with_data_rows_is_not_a_no_input_warning()
    {
        using var d = new TempDir();
        var csv = Path.Combine(d.Path, "a.csv");
        File.WriteAllText(csv, "h\n1\n");
        JobOutcomeClassifier.Classify(0, false, false, new[] { "Found 0 files" }, new[] { csv }).Reason.Should().StartWith("Completed: 1 output file(s)");
    }

    [Fact]
    public void Bookkeeping_files_are_recognised()
    {
        var log = Path.Combine(Path.GetTempPath(), "case", "logs", "abc_JLECmd.log");
        JobQueue.IsJobBookkeepingFile(log, log).Should().BeTrue();
        JobQueue.IsJobBookkeepingFile(Path.Combine(Path.GetDirectoryName(log)!, "other.log"), log).Should().BeTrue();
        JobQueue.IsJobBookkeepingFile(Path.Combine(Path.GetTempPath(), "case", "EzForensics_manifest_1.json"), log).Should().BeTrue();
        JobQueue.IsJobBookkeepingFile(Path.Combine(Path.GetTempPath(), "case", "out", "a.csv"), log).Should().BeFalse();
    }

    [Fact]
    public void Explorer_target_keeps_drive_root_backslash()
    {
        var r = EzForensics.Core.Results.ExplorerTarget.Resolve(@"C:", fileExists: _ => false, dirExists: _ => true);
        r!.Value.Arguments.Should().Be("\"C:\\\"");
    }
}
