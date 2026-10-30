using System.Text;
using System.Text.Json;
using EzForensics.Core.Advisories;
using EzForensics.Core.Case;
using EzForensics.Core.Catalog;
using EzForensics.Core.Execution;
using EzForensics.Core.Platforms;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Case layout v2: artifact naming (#21), per-host layout (#19), multi-source triage (#23), manifest provenance (#20), case index.</summary>
public sealed class CaseLayoutNamingTests
{
    private static OutputPlan Plan(string name, string id, string? q = null, string? csvf = null) => new(@"C:\case\.ezstaging\x", name, id, q, csvf);

    [Theory]
    // PECmd: --csvf Prefetch.csv (PECmd derives Prefetch_Timeline.csv) and its default names
    [InlineData("PECmd", "Prefetch", "prefetch", null, "Prefetch.csv", "Prefetch.csv", "Prefetch.csv", "prefetch")]
    [InlineData("PECmd", "Prefetch", "prefetch", null, "Prefetch.csv", "Prefetch_Timeline.csv", "Prefetch_Timeline.csv", "prefetch-timeline")]
    [InlineData("PECmd", "Prefetch", "prefetch", null, null, "20261007123456_PECmd_Output.csv", "Prefetch.csv", "prefetch")]
    [InlineData("PECmd", "Prefetch", "prefetch", null, null, "20261007123456_PECmd_Output_Timeline.csv", "Prefetch_Timeline.csv", "prefetch-timeline")]
    [InlineData("EvtxECmd", "EventLogs", "eventlog", null, null, "20261007123456_EvtxECmd_Output.csv", "EventLogs.csv", "eventlog")]
    [InlineData("MFTECmd", "MFT", "mft", null, "MFT.csv", "MFT.csv", "MFT.csv", "mft")]
    [InlineData("MFTECmd", "MFT", "mft", null, null, "20261007123456_MFTECmd_$MFT_Output.csv", "MFT.csv", "mft")]
    [InlineData("MFTECmd", "UsnJournal", "usnjournal", null, null, "20261007123456_MFTECmd_$J_Output.csv", "UsnJournal.csv", "usnjournal")]
    [InlineData("MFTECmd", "MFT", "mft", null, null, "20261007123456_MFTECmd_$Boot_Output.csv", "MFT_Boot.csv", "mft-boot")]
    [InlineData("MFTECmd", "MFT", "mft", null, null, "20261007123456_MFTECmd_$SDS_Output.csv", "MFT_SDS.csv", "mft-sds")]
    [InlineData("MFTECmd", "MFT", "mft", null, null, "20261007123456_MFTECmd_$I30_Output.csv", "MFT_I30.csv", "i30")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_ProgramEntries.csv", "Amcache_ProgramEntries.csv", "amcache-program-entries")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_AssociatedFileEntries.csv", "Amcache_AssociatedFileEntries.csv", "amcache-associated-file-entries")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_UnassociatedFileEntries.csv", "Amcache_UnassociatedFileEntries.csv", "amcache-unassociated-file-entries")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_DeviceContainers.csv", "Amcache_DeviceContainers.csv", "amcache-device-containers")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_DevicePnps.csv", "Amcache_DevicePnps.csv", "amcache-device-pnps")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_DriveBinaries.csv", "Amcache_DriveBinaries.csv", "amcache-drive-binaries")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_DriverPackages.csv", "Amcache_DriverPackages.csv", "amcache-driver-packages")]
    [InlineData("AmcacheParser", "Amcache", "amcache", null, null, "20261007123456_Amcache_ShortCuts.csv", "Amcache_Shortcuts.csv", "amcache-shortcuts")]
    [InlineData("AppCompatCacheParser", "ShimCache", "shimcache", null, null, "20261007123456_AppCompatCache.csv", "ShimCache.csv", "shimcache")]
    [InlineData("RECmd", "Registry", "registry", "SYSTEM", null, "20261007123456_RECmd_Batch_DFIRBatch_Output.csv", "Registry_SYSTEM.csv", "registry")]
    [InlineData("RECmd", "Registry", "registry", "NTUSER_jdoe", null, "20261007123456_RECmd_Batch_DFIRBatch_Output.csv", "Registry_NTUSER_jdoe.csv", "registry")]
    [InlineData("RECmd", "Registry", "registry", "UsrClass_jdoe", null, "20261007123456_RECmd_Batch_DFIRBatch_Output.csv", "Registry_UsrClass_jdoe.csv", "registry")]
    [InlineData("RECmd", "Registry", "registry", "NTUSER_jdoe", null, "20261007123456_TypedURLs__C_Users_jdoe_NTUSER.DAT.csv", "Registry_TypedURLs_NTUSER_jdoe.csv", "registry-plugin")]
    [InlineData("RECmd", "Registry", "registry", "SOFTWARE", null, "20261007123456_Uninstall__SOFTWARE.csv", "Registry_Uninstall_SOFTWARE.csv", "registry-plugin")]
    [InlineData("SBECmd", "ShellBags", "shellbags", "jdoe", null, "jdoe_UsrClass.csv", "ShellBags_UsrClass_jdoe.csv", "shellbags")]
    [InlineData("SBECmd", "ShellBags", "shellbags", "jdoe", null, "jdoe_NTUSER.csv", "ShellBags_NTUSER_jdoe.csv", "shellbags")]
    [InlineData("LECmd", "LNK", "lnk", "jdoe", "LNK_jdoe.csv", "LNK_jdoe.csv", "LNK_jdoe.csv", "lnk")]
    [InlineData("LECmd", "LNK", "lnk", "jdoe", null, "20261007123456_LECmd_Output.csv", "LNK_jdoe.csv", "lnk")]
    [InlineData("JLECmd", "JumpLists", "jumplist", "jdoe", null, "20261007123456_AutomaticDestinations.csv", "JumpLists_Auto_jdoe.csv", "jumplist-automatic")]
    [InlineData("JLECmd", "JumpLists", "jumplist", "jdoe", null, "20261007123456_CustomDestinations.csv", "JumpLists_Custom_jdoe.csv", "jumplist-custom")]
    [InlineData("RBCmd", "RecycleBin", "recyclebin", null, null, "20261007123456_RBCmd_Output.csv", "RecycleBin.csv", "recyclebin")]
    [InlineData("SrumECmd", "SRUM", "srum", null, null, "20261007123456_SrumECmd_NetworkUsages_Output.csv", "SRUM_NetworkUsages.csv", "srum")]
    [InlineData("SrumECmd", "SRUM", "srum", null, null, "20261007123456_SrumECmd_AppResourceUseInfo_Output.csv", "SRUM_AppResourceUseInfo.csv", "srum")]
    [InlineData("SumECmd", "UAL", "ual", null, null, "20261007123456_SumECmd_SUMMARY_ClientsDetailed_Output.csv", "UAL_SUMMARY_ClientsDetailed.csv", "ual")]
    [InlineData("SumECmd", "UAL", "ual", null, null, "20261007123456_SumECmd_DETAIL_DnsInfo_Output.csv", "UAL_DETAIL_DnsInfo.csv", "ual")]
    [InlineData("WxTCmd", "ActivitiesCache", "activitiescache", "jdoe", null, "20261007123456_Activity.csv", "ActivitiesCache_jdoe.csv", "activitiescache")]
    [InlineData("WxTCmd", "ActivitiesCache", "activitiescache", "jdoe", null, "20261007123456_Activity_PackageIDs.csv", "ActivitiesCache_PackageIDs_jdoe.csv", "activitiescache-packages")]
    [InlineData("WxTCmd", "ActivitiesCache", "activitiescache", "Testuser", null, "20261007123456_Testuser_Activity.csv", "ActivitiesCache_Testuser.csv", "activitiescache")]
    [InlineData("WxTCmd", "ActivitiesCache", "activitiescache", "Testuser", null, "20261007123456_Testuser_Activity_PackageIDs.csv", "ActivitiesCache_PackageIDs_Testuser.csv", "activitiescache-packages")]
    [InlineData("WxTCmd", "ActivitiesCache", "activitiescache", "Testuser", null, "20261007123456_Testuser_Activity_Operation.csv", "ActivitiesCache_Operation_Testuser.csv", "activitiescache")]
    [InlineData("RecentFileCacheParser", "RecentFileCache", "recentfilecache", null, null, "20261007123456_RecentFileCacheParser_Output.csv", "RecentFileCache.csv", "recentfilecache")]
    [InlineData("SQLECmd", "SQLite", "sqlite", null, null, "20261007123456123456_Chromium Browser_History.csv", "SQLite_Chromium_Browser_History.csv", "sqlite")]
    [InlineData("EzForensics.Persistence", "Persistence", "persistence", null, null, "EzForensics_Persistence.csv", "Persistence.csv", "persistence")]
    [InlineData("EzForensics.Persistence", "Persistence", "persistence", null, null, "EzForensics_ScheduledTasks.csv", "Persistence_ScheduledTasks.csv", "persistence-tasks")]
    [InlineData("EzForensics.Persistence", "Persistence", "persistence", null, null, "EzForensics_WmiPersistence.csv", "Persistence_Wmi.csv", "persistence-wmi")]
    [InlineData("Autoruns", "Autoruns", "autoruns", "bob", null, "Autoruns_bob.csv", "Autoruns_bob.csv", "autoruns")]
    [InlineData("Autoruns", "Autoruns", "autoruns", "System", null, "autorunsc_System_raw.txt", "Autoruns_Raw_System.txt", "autoruns")]
    [InlineData("Zeek", "Zeek", "zeek", "cap", null, "conn.csv", "Zeek_Conn_cap.csv", "zeek-conn")]
    [InlineData("Zeek", "Zeek", "zeek", "cap", null, "dce_rpc.log", "Zeek_DceRpc_cap.log", "zeek-dce-rpc")]
    [InlineData("Zeek", "Zeek", "zeek", "cap", null, "known_hosts.csv", "Zeek_KnownHosts_cap.csv", "zeek-known-hosts")]
    [InlineData("SomeImportedTool", "SomeImportedTool", "imported", null, null, "20261007123456_weird name!.csv", "SomeImportedTool_weird_name.csv", "imported")]
    public void Rename_table_gives_convention_names(string toolId, string name, string id, string? q, string? csvf, string toolFile,
        string expectedName, string expectedId)
    {
        var plan = Plan(name, id, q, csvf);
        ArtifactNaming.FinalFileName(toolId, plan, toolFile).Should().Be(expectedName);
        ArtifactNaming.Map(toolId, plan, toolFile).ArtifactId.Should().Be(expectedId);
        if (expectedName.EndsWith(".csv"))
        {
            ArtifactOutputLayout.IsValidOutputName(expectedName).Should().BeTrue();
            ArtifactNaming.IdentifyNewName(expectedName)?.ToolId.Should().Be(toolId == "SomeImportedTool" ? null : toolId);
        }
    }

    [Theory]
    [InlineData("john.doe", "johndoe")]
    [InlineData("j_doe", "jdoe")]
    [InlineData("Administrator", "Administrator")]
    [InlineData("  ", null)]
    public void Qualifiers_are_alphanumeric(string value, string? expected) => ArtifactNaming.SanitizeQualifier(value).Should().Be(expected);

    [Fact]
    public void Qualifier_without_ascii_characters_gets_a_stable_hash_and_parts_join_with_underscore()
    {
        var q = ArtifactNaming.SanitizeQualifier("日本語");
        q.Should().MatchRegex("^X[0-9A-F]{8}$").And.Be(ArtifactNaming.SanitizeQualifier("日本語"));
        ArtifactNaming.Qualifier("NTUSER", "j.doe").Should().Be("NTUSER_jdoe");
        ArtifactNaming.Qualifier("UsrClass", null).Should().Be("UsrClass");
        ArtifactNaming.KebabId("AssociatedFileEntries").Should().Be("associated-file-entries");
        ArtifactNaming.KebabId("dce_rpc").Should().Be("dce-rpc");
        ArtifactNaming.Pascal("conn-summary").Should().Be("ConnSummary");
    }

    [Fact]
    public void Collisions_get_numbered_suffixes_and_staging_is_cleaned()
    {
        using var t = new TempDir();
        var staging = Path.Combine(t.Path, ".ezstaging", "job");
        var final = Path.Combine(t.Path, "HOST", "Databases");
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "SQLite_Chrome_History.csv"), "old");     // an earlier run
        var before = OutputSnapshot.Capture(staging);
        Directory.CreateDirectory(Path.Combine(staging, "sub"));
        File.WriteAllText(Path.Combine(staging, "20261007123456123456_Chrome_History.csv"), "a");
        File.WriteAllText(Path.Combine(staging, "sub", "20261007123457123456_Chrome History.csv"), "b"); // same final name, other profile

        var published = OutputPublisher.Publish("SQLECmd", new OutputPlan(staging, "SQLite", "sqlite"), final, before);

        published.Select(p => p.FinalName).Should().Equal("SQLite_Chrome_History_2.csv", "SQLite_Chrome_History_3.csv");
        File.ReadAllText(Path.Combine(final, "SQLite_Chrome_History.csv")).Should().Be("old", "never overwrite");
        published.Should().OnlyContain(p => ArtifactOutputLayout.IsValidOutputName(p.FinalName) && p.ArtifactId == "sqlite");
        Directory.Exists(Path.Combine(t.Path, ".ezstaging")).Should().BeFalse("empty staging folders are removed");
    }

    [Fact]
    public void Every_rule_has_a_valid_output_spec_and_csvf_only_for_tools_that_have_it()
    {
        foreach (var rule in TriageRules.LoadEmbedded())
        {
            rule.Output.Should().NotBeNull($"rule {rule.Id} needs an output naming spec");
            ArtifactOutputLayout.IsValidOutputName(rule.Output!.Name + ".csv").Should().BeTrue(rule.Id);
            ArtifactOutputLayout.IsValidCategory(rule.Category).Should().BeTrue(rule.Id);
            rule.Output.ArtifactId.Should().MatchRegex("^[a-z0-9-]+$");
            rule.Output.Name.Should().NotStartWith(rule.ToolId, "no tool-name prefixes");
            if (rule.Output.Csvf)
                ToolCatalog.Find(rule.ToolId)!.Parameters.Should().Contain(p => p.Flag == "--csvf", $"{rule.ToolId} must support --csvf");
        }
    }

    [Fact]
    public void Imported_rules_get_a_default_output_spec()
    {
        var r = new TriageRule { Id = "mkape:Foo:Bar", ToolId = "foo-tool", Label = "x", PerMatch = true };
        r.EffectiveOutput.Name.Should().Be("FooTool");
        r.EffectiveOutput.ArtifactId.Should().Be("mkape-foo-bar");
        r.EffectiveOutput.Qualifier.Should().Be(OutputQualifier.File);
    }

    [Fact]
    public void Legacy_names_are_identified_per_tool()
    {
        ArtifactNaming.IdentifyLegacyName("PECmd", "20261007123456_PECmd_Output.csv").Should().Be("prefetch");
        ArtifactNaming.IdentifyLegacyName("PECmd", "PECmd_Output_Timeline (2).csv").Should().Be("prefetch-timeline");
        ArtifactNaming.IdentifyLegacyName("RECmd", "TypedURLs_NTUSER.DAT.csv").Should().Be("registry-plugin");
        ArtifactNaming.IdentifyLegacyName("RECmd", "RECmd_Batch_DFIRBatch_Output.csv").Should().Be("registry");
        ArtifactNaming.IdentifyLegacyName("SBECmd", "bob_UsrClass.csv").Should().Be("shellbags");
        ArtifactNaming.IdentifyLegacyName("EzForensics.Persistence", "EzForensics_Persistence.csv").Should().Be("persistence");
        ArtifactNaming.IdentifyLegacyName("EzForensics.Persistence", "EzHub_Persistence.csv").Should().Be("persistence");   // written before the rename
        ArtifactNaming.IdentifyLegacyName("Nope", "x.csv").Should().BeNull();
    }
}

public sealed class CaseLayoutTriageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezforensics_layout_" + Guid.NewGuid().ToString("N")[..8]);

    public CaseLayoutTriageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Windows root with prefetch files and one profile (no hives: host names come from the folders).</summary>
    internal static string MakeRoot(string path, params string[] users)
    {
        Directory.CreateDirectory(Path.Combine(path, "Windows", "System32", "config"));
        Directory.CreateDirectory(Path.Combine(path, "Windows", "Prefetch"));
        File.WriteAllText(Path.Combine(path, "Windows", "Prefetch", "CALC.EXE-1234.pf"), "pf1");
        File.WriteAllText(Path.Combine(path, "Windows", "Prefetch", "CMD.EXE-5678.pf"), "pf2");
        foreach (var u in users.Length == 0 ? new[] { "alice" } : users)
        {
            var recent = Path.Combine(path, "Users", u, "AppData", "Roaming", "Microsoft", "Windows", "Recent");
            Directory.CreateDirectory(recent);
            File.WriteAllText(Path.Combine(recent, "doc.lnk"), "lnk");
            File.WriteAllText(Path.Combine(path, "Users", u, "NTUSER.DAT"), "regf");
        }
        return path;
    }

    [Fact]
    public void Multi_host_source_is_planned_per_host()
    {
        var src = Path.Combine(_dir, "src");
        var r1 = MakeRoot(Path.Combine(src, "HOST1", "C"));
        var r2 = MakeRoot(Path.Combine(src, "HOST2", "C"), "bob");
        var scan = ArtifactScanner.ScanSource(src);

        scan.Systems.Select(s => (s.HostName, s.RootPath)).Should().Equal(("HOST1", r1), ("HOST2", r2));
        scan.Systems.Should().OnlyContain(s => s.AcquisitionKind == AcquisitionKind.CopiedCollection && s.HostNameSource == HostDiscovery.FromFolderName);
        scan.Layout.SystemRoots.Should().Equal(r1, r2);
        scan.Artifacts.Where(a => a.RuleId == "prefetch").Select(a => (a.Host, a.SystemRoot)).Should().Equal(("HOST1", r1), ("HOST2", r2));

        var caseDir = Path.Combine(_dir, "case");
        var plan = TriagePlanner.PlanJobs(scan.Artifacts, TriageRules.LoadEmbedded(), caseDir);
        var pf = plan.Where(p => p.Artifact.RuleId == "prefetch").ToList();
        pf.Select(p => p.Spec.OutputDir).Should().Equal(Path.Combine(caseDir, "HOST1", "ProgramExecution"), Path.Combine(caseDir, "HOST2", "ProgramExecution"));
        pf.Select(p => p.Spec.Args[1]).Should().Equal(Path.Combine(r1, "Windows", "Prefetch"), Path.Combine(r2, "Windows", "Prefetch"));
        pf.Select(p => p.Spec.Output!.StagingDir).Should().OnlyHaveUniqueItems();
        pf.Select(p => p.Spec.Label).Should().Equal("HOST1 · Prefetch", "HOST2 · Prefetch");
        pf.Should().OnlyContain(p => p.Spec.Provenance!.AcquisitionKind == "CopiedCollection" && p.Spec.Provenance.HostNameSource == HostDiscovery.FromFolderName);

        var lnk = plan.Where(p => p.Artifact.RuleId == "lnk").Select(p => (p.Spec.OutputDir, p.Spec.Output!.FixedFileName)).ToList();
        lnk.Should().Equal((Path.Combine(caseDir, "HOST1", "FileFolderAccess"), "LNK_alice.csv"), (Path.Combine(caseDir, "HOST2", "FileFolderAccess"), "LNK_bob.csv"));

        var persistence = PersistenceTriage.Plan(scan.Systems, caseDir, @"C:\app\EzForensics.exe");
        persistence.Where(p => p.Artifact.RuleId == PersistenceTriage.RuleId).Select(p => p.Spec.OutputDir)
            .Should().Equal(Path.Combine(caseDir, "HOST1", "Persistence"), Path.Combine(caseDir, "HOST2", "Persistence"));
    }

    [Fact]
    public void Single_host_source_still_has_the_host_level()
    {
        var root = MakeRoot(Path.Combine(_dir, "WKS-01"));
        var scan = ArtifactScanner.ScanSource(root);
        scan.Systems.Should().ContainSingle().Which.HostName.Should().Be("WKS-01");
        var plan = TriagePlanner.PlanJobs(scan.Artifacts, TriageRules.LoadEmbedded(), Path.Combine(_dir, "case"));
        plan.Should().OnlyContain(p => p.Spec.OutputDir.StartsWith(Path.Combine(_dir, "case", "WKS-01") + "\\"));
        plan.Should().OnlyContain(p => !p.Spec.Label.Contains(" · "), "one host: labels stay short");
    }

    [Fact]
    public void Same_qualifier_twice_for_one_host_gets_a_number_and_reserved_host_names_are_prefixed()
    {
        var used = new HashSet<string>();
        TriagePlanner.UniqueQualifier(used, "H", "activitiescache", "bob").Should().Be("bob");
        TriagePlanner.UniqueQualifier(used, "H", "activitiescache", "bob").Should().Be("bob_2");
        TriagePlanner.UniqueQualifier(used, "H2", "activitiescache", "bob").Should().Be("bob");
        TriagePlanner.HostFolder("Timeline").Should().Be("Host_Timeline");
        TriagePlanner.HostFolder("Registry").Should().Be("Host_Registry");
        TriagePlanner.HostFolder("WKS:01").Should().Be("WKS_01");
        TriagePlanner.QualifierFor(OutputQualifier.Hive, @"C:\r\Users\j.doe\AppData\Local\Microsoft\Windows\UsrClass.dat", @"C:\r", true).Should().Be("UsrClass_jdoe");
        TriagePlanner.QualifierFor(OutputQualifier.Hive, @"C:\r\Windows\System32\config\SAM", @"C:\r", true).Should().Be("SAM");
    }
}

public sealed class CaseLayoutManifestTests
{
    internal const string OldManifest = """
    {
      "appVersion": "1.0.0.0", "machine": "M", "user": "U", "isElevated": false, "sourcePath": "D:\\evidence",
      "createdUtc": "2026-10-01T10:00:00.0000000Z",
      "jobs": [
        { "jobId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "toolId": "PECmd", "label": "Prefetch", "exePath": "C:\\tools\\PECmd.exe",
          "argv": ["-d", "D:\\evidence\\C\\Windows\\Prefetch", "--csv", "E:\\old\\case\\ProgramExecution\\PECmd", "-q"],
          "state": "Succeeded", "exitCode": 0, "logPath": "E:\\old\\case\\logs\\3f25_PECmd.log",
          "outputDir": "E:\\old\\case\\ProgramExecution\\PECmd",
          "outputFiles": [ { "path": "E:\\old\\case\\ProgramExecution\\PECmd\\PECmd_Output.csv", "size": 10, "sha256": "AB",
                             "originalName": "20261001100000_PECmd_Output.csv" } ] }
      ]
    }
    """;

    [Fact]
    public void Old_manifest_still_loads_with_new_fields_empty()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Path, "EzHub_manifest_20261001T100000Z.json");
        File.WriteAllText(p, OldManifest);
        var m = RunManifest.Load(p);
        m.Jobs.Should().ContainSingle();
        m.Jobs[0].ToolId.Should().Be("PECmd");
        m.Jobs[0].OutputFiles.Single().OriginalName.Should().Be("20261001100000_PECmd_Output.csv");
        m.LayoutVersion.Should().BeNull();
        m.Sources.Should().BeNull();
        m.CaseDir.Should().BeNull();
        m.Jobs[0].InputFacts.Should().BeNull();
        m.Jobs[0].Host.Should().BeNull();
        m.Jobs[0].OutputFiles[0].ArtifactId.Should().BeNull();
    }

    [Fact]
    public void New_manifest_fields_round_trip()
    {
        var m = new RunManifest
        {
            LayoutVersion = 2, CaseDir = @"D:\case",
            Sources = new() { new ManifestSource { RootPath = @"D:\src\HOST1\C", Host = "HOST1", HostNameSource = HostDiscovery.FromSystemHive, AcquisitionKind = "CopiedCollection", OsFamily = "Windows" } },
        };
        m.Jobs.Add(new ManifestJob
        {
            ToolId = "PECmd", Category = "ProgramExecution", RuleId = "prefetch", ArtifactId = "prefetch", Host = "HOST1", SystemRoot = @"D:\src\HOST1\C",
            AcquisitionKind = "CopiedCollection", InputFactsNote = "note",
            InputFacts = new() { new ManifestInputFact { Path = @"D:\src\HOST1\C\Windows\Prefetch\A.pf", CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Size = 3, Sha256 = "AA" } },
            OutputFiles = new() { new ManifestOutputFile { Path = @"D:\case\HOST1\ProgramExecution\Prefetch.csv", ArtifactId = "prefetch", ToolId = "PECmd", Host = "HOST1" } },
        });
        var json = JsonSerializer.Serialize(m, ManifestWriter.JsonOptions);
        json.Should().Contain("\"layoutVersion\": 2").And.Contain("\"inputFacts\"").And.Contain("\"hostNameSource\"");
        var back = JsonSerializer.Deserialize<RunManifest>(json, ManifestWriter.ReadOptions)!;
        back.Sources!.Single().Host.Should().Be("HOST1");
        var j = back.Jobs.Single();
        (j.Category, j.RuleId, j.ArtifactId, j.Host, j.AcquisitionKind, j.InputFactsNote).Should().Be(("ProgramExecution", "prefetch", "prefetch", "HOST1", "CopiedCollection", "note"));
        j.InputFacts!.Single().CreatedUtc.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        j.OutputFiles.Single().ArtifactId.Should().Be("prefetch");
        // Jobs without a triage origin keep the old JSON shape (new members omitted).
        JsonSerializer.Serialize(new ManifestJob { ToolId = "X" }, ManifestWriter.JsonOptions).Should().NotContain("inputFacts").And.NotContain("\"host\"");
    }

    [Fact]
    public void Input_facts_record_times_hashes_and_skip_hashing_on_live_volumes()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "A.pf"), "one");
        File.WriteAllText(Path.Combine(t.Path, "B.pf"), "two");
        File.WriteAllText(Path.Combine(t.Path, "x.txt"), "no");
        var sel = new[] { new InputSelector(t.Path, "*.pf") };
        var (facts, note) = InputFacts.Collect(sel, hashInputs: true, isLiveVolume: false);
        facts.Select(f => Path.GetFileName(f.Path)).Should().Equal("A.pf", "B.pf");
        facts.Should().OnlyContain(f => f.Sha256!.Length == 64 && f.CreatedUtc != null && f.ModifiedUtc != null && f.AccessedUtc != null && f.Size == 3);
        note.Should().BeNull();

        var (live, liveNote) = InputFacts.Collect(sel, hashInputs: true, isLiveVolume: true);
        live.Should().HaveCount(2).And.OnlyContain(f => f.Sha256 == null && f.ModifiedUtc != null);
        liveNote.Should().Be(InputFacts.LiveVolumeNote);
    }

    [Fact]
    public void Input_facts_live_volume_note_is_written_whether_or_not_hashing_runs()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "A.pf"), "one");
        var sel = new[] { new InputSelector(t.Path, "*.pf") };
        // hashing switched off for the job: still a live volume → the note says so
        var (noHash, noHashNote) = InputFacts.Collect(sel, hashInputs: false, isLiveVolume: true);
        noHash.Should().ContainSingle().Which.Sha256.Should().BeNull();
        noHashNote.Should().Be(InputFacts.LiveVolumeNote);
        // hashing on live volumes requested: hashed, and the note warns about access times
        var (hashed, hashedNote) = InputFacts.Collect(sel, hashInputs: true, isLiveVolume: true, hashOnLiveVolume: true);
        hashed.Should().ContainSingle().Which.Sha256.Should().HaveLength(64);
        hashedNote.Should().Be(InputFacts.LiveVolumeHashedNote);
        // not live, no hashing: no note
        InputFacts.Collect(sel, hashInputs: false, isLiveVolume: false).Note.Should().BeNull();
    }
}

public sealed class CaseIndexTests
{
    [Fact]
    public void Legacy_case_with_moved_folder_resolves_manifest_entries_and_infers_the_rest()
    {
        using var t = new TempDir();
        var caseDir = Path.Combine(t.Path, "case");
        var pe = Path.Combine(caseDir, "ProgramExecution", "PECmd", "PECmd_Output.csv");
        var plugin = Path.Combine(caseDir, "Registry", "RECmd", "Users_bob_NTUSER.DAT", "TypedURLs_NTUSER.DAT.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(pe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        File.WriteAllText(pe, "0123456789");   // the manifest records size 10
        File.WriteAllText(plugin, "a");
        File.WriteAllText(Path.Combine(caseDir, "EzHub_manifest_20261001T100000Z.json"), CaseLayoutManifestTests.OldManifest);

        var idx = CaseIndex.Load(caseDir);
        idx.LayoutVersion.Should().Be(CaseLayoutVersion.Legacy);
        idx.HasLegacyOutputs.Should().BeTrue();

        var p = idx.Describe(pe);
        p.Source.Should().Be(ProvenanceSource.Manifest, "the manifest path E:\\old\\case is rebased onto the current case folder");
        (p.ToolId, p.ArtifactId, p.Category, p.Host).Should().Be(("PECmd", "prefetch", "ProgramExecution", null));
        p.Layout.Should().Be(CaseLayoutVersion.Legacy);
        p.EvidenceAcquisition.Should().Be(EvidenceAcquisition.Unknown);

        var q = idx.Describe(plugin);
        q.Source.Should().Be(ProvenanceSource.LegacyPath);
        (q.ToolId, q.ArtifactId, q.Category).Should().Be(("RECmd", "registry-plugin", "Registry"));
        q.AdvisoryArtifactId.Should().Be("registry-plugin");
    }

    [Fact]
    public void New_layout_paths_are_inferred_without_a_manifest()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "HOST1", "FileFolderAccess", "LNK_jdoe.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, "a");
        var idx = CaseIndex.Load(t.Path);
        idx.LayoutVersion.Should().Be(CaseLayoutVersion.HostArtifact);
        idx.HasLegacyOutputs.Should().BeFalse();
        idx.Hosts.Should().Equal("HOST1");
        var p = idx.Describe(f);
        (p.Source, p.Host, p.Category, p.ToolId, p.ArtifactId).Should().Be((ProvenanceSource.NewLayoutPath, "HOST1", "FileFolderAccess", "LECmd", "lnk"));
    }

    [Fact]
    public void Acquisition_and_advisory_id_mapping()
    {
        CaseIndex.ToEvidenceAcquisition(AcquisitionKind.LiveVolume).Should().Be(EvidenceAcquisition.LiveVolume);
        CaseIndex.ToEvidenceAcquisition(AcquisitionKind.MountedImageOrRawCopy).Should().Be(EvidenceAcquisition.MountedImageOrRawCopy);
        CaseIndex.ToEvidenceAcquisition(AcquisitionKind.CopiedCollection).Should().Be(EvidenceAcquisition.CopiedCollection);
        CaseIndex.ToEvidenceAcquisition(null).Should().Be(EvidenceAcquisition.Unknown);
        CaseIndex.AdvisoryArtifactId("amcache-program-entries").Should().Be("amcache");
        CaseIndex.AdvisoryArtifactId("jumplist-automatic").Should().Be("jumplist");
        CaseIndex.AdvisoryArtifactId("prefetch-timeline").Should().Be("prefetch-timeline");
        CaseIndex.AdvisoryArtifactId("usnjournal").Should().Be("usnjournal");
        CaseIndex.AdvisoryArtifactId("zeek-conn").Should().BeNull();
    }

    [Fact]
    public void Marker_is_written_once()
    {
        using var t = new TempDir();
        CaseLayout.ReadMarker(t.Path).Should().BeNull();
        CaseLayout.EnsureMarker(t.Path);
        var first = File.ReadAllText(CaseLayout.MarkerPath(t.Path));
        CaseLayout.EnsureMarker(t.Path, 1);
        File.ReadAllText(CaseLayout.MarkerPath(t.Path)).Should().Be(first);
        CaseLayout.ReadMarker(t.Path)!.LayoutVersion.Should().Be(2);
    }
}

public sealed class CaseLayoutTimelineTests
{
}

/// <summary>End to end with the fake EZ tool: plan → JobQueue (staging + publish) → manifest → case index.</summary>
public sealed class CaseLayoutEndToEndTests
{
    [Fact]
    public async Task Triage_jobs_publish_convention_names_and_record_provenance()
    {
        using var t = new TempDir();
        var root = CaseLayoutTriageTests.MakeRoot(Path.Combine(t.Path, "src", "WKS-01"));
        var caseDir = Path.Combine(t.Path, "case");
        var scan = ArtifactScanner.ScanSource(Path.Combine(t.Path, "src"));
        var plan = TriagePlanner.PlanJobs(scan.Artifacts, TriageRules.LoadEmbedded(), caseDir);

        // PECmd with --csvf Prefetch.csv writes Prefetch.csv and Prefetch_Timeline.csv; RECmd writes its batch file and a plugin file
        // in a run-time sub-folder. The fake tool emits the same names.
        var pf = plan.Single(p => p.Artifact.RuleId == "prefetch").Spec;
        pf = pf with { Args = pf.Args.Concat(new[] { "--emit", "Prefetch.csv", "--emit", "Prefetch_Timeline.csv" }).ToList() };
        var reg = plan.Single(p => p.Artifact.RuleId == "registry" && p.Spec.Output!.Qualifier == "NTUSER_alice").Spec;
        reg = reg with { Args = reg.Args.Concat(new[] { "--emit", "{ts}_RECmd_Batch_DFIRBatch_Output.csv", "--emit", @"{ts}\{ts}_TypedURLs__NTUSER.DAT.csv" }).ToList() };

        var q = new JobQueue(new FakeLocator(FakeToolPaths.Exe), 2, TimeSpan.FromMinutes(1));
        var j1 = q.Enqueue(pf, caseDir);
        var j2 = q.Enqueue(reg, caseDir);
        await q.WhenAllAsync();
        var again = q.Enqueue(pf, caseDir);   // a second run into the same case never overwrites
        await q.WhenAllAsync();

        foreach (var j in new[] { j1, j2, again }) j.State.Should().Be(JobState.Succeeded, j.StatusReason);
        var pe = Path.Combine(caseDir, "WKS-01", "ProgramExecution");
        var rg = Path.Combine(caseDir, "WKS-01", "Registry");
        Directory.GetFiles(pe).Select(Path.GetFileName).Should().BeEquivalentTo("Prefetch.csv", "Prefetch_Timeline.csv", "Prefetch_2.csv", "Prefetch_Timeline_2.csv");
        Directory.GetFiles(rg).Select(Path.GetFileName).Should().BeEquivalentTo("Registry_NTUSER_alice.csv", "Registry_TypedURLs_NTUSER_alice.csv");
        Directory.GetFiles(pe).Concat(Directory.GetFiles(rg)).Should().OnlyContain(f => ArtifactOutputLayout.IsValidOutputName(Path.GetFileName(f)));
        Directory.Exists(Path.Combine(caseDir, OutputPlan.StagingFolderName)).Should().BeFalse("staging folders are removed after publishing");
        CaseLayout.ReadMarker(caseDir)!.LayoutVersion.Should().Be(2);

        // Manifest
        var manifest = RunManifest.Load(Directory.GetFiles(caseDir, "EzForensics_manifest_*.json").Single());
        manifest.LayoutVersion.Should().Be(2);
        manifest.CaseDir.Should().Be(caseDir);
        manifest.Sources!.Should().ContainSingle().Which.Should().Match<ManifestSource>(s =>
            s.Host == "WKS-01" && s.RootPath == root && s.AcquisitionKind == "CopiedCollection" && s.HostNameSource == HostDiscovery.FromFolderName && s.OsFamily == "Windows");
        var mj = manifest.Jobs.First(j => j.JobId == j1.Id);
        (mj.Category, mj.RuleId, mj.Host, mj.SystemRoot, mj.AcquisitionKind).Should().Be(("ProgramExecution", "prefetch", "WKS-01", root, "CopiedCollection"));
        mj.OutputDir.Should().Be(pe);
        mj.InputFacts!.Select(f => Path.GetFileName(f.Path)).Should().Equal("CALC.EXE-1234.pf", "CMD.EXE-5678.pf");
        mj.InputFacts.Should().OnlyContain(f => f.Sha256!.Length == 64 && f.CreatedUtc != null);
        mj.OutputFiles.Select(f => (Path.GetFileName(f.Path), f.ArtifactId, f.ToolId, f.Host)).Should().BeEquivalentTo(new[]
        {
            ("Prefetch.csv", "prefetch", "PECmd", "WKS-01"), ("Prefetch_Timeline.csv", "prefetch-timeline", "PECmd", "WKS-01"),
        });
        mj.OutputFiles.Should().OnlyContain(f => f.Sha256!.Length == 64 && f.OriginalPath!.Contains(OutputPlan.StagingFolderName));
        var rj = manifest.Jobs.First(j => j.JobId == j2.Id);
        rj.OutputFiles.Select(f => f.ArtifactId).Should().BeEquivalentTo("registry", "registry-plugin");
        rj.InputFacts!.Single().Path.Should().EndWith(Path.Combine("alice", "NTUSER.DAT"));

        // Case index
        var idx = CaseIndex.Load(caseDir);
        idx.LayoutVersion.Should().Be(CaseLayoutVersion.HostArtifact);
        idx.Hosts.Should().Equal("WKS-01");
        var p = idx.Describe(Path.Combine(pe, "Prefetch_Timeline_2.csv"));
        (p.Source, p.ToolId, p.ArtifactId, p.Host, p.Category, p.RuleId).Should().Be((ProvenanceSource.Manifest, "PECmd", "prefetch-timeline", "WKS-01", "ProgramExecution", "prefetch"));
        p.Acquisition.Should().Be(AcquisitionKind.CopiedCollection);
        p.EvidenceAcquisition.Should().Be(EvidenceAcquisition.CopiedCollection);
        p.InputTimestampFacts.Should().HaveCount(2);
        p.AdvisoryArtifactId.Should().Be("prefetch-timeline");
        idx.Describe(Path.Combine(rg, "Registry_TypedURLs_NTUSER_alice.csv")).ArtifactId.Should().Be("registry-plugin");
    }
}
