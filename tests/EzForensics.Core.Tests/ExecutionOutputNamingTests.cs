using System.Reflection;
using System.Text.Json;
using EzForensics.Core.Execution;
using FluentAssertions;
using Xunit.Sdk;

namespace EzForensics.Core.Tests;

public class ExecutionOutputNamingTests
{
    // ------------------------------------------------------------------ name rules

    [Theory]
    [InlineData("20261007123456_EvtxECmd_Output.csv", "EvtxECmd_Output.csv")]
    [InlineData("20261007065842_MFTECmd_$MFT_Output.csv", "MFTECmd_$MFT_Output.csv")]
    [InlineData("20261007155643_TypedURLs__NTUSER.DAT.csv", "TypedURLs__NTUSER.DAT.csv")]
    [InlineData("20261007155643_RECmd_Batch_DFIRBatch_Output.csv", "RECmd_Batch_DFIRBatch_Output.csv")]
    [InlineData("20261007235641_Amcache_AssociatedFileEntries.csv", "Amcache_AssociatedFileEntries.csv")]
    [InlineData("20261007235642_Windows10C_11_SYSTEM_AppCompatCache.csv", "Windows10C_11_SYSTEM_AppCompatCache.csv")]
    [InlineData("20261007155645_AutomaticDestinations.csv", "AutomaticDestinations.csv")]
    [InlineData("20261007065845_SrumECmd_NetworkConnections_Output.csv", "SrumECmd_NetworkConnections_Output.csv")]
    [InlineData("20261007065841_PECmd_Output_Timeline.csv", "PECmd_Output_Timeline.csv")]
    [InlineData("20261007155647134825_Windows_ActivitiesCacheDB_d0022b46-3896-4b9f-a9e5-499241eba806.csv",
        "Windows_ActivitiesCacheDB_d0022b46-3896-4b9f-a9e5-499241eba806.csv")]   // SQLECmd: 20 digits
    [InlineData("20261007065845_Activity.json", "Activity.json")]
    public void Strips_ez_run_time_prefix(string name, string expected)
    {
        OutputNaming.TryStripTimestampPrefix(name, out var s).Should().BeTrue();
        s.Should().Be(expected);
    }

    [Theory]
    [InlineData("sample_UsrClass.csv")]            // SBECmd: no prefix
    [InlineData("!SBECmd_Messages.txt")]
    [InlineData("EvtxECmd_Output.csv")]            // already clean
    [InlineData("12345_foo.csv")]                  // too few digits
    [InlineData("20261007_ChromiumBrowser.csv")]   // date only, not a run time
    [InlineData("99999999999999_foo.csv")]         // not a date
    [InlineData("20261307123456_foo.csv")]         // month 13
    [InlineData("20261007123456_.csv")]            // would leave a bare extension
    [InlineData("20261007123456")]                 // no separator
    public void Leaves_names_without_a_run_time_prefix(string name)
    {
        OutputNaming.TryStripTimestampPrefix(name, out var s).Should().BeFalse();
        s.Should().Be(name);
    }

    [Theory]
    [InlineData("20261007123456_EvtxECmd_Output.csv", "EvtxECmd_Output.csv")]
    [InlineData("EvtxECmd_Output.csv", "EvtxECmd_Output.csv")]
    [InlineData("EvtxECmd_Output (2).csv", "EvtxECmd_Output.csv")]
    [InlineData("TypedURLs_NTUSER.DAT (12).csv", "TypedURLs_NTUSER.DAT.csv")]
    [InlineData(@"C:\case\x\20261007123456_LECmd_Output.csv", "LECmd_Output.csv")]
    public void Canonical_name_ignores_prefix_and_collision_suffix(string name, string expected) =>
        OutputNaming.CanonicalName(name).Should().Be(expected);

    [Fact]
    public void Copy_number_goes_before_the_last_extension()
    {
        OutputNaming.WithCopyNumber(@"C:\o\TypedURLs_NTUSER.DAT.csv", 2).Should().Be(@"C:\o\TypedURLs_NTUSER.DAT (2).csv");
        OutputNaming.WithCopyNumber(@"C:\o\TypedURLs_NTUSER.DAT.csv", 1).Should().Be(@"C:\o\TypedURLs_NTUSER.DAT.csv");
        OutputNaming.IsTimestampFolderName("20261007155643").Should().BeTrue();
        OutputNaming.IsTimestampFolderName("Users_sample_NTUSER.DAT").Should().BeFalse();
    }

    [Fact]
    public void User_named_files_come_from_the_file_name_switches()
    {
        OutputNaming.UserNamedFiles(new[] { "-f", "a.evtx", "--csv", @"C:\o", "--csvf", "20261007123456_mine.csv", "--JSONF", @"sub\x.json", "--bodyf" })
            .Should().BeEquivalentTo("20261007123456_mine.csv", "x.json");
    }

    // ------------------------------------------------------------------ renamer

    private static void Touch(string path, string content = "A,B\n1,2\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Renames_only_new_files_flattens_recmd_stamp_folder_and_keeps_user_named()
    {
        using var t = new TempDir();
        var o = t.Path;
        // Earlier run (old naming) - must stay untouched.
        Touch(Path.Combine(o, "20200101000000_RECmd_Batch_DFIRBatch_Output.csv"), "old");
        var snap = OutputSnapshot.Capture(o);

        Touch(Path.Combine(o, "20261007155643_RECmd_Batch_DFIRBatch_Output.csv"));
        Touch(Path.Combine(o, "20261007155643", "20261007155643_TypedURLs__NTUSER.DAT.csv"));
        Touch(Path.Combine(o, "20261007155643", "20261007155643_UserAssist__NTUSER.DAT.csv"));
        Touch(Path.Combine(o, "20261007155643_mine.csv"));   // --csvf 20261007155643_mine.csv
        Touch(Path.Combine(o, "!SBECmd_Messages.txt"));

        var renames = OutputRenamer.StripTimestampPrefixes(snap, new[] { "20261007155643_mine.csv" }, "RECmd");

        var names = Directory.EnumerateFiles(o, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(o, f)).ToList();
        names.Should().BeEquivalentTo(
            "20200101000000_RECmd_Batch_DFIRBatch_Output.csv",
            "RECmd_Batch_DFIRBatch_Output.csv",
            "TypedURLs_NTUSER.DAT.csv",
            "UserAssist_NTUSER.DAT.csv",
            "20261007155643_mine.csv",
            "!SBECmd_Messages.txt");
        Directory.Exists(Path.Combine(o, "20261007155643")).Should().BeFalse("the emptied run-time folder is removed");
        File.ReadAllText(Path.Combine(o, "20200101000000_RECmd_Batch_DFIRBatch_Output.csv")).Should().Be("old");
        renames.Should().HaveCount(3);
        renames.Single(r => r.FinalName == "TypedURLs_NTUSER.DAT.csv").OriginalName.Should().Be("20261007155643_TypedURLs__NTUSER.DAT.csv");
    }

    [Fact]
    public void Collisions_never_overwrite_and_number_the_new_file()
    {
        using var t = new TempDir();
        var o = t.Path;
        Touch(Path.Combine(o, "EvtxECmd_Output.csv"), "first run");
        var snap = OutputSnapshot.Capture(o);
        Touch(Path.Combine(o, "20261008010101_EvtxECmd_Output.csv"), "second run");
        // SQLECmd can emit the same map twice in one run (two profiles): earliest stamp gets the plain name.
        Touch(Path.Combine(o, "20261007155649127713_ChromiumBrowser_Favicons_d13f.csv"), "profile 1");
        Touch(Path.Combine(o, "20261007155649734916_ChromiumBrowser_Favicons_d13f.csv"), "profile 2");

        OutputRenamer.StripTimestampPrefixes(snap, null, "SQLECmd");

        File.ReadAllText(Path.Combine(o, "EvtxECmd_Output.csv")).Should().Be("first run");
        File.ReadAllText(Path.Combine(o, "EvtxECmd_Output (2).csv")).Should().Be("second run");
        File.ReadAllText(Path.Combine(o, "ChromiumBrowser_Favicons_d13f.csv")).Should().Be("profile 1");
        File.ReadAllText(Path.Combine(o, "ChromiumBrowser_Favicons_d13f (2).csv")).Should().Be("profile 2");
        Directory.EnumerateFiles(o).Should().HaveCount(4);
    }

    [Fact]
    public void Double_underscore_is_collapsed_only_for_recmd()
    {
        using var t = new TempDir();
        var snap = OutputSnapshot.Capture(t.Path);
        Touch(Path.Combine(t.Path, "20261007155643_A__B.csv"));
        OutputRenamer.StripTimestampPrefixes(snap, null, "OtherTool");
        File.Exists(Path.Combine(t.Path, "A__B.csv")).Should().BeTrue();
    }

    // ------------------------------------------------------------------ JobQueue integration (FakeTool writes EZ-style names)

    private static JobQueue NewQueue()
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), 2, TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    private static JobSpec Spec(string outDir, params string[] extra) =>
        new("RECmd", "EzForensics.FakeTool.exe", new[] { "--csv", outDir }.Concat(extra).ToList(), outDir, "R", "Registry");

    [Fact]
    public async Task Job_renames_outputs_and_manifest_records_original_names_and_final_hashes()
    {
        using var t = new TempDir();
        var outDir = Path.Combine(t.Path, "Registry", "RECmd", "Users_bob_NTUSER.DAT");
        var q = NewQueue();
        var job = q.Enqueue(Spec(outDir, "--emit", "{ts}_RECmd_Batch_DFIRBatch_Output.csv", "--emit", @"{ts}\{ts}_TypedURLs__NTUSER.DAT.csv"), t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        job.StatusReason.Should().Contain("2 output file(s)");
        File.Exists(Path.Combine(outDir, "RECmd_Batch_DFIRBatch_Output.csv")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "TypedURLs_NTUSER.DAT.csv")).Should().BeTrue();
        Directory.EnumerateDirectories(outDir).Should().BeEmpty();
        job.OutputRenames.Should().HaveCount(2);

        using var doc = JsonDocument.Parse(File.ReadAllText(q.GetManifestPath(t.Path)!));
        var files = doc.RootElement.GetProperty("jobs")[0].GetProperty("outputFiles").EnumerateArray().ToList();
        files.Should().HaveCount(2);
        var typed = files.Single(f => f.GetProperty("path").GetString()!.EndsWith("TypedURLs_NTUSER.DAT.csv"));
        typed.GetProperty("originalName").GetString().Should().MatchRegex(@"^\d{14}_TypedURLs__NTUSER\.DAT\.csv$");
        typed.GetProperty("originalPath").GetString().Should().Contain(Path.DirectorySeparatorChar + typed.GetProperty("originalName").GetString());
        typed.GetProperty("sha256").GetString().Should().Be(ManifestWriter.Hash(Path.Combine(outDir, "TypedURLs_NTUSER.DAT.csv"), System.Security.Cryptography.SHA256.Create()));
    }

    [Theory]
    [InlineData("EvtxECmd_Output.ezstats.json", true)]
    [InlineData(@"C:\case\x\Foo.EZFLAGS.JSON", true)]
    [InlineData("EvtxECmd_Output.csv", false)]
    [InlineData("stats.json", false)]
    public void Sidecars_are_recognised(string name, bool expected) => OutputNaming.IsEzForensicsSidecar(name).Should().Be(expected);

    [Fact]
    public async Task EzForensics_sidecars_are_never_outputs_renamed_hashed_or_counted()
    {
        using var t = new TempDir();
        var outDir = Path.Combine(t.Path, "EventLogs", "EvtxECmd");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "Old.ezflags.json"), "{}"); // left by the Results page before the run
        var q = NewQueue();
        // The run creates one CSV plus (stand-ins for) a stats cache and a flags file written during the run.
        var job = q.Enqueue(Spec(outDir, "--emit", "{ts}_EvtxECmd_Output.csv", "--emit", "{ts}_EvtxECmd_Output.ezstats.json",
            "--emit", "Other.ezflags.json"), t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        job.StatusReason.Should().Contain("1 output file(s)");
        job.OutputRenames.Should().ContainSingle().Which.FinalName.Should().Be("EvtxECmd_Output.csv");
        Directory.GetFiles(outDir, "*.ezstats.json").Should().ContainSingle().Which.Should().MatchRegex(@"\\\d{14}_EvtxECmd_Output\.ezstats\.json$",
            "a sidecar keeps its name");
        using var doc = JsonDocument.Parse(File.ReadAllText(q.GetManifestPath(t.Path)!));
        var files = doc.RootElement.GetProperty("jobs")[0].GetProperty("outputFiles").EnumerateArray()
            .Select(f => Path.GetFileName(f.GetProperty("path").GetString()!)).ToList();
        files.Should().Equal("EvtxECmd_Output.csv");
    }

    [Fact]
    public async Task Setting_off_keeps_the_tool_names()
    {
        using var t = new TempDir();
        var outDir = Path.Combine(t.Path, "out");
        var q = NewQueue();
        q.StripTimestampPrefix = false;
        var job = q.Enqueue(Spec(outDir, "--emit", "{ts}_EvtxECmd_Output.csv"), t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        job.OutputRenames.Should().BeEmpty();
        Directory.EnumerateFiles(outDir).Select(Path.GetFileName).Should().ContainSingle()
            .Which.Should().MatchRegex(@"^\d{14}_EvtxECmd_Output\.csv$");
        using var doc = JsonDocument.Parse(File.ReadAllText(q.GetManifestPath(t.Path)!));
        doc.RootElement.GetProperty("jobs")[0].GetProperty("outputFiles")[0].TryGetProperty("originalName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task User_named_csvf_file_is_not_renamed()
    {
        using var t = new TempDir();
        var outDir = Path.Combine(t.Path, "out");
        var q = NewQueue();
        var job = q.Enqueue(Spec(outDir, "--csvf", "20261007123456_keep.csv"), t.Path);
        await q.WhenAllAsync();

        job.State.Should().Be(JobState.Succeeded);
        Directory.EnumerateFiles(outDir).Select(Path.GetFileName).Should().Equal("20261007123456_keep.csv");
    }

    [Fact]
    public async Task Rerun_into_the_same_folder_numbers_the_new_output()
    {
        using var t = new TempDir();
        var outDir = Path.Combine(t.Path, "out");
        var q = NewQueue();
        q.Enqueue(Spec(outDir, "--emit", "{ts}_EvtxECmd_Output.csv"), t.Path);
        await q.WhenAllAsync();
        q.Enqueue(Spec(outDir, "--emit", "{ts}_EvtxECmd_Output.csv"), t.Path);
        await q.WhenAllAsync();

        Directory.EnumerateFiles(outDir).Select(Path.GetFileName).Should().BeEquivalentTo("EvtxECmd_Output.csv", "EvtxECmd_Output (2).csv");
    }

}
