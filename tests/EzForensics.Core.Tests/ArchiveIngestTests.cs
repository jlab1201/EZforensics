using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Archives;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public sealed class ArchiveIngestTests : IDisposable
{
    private const string Pw = ArchiveEngineTests.Pw;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ezarch-ing-" + Guid.NewGuid().ToString("N"));
    private readonly string _case;
    private readonly List<ArchivePasswordRequest> _prompts = new();

    public ArchiveIngestTests()
    {
        Directory.CreateDirectory(_tmp);
        _case = Path.Combine(_tmp, "case");
        Directory.CreateDirectory(_case);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private Func<ArchivePasswordRequest, Task<string?>> Answers(params string?[] answers)
    {
        var q = new Queue<string?>(answers);
        return req => { _prompts.Add(req); return Task.FromResult(q.Count > 0 ? q.Dequeue() : null); };
    }

    private static readonly Func<ArchivePasswordRequest, Task<string?>> NoPrompt =
        _ => throw new InvalidOperationException("password prompt not expected");

    private Task<ArchiveIngestResult> Run(string archive, Func<ArchivePasswordRequest, Task<string?>>? provider = null, ArchiveIngestOptions? options = null,
        IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
        ArchiveIngest.PrepareAsync(archive, _case, provider ?? NoPrompt, progress, ct, options);

    private string Zip(string name, ZipTestWriter w) { var p = Path.Combine(_tmp, name); w.Save(p); return p; }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ExtractedRoot(ArchiveIngestResult r) => r.Origin.ExtractedTo;

    // ------------------------------------------------------------------ plain zip, record, reuse

    [Fact]
    public async Task Plain_zip_is_extracted_with_record_hashes_and_reused()
    {
        var zip = Zip("Evidence Set.zip", new ZipTestWriter().Add("a.txt", "alpha").AddDirectory("sub").Add("sub/b.txt", "bravo"));
        var before = File.GetLastWriteTimeUtc(zip);
        var archiveSha = Sha(zip);

        var r = await Run(zip);

        var root = ExtractedRoot(r);
        root.Should().Be(Path.Combine(_case, ".ezstaging", "extracted", archiveSha[..8], "Evidence Set"));
        r.ExtractedRoots.Should().Equal(root);
        File.ReadAllText(Path.Combine(root, "a.txt")).Should().Be("alpha");
        File.ReadAllText(Path.Combine(root, "sub", "b.txt")).Should().Be("bravo");
        r.Origin.Sha256.Should().Be(archiveSha);
        r.Origin.Chain.Should().Equal(Path.GetFullPath(zip));
        r.Origin.TimestampCoverage.Should().Be(TimestampCoverage.DosLocal);

        var rec = ExtractionRecord.Load(root)!;
        rec.Complete.Should().BeTrue();
        rec.Sha256.Should().Be(archiveSha);
        rec.Format.Should().Be(ArchiveFormat.Zip);
        rec.Engine.Should().Be("SharpCompress");
        var a = rec.Entries.Single(e => e.OriginalName == "a.txt");
        a.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("alpha"))).ToLowerInvariant());
        a.CrcOk.Should().BeTrue();
        a.TimesRestored.Should().BeTrue();
        a.TimeSource.Should().Be("DosLocal");
        File.GetLastWriteTimeUtc(Path.Combine(root, "a.txt")).Should().Be(DateTime.SpecifyKind(new DateTime(2020, 1, 2, 3, 4, 6), DateTimeKind.Local).ToUniversalTime());

        // the archive itself is untouched
        File.GetLastWriteTimeUtc(zip).Should().Be(before);
        Sha(zip).Should().Be(archiveSha);

        var again = await Run(zip);
        ExtractedRoot(again).Should().Be(root);
        again.Warnings.Should().Contain(w => w.StartsWith("Reused"));
        ExtractionRecord.Load(root)!.ExtractedUtc.Should().Be(rec.ExtractedUtc);
    }

    [Fact]
    public async Task Incomplete_extraction_is_wiped_and_redone()
    {
        var zip = Zip("x.zip", new ZipTestWriter().Add("a.txt", "alpha"));
        var folder = ArchiveIngest.ExtractionFolder(_case, Sha(zip), zip);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "stale.txt"), "old");
        new ExtractionRecord { Sha256 = Sha(zip), Complete = false }.Save(folder);

        var r = await Run(zip);
        File.Exists(Path.Combine(folder, "stale.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(ExtractedRoot(r), "a.txt")).Should().Be("alpha");
        ExtractionRecord.Load(folder)!.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task Progress_reports_phases_in_order()
    {
        var zip = Zip("p.zip", new ZipTestWriter().Add("a.txt", "alpha"));
        var phases = new List<ArchivePhase>();
        await Run(zip, progress: new SyncProgress(p => { if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase); }));
        phases.Should().Equal(ArchivePhase.Hashing, ArchivePhase.Inspecting, ArchivePhase.Extracting, ArchivePhase.RestoringTimes);
    }

    // ------------------------------------------------------------------ password flow

    [Fact]
    public async Task Password_prompt_is_called_once_with_the_right_password()
    {
        var r = await Run(ArchiveEngineTests.Fx("zip-zipcrypto/sample.zip"), Answers(Pw));
        _prompts.Should().ContainSingle();
        _prompts[0].WrongPassword.Should().BeFalse();
        _prompts[0].Format.Should().Be(ArchiveFormat.Zip);
        _prompts[0].EncryptionMethod.Should().Be("ZipCrypto");
        File.ReadAllText(Path.Combine(ExtractedRoot(r), "a.txt")).Replace("\r\n", "\n").Should().Be("hello secret one\n");
        r.Origin.Encrypted.Should().BeTrue();
        r.Origin.EncryptionMethod.Should().Be("ZipCrypto");
        AssertPasswordNotPersisted(ExtractedRoot(r), Pw);
    }

    [Fact]
    public async Task Wrong_password_reprompts_with_the_incorrect_flag()
    {
        var r = await Run(ArchiveEngineTests.Fx("zip-aes256/sample.zip"), Answers("nope-1", "nope-2", Pw));
        _prompts.Select(p => p.WrongPassword).Should().Equal(false, true, true);
        File.ReadAllText(Path.Combine(ExtractedRoot(r), "sub", "b.txt")).Replace("\r\n", "\n").Should().Be("second file contents 12345\n");
        AssertPasswordNotPersisted(ExtractedRoot(r), Pw);
    }

    [Fact]
    public async Task Cancel_at_the_prompt_cancels_cleanly_and_leaves_nothing()
    {
        var fx = ArchiveEngineTests.Fx("zip-zipcrypto/sample.zip");
        var act = () => Run(fx, Answers("wrong-Secret-77", null));
        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.ToString().Should().NotContain("wrong-Secret-77");
        _prompts.Should().HaveCount(2);
        Directory.Exists(ArchiveIngest.ExtractionFolder(_case, Sha(fx), fx)).Should().BeFalse();
    }

    [Theory]
    [InlineData("7z-aes-enchdr/sample.7z", Pw, "a.txt")]
    [InlineData("7z-aes-plainhdr/sample.7z", Pw, "a.txt")]
    [InlineData("rar-Rar.encrypted_filesAndHeader/Rar.encrypted_filesAndHeader.rar", ArchiveEngineTests.RarPw, @"jpg\test.jpg")]
    [InlineData("rar-Rar5.encrypted_filesAndHeader/Rar5.encrypted_filesAndHeader.rar", ArchiveEngineTests.RarPw, @"exe\test.exe")]
    [InlineData("rar-Rar5.encrypted_filesOnly/Rar5.encrypted_filesOnly.rar", ArchiveEngineTests.RarPw, "тест.txt")]
    public async Task Encrypted_fixtures_extract_after_one_prompt(string rel, string pw, string expectedFile)
    {
        var r = await Run(ArchiveEngineTests.Fx(rel), Answers(pw));
        _prompts.Should().ContainSingle();
        File.Exists(Path.Combine(ExtractedRoot(r), expectedFile)).Should().BeTrue();
        var rec = ExtractionRecord.Load(ExtractedRoot(r))!;
        rec.Encrypted.Should().BeTrue();
        rec.Entries.Where(e => e.ExtractedPath.Length > 0 && e.Sha256 != null).Should().OnlyContain(e => e.CrcOk);
        AssertPasswordNotPersisted(ExtractedRoot(r), pw);
    }

    [Fact]
    public async Task Session_password_is_tried_before_prompting()
    {
        var session = new ArchivePasswordSession();
        session.Remember(Pw);
        await Run(ArchiveEngineTests.Fx("7z-aes-plainhdr/sample.7z"), Answers(), new ArchiveIngestOptions { Passwords = session });
        _prompts.Should().BeEmpty();
        session.ToString().Should().NotContain(Pw);
    }

    [Fact]
    public async Task Generated_zipcrypto_archive_round_trips()
    {
        var zip = Zip("zc.zip", new ZipTestWriter().Add("note.txt", "classified", e => e.ZipCryptoPassword = "Gen-Pass-9"));
        var r = await Run(zip, Answers("bad", "Gen-Pass-9"));
        File.ReadAllText(Path.Combine(ExtractedRoot(r), "note.txt")).Should().Be("classified");
        _prompts.Select(p => p.WrongPassword).Should().Equal(false, true);
        AssertPasswordNotPersisted(ExtractedRoot(r), "Gen-Pass-9");
    }

    private static void AssertPasswordNotPersisted(string root, string pw)
    {
        var json = File.ReadAllText(Path.Combine(root, ExtractionRecord.FileName));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Strings(doc.RootElement).Should().NotContain(pw);
        if (pw.Length >= 8) json.Should().NotContain(pw);
    }

    private static IEnumerable<string> Strings(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => new[] { e.GetString()! },
        System.Text.Json.JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value).Append(p.Name)),
        System.Text.Json.JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        _ => Array.Empty<string>(),
    };

    // ------------------------------------------------------------------ path safety, timestamps, links

    [Fact]
    public async Task Hostile_names_are_sanitised_and_recorded()
    {
        var zip = Zip("hostile.zip", new ZipTestWriter()
            .Add("../escape.txt", "x")
            .Add("/abs/root.txt", "r")
            .Add("C:/Windows/win.ini", "w")
            .Add("doc.txt:Zone.Identifier", "[ZoneTransfer]")
            .Add("NUL.txt", "nul")
            .Add("trail. ", "t")
            .Add("$MFT", "mft")
            .Add("Case.txt", "upper")
            .Add("case.txt", "lower")
            .Add("evil-link", "/etc/passwd", e => { e.UnixSymlink = true; e.Deflate = false; }));

        var r = await Run(zip);
        var root = ExtractedRoot(r);
        File.Exists(Path.Combine(_case, ".ezstaging", "extracted", "escape.txt")).Should().BeFalse();
        Directory.EnumerateFiles(_tmp, "escape.txt", SearchOption.AllDirectories).Should().BeEmpty();
        File.ReadAllText(Path.Combine(root, "abs", "root.txt")).Should().Be("r");
        File.ReadAllText(Path.Combine(root, "Windows", "win.ini")).Should().Be("w");
        File.ReadAllText(Path.Combine(root, "doc.txt%3AZone.Identifier")).Should().Be("[ZoneTransfer]");
        File.ReadAllText(Path.Combine(root, "%4EUL.txt")).Should().Be("nul");
        File.Exists(Path.Combine(root, "trail%2E%20")).Should().BeTrue();
        File.ReadAllText(Path.Combine(root, "$MFT")).Should().Be("mft");
        File.ReadAllText(Path.Combine(root, "Case.txt")).Should().Be("upper");
        File.ReadAllText(Path.Combine(root, "case~2.txt")).Should().Be("lower");
        File.Exists(Path.Combine(root, "evil-link")).Should().BeFalse();

        var rec = ExtractionRecord.Load(root)!;
        rec.Entries.Single(e => e.OriginalName == "../escape.txt").Reason.Should().Contain("traversal");
        rec.Entries.Single(e => e.OriginalName == "../escape.txt").ExtractedPath.Should().BeEmpty();
        rec.Entries.Single(e => e.OriginalName == "evil-link").Reason.Should().Contain("link");
        rec.Entries.Single(e => e.OriginalName == "case.txt").Reason.Should().Contain("~N");
        rec.Entries.Single(e => e.OriginalName == "NUL.txt").Reason.Should().Contain("percent-encoded");
        rec.Entries.Single(e => e.OriginalName == "$MFT").Reason.Should().BeNull();
        r.Warnings.Should().Contain(w => w.Contains("../escape.txt"));
    }

    [Fact]
    public async Task Ntfs_extra_field_restores_created_accessed_and_modified()
    {
        var m = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var a = new DateTime(2019, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var c = new DateTime(2018, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var zip = Zip("ntfs.zip", new ZipTestWriter().Add("evt.evtx", "data", e => e.Extra = ZipTestWriter.NtfsExtra(m, a, c)));
        var r = await Run(zip);
        var f = Path.Combine(ExtractedRoot(r), "evt.evtx");
        File.GetLastWriteTimeUtc(f).Should().Be(m);
        File.GetCreationTimeUtc(f).Should().Be(c);
        File.GetLastAccessTimeUtc(f).Should().Be(a);
        r.Origin.TimestampCoverage.Should().Be(TimestampCoverage.All);
        ExtractionRecord.Load(ExtractedRoot(r))!.Entries.Single().TimeSource.Should().Be("NtfsExtra");
    }

    [Fact]
    public async Task Ntfs_times_fixture_restores_created_time()
    {
        var fx = ArchiveEngineTests.Fx("zip-ntfs-times/sample.zip");
        var expected = new SharpCompressArchiveEngine().Inspect(fx).Entries.Single(e => e.Name == "a.txt");
        var r = await Run(fx);
        File.GetCreationTimeUtc(Path.Combine(ExtractedRoot(r), "a.txt")).Should().Be(expected.CreatedUtc!.Value);
    }

    [Fact]
    public async Task Directory_times_are_restored_bottom_up()
    {
        var dm = new DateTime(2017, 7, 7, 7, 7, 8, DateTimeKind.Utc);
        var zip = Zip("dirs.zip", new ZipTestWriter()
            .Add("top/", Array.Empty<byte>(), e => { e.IsDirectory = true; e.Deflate = false; e.Extra = ZipTestWriter.NtfsExtra(dm, dm, dm); })
            .Add("top/inner.txt", "i"));
        var r = await Run(zip);
        Directory.GetLastWriteTimeUtc(Path.Combine(ExtractedRoot(r), "top")).Should().Be(dm);
    }

    [Fact]
    public async Task Lying_sizes_never_write_more_than_declared()
    {
        var big = Enumerable.Repeat((byte)'A', 100_000).ToArray();
        var zip = Zip("lying.zip", new ZipTestWriter().Add("big.bin", big, e => e.DeclaredSize = 10).Add("ok.txt", "fine"));
        var r = await Run(zip);
        var root = ExtractedRoot(r);
        var f = Path.Combine(root, "big.bin");
        (File.Exists(f) ? new FileInfo(f).Length : 0).Should().BeLessThanOrEqualTo(10);
        File.ReadAllText(Path.Combine(root, "ok.txt")).Should().Be("fine");
    }

    [Fact]
    public async Task Zip_bomb_against_small_caps_is_stopped_and_cleaned_up()
    {
        var zip = Zip("bomb.zip", new ZipTestWriter()
            .Add("b1.bin", new byte[200_000]).Add("b2.bin", new byte[200_000]).Add("b3.bin", new byte[200_000]));
        var limits = new ArchiveLimits { MaxTotalOutputBytes = 300_000, FreeSpaceMarginBytes = 0 };
        var act = () => Run(zip, options: new ArchiveIngestOptions { Limits = limits });
        await act.Should().ThrowAsync<ArchiveLimitExceededException>();
        Directory.Exists(ArchiveIngest.ExtractionFolder(_case, Sha(zip), zip)).Should().BeFalse();
    }

    [Fact]
    public async Task Too_many_entries_is_refused()
    {
        var w = new ZipTestWriter();
        for (int i = 0; i < 20; i++) w.Add($"f{i}.txt", "x");
        var zip = Zip("many.zip", w);
        var act = () => Run(zip, options: new ArchiveIngestOptions { Limits = new ArchiveLimits { MaxEntries = 10 } });
        await act.Should().ThrowAsync<ArchiveLimitExceededException>();
    }

    [Fact]
    public async Task High_compression_ratio_asks_the_user()
    {
        var zip = Zip("ratio.zip", new ZipTestWriter().Add("mem.raw", new byte[2_000_000]));
        var asked = new List<ArchiveConfirmRequest>();
        var limits = new ArchiveLimits { PromptRatio = 50, PromptRatioMinBytes = 1000 };
        var act = () => Run(zip, options: new ArchiveIngestOptions { Limits = limits, Confirm = q => { asked.Add(q); return Task.FromResult(false); } });
        await act.Should().ThrowAsync<OperationCanceledException>();
        asked.Should().ContainSingle().Which.Kind.Should().Be(ArchiveConfirmKind.HighCompressionRatio);

        asked.Clear();
        var r = await Run(zip, options: new ArchiveIngestOptions { Limits = limits, Confirm = q => { asked.Add(q); return Task.FromResult(true); } });
        asked.Should().ContainSingle();
        new FileInfo(Path.Combine(ExtractedRoot(r), "mem.raw")).Length.Should().Be(2_000_000);
    }

    [Fact]
    public async Task Low_disk_space_asks_and_cancel_stops()
    {
        var zip = Zip("space.zip", new ZipTestWriter().Add("a.txt", "alpha"));
        var asked = new List<ArchiveConfirmRequest>();
        var act = () => Run(zip, options: new ArchiveIngestOptions { FreeSpace = _ => 10, Confirm = q => { asked.Add(q); return Task.FromResult(false); } });
        await act.Should().ThrowAsync<OperationCanceledException>();
        asked.Single().Kind.Should().Be(ArchiveConfirmKind.LowDiskSpace);

        var noCallback = () => Run(zip, options: new ArchiveIngestOptions { FreeSpace = _ => 10 });
        await noCallback.Should().ThrowAsync<ArchiveIngestException>();
    }

    [Fact]
    public async Task Cancellation_during_extraction_removes_the_partial_folder()
    {
        var w = new ZipTestWriter();
        for (int i = 0; i < 10; i++) w.Add($"f{i}.txt", "data " + i);
        var zip = Zip("cancel.zip", w);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Phase == ArchivePhase.Extracting && p.EntriesDone >= 3) cts.Cancel(); });
        var act = () => Run(zip, progress: progress, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(ArchiveIngest.ExtractionFolder(_case, Sha(zip), zip)).Should().BeFalse();
    }

    // ------------------------------------------------------------------ tar / gz

    [Fact]
    public async Task Tgz_with_traversal_and_symlink_is_extracted_safely()
    {
        var tgz = Path.Combine(_tmp, "coll.tgz");
        var atime = new DateTime(2022, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new TarWriter(gz, TarEntryFormat.Pax))
        {
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../../outside.txt") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("bad")) });
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "etc-link") { LinkName = "/etc" });
            w.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "var/log/syslog" });
            var attrs = new Dictionary<string, string> { ["atime"] = new DateTimeOffset(atime).ToUnixTimeSeconds().ToString() };
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "var/log/syslog", attrs)
                { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("log line")), ModificationTime = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        }
        ArchiveFormatDetector.Detect(tgz).Should().Be(ArchiveFormat.TarGz);
        var r = await Run(tgz);
        var root = ExtractedRoot(r);
        Path.GetFileName(root).Should().Be("coll");
        var log = Path.Combine(root, "var", "log", "syslog");
        File.GetLastWriteTimeUtc(log).Should().Be(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.GetLastAccessTimeUtc(log).Should().Be(atime);   // before reading the file (reading updates it)
        File.ReadAllText(log).Should().Be("log line");
        File.Exists(Path.Combine(root, "etc-link")).Should().BeFalse();
        File.Exists(Path.Combine(root, "hard")).Should().BeFalse();
        Directory.EnumerateFiles(_tmp, "outside.txt", SearchOption.AllDirectories).Should().BeEmpty();
        var rec = ExtractionRecord.Load(root)!;
        rec.Entries.Single(e => e.OriginalName == "etc-link").Reason.Should().Contain("link");
        rec.Entries.Single(e => e.OriginalName == "../../outside.txt").Reason.Should().Contain("traversal");
    }

    [Fact]
    public async Task Checked_in_tar_gz_fixture_extracts()
    {
        var r = await Run(ArchiveEngineTests.Fx("tar-gz/sample.tar.gz"));
        // 7-Zip wrapped x.tar in gzip: the tar inside is detected and listed.
        Directory.EnumerateFiles(ExtractedRoot(r), "*", SearchOption.AllDirectories).Should().Contain(f => f.EndsWith("a.txt"));
    }

    [Fact]
    public async Task Single_file_gz_extracts_to_a_folder_with_one_file()
    {
        var gzPath = Path.Combine(_tmp, "auth.log.gz");
        using (var fs = File.Create(gzPath)) using (var g = new GZipStream(fs, CompressionLevel.Fastest)) g.Write(Encoding.ASCII.GetBytes("sshd: accepted"));
        var r = await Run(gzPath);
        var files = Directory.GetFiles(ExtractedRoot(r)).Where(f => !f.EndsWith(ExtractionRecord.FileName)).ToList();
        files.Should().ContainSingle().Which.Should().EndWith("auth.log");
        File.ReadAllText(files[0]).Should().Be("sshd: accepted");
    }

    // ------------------------------------------------------------------ nesting, layouts, vhdx

    [Fact]
    public async Task Zip_in_zip_container_is_auto_extracted_with_chain()
    {
        var inner = new ZipTestWriter().Add("C/Windows/System32/config/SYSTEM", "hive").ToArray();
        var outer = Zip("outer.zip", new ZipTestWriter().Add("data.zip", inner, e => e.Deflate = false));
        var r = await Run(outer);
        r.ExtractedRoots.Should().ContainSingle();
        var root = r.ExtractedRoots[0];
        File.ReadAllText(Path.Combine(root, "C", "Windows", "System32", "config", "SYSTEM")).Should().Be("hive");
        r.Origin.Chain.Should().HaveCount(2);
        r.Origin.Chain[0].Should().Be(Path.GetFullPath(outer));
        r.Origin.Chain[1].Should().EndWith("data.zip");
        r.Origins.Should().ContainSingle();
    }

    [Fact]
    public async Task Encrypted_outer_with_encrypted_inner_reuses_the_session_password()
    {
        var inner = new ZipTestWriter().Add("x.txt", "inner secret", e => e.ZipCryptoPassword = "Same-Pass-1").ToArray();
        var outer = Zip("outer-enc.zip", new ZipTestWriter().Add("data.zip", inner, e => { e.Deflate = false; e.ZipCryptoPassword = "Same-Pass-1"; }));
        var r = await Run(outer, Answers("Same-Pass-1"));
        _prompts.Should().ContainSingle();
        File.ReadAllText(Path.Combine(r.ExtractedRoots.Single(), "x.txt")).Should().Be("inner secret");
    }

    [Fact]
    public async Task Archives_inside_a_system_tree_are_evidence_not_containers()
    {
        var evidenceZip = new ZipTestWriter().Add("payload.exe", "MZ").ToArray();
        var outer = Zip("triage.zip", new ZipTestWriter()
            .Add("C/Windows/System32/config/SYSTEM", "hive")
            .Add("C/Users/bob/Downloads/a.zip", evidenceZip, e => e.Deflate = false));
        var r = await Run(outer);
        r.ExtractedRoots.Should().ContainSingle().Which.Should().Be(ExtractedRoot(r));
        File.Exists(Path.Combine(ExtractedRoot(r), "C", "Users", "bob", "Downloads", "a.zip")).Should().BeTrue();
        Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(ExtractedRoot(r))!)!).Should().ContainSingle();
    }

    [Fact]
    public async Task Velociraptor_collection_is_mapped_to_host_and_drive()
    {
        var zip = Zip("Collection-WKS-01-2026-10-10.zip", new ZipTestWriter()
            .Add("client_info.json", "{\"Hostname\":\"WKS-01\",\"Fqdn\":\"wks-01.corp.local\"}")
            .Add("collection_context.json", "{}")
            .Add("uploads/auto/C%3A/Windows/System32/config/SAM", "auto-sam")
            .Add("uploads/ntfs/%5C%5C.%5CC%3A/Windows/System32/config/SAM", "ntfs-sam")
            .Add("uploads/ntfs/%5C%5C.%5CC%3A/%24MFT", "mft")
            .Add("uploads/auto/C%3A/Users/bob/doc.txt%3AZone.Identifier", "[ZoneTransfer]")
            .Add("uploads/ntfs/%5C%5C%3F%5CGLOBALROOT%5CDevice%5CHarddiskVolumeShadowCopy1/Windows/System32/winevt/Logs/Security.evtx", "vss")
            .Add("results/Windows.KapeFiles.Targets%2FAll File Metadata.json", "{}")
            .Add("log.json", "{}"));

        var r = await Run(zip);
        var root = ExtractedRoot(r);
        var host = Path.Combine(root, "WKS-01");
        File.ReadAllText(Path.Combine(host, "C", "Windows", "System32", "config", "SAM")).Should().Be("ntfs-sam");
        File.ReadAllText(Path.Combine(host, "C", "$MFT")).Should().Be("mft");
        File.ReadAllText(Path.Combine(host, "C", "Users", "bob", "doc.txt%3AZone.Identifier")).Should().Be("[ZoneTransfer]");
        File.ReadAllText(Path.Combine(host, "_velociraptor", "vss", "HarddiskVolumeShadowCopy1", "Windows", "System32", "winevt", "Logs", "Security.evtx")).Should().Be("vss");
        File.Exists(Path.Combine(host, "_velociraptor", "log.json")).Should().BeTrue();
        File.Exists(Path.Combine(host, "_velociraptor", "client_info.json")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(host, "_velociraptor", "results")).Should().ContainSingle();

        var rec = ExtractionRecord.Load(root)!;
        rec.Layout.Should().Be("velociraptor");
        rec.Entries.Single(e => e.OriginalName.StartsWith("uploads/auto/C%3A/Windows")).Reason.Should().Contain("ntfs wins");
    }

    [Fact]
    public async Task Kape_copy_log_is_recorded()
    {
        var zip = Zip("kape.zip", new ZipTestWriter()
            .Add("C/Windows/System32/config/SYSTEM", "hive")
            .Add("2026-10-10T120000_CopyLog.csv", "SourceFile,DestinationFile"));
        var r = await Run(zip);
        var rec = ExtractionRecord.Load(ExtractedRoot(r))!;
        rec.Layout.Should().Be("kape");
        rec.CopyLogPath.Should().Be("2026-10-10T120000_CopyLog.csv");
    }

    [Fact]
    public async Task Vhdx_is_detected_and_not_extracted()
    {
        var vhdx = Path.Combine(_tmp, "disk.vhdx");
        File.WriteAllBytes(vhdx, Encoding.ASCII.GetBytes("vhdxfile").Concat(new byte[4096]).ToArray());
        var r = await Run(vhdx);
        r.ExtractedRoots.Should().BeEmpty();
        r.Warnings.Should().ContainSingle().Which.Should().Contain("mount");
        Directory.Exists(Path.Combine(_case, ".ezstaging")).Should().BeFalse();
    }

    private sealed class SyncProgress : IProgress<ArchiveProgress>
    {
        private readonly Action<ArchiveProgress> _a;
        public SyncProgress(Action<ArchiveProgress> a) => _a = a;
        public void Report(ArchiveProgress value) => _a(value);
    }
}
