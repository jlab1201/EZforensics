using System.Security.Cryptography;
using System.Text.Json;
using EzForensics.Core.Case;
using EzForensics.Core.Execution;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>CaseIndex follows Analyze-tree file operations (logs\file_operations.log) and validates manifest entries against the files.</summary>
public sealed class CaseIndexFileOpsTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly string _case;
    private readonly string _pe;
    private static readonly DateTime Job1End = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    public CaseIndexFileOpsTests()
    {
        _case = Path.Combine(_t.Path, "case");
        _pe = Path.Combine(_case, "H", "ProgramExecution");
        Directory.CreateDirectory(_pe);
    }

    public void Dispose() => _t.Dispose();

    private static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));

    private Guid WriteManifest(string name, DateTime end, params string[] outputs)
    {
        var id = Guid.NewGuid();
        var m = new RunManifest
        {
            CaseDir = _case, LayoutVersion = 2,
            Jobs = { new ManifestJob
            {
                JobId = id, ToolId = "PECmd", Label = "Prefetch", State = "Succeeded", EndUtc = end.ToString("o"), StartUtc = end.AddMinutes(-1).ToString("o"),
                OutputDir = _pe, Category = "ProgramExecution", RuleId = "prefetch", Host = "H",
                OutputFiles = outputs.Select(o => new ManifestOutputFile { Path = o, Size = new FileInfo(o).Length, Sha256 = Sha(o), ArtifactId = "prefetch", ToolId = "PECmd", Host = "H" }).ToList(),
            } },
        };
        File.WriteAllText(Path.Combine(_case, $"EzHub_manifest_{name}.json"), JsonSerializer.Serialize(m, ManifestWriter.JsonOptions));
        return id;
    }

    private CaseFileOperations Ops(DateTime when) => new(_case, (p, d) => { if (d) Directory.Delete(p, true); else File.Delete(p); }, "tester", () => when);

    private string WriteCsv(string path, string content = "A,B\r\n1,2\r\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Job1End.AddMinutes(-2));   // written by the job, before it ended
        return path;
    }

    [Fact]
    public void Rename_keeps_provenance_and_a_reused_name_does_not_inherit_it()
    {
        var pf = WriteCsv(Path.Combine(_pe, "Prefetch.csv"));
        var job1 = WriteManifest("1", Job1End, pf);
        var renamed = Ops(Job1End.AddDays(1)).Rename(pf, "Old.csv");
        WriteCsv(pf, "Other,Header,Row\r\nx,y,z\r\n");    // a new file under the old name (no manifest entry)

        var idx = CaseIndex.Load(_case);
        var a = idx.Describe(renamed);
        a.Source.Should().Be(ProvenanceSource.Manifest);
        a.Job!.JobId.Should().Be(job1);
        (a.ToolId, a.ArtifactId, a.Host).Should().Be(("PECmd", "prefetch", "H"));
        idx.Describe(pf).Source.Should().NotBe(ProvenanceSource.Manifest, "the reused name is a different file");
    }

    [Fact]
    public void A_later_run_at_the_old_name_gets_its_own_provenance()
    {
        var pf = WriteCsv(Path.Combine(_pe, "Prefetch.csv"));
        var job1 = WriteManifest("1", Job1End, pf);
        var renamed = Ops(Job1End.AddDays(1)).Rename(pf, "Old.csv");
        WriteCsv(pf, "A,B\r\n3,4,5\r\n");
        var job2 = WriteManifest("2", Job1End.AddDays(2), pf);

        var idx = CaseIndex.Load(_case);
        idx.Describe(renamed).Job!.JobId.Should().Be(job1);
        idx.Describe(pf).Job!.JobId.Should().Be(job2);
    }

    [Fact]
    public void Copy_move_folder_and_delete_are_replayed()
    {
        var pf = WriteCsv(Path.Combine(_pe, "Prefetch.csv"));
        var job1 = WriteManifest("1", Job1End, pf);
        var other = Directory.CreateDirectory(Path.Combine(_case, "Work")).FullName;
        var copy = Ops(Job1End.AddDays(1)).Copy(pf, other);
        var archive = Directory.CreateDirectory(Path.Combine(_case, "Archive")).FullName;
        var movedDir = Ops(Job1End.AddDays(2)).Move(_pe, archive);
        var moved = Path.Combine(movedDir, "Prefetch.csv");

        var idx = CaseIndex.Load(_case);
        idx.Describe(copy).Job!.JobId.Should().Be(job1);
        idx.Describe(moved).Job!.JobId.Should().Be(job1);
        idx.Describe(moved).Source.Should().Be(ProvenanceSource.Manifest);

        Ops(Job1End.AddDays(3)).Delete(new[] { copy });
        WriteCsv(copy);   // same name and content, but not the copy (it was deleted)
        CaseIndex.Load(_case).Describe(copy).Source.Should().NotBe(ProvenanceSource.Manifest);
    }

    [Fact]
    public void Entry_is_ignored_when_the_file_no_longer_matches_the_manifest()
    {
        var pf = WriteCsv(Path.Combine(_pe, "Prefetch.csv"));
        WriteManifest("1", Job1End, pf);
        CaseIndex.Load(_case).Describe(pf).Source.Should().Be(ProvenanceSource.Manifest);

        File.AppendAllText(pf, "9,9\r\n");                     // size differs
        CaseIndex.Load(_case).Describe(pf).Source.Should().NotBe(ProvenanceSource.Manifest);

        WriteCsv(pf, "A,B\r\n7,8\r\n");                         // same size, different content, written after the job
        File.SetLastWriteTimeUtc(pf, Job1End.AddDays(1));
        CaseIndex.Load(_case).Describe(pf).Source.Should().NotBe(ProvenanceSource.Manifest, "the hash is checked when the file changed after the job");
    }
}
