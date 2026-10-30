using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Case;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class CaseFileOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ezforensics_fileops_" + Guid.NewGuid().ToString("N"));
    private readonly string _case;
    private readonly string _bin;
    private readonly List<(string Path, bool Dir)> _recycled = new();
    private readonly CaseFileOperations _ops;
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    public CaseFileOperationsTests()
    {
        _case = Path.Combine(_root, "Case1");
        _bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(_case);
        Directory.CreateDirectory(_bin);
        _ops = new CaseFileOperations(_case, Recycle, "tester", () => Now);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Fake recycle bin: moves the item into a folder outside the case.</summary>
    private void Recycle(string path, bool dir)
    {
        _recycled.Add((path, dir));
        var target = Path.Combine(_bin, Guid.NewGuid().ToString("N"));
        if (dir) Directory.Move(path, target); else File.Move(path, target);
    }

    private string Write(string rel, string content = "A,B\r\n1,2\r\n")
    {
        var p = Path.Combine(_case, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    /// <summary>A CSV with its two sidecars (row flags + tree stats).</summary>
    private string WriteCsvWithSidecars(string rel)
    {
        var csv = Write(rel);
        File.WriteAllText(RowFlagStore.SidecarPathFor(csv), "{\"flags\":[]}");
        File.WriteAllText(FileStatsCache.SidecarPathFor(csv), "{}");
        return csv;
    }

    private string[] LogLines() => File.Exists(_ops.LogPath) ? File.ReadAllLines(_ops.LogPath) : Array.Empty<string>();

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // ------------------------------------------------------------ bounds / refusals

    [Fact]
    public void Refuses_PathsOutsideTheCase()
    {
        var outside = Path.Combine(_root, "outside.csv");
        File.WriteAllText(outside, "A\r\n1\r\n");
        var inside = Write(@"HOST1\EventLogs\EventLogs.csv");

        FluentActions.Invoking(() => _ops.Rename(outside, "x.csv")).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Delete(new[] { outside })).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Copy(inside, _root)).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Move(inside, _bin)).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Rename(Path.Combine(_case, @"HOST1\..\..\outside.csv"), "y.csv")).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Delete(new[] { _case })).Should().Throw<CaseFileOperationException>();
        File.Exists(outside).Should().BeTrue();
        _recycled.Should().BeEmpty();
    }

    [Fact]
    public void Refuses_ManifestsCaseMarkerLogsAndStaging()
    {
        var manifest = Write("EzHub_manifest_20261010.json", "{}");
        var marker = Write("case.json", "{}");
        var log = Write(@"logs\job1.log", "x");
        var staged = Write(@".ezstaging\HOST1_mft_abcd1234\MFT.csv");
        var csv = Write(@"HOST1\FileSystem\MFT.csv");

        FluentActions.Invoking(() => _ops.Delete(new[] { manifest })).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Rename(marker, "other.json")).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Delete(new[] { Path.Combine(_case, "logs") })).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Delete(new[] { log })).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Move(staged, Path.Combine(_case, "HOST1"))).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Copy(csv, Path.Combine(_case, "logs"))).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Copy(csv, Path.Combine(_case, ".ezstaging"))).Should().Throw<CaseFileOperationException>();
        // a sidecar on its own
        var flags = RowFlagStore.SidecarPathFor(csv);
        File.WriteAllText(flags, "{}");
        FluentActions.Invoking(() => _ops.Delete(new[] { flags })).Should().Throw<CaseFileOperationException>();
        // a folder cannot take a manifest name; a CSV keeps .csv, so it can never become one
        Directory.CreateDirectory(Path.Combine(_case, "Notes"));
        FluentActions.Invoking(() => _ops.Rename(Path.Combine(_case, "Notes"), "EzHub_manifest_x.json")).Should().Throw<CaseFileOperationException>();
        _ops.Rename(csv, "EzHub_manifest_x.json").Should().EndWith("EzHub_manifest_x.json.csv");
        _recycled.Should().BeEmpty();
        File.Exists(manifest).Should().BeTrue();
        File.Exists(log).Should().BeTrue();
    }

    [Fact]
    public void Refuses_FolderIntoItself_AndChecksAllBeforeDeleting()
    {
        var csv = Write(@"HOST1\EventLogs\EventLogs.csv");
        var host = Path.Combine(_case, "HOST1");
        FluentActions.Invoking(() => _ops.Move(host, Path.Combine(host, "EventLogs"))).Should().Throw<CaseFileOperationException>();
        FluentActions.Invoking(() => _ops.Copy(host, host)).Should().Throw<CaseFileOperationException>();
        var manifest = Write("EzHub_manifest_1.json", "{}");
        FluentActions.Invoking(() => _ops.Delete(new[] { csv, manifest })).Should().Throw<CaseFileOperationException>();
        File.Exists(csv).Should().BeTrue("nothing is deleted when one of the items is refused");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a/b.csv", false)]
    [InlineData("a:b", false)]
    [InlineData("CON", false)]
    [InlineData("name.", false)]
    [InlineData("case.json", false)]
    [InlineData("x.ezflags.json", false)]
    [InlineData("Fine name", true)]
    public void ValidateName_Rules(string name, bool ok)
    {
        var act = () => CaseFileOperations.ValidateName(name, false, false);
        if (ok) act.Should().NotThrow(); else act.Should().Throw<CaseFileOperationException>();
    }

    [Fact]
    public void ValidateName_CsvKeepsCsv_DotFoldersRefused()
    {
        CaseFileOperations.ValidateName("Renamed", true, false).Should().Be("Renamed.csv");
        CaseFileOperations.ValidateName("Renamed.txt", true, false).Should().Be("Renamed.txt.csv");
        CaseFileOperations.ValidateName("Renamed.CSV", true, false).Should().Be("Renamed.CSV");
        FluentActions.Invoking(() => CaseFileOperations.ValidateName(".hidden", false, true)).Should().Throw<CaseFileOperationException>();
    }

    // ------------------------------------------------------------ sidecars / collisions

    [Fact]
    public void Rename_MovesSidecars_AndLogs()
    {
        var csv = WriteCsvWithSidecars(@"HOST1\EventLogs\EventLogs.csv");
        var other = csv + ".notes"; // another <csv>.* helper file
        File.WriteAllText(other, "n");

        var result = _ops.Rename(csv, "Security");

        result.Should().Be(Path.Combine(_case, @"HOST1\EventLogs\Security.csv"));
        File.Exists(result).Should().BeTrue();
        File.Exists(csv).Should().BeFalse();
        File.Exists(RowFlagStore.SidecarPathFor(result)).Should().BeTrue();
        File.Exists(FileStatsCache.SidecarPathFor(result)).Should().BeTrue();
        File.Exists(result + ".notes").Should().BeTrue();
        File.Exists(RowFlagStore.SidecarPathFor(csv)).Should().BeFalse();
        File.Exists(FileStatsCache.SidecarPathFor(csv)).Should().BeFalse();

        var lines = LogLines();
        lines.Should().HaveCount(4);
        var cols = lines[0].Split('\t');
        cols.Should().HaveCount(6);
        cols[0].Should().Be("2026-10-10T12:00:00.000Z");
        cols[1].Should().Be("tester");
        cols[2].Should().Be("RENAME");
        cols[3].Should().Be(csv);
        cols[4].Should().Be(result);
    }

    [Fact]
    public void Rename_CollisionGetsSuffix_NeverOverwrites()
    {
        var a = Write(@"H\EventLogs\A.csv", "A\r\n1\r\n");
        var b = Write(@"H\EventLogs\B.csv", "B\r\n2\r\n");
        var r = _ops.Rename(a, "B.csv");
        r.Should().Be(Path.Combine(_case, @"H\EventLogs\B_2.csv"));
        File.ReadAllText(b).Should().StartWith("B");
        File.ReadAllText(r).Should().StartWith("A");
    }

    [Fact]
    public void Rename_CollisionWithOrphanSidecar_GetsSuffix()
    {
        var a = WriteCsvWithSidecars(@"H\X\A.csv");
        var orphan = Path.Combine(_case, @"H\X\B.ezflags.json");
        File.WriteAllText(orphan, "orphan");
        var r = _ops.Rename(a, "B.csv");
        r.Should().EndWith("B_2.csv");
        File.ReadAllText(orphan).Should().Be("orphan");
        File.Exists(Path.Combine(_case, @"H\X\B_2.ezflags.json")).Should().BeTrue();
    }

    [Fact]
    public void Rename_CaseOnlyChange()
    {
        var a = Write(@"H\X\events.csv");
        var r = _ops.Rename(a, "Events.csv");
        Path.GetFileName(r).Should().Be("Events.csv");
        Directory.GetFiles(Path.GetDirectoryName(a)!).Select(Path.GetFileName).Should().Contain("Events.csv");
    }

    [Fact]
    public void Copy_CopiesSidecars_HashesInLog_AndNumbers()
    {
        var csv = WriteCsvWithSidecars(@"HOST1\EventLogs\EventLogs.csv");
        var dest = Path.Combine(_case, @"HOST1\Other");
        Directory.CreateDirectory(dest);

        var c1 = _ops.Copy(csv, dest);
        var c2 = _ops.Copy(csv, dest);

        c1.Should().Be(Path.Combine(dest, "EventLogs.csv"));
        c2.Should().Be(Path.Combine(dest, "EventLogs_2.csv"));
        File.Exists(RowFlagStore.SidecarPathFor(c2)).Should().BeTrue();
        File.Exists(FileStatsCache.SidecarPathFor(c2)).Should().BeTrue();
        File.Exists(csv).Should().BeTrue();
        var line = LogLines().First(l => l.Contains("\tCOPY\t") && l.EndsWith(Sha(csv)));
        line.Split('\t')[3].Should().Be(csv);
    }

    [Fact]
    public void Duplicate_FileAndFolder()
    {
        var csv = Write(@"H\X\Prefetch_2.csv");
        _ops.Duplicate(csv).Should().EndWith("Prefetch_3.csv");
        var folder = Path.Combine(_case, @"H\X");
        var dup = _ops.Duplicate(folder);
        dup.Should().Be(Path.Combine(_case, @"H\X_2"));
        File.Exists(Path.Combine(dup, "Prefetch_2.csv")).Should().BeTrue();
        File.Exists(Path.Combine(dup, "Prefetch_3.csv")).Should().BeTrue();
    }

    [Fact]
    public void Move_FileWithSidecars_AndFolder()
    {
        var csv = WriteCsvWithSidecars(@"H\A\X.csv");
        var dest = Path.Combine(_case, @"H\B");
        Directory.CreateDirectory(dest);
        var moved = _ops.Move(csv, dest);
        moved.Should().Be(Path.Combine(dest, "X.csv"));
        File.Exists(csv).Should().BeFalse();
        File.Exists(RowFlagStore.SidecarPathFor(moved)).Should().BeTrue();

        var movedFolder = _ops.Move(Path.Combine(_case, @"H\A"), dest);
        movedFolder.Should().Be(Path.Combine(dest, "A"));
        Directory.Exists(Path.Combine(_case, @"H\A")).Should().BeFalse();
        LogLines().Should().Contain(l => l.Contains("\tMOVE\t"));
    }

    [Fact]
    public void CreateFolder_Numbers_AndRefusesCaseLevelNames()
    {
        var f1 = _ops.CreateFolder(_case);
        var f2 = _ops.CreateFolder(_case);
        Path.GetFileName(f1).Should().Be("New folder");
        Path.GetFileName(f2).Should().Be("New folder_2");
        FluentActions.Invoking(() => _ops.CreateFolder(_case, "logs")).Should().Throw<CaseFileOperationException>();
        LogLines().Should().Contain(l => l.Contains("\tNEWFOLDER\t"));
    }

    [Fact]
    public void Delete_RecyclesCsvAndSidecars_LogsHashes()
    {
        var csv = WriteCsvWithSidecars(@"H\X\A.csv");
        var hash = Sha(csv);
        var folder = Path.Combine(_case, @"H\Y");
        var inner = Write(@"H\Y\B.csv");
        var innerHash = Sha(inner);

        _ops.Delete(new[] { csv, folder, inner });

        File.Exists(csv).Should().BeFalse();
        File.Exists(RowFlagStore.SidecarPathFor(csv)).Should().BeFalse();
        File.Exists(FileStatsCache.SidecarPathFor(csv)).Should().BeFalse();
        Directory.Exists(folder).Should().BeFalse();
        _recycled.Should().HaveCount(4); // csv + 2 sidecars + folder (its file goes with it)
        _recycled.Should().Contain((folder, true));
        var lines = LogLines();
        lines.Should().Contain(l => l.Contains("\tDELETE\t" + csv + "\t-\t" + hash));
        lines.Should().Contain(l => l.Contains("\tDELETE\t" + inner + "\t-\t" + innerHash));
    }

    // ------------------------------------------------------------ merge / derived outputs

    [Fact]
    public async Task Merge_RecordsDerivedOutput_SanitisesName_InsideCaseOnly()
    {
        var a = Write(@"HOST1\EventLogs\EventLogs.csv", "A,B\r\n1,2\r\n");
        var b = Write(@"HOST2\EventLogs\EventLogs.csv", "A,B\r\n3,4\r\n");
        var target = Path.Combine(_case, @"Merged\Merged\my merged (2).csv");

        var (path, result) = await _ops.MergeAsync(new[] { new CsvMergeInput(a, "HOST1"), new CsvMergeInput(b, "HOST2") }, target);

        Path.GetFileName(path).Should().Be("My_merged_2.csv");
        result.RowsWritten.Should().Be(2);
        var recs = CaseFileOperations.ReadDerivedOutputs(_case);
        recs.Should().HaveCount(1);
        recs[0].Output.Should().Be(@"Merged\Merged\My_merged_2.csv");
        recs[0].OutputSha256.Should().Be(Sha(path));
        recs[0].Inputs.Select(i => i.Sha256).Should().Equal(Sha(a), Sha(b));
        recs[0].Inputs.Select(i => i.Host).Should().Equal("HOST1", "HOST2");
        recs[0].User.Should().Be("tester");
        recs[0].CreatedUtc.Should().Be(Now);
        LogLines().Should().Contain(l => l.Contains("\tMERGE\t"));

        // second merge to the same name: numbered, appended to the record file
        var (path2, _) = await _ops.MergeAsync(new[] { new CsvMergeInput(a), new CsvMergeInput(b) }, target);
        Path.GetFileName(path2).Should().Be("My_merged_3.csv");
        CaseFileOperations.ReadDerivedOutputs(_case).Should().HaveCount(2);

        await FluentActions.Awaiting(() => _ops.MergeAsync(new[] { new CsvMergeInput(a) }, Path.Combine(_root, "out.csv")))
            .Should().ThrowAsync<CaseFileOperationException>();
    }

    private static DerivedOutputRecord Rec(string output) => new() { Output = output, OutputSha256 = new string('a', 64) };

    [Fact]
    public void A_corrupt_derived_outputs_file_is_kept_as_a_backup_not_overwritten()
    {
        var logs = Path.Combine(_case, "logs");
        Directory.CreateDirectory(logs);
        const string corrupt = "[{\"kind\":\"merge\",\"output\":\"Merged\\\\Merged_1.csv\",\"outputSha256\":\"ab";   // truncated
        File.WriteAllText(_ops.DerivedOutputsPath, corrupt);

        _ops.RecordDerivedOutput(Rec(@"Merged\Merged_2.csv"));

        var backup = Path.Combine(logs, "derived_outputs.corrupt-20261010T120000Z.json");
        File.ReadAllText(backup).Should().Be(corrupt);
        CaseFileOperations.ReadDerivedOutputs(_case).Select(r => r.Output).Should().Equal(@"Merged\Merged_2.csv");
        LogLines().Should().Contain(l => l.Contains("\tDERIVED-BACKUP\t") && l.Contains("derived_outputs.corrupt-20261010T120000Z.json"));

        // a second corrupt file the same second gets its own backup name
        File.WriteAllText(_ops.DerivedOutputsPath, "not json");
        _ops.RecordDerivedOutput(Rec(@"Merged\Merged_3.csv"));
        File.ReadAllText(Path.Combine(logs, "derived_outputs.corrupt-20261010T120000Z_2.json")).Should().Be("not json");
        File.ReadAllText(backup).Should().Be(corrupt);
    }

    [Fact]
    public async Task Concurrent_derived_output_records_are_all_kept()
    {
        var other = new CaseFileOperations(_case, Recycle, "tester2", () => Now);
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() => (i % 2 == 0 ? _ops : other).RecordDerivedOutput(Rec($"Merged\\Merged_{i}.csv"))));
        await Task.WhenAll(tasks);
        CaseFileOperations.ReadDerivedOutputs(_case).Should().HaveCount(40);
        Directory.EnumerateFiles(Path.Combine(_case, "logs"), "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void DefaultMergePath_HostOrMerged_FirstFreeNumber()
    {
        CaseFileOperations.DefaultMergePath(_case, "HOST1").Should().Be(Path.Combine(_case, @"HOST1\Merged\Merged_1.csv"));
        Write(@"Merged\Merged\Merged_1.csv");
        CaseFileOperations.DefaultMergePath(_case, null).Should().Be(Path.Combine(_case, @"Merged\Merged\Merged_2.csv"));
    }

    [Theory]
    [InlineData("my merged (2).csv", "My_merged_2.csv")]
    [InlineData("Merged_1.csv", "Merged_1.csv")]
    [InlineData("2024 run", "Merged_2024_run.csv")]
    [InlineData("!!!", "Merged.csv")]
    [InlineData("EventLogs_All", "EventLogs_All.csv")]
    public void SanitizeOutputName_PassesLayoutValidator(string input, string expected)
    {
        var s = CaseFileOperations.SanitizeOutputName(input);
        s.Should().Be(expected);
        Platforms.ArtifactOutputLayout.IsValidOutputName(s).Should().BeTrue();
    }

    [Fact]
    public void CombinedCache_KeyStable_HiddenFolder_Prune()
    {
        var a = Write(@"H\X\A.csv");
        var b = Write(@"H\X\B.csv");
        var k1 = CaseFileOperations.CombinedKey(new[] { a, b });
        CaseFileOperations.CombinedKey(new[] { a, b }).Should().Be(k1);
        CaseFileOperations.CombinedKey(new[] { b, a }).Should().NotBe(k1);
        var dir = CaseFileOperations.EnsureCombinedCacheDir(_case);
        dir.Should().Be(CaseFileOperations.CombinedCacheDir(_case));
        (new DirectoryInfo(Path.Combine(_case, ".ezcache")).Attributes & FileAttributes.Hidden).Should().Be(FileAttributes.Hidden);
        for (int i = 0; i < 4; i++)
        {
            var p = Path.Combine(dir, $"k{i}.csv");
            File.WriteAllText(p, "A\r\n");
            File.SetLastWriteTimeUtc(p, Now.AddMinutes(i));
            File.SetLastAccessTimeUtc(p, Now.AddMinutes(i));
        }
        CaseFileOperations.PruneCombinedCache(_case, 2);
        Directory.GetFiles(dir, "*.csv").Select(Path.GetFileName).Should().BeEquivalentTo("k2.csv", "k3.csv");
        // operations refuse the cache folder
        FluentActions.Invoking(() => _ops.Delete(new[] { Path.Combine(dir, "k3.csv") })).Should().Throw<CaseFileOperationException>();
    }

    // ------------------------------------------------------------ recycle safety

    [Fact]
    public void RecycleRefusal_NetworkAndRemovableVolumes()
    {
        CaseFileOperations.RecycleRefusal(@"\\nas\evidence\Case1").Should().Contain("network share");
        CaseFileOperations.RecycleRefusal(@"E:\Case1", _ => DriveType.Removable).Should().Contain("removable");
        CaseFileOperations.RecycleRefusal(@"Z:\Case1", _ => DriveType.Network).Should().Contain("network");
        CaseFileOperations.RecycleRefusal(@"D:\Case1", _ => DriveType.Fixed).Should().BeNull();
        CaseFileOperations.RecycleRefusal(_case).Should().BeNull("the temp folder is on a fixed drive");
    }

    [Fact]
    public void Delete_FailureMidway_ReportsWhatWasDone_AndLogsOnlyRecycledItems()
    {
        var a = Write("H\\a.csv");
        var b = Write("H\\b.csv");
        int calls = 0;
        var ops = new CaseFileOperations(_case, (p, d) =>
        {
            if (++calls == 2) throw new IOException("aborted by the user");
            Recycle(p, d);
        }, "tester", () => Now);
        FluentActions.Invoking(() => ops.Delete(new[] { a, b })).Should().Throw<CaseFileOperationException>()
            .WithMessage("*after 1 of 2*aborted by the user*");
        File.Exists(a).Should().BeFalse();
        File.Exists(b).Should().BeTrue();
        var deletes = LogLines().Where(l => l.Split('\t')[2] == "DELETE").ToList();
        deletes.Should().ContainSingle().Which.Should().Contain("a.csv");
    }

    // ------------------------------------------------------------ links (junctions) and roots

    /// <summary>Creates a directory junction (no admin needed); false when the platform / file system cannot.</summary>
    private static bool TryJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && CaseFileOperations.IsReparsePoint(link);
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void Junctions_are_never_followed()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "evidence.csv");
        File.WriteAllText(victim, "A\r\n1\r\n");
        var link = Path.Combine(_case, "H", "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TryJunction(link, outside)) return;   // junctions unavailable here: nothing to test
        try
        {
            var through = Path.Combine(link, "evidence.csv");
            Write("H\\a.csv");
            FluentActions.Invoking(() => _ops.Delete(new[] { through })).Should().Throw<CaseFileOperationException>().WithMessage("*link*");
            FluentActions.Invoking(() => _ops.Rename(through, "x.csv")).Should().Throw<CaseFileOperationException>();
            FluentActions.Invoking(() => _ops.Delete(new[] { link })).Should().Throw<CaseFileOperationException>();
            FluentActions.Invoking(() => _ops.Delete(new[] { Path.Combine(_case, "H") })).Should().Throw<CaseFileOperationException>().WithMessage("*contains a link*");
            FluentActions.Invoking(() => _ops.Copy(Path.Combine(_case, "H", "a.csv"), link)).Should().Throw<CaseFileOperationException>();
            FluentActions.Invoking(() => _ops.Copy(Path.Combine(_case, "H"), _case)).Should().Throw<CaseFileOperationException>().WithMessage("*contains a link*");
            FluentActions.Invoking(() => _ops.CreateFolder(link)).Should().Throw<CaseFileOperationException>();
            _recycled.Should().BeEmpty();
            File.Exists(victim).Should().BeTrue("nothing outside the case is touched");
            Directory.GetFileSystemEntries(outside).Should().ContainSingle();
        }
        finally { try { Directory.Delete(link); } catch { } }   // removes the junction only
    }

    [Fact]
    public void Case_at_a_drive_root_keeps_its_separator()
    {
        var drive = Path.GetPathRoot(_case)!;   // e.g. C:\  (nothing is written: paths only)
        var ops = new CaseFileOperations(drive, Recycle, "tester", () => Now);
        ops.CaseDir.Should().Be(drive);
        ops.LogPath.Should().Be(Path.Combine(drive, "logs", CaseFileOperations.LogFileName));
        Path.IsPathFullyQualified(ops.LogPath).Should().BeTrue();
        ops.IsInsideCase(Path.Combine(drive, "HOST", "x.csv")).Should().BeTrue();
        ops.IsInsideCase(drive).Should().BeFalse();
        ops.Relative(Path.Combine(drive, "HOST")).Should().Be("HOST");
    }

    [Fact]
    public void Move_and_copy_refuse_case_level_folder_names_at_the_case_root()
    {
        var timeline = Path.Combine(_case, "H", "Timeline");
        Directory.CreateDirectory(timeline);
        File.WriteAllText(Path.Combine(timeline, "t.csv"), "A\r\n");
        FluentActions.Invoking(() => _ops.Move(timeline, _case)).Should().Throw<CaseFileOperationException>().WithMessage("*case-level*");
        FluentActions.Invoking(() => _ops.Copy(timeline, _case)).Should().Throw<CaseFileOperationException>().WithMessage("*case-level*");
        Directory.Exists(timeline).Should().BeTrue();
        // elsewhere the name is fine
        Directory.CreateDirectory(Path.Combine(_case, "Other"));
        _ops.Copy(timeline, Path.Combine(_case, "Other")).Should().Be(Path.Combine(_case, "Other", "Timeline"));
        // a file may keep its name at the root
        var f = Write("H\\Timeline.csv");
        _ops.Move(f, _case).Should().Be(Path.Combine(_case, "Timeline.csv"));
    }

    [Fact]
    public void Delete_FirstItemFails_NothingLogged()
    {
        var a = Write("H\\a.csv");
        var ops = new CaseFileOperations(_case, (_, _) => throw new CaseFileOperationException("Delete was cancelled"), "tester", () => Now);
        FluentActions.Invoking(() => ops.Delete(new[] { a })).Should().Throw<CaseFileOperationException>().WithMessage("*cancelled*");
        File.Exists(a).Should().BeTrue();
        LogLines().Should().BeEmpty();
    }
}
