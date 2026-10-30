using System.Text.Json;
using EzForensics.Core.Archives;
using EzForensics.Core.Execution;
using EzForensics.Core.Settings;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Archive provenance: ExtractedArchive acquisition, ScanSources, JobProvenance.Archive and the manifest archive fields.</summary>
public sealed class TriageArchiveProvenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_arcprov_" + Guid.NewGuid().ToString("N")[..8]);

    public TriageArchiveProvenanceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ArchiveOrigin Origin(string zip, string extracted) =>
        new(zip, new string('a', 64), ArchiveFormat.Zip, true, "AES-256", extracted, new[] { zip }, TimestampCoverage.ModifiedOnly);

    [Fact]
    public void ScanSources_merges_roots_keeps_hosts_unique_and_tags_archive_origin()
    {
        var e1 = Path.Combine(_dir, "ex1", "case");
        var e2 = Path.Combine(_dir, "ex2", "case");
        // two extracted archives, both with a folder that yields the same host name
        var r1 = CaseLayoutTriageTests.MakeRoot(Path.Combine(e1, "WKS", "C"));
        var r2 = CaseLayoutTriageTests.MakeRoot(Path.Combine(e2, "WKS", "C"), "bob");
        var o1 = Origin(Path.Combine(_dir, "a.zip"), e1);
        var o2 = Origin(Path.Combine(_dir, "b.zip"), e2);

        var scan = ArtifactScanner.ScanSources(new[] { e1, e2 }, origins: new Dictionary<string, ArchiveOrigin>(StringComparer.OrdinalIgnoreCase) { [e1] = o1, [e2] = o2 });

        scan.Systems.Select(s => s.HostName).Should().Equal("WKS", "WKS (2)");
        scan.Systems.Should().OnlyContain(s => s.AcquisitionKind == AcquisitionKind.ExtractedArchive);
        scan.Systems.Select(s => s.Archive).Should().Equal(o1, o2);
        scan.Layout.SystemRoots.Should().Equal(r1, r2);
        scan.Artifacts.Where(a => a.RuleId == "prefetch").Select(a => a.Host).Should().Equal("WKS", "WKS (2)");

        var plan = TriagePlanner.PlanJobs(scan.Artifacts, TriageRules.LoadEmbedded(), Path.Combine(_dir, "case"));
        plan.Where(p => p.Artifact.RuleId == "prefetch").Select(p => p.Spec.Provenance!.Archive).Should().Equal(o1, o2);
        plan.Should().OnlyContain(p => p.Spec.Provenance!.AcquisitionKind == "ExtractedArchive");
        PersistenceTriage.Plan(scan.Systems, Path.Combine(_dir, "case"), @"C:\app\EzForensics.exe")
            .Should().OnlyContain(p => p.Spec.Provenance!.Archive != null);
    }

    [Fact]
    public void ScanSources_without_origins_behaves_like_ScanSource()
    {
        var src = Path.Combine(_dir, "src");
        CaseLayoutTriageTests.MakeRoot(Path.Combine(src, "HOST1", "C"));
        var a = ArtifactScanner.ScanSources(new[] { src });
        a.Systems.Should().ContainSingle().Which.Archive.Should().BeNull();
        a.Systems[0].AcquisitionKind.Should().Be(AcquisitionKind.CopiedCollection);
    }

    [Fact]
    public void Describe_names_extracted_archive() =>
        ArtifactScanner.Describe(AcquisitionKind.ExtractedArchive).Should().Be("extracted archive");

    [Fact]
    public void Manifest_records_archive_fields_roundtrips_and_has_no_password()
    {
        var zip = Path.Combine(_dir, "evidence.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
        var o = Origin(zip, Path.Combine(_dir, "ex")) with { Chain = new[] { zip, zip + "!inner.zip" } };
        var w = new ManifestWriter(_dir);
        var spec = new JobSpec("t", "t.exe", new List<string>(), _dir, "L", "", false,
            Provenance: new JobProvenance { RuleId = "prefetch", Host = "H", SystemRoot = @"C:\ex\H\C", AcquisitionKind = "ExtractedArchive", Archive = o });
        w.NoteOrigin(spec);
        w.NoteOrigin(spec); // idempotent

        var json = JsonSerializer.Serialize(w.Manifest, ManifestWriter.JsonOptions);
        json.Should().NotContainEquivalentOf("password");
        var back = JsonSerializer.Deserialize<RunManifest>(json, ManifestWriter.ReadOptions)!;
        var arc = back.Archives.Should().ContainSingle().Subject;
        (arc.ArchivePath, arc.Sha256, arc.Format, arc.Encrypted, arc.EncryptionMethod, arc.Size, arc.TimestampCoverage)
            .Should().Be((zip, o.Sha256, "Zip", true, "AES-256", 3L, "ModifiedOnly"));
        arc.Chain.Should().Equal(o.Chain);
        var src = back.Sources.Should().ContainSingle().Subject;
        (src.ArchivePath, src.ArchiveSha256, src.ArchiveFormat, src.ArchiveEncrypted, src.ExtractedTo, src.TimestampCoverage, src.AcquisitionKind)
            .Should().Be((zip, o.Sha256, "Zip", true, o.ExtractedTo, "ModifiedOnly", "ExtractedArchive"));
        src.ArchiveChain.Should().Equal(o.Chain);
    }

    [Fact]
    public void Old_manifest_without_archive_fields_loads_and_new_fields_are_omitted_when_absent()
    {
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, """{"sourcePath":"D:\\x","sources":[{"rootPath":"D:\\x","host":"H","acquisitionKind":"CopiedCollection"}],"jobs":[]}""");
        var m = RunManifest.Load(path);
        m.Archives.Should().BeNull();
        m.Sources!.Single().ArchivePath.Should().BeNull();

        var w = new ManifestWriter(_dir);
        w.NoteOrigin(new JobSpec("t", "t.exe", new List<string>(), _dir, "L", "", false, Provenance: new JobProvenance { SystemRoot = @"C:\r", Host = "H" }));
        var json = JsonSerializer.Serialize(w.Manifest, ManifestWriter.JsonOptions);
        json.Should().NotContain("archive", "no archive keys for ordinary evidence");
    }

    [Fact]
    public void KeepExtractedEvidence_defaults_to_true() => new AppSettings().KeepExtractedEvidence.Should().BeTrue();
}
