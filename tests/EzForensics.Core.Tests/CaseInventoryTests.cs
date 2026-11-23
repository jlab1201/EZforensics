using System.Text.RegularExpressions;
using EzForensics.Core.Assistant;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class CaseInventoryTests : IDisposable
{
    private readonly string _case = Path.Combine(Path.GetTempPath(), "ezinv-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_case, true); } catch (IOException) { } }

    /// <summary>A small layout v2 case with the real EZ tool file names and header rows.</summary>
    internal static void BuildCase(string dir)
    {
        void W(string rel, string header, params string[] rows)
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, header + "\n" + string.Join("\n", rows) + "\n");
        }
        W(@"WS01\Execution\Prefetch.csv", "SourceFilename,SourceCreated,SourceModified,SourceAccessed,ExecutableName,Hash,Size,Version,RunCount,LastRun,PreviousRun0,Volume0Name",
            @"C:\Windows\prefetch\POWERSHELL.EXE-022A1004.pf,2026-10-01 10:00:00,2026-10-01 10:00:00,2026-10-01 10:00:00,POWERSHELL.EXE,022A1004,10240,30,7,2026-10-01 10:00:00,2026-09-30 09:00:00,\Device\HarddiskVolume3",
            @"C:\Windows\prefetch\EVIL.EXE-AB12CD34.pf,2026-10-02 11:00:00,2026-10-02 11:00:00,2026-10-02 11:00:00,EVIL.EXE,AB12CD34,5120,30,1,2026-10-02 11:00:00,,\Device\HarddiskVolume3");
        W(@"WS01\Execution\Amcache_UnassociatedFileEntries.csv", "ApplicationName,ProgramId,FileKeyLastWriteTimestamp,SHA1,FullPath,Name",
            @"evil.exe,0006abc,2026-10-02 11:00:00,0123456789abcdef0123456789abcdef01234567,c:\users\bob\downloads\evil.exe,evil.exe");
        W(@"WS01\Execution\ShimCache.csv", "ControlSet,CacheEntryPosition,Path,LastModifiedTimeUTC,Executed", @"1,0,C:\Users\bob\Downloads\evil.exe,2026-10-02 10:59:00,NA");
        W(@"WS01\EventLogs\EventLogs.csv", "TimeCreated,EventId,Provider,Computer,Channel,PayloadData1",
            "2026-10-02 11:00:01,4688,Microsoft-Windows-Security-Auditing,WS01,Security,NewProcess evil.exe",
            "2026-10-02 11:05:00,4624,Microsoft-Windows-Security-Auditing,WS01,Security,Logon type 3");
        W(@"WS01\FileSystem\MFT.csv", "EntryNumber,ParentPath,FileName,Created0x10,IsDirectory", @"100,.\Users\bob\Downloads,evil.exe,2026-10-02 10:58:00,False");
    }

    [Fact]
    public void Lists_each_file_with_its_hint_and_execution_files_first()
    {
        BuildCase(_case);
        var text = CaseInventory.Build(_case);
        var lines = text.Split('\n');
        lines[0].Should().Contain("5 CSV file(s)");
        text.Should().Contain(@"WS01\Execution\Prefetch.csv").And.Contain("PECmd/prefetch").And.Contain("run count");
        text.Should().Contain("Amcache_UnassociatedFileEntries.csv").And.Contain("SHA1");
        text.Should().Contain("ShimCache.csv").And.Contain("not proven");
        lines.ToList().FindIndex(l => l.Contains("Prefetch.csv")).Should().BeLessThan(lines.ToList().FindIndex(l => l.Contains("MFT.csv")));
        text.Should().Contain("WS01").And.Contain("rows 2");
    }

    [Fact]
    public void Is_capped_and_points_to_case_list_files()
    {
        for (int i = 0; i < 60; i++)
        {
            var p = Path.Combine(_case, "H1", "Other", $"File{i:D2}.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "A,B\n1,2\n");
        }
        var text = CaseInventory.Build(_case);
        text.Length.Should().BeLessThanOrEqualTo(CaseInventory.MaxChars + 100);
        text.Split('\n').Length.Should().BeLessThanOrEqualTo(CaseInventory.MaxLines + 3);
        text.Should().Contain("more, use case_list_files");
    }

    [Fact]
    public void Cache_follows_file_changes_and_empty_cases_are_empty()
    {
        CaseInventory.Build(_case).Should().BeEmpty();
        Directory.CreateDirectory(_case);
        CaseInventory.Build(_case).Should().BeEmpty();
        CaseInventory.AppStateText(_case).Should().Contain("none");
        BuildCase(_case);
        var a = CaseInventory.Build(_case);
        CaseInventory.Build(_case).Should().Be(a);
        File.WriteAllText(Path.Combine(_case, "WS01", "Execution", "Srum_Foo.csv"), "A\n1\n");
        CaseInventory.Build(_case).Should().NotBe(a).And.Contain("Srum_Foo.csv");
    }

    [Fact]
    public void Small_file_row_count_is_exact_for_quoted_newlines_and_a_missing_final_newline()
    {
        Directory.CreateDirectory(Path.Combine(_case, "H1", "Other"));
        File.WriteAllText(Path.Combine(_case, "H1", "Other", "Quoted.csv"), "A,B\n1,\"l1\nl2\nl3\"\n2,\"l1\nl2\"\n");
        File.WriteAllText(Path.Combine(_case, "H1", "Other", "NoEol.csv"), "A,B\n1,2\n3,4");
        var text = CaseInventory.Build(_case);
        text.Split('\n').First(l => l.Contains("Quoted.csv")).Should().Contain("rows 2").And.NotContain("≈");
        text.Split('\n').First(l => l.Contains("NoEol.csv")).Should().Contain("rows 2");
    }

    [Theory]
    [InlineData("PECmd", "prefetch", null, "run count")]
    [InlineData("AmcacheParser", "amcache-associated-file-entries", null, "SHA1")]
    [InlineData("AppCompatCacheParser", "shimcache", null, "not proven")]
    [InlineData("EvtxECmd", "eventlog", null, "4688")]
    [InlineData("SrumECmd", "srum", null, "network")]
    [InlineData(null, null, "PECmd_Output.csv", "run count")]
    [InlineData(null, null, "20261001_AppCompatCache.csv", "not proven")]
    [InlineData("RECmd", "registry", "Registry_NTUSER_bob.csv", "registry")]
    public void Guide_maps_ids_and_file_names(string? tool, string? artifact, string? file, string expected)
    {
        var h = ArtifactGuide.For(tool, artifact, file);
        h.Should().NotBeNull();
        h!.Hint.Should().Contain(expected);
    }

    [Fact]
    public void Guide_knows_nothing_about_unknown_files_and_does_not_overstate()
    {
        ArtifactGuide.For(null, null, "random.csv").Should().BeNull();
        ArtifactGuide.For("AmcacheParser", "amcache", null)!.Hint.Should().Contain("not proof");
        Regex.IsMatch(ArtifactGuide.For("EvtxECmd", "eventlog", null)!.Hint, "if auditing on").Should().BeTrue();
    }

    [Fact]
    public void Chat_output_is_capped_for_openai_compatible_clients()
    {
        AssistantModelFactory.ChatMaxTokens.Should().Be(8192);
    }
}
