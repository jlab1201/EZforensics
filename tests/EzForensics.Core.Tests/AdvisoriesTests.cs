using EzForensics.Core.Advisories;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class AdvisoriesTests
{
    private static readonly ReliabilityAdvisor Advisor = new();

    private const string PecmdHeaders = "SourceFilename,SourceCreated,SourceModified,SourceAccessed,ExecutableName,Hash,Size,Version,RunCount,LastRun,PreviousRun0,PreviousRun1,PreviousRun2,PreviousRun3,PreviousRun4,PreviousRun5,PreviousRun6,Volume0Name,Directories,FilesLoaded,ParsingError";
    private const string LecmdHeaders = "SourceFile,SourceCreated,SourceModified,SourceAccessed,TargetCreated,TargetModified,TargetAccessed,FileSize,RelativePath,WorkingDirectory,LocalPath,NetworkPath,CommonPath,Arguments,TargetIDAbsolutePath,MachineID,TrackerCreatedOn";
    private const string JlecmdHeaders = "SourceFile,SourceCreated,SourceModified,SourceAccessed,AppId,AppIdDescription,DestListVersion,EntryNumber,CreationTime,LastModified,Hostname,Path,InteractionCount,TargetCreated,TargetModified,TargetAccessed,LocalPath";
    private const string MftHeaders = "EntryNumber,SequenceNumber,InUse,ParentEntryNumber,ParentPath,FileName,Extension,FileSize,SI<FN,uSecZeros,Created0x10,Created0x30,LastModified0x10,LastModified0x30,LastRecordChange0x10,LastRecordChange0x30,LastAccess0x10,LastAccess0x30";
    private const string UsnHeaders = "Name,Extension,EntryNumber,SequenceNumber,ParentEntryNumber,ParentPath,UpdateSequenceNumber,UpdateTimestamp,UpdateReasons,FileAttributes,SourceFile";
    private const string EvtxHeaders = "RecordNumber,EventRecordId,TimeCreated,EventId,Level,Provider,Channel,ProcessId,ThreadId,Computer,MapDescription,UserName,PayloadData1,ExecutableInfo,SourceFile,Payload";
    private const string RecmdHeaders = "HivePath,HiveType,Description,Category,KeyPath,ValueName,ValueType,ValueData,ValueData2,ValueData3,Comment,Recursive,Deleted,LastWriteTimestamp,PluginDetailFile";
    private const string AmcacheHeaders = "ProgramName,ProgramID,VolumeID,FileKeyLastWriteTimestamp,SHA1,IsOsComponent,FullPath,Name,FileExtension,LinkDate,ProductName,Size,Version";
    private const string ShimHeaders = "ControlSet,CacheEntryPosition,Path,LastModifiedTimeUTC,Executed,Duplicate,SourceFile";
    private const string SrumHeaders = "Id,Timestamp,ExeInfo,ExeInfoDescription,ExeTimestamp,SidType,Sid,UserName,UserId,AppId,BytesReceived,BytesSent";
    private const string WxtHeaders = "Id,ActivityTypeOrg,ActivityType,Executable,DisplayText,ContentInfo,StartTime,EndTime,Duration,LastModifiedTime,LastModifiedOnClient,CreatedInCloud";
    private const string RbcmdHeaders = "SourceName,FileType,FileName,FileSize,DeletedOn";

    private static string[] H(string line) => line.Split(',');

    private static AdvisoryContext Ctx(string headers, string fileName = "x.csv", EvidenceAcquisition acq = EvidenceAcquisition.Unknown,
        IReadOnlyList<InputTimestampFact>? facts = null, string? artifactId = null) =>
        new() { Headers = H(headers), CsvFileName = fileName, Acquisition = acq, InputTimestampFacts = facts, ArtifactId = artifactId };

    /// <summary>25 prefetch files, Modified spread over ~50 days; <paramref name="copied"/> of them created during a 6-minute copy afterwards.</summary>
    private static List<InputTimestampFact> PrefetchFacts(int copied, int total = 25)
    {
        var copyStart = new DateTime(2026, 10, 1, 14, 2, 0, DateTimeKind.Utc);
        var list = new List<InputTimestampFact>();
        for (var i = 0; i < total; i++)
        {
            var modified = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(i * 2).AddMinutes(i * 7);
            var created = i < copied
                ? copyStart.AddSeconds(i * 15)                // copy time, after Modified
                : modified.AddDays(-30).AddHours(i);          // original: first run before last run
            list.Add(new InputTimestampFact($@"C:\Windows\Prefetch\APP{i}.EXE-1234ABCD.pf", created, modified, created));
        }
        return list;
    }

    // ---------- rule set ----------

    [Fact]
    public void Embedded_rules_load_and_validate()
    {
        var rules = AdvisoryRuleSet.LoadEmbedded();
        rules.Artifacts.Should().HaveCountGreaterThan(15);
        rules.Rules.Should().HaveCountGreaterThan(15);
        rules.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Override_file_is_used_when_present_and_embedded_otherwise()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ezforensics-adv-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            { "version": 9, "artifacts": [ { "id": "a", "label": "A", "headerSignatures": [ [ "Foo" ] ] } ],
              "rules": [ { "id": "r", "artifacts": [ "a" ], "when": "always", "severity": "critical", "title": "T", "message": "M {artifact}" } ] }
            """);
            var custom = AdvisoryRuleSet.LoadWithOverride(path);
            custom.Version.Should().Be(9);
            var adv = new ReliabilityAdvisor(custom).GetAdvisories(Ctx("Foo,Bar"));
            adv.Should().ContainSingle().Which.Should().Match<Advisory>(a => a.Severity == AdvisorySeverity.Critical && a.Message == "M A");

            AdvisoryRuleSet.LoadWithOverride(path + ".missing").Should().BeSameAs(AdvisoryRuleSet.LoadEmbedded());
            File.WriteAllText(path, "{ not json");
            AdvisoryRuleSet.LoadWithOverride(path).Should().BeSameAs(AdvisoryRuleSet.LoadEmbedded());
        }
        finally { File.Delete(path); }
    }

    // ---------- identification ----------

    [Theory]
    [InlineData(PecmdHeaders, "prefetch")]
    [InlineData("RunTime,ExecutableName", "prefetch-timeline")]
    [InlineData(LecmdHeaders, "lnk")]
    [InlineData(JlecmdHeaders, "jumplist")]
    [InlineData(MftHeaders, "mft")]
    [InlineData(UsnHeaders, "usnjournal")]
    [InlineData(EvtxHeaders, "eventlog")]
    [InlineData(RecmdHeaders, "registry")]
    [InlineData("BatchKeyPath,BatchValueName,LastWriteTimestamp,Foo", "registry-plugin")]
    [InlineData(AmcacheHeaders, "amcache")]
    [InlineData(ShimHeaders, "shimcache")]
    [InlineData(SrumHeaders, "srum")]
    [InlineData(WxtHeaders, "activitiescache")]
    [InlineData(RbcmdHeaders, "recyclebin")]
    [InlineData("BagPath,Slot,NodeSlot,MRUPosition,AbsolutePath,ShellType,Value,LastWriteTime", "shellbags")]
    public void Identifies_artifact_by_header_signature(string headers, string expected)
    {
        Advisor.Identify(H(headers), "unrelated.csv")!.Id.Should().Be(expected);
        Advisor.Evaluate(Ctx(headers, "unrelated.csv")).ArtifactId.Should().Be(expected);
    }

    [Theory]
    [InlineData("20261007123456_PECmd_Output.csv", "prefetch")]
    [InlineData("20261007123456_PECmd_Output_Timeline.csv", "prefetch-timeline")]
    [InlineData("LECmd_Output.csv", "lnk")]
    [InlineData("20261007123456_AutomaticDestinations.csv", "jumplist")]
    [InlineData("20261007123456_CustomDestinations.csv", "jumplist")]
    [InlineData("20261007123456_MFTECmd_$MFT_Output.csv", "mft")]
    [InlineData("20261007123456_MFTECmd_$J_Output.csv", "usnjournal")]
    [InlineData("20261007123456_EvtxECmd_Output.csv", "eventlog")]
    [InlineData("20261007123456_RECmd_Batch_Kroll_Batch_Output.csv", "registry")]
    [InlineData("20261007123456_Amcache_UnassociatedFileEntries.csv", "amcache")]
    [InlineData("20261007123456_SYSTEM_AppCompatCache.csv", "shimcache")]
    [InlineData("20261007123456_SrumECmd_NetworkUsages_Output.csv", "srum")]
    [InlineData("20261007123456_Activity.csv", "activitiescache")]
    [InlineData("20261007123456_RBCmd_Output.csv", "recyclebin")]
    // new artifact-style names
    [InlineData("Prefetch.csv", "prefetch")]
    [InlineData("Prefetch_Timeline.csv", "prefetch-timeline")]
    [InlineData("LNK_Recent.csv", "lnk")]
    [InlineData("JumpLists_Automatic.csv", "jumplist")]
    [InlineData("MFT.csv", "mft")]
    [InlineData("UsnJournal.csv", "usnjournal")]
    [InlineData("EventLogs.csv", "eventlog")]
    [InlineData("Registry_Kroll.csv", "registry")]
    [InlineData("SRUM_NetworkUsages.csv", "srum")]
    [InlineData("ActivitiesCache_Activity.csv", "activitiescache")]
    [InlineData("RecycleBin.csv", "recyclebin")]
    [InlineData("Amcache_ProgramEntries.csv", "amcache")]
    [InlineData("ShimCache.csv", "shimcache")]
    [InlineData(@"D:\cases\001\out\prefetch.CSV", "prefetch")]
    public void Identifies_artifact_by_file_name(string fileName, string expected) =>
        Advisor.Identify(new[] { "Unrelated", "Columns" }, fileName)!.Id.Should().Be(expected);

    [Fact]
    public void Explicit_artifact_id_wins_and_unknown_csv_gets_no_artifact_advisories()
    {
        Advisor.Evaluate(Ctx("A,B,C", "random.csv", artifactId: "srum")).ArtifactId.Should().Be("srum");
        Advisor.Evaluate(Ctx(PecmdHeaders, "x.csv", artifactId: "nope")).ArtifactId.Should().Be("prefetch");

        var unknown = Advisor.Evaluate(Ctx("Name,Value", "notes.csv"));
        unknown.ArtifactId.Should().BeNull();
        unknown.Advisories.Should().BeEmpty();
    }

    // ---------- copy heuristics ----------

    [Fact]
    public void Created_after_modified_and_clustered_created_means_high_confidence_copy()
    {
        var a = CopyHeuristics.Assess(PrefetchFacts(23), EvidenceAcquisition.Unknown, "prefetch files");
        a.Suspected.Should().BeTrue();
        a.CreatedAfterModifiedHit.Should().BeTrue();
        a.CreatedAfterModified.Should().Be(23);
        a.CreatedClusterHit.Should().BeTrue();
        a.CreatedClustered.Should().Be(23);
        a.Confidence.Should().Be(CopyConfidence.High);
        a.Summary.Should().Contain("23 of 25 prefetch files have a Created time later than their Modified time")
            .And.Contain("created within 10 minutes")
            .And.Contain("typical of a file copy");
    }

    [Fact]
    public void Created_after_modified_alone_is_medium_confidence()
    {
        // Created after Modified, but spread out (no cluster).
        var facts = Enumerable.Range(0, 10).Select(i =>
        {
            var m = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i * 3);
            return new InputTimestampFact($"f{i}", m.AddDays(1), m);
        }).ToList();
        var a = CopyHeuristics.Assess(facts, EvidenceAcquisition.Unknown);
        a.CreatedAfterModifiedHit.Should().BeTrue();
        a.CreatedClusterHit.Should().BeFalse();
        a.Confidence.Should().Be(CopyConfidence.Medium);
        a.Summary.Should().Contain("10 of 10 input files").And.Contain("does not prove");
    }

    [Fact]
    public void Clustered_created_with_spread_modified_alone_is_medium_confidence()
    {
        // Created times bunched within minutes (but still before every Modified time), Modified spread over weeks.
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var facts = Enumerable.Range(0, 10).Select(i =>
            new InputTimestampFact($"f{i}", start.AddSeconds(i * 20), start.AddDays(1 + i * 4))).ToList();
        var a = CopyHeuristics.Assess(facts, EvidenceAcquisition.Unknown);
        a.CreatedAfterModifiedHit.Should().BeFalse();
        a.CreatedClusterHit.Should().BeTrue();
        a.Confidence.Should().Be(CopyConfidence.Medium);
        a.ModifiedSpan.Should().BeGreaterThan(TimeSpan.FromDays(30));
    }

    [Fact]
    public void Clustered_created_and_clustered_modified_is_not_a_copy_signal()
    {
        // e.g. software installed in one go: everything created and modified within minutes.
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var facts = Enumerable.Range(0, 10).Select(i =>
            new InputTimestampFact($"f{i}", start.AddSeconds(i * 10), start.AddSeconds(i * 10 + 30))).ToList();
        CopyHeuristics.Assess(facts, EvidenceAcquisition.Unknown).Suspected.Should().BeFalse();
    }

    [Fact]
    public void Too_few_inputs_or_missing_times_do_not_trigger()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var facts = new List<InputTimestampFact>
        {
            new("a", t.AddDays(5), t),
            new("b", t.AddDays(5), t.AddDays(1)),
            new("c", null, t),
            new("d", t.AddDays(5), null),
        };
        var a = CopyHeuristics.Assess(facts, EvidenceAcquisition.Unknown);
        a.SampleSize.Should().Be(2);
        a.Suspected.Should().BeFalse();
        CopyHeuristics.Assess(null, EvidenceAcquisition.Unknown).Suspected.Should().BeFalse();
    }

    [Fact]
    public void Declared_copied_collection_is_suspected_without_facts()
    {
        var a = CopyHeuristics.Assess(null, EvidenceAcquisition.CopiedCollection);
        a.Suspected.Should().BeTrue();
        a.Declared.Should().BeTrue();
        a.Confidence.Should().Be(CopyConfidence.High);
        a.Summary.Should().Contain("copied collection");
    }

    // ---------- advisories ----------

    [Fact]
    public void Copied_prefetch_warns_on_source_columns_and_keeps_run_times_reliable()
    {
        var report = Advisor.Evaluate(Ctx(PecmdHeaders, "20261007123456_PECmd_Output.csv", facts: PrefetchFacts(23)));
        report.ArtifactId.Should().Be("prefetch");
        report.HighestSeverity.Should().Be(AdvisorySeverity.Warning);

        var warn = report.Advisories.First();
        warn.Id.Should().Be("prefetch-source-times-copied");
        warn.Severity.Should().Be(AdvisorySeverity.Warning);
        warn.AffectedColumns.Should().Equal("SourceCreated", "SourceModified", "SourceAccessed");
        warn.ReliableColumns.Should().Equal("RunCount", "LastRun", "PreviousRun0", "PreviousRun1", "PreviousRun2",
            "PreviousRun3", "PreviousRun4", "PreviousRun5", "PreviousRun6");
        warn.Message.Should().Contain("23 of 25 prefetch files").And.Contain("LastRun").And.NotContain("{copyEvidence}");
        warn.ReferenceUrl.Should().NotBeNullOrEmpty();

        report.Advisories.Select(a => a.Id).Should().NotContain("prefetch-source-times-original")
            .And.NotContain("source-times-copied", "the generic Source* rule must not duplicate the prefetch one")
            .And.Contain("ez-utc").And.Contain("source-accessed-weak");

        report.ColumnSeverities["SourceCreated"].Should().Be(AdvisorySeverity.Warning);
        report.ColumnSeverities["sourceaccessed"].Should().Be(AdvisorySeverity.Warning);
        report.ColumnSeverities.Should().NotContainKey("LastRun");
        report.ForColumn("SourceAccessed").Select(a => a.Id).Should().Contain(new[] { "prefetch-source-times-copied", "source-accessed-weak" });
    }

    [Fact]
    public void Image_acquisition_with_consistent_times_gives_only_info()
    {
        var report = Advisor.Evaluate(Ctx(PecmdHeaders, "Prefetch.csv", EvidenceAcquisition.MountedImageOrRawCopy, PrefetchFacts(0)));
        report.Copy.Suspected.Should().BeFalse();
        report.Advisories.Should().NotBeEmpty();
        report.Advisories.Should().OnlyContain(a => a.Severity == AdvisorySeverity.Info);
        report.Advisories.Select(a => a.Id).Should().Contain("prefetch-source-times-original");
    }

    [Fact]
    public void Declared_copied_collection_warns_for_lnk_and_lists_target_times_as_reliable()
    {
        var adv = Advisor.GetAdvisories(Ctx(LecmdHeaders, "LECmd_Output.csv", EvidenceAcquisition.CopiedCollection));
        var warn = adv.Single(a => a.Id == "lnk-source-times-copied");
        warn.AffectedColumns.Should().Equal("SourceCreated", "SourceModified", "SourceAccessed");
        warn.ReliableColumns.Should().Equal("TargetCreated", "TargetModified", "TargetAccessed", "TrackerCreatedOn");
        warn.Message.Should().Contain("copied collection");
        adv.Single(a => a.Id == "lnk-target-times").AffectedColumns.Should().Equal("TargetCreated", "TargetModified", "TargetAccessed");
    }

    [Fact]
    public void Jumplist_copy_warning_keeps_destlist_times_reliable()
    {
        var adv = Advisor.GetAdvisories(Ctx(JlecmdHeaders, "x.csv", EvidenceAcquisition.CopiedCollection));
        var warn = adv.Single(a => a.Id == "jumplist-source-times-copied");
        warn.ReliableColumns.Should().Contain(new[] { "CreationTime", "LastModified", "TargetCreated" });
        adv.Should().NotContain(a => a.Id == "lnk-source-times-copied" || a.Id == "source-times-copied");
    }

    [Fact]
    public void Live_acquisition_warns_about_access_times_and_changing_ntfs()
    {
        var pf = Advisor.GetAdvisories(Ctx(PecmdHeaders, "x.csv", EvidenceAcquisition.LiveVolume));
        pf.Single(a => a.Id == "source-accessed-live").AffectedColumns.Should().Equal("SourceAccessed");
        pf.Should().Contain(a => a.Id == "live-generic");

        var mft = Advisor.GetAdvisories(Ctx(MftHeaders, "MFT.csv", EvidenceAcquisition.LiveVolume));
        mft.Should().Contain(a => a.Id == "ntfs-live" && a.Severity == AdvisorySeverity.Warning);
        mft.Should().NotContain(a => a.Id == "live-generic");
        mft.Should().NotContain(a => a.Id == "source-accessed-live", "the $MFT CSV has no SourceAccessed column");
    }

    [Fact]
    public void Mft_flags_si_columns_and_fn_columns_as_reliable()
    {
        var adv = Advisor.GetAdvisories(Ctx(MftHeaders, "x.csv", EvidenceAcquisition.CopiedCollection));
        var mft = adv.Single(a => a.Id == "mft-internal-times");
        mft.AffectedColumns.Should().Equal("Created0x10", "LastModified0x10", "LastRecordChange0x10", "LastAccess0x10");
        mft.ReliableColumns.Should().Equal("Created0x30", "LastModified0x30", "LastRecordChange0x30", "LastAccess0x30");
        adv.Should().NotContain(a => a.Severity >= AdvisorySeverity.Warning, "internal $MFT times are not affected by copying the $MFT");
    }

    [Fact]
    public void Registry_flags_and_semantics()
    {
        var reg = Advisor.GetAdvisories(Ctx(RecmdHeaders, "x.csv") with { });
        reg.Should().Contain(a => a.Id == "registry-lastwrite");
        reg.Should().NotContain(a => a.Id == "registry-dirty-replayed");

        var dirty = Advisor.GetAdvisories(Ctx(RecmdHeaders, "x.csv") with { HiveWasDirtyReplayed = true });
        dirty.Single(a => a.Id == "registry-dirty-replayed").Severity.Should().Be(AdvisorySeverity.Info);

        var shim = Advisor.GetAdvisories(Ctx(ShimHeaders, "x.csv"));
        var s = shim.Single(a => a.Id == "shimcache-not-execution");
        s.Severity.Should().Be(AdvisorySeverity.Warning);
        s.AffectedColumns.Should().Equal("LastModifiedTimeUTC", "Executed");

        Advisor.GetAdvisories(Ctx(AmcacheHeaders, "x.csv")).Single(a => a.Id == "amcache-semantics")
            .AffectedColumns.Should().Equal("FileKeyLastWriteTimestamp", "LinkDate");
    }

    [Fact]
    public void Srum_and_activities_and_eventlog_and_recyclebin_advisories()
    {
        var srum = Advisor.GetAdvisories(Ctx(SrumHeaders, "x.csv"));
        srum.Should().Contain(a => a.Id == "srum-lag").And.NotContain(a => a.Id == "ese-dirty");
        Advisor.GetAdvisories(Ctx(SrumHeaders, "x.csv") with { EseDatabaseDirty = true })
            .Should().Contain(a => a.Id == "ese-dirty" && a.Severity == AdvisorySeverity.Warning);

        Advisor.GetAdvisories(Ctx(WxtHeaders, "x.csv")).Single(a => a.Id == "activitiescache-wal")
            .AffectedColumns.Should().Equal("StartTime", "EndTime", "LastModifiedTime");

        var evtx = Advisor.GetAdvisories(Ctx(EvtxHeaders, "x.csv"));
        evtx.Single(a => a.Id == "eventlog-cleared").AffectedColumns.Should().Equal("EventRecordId", "EventId");
        evtx.Single(a => a.Id == "eventlog-computer").AffectedColumns.Should().Equal("Computer");
        evtx.Single(a => a.Id == "eventlog-cleared").Message.Should().Contain("1102").And.Contain("104");

        var rb = Advisor.GetAdvisories(Ctx(RbcmdHeaders + ",SourceCreated,SourceModified", "x.csv", EvidenceAcquisition.CopiedCollection));
        rb.Single(a => a.Id == "recyclebin-deletedon").ReliableColumns.Should().Equal("DeletedOn");
        rb.Single(a => a.Id == "source-times-copied").AffectedColumns.Should().Equal("SourceCreated", "SourceModified");
    }

    [Fact]
    public void Generic_source_rule_is_skipped_when_csv_has_no_source_columns()
    {
        var adv = Advisor.GetAdvisories(Ctx(SrumHeaders, "x.csv", EvidenceAcquisition.CopiedCollection));
        adv.Should().NotContain(a => a.Id == "source-times-copied");
    }

    [Fact]
    public void Advisories_are_sorted_by_severity_and_use_csv_column_spelling()
    {
        var headers = PecmdHeaders.Replace("SourceCreated", "sourcecreated");
        var adv = Advisor.GetAdvisories(Ctx(headers, "x.csv", EvidenceAcquisition.CopiedCollection));
        adv.Select(a => (int)a.Severity).Should().BeInDescendingOrder();
        adv.First().AffectedColumns.Should().Contain("sourcecreated");
    }

    // ---- extracted archives ----

    private static AdvisoryContext Arch(string? coverage, string artifact = "prefetch") =>
        Ctx(PecmdHeaders, "x.csv", EvidenceAcquisition.ExtractedArchive, artifactId: artifact) with { ArchiveTimestampCoverage = coverage };

    [Fact]
    public void Extracted_archive_maps_and_counts_as_declared_copy()
    {
        EzForensics.Core.Case.CaseIndex.ToEvidenceAcquisition(EzForensics.Core.Triage.AcquisitionKind.ExtractedArchive).Should().Be(EvidenceAcquisition.ExtractedArchive);
        var a = CopyHeuristics.Assess(null, EvidenceAcquisition.ExtractedArchive, "prefetch files");
        a.Suspected.Should().BeTrue();
        a.Declared.Should().BeTrue();
        a.Confidence.Should().Be(CopyConfidence.High);
        a.Summary.Should().Contain("archive");
    }

    [Theory]
    [InlineData("ModifiedOnly")]
    [InlineData("Mixed")]
    [InlineData(null)]
    public void Archive_with_modified_only_warns_about_created_and_accessed(string? coverage)
    {
        var adv = Advisor.GetAdvisories(Arch(coverage));
        var a = adv.Single(x => x.Id == "archive-times-mtime-only");
        a.Severity.Should().Be(AdvisorySeverity.Warning);
        a.AffectedColumns.Should().Contain("SourceCreated");
        a.ReliableColumns.Should().Contain("SourceModified");
        adv.Should().NotContain(x => x.Id == "archive-times-restored" || x.Id == "archive-times-dos-local");
    }

    [Fact]
    public void Archive_with_full_coverage_gives_restored_info_only()
    {
        var adv = Advisor.GetAdvisories(Arch("All"));
        adv.Should().Contain(x => x.Id == "archive-times-restored" && x.Severity == AdvisorySeverity.Info);
        adv.Should().NotContain(x => x.Id == "archive-times-mtime-only");
    }

    [Fact]
    public void Archive_with_dos_local_times_adds_timezone_warning()
    {
        var adv = Advisor.GetAdvisories(Arch("DosLocal"));
        adv.Should().Contain(x => x.Id == "archive-times-mtime-only");
        adv.Single(x => x.Id == "archive-times-dos-local").Message.Should().Contain("2-second").And.Contain("time zone");
    }

    [Fact]
    public void Archive_rules_need_a_known_artifact_and_an_archive_acquisition()
    {
        Advisor.GetAdvisories(Ctx("a,b", "unknown.csv", EvidenceAcquisition.ExtractedArchive)).Should().NotContain(x => x.Id.StartsWith("archive-"));
        Advisor.GetAdvisories(Ctx(PecmdHeaders, "x.csv", EvidenceAcquisition.CopiedCollection)).Should().NotContain(x => x.Id.StartsWith("archive-"));
    }

    // ---- memory ----

    private const string PsListHeaders = "PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime";
    private const string NetScanHeaders = "Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created";

    [Theory]
    [InlineData("memory-pslist", "memory-process")]
    [InlineData("memory-pstree", "memory-process")]
    [InlineData("memory-psscan", "memory-carved")]
    [InlineData("memory-netscan", "memory-carved")]
    [InlineData("memory-filescan", "memory-carved")]
    [InlineData("memory-netstat", "memory-network")]
    [InlineData("memory-timeliner", "memory-timeliner")]
    [InlineData("memory-info", "memory-info")]
    public void Memory_artifact_id_from_manifest_wins_over_headers(string id, string expected)
    {
        Advisor.Evaluate(Ctx(PsListHeaders, "x.csv", artifactId: id)).ArtifactId.Should().Be(expected);
    }

    [Fact]
    public void Memory_artifact_identified_by_file_name()
    {
        Advisor.Evaluate(Ctx(PsListHeaders, "Memory_PsList.csv")).ArtifactId.Should().Be("memory-process");
        Advisor.Evaluate(Ctx("a,b", "20261007_Memory_PsScan_img.csv")).ArtifactId.Should().Be("memory-carved");
        Advisor.Evaluate(Ctx("Variable,Value", "Memory_Info.csv")).ArtifactId.Should().Be("memory-info");
    }

    /// <summary>
    /// Real Volatility 2.28.2 output: psscan has exactly pslist's columns and netscan exactly netstat's, so the headers cannot tell a live list
    /// from a pool scan. Without a manifest the file name decides; the carved-objects warning must not be lost to a header match.
    /// </summary>
    [Fact]
    public void Memory_scan_outputs_with_list_headers_are_identified_as_carved()
    {
        const string realPsScan = "TreeDepth,PID,PPID,ImageFileName,Offset(V),Threads,Handles,SessionId,Wow64,CreateTime,ExitTime,File output";
        const string realNetScan = "TreeDepth,Offset,Proto,LocalAddr,LocalPort,ForeignAddr,ForeignPort,State,PID,Owner,Created";
        var ps = Advisor.Evaluate(Ctx(realPsScan, "Memory_PsScan.csv"));
        ps.ArtifactId.Should().Be("memory-carved");
        ps.Advisories.Should().Contain(a => a.Id == "memory-carved-objects");
        var net = Advisor.Evaluate(Ctx(realNetScan, "Memory_NetScan.csv"));
        net.ArtifactId.Should().Be("memory-carved");
        net.Advisories.Should().Contain(a => a.Id == "memory-carved-objects");
        Advisor.Evaluate(Ctx(realNetScan, "Memory_NetStat.csv")).ArtifactId.Should().Be("memory-network");
        Advisor.Evaluate(Ctx(realPsScan, "export.csv")).ArtifactId.Should().BeNull("pslist and psscan headers are identical");
    }

    [Theory]
    [InlineData(false, false)]   // written by Windows at a bug check (samples\C\memory.dmp: 0x80 NMI) - system halted, no smear
    [InlineData(true, true)]     // bug check 0 (acquisition tool) or 0x161 live dump
    [InlineData(null, true)]     // unknown: stay cautious
    public void Memory_smear_for_crash_dumps_depends_on_live_acquisition(bool? live, bool expected)
    {
        var adv = Advisor.GetAdvisories(Ctx(PsListHeaders, artifactId: "memory-pslist") with { MemoryFormat = "WindowsCrashDump", MemoryLiveDump = live });
        adv.Any(a => a.Id == "memory-smear").Should().Be(expected);
        adv.Should().Contain(a => a.Id == "memory-point-in-time");
    }

    [Fact]
    public void Memory_process_gets_point_in_time_and_smear_but_not_carved_or_ez_utc()
    {
        var adv = Advisor.GetAdvisories(Ctx(PsListHeaders, artifactId: "memory-pslist"));
        adv.Should().Contain(a => a.Id == "memory-point-in-time" && a.Severity == AdvisorySeverity.Info);
        adv.Should().Contain(a => a.Id == "memory-smear" && a.Severity == AdvisorySeverity.Warning);
        adv.Should().NotContain(a => a.Id == "memory-carved-objects" || a.Id == "memory-kernel-dump" || a.Id == "ez-utc");
    }

    [Fact]
    public void Memory_carved_output_warns_about_stale_objects()
    {
        var adv = Advisor.GetAdvisories(Ctx(NetScanHeaders, artifactId: "memory-netscan"));
        var c = adv.Single(a => a.Id == "memory-carved-objects");
        c.Severity.Should().Be(AdvisorySeverity.Warning);
        c.AffectedColumns.Should().Contain("Created");
    }

    [Theory]
    [InlineData("VMware", false)]
    [InlineData("Hibernation", false)]
    [InlineData("Raw", true)]
    [InlineData("LiME", true)]
    [InlineData(null, true)]
    public void Memory_smear_depends_on_image_format(string? format, bool expected)
    {
        var adv = Advisor.GetAdvisories(Ctx(PsListHeaders, artifactId: "memory-pslist") with { MemoryFormat = format });
        adv.Any(a => a.Id == "memory-smear").Should().Be(expected);
    }

    [Theory]
    [InlineData("Kernel", true)]
    [InlineData("BitmapKernel", true)]
    [InlineData("Active", true)]
    [InlineData("Full", false)]
    [InlineData(null, false)]
    public void Memory_kernel_dump_note(string? kind, bool expected)
    {
        var adv = Advisor.GetAdvisories(Ctx(PsListHeaders, artifactId: "memory-pslist") with { MemoryDumpKind = kind });
        adv.Any(a => a.Id == "memory-kernel-dump").Should().Be(expected);
    }

    [Fact]
    public void Memory_advisories_do_not_fire_for_other_artifacts()
    {
        Advisor.GetAdvisories(Ctx(PecmdHeaders)).Should().NotContain(a => a.Id.StartsWith("memory-"));
    }

    // ---------- RITA ----------

    private const string RitaHeaders = "DateTimeUtc,FirstSeenUtc,LastSeenUtc,Severity,Src,Dst,Fqdn,Source IP,Destination IP,FQDN,Beacon Score,Strobe,Total Duration,Long Connection Score,Subdomains,C2 Over DNS Score,Threat Intel,Prevalence,First Seen,Missing Host Header,Connection Count,Total Bytes,Port:Proto:Service,Modifiers,Captures";

    [Theory]
    [InlineData("rita-threats")]
    [InlineData("rita-beacons")]
    [InlineData("rita-long-connections")]
    [InlineData("rita-dns-tunneling")]
    public void Rita_artifact_ids_resolve_to_rita(string id)
    {
        Advisor.Evaluate(Ctx(RitaHeaders, "x.csv", artifactId: id)).ArtifactId.Should().Be("rita");
        EzForensics.Core.Case.CaseIndex.AdvisoryArtifactId(id).Should().Be("rita");
    }

    [Theory]
    [InlineData("Rita_Threats.csv")]
    [InlineData("20261007_Rita_Beacons.csv")]
    [InlineData("Rita_LongConnections.csv")]
    [InlineData("Rita_DnsTunneling.csv")]
    public void Rita_artifact_identified_by_file_name(string name) =>
        Advisor.Evaluate(Ctx("a,b", name)).ArtifactId.Should().Be("rita");

    [Fact]
    public void Rita_static_advisories_always_fire_as_info()
    {
        var adv = Advisor.GetAdvisories(Ctx(RitaHeaders, artifactId: "rita-threats"));
        adv.Select(a => a.Id).Should().Contain(new[] { "rita-heuristic-scores", "rita-nat-proxy", "rita-internal-subnets", "rita-threat-intel-offline" });
        adv.Where(a => a.Id.StartsWith("rita-") && a.Id != "rita-short-capture").Should().OnlyContain(a => a.Severity == AdvisorySeverity.Info);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2.0, true)]
    [InlineData(23.9, true)]
    [InlineData(24.0, false)]
    [InlineData(100.0, false)]
    public void Rita_short_capture_warning_depends_on_data_span(double? hours, bool expected)
    {
        var adv = Advisor.GetAdvisories(Ctx(RitaHeaders, artifactId: "rita-beacons") with { RitaDataSpanHours = hours });
        adv.Any(a => a.Id == "rita-short-capture").Should().Be(expected);
        if (expected) adv.First(a => a.Id == "rita-short-capture").Severity.Should().Be(AdvisorySeverity.Warning);
    }

    [Fact]
    public void Rita_does_not_get_generic_utc_or_live_notes_and_others_get_no_rita_notes()
    {
        var rita = Advisor.GetAdvisories(Ctx(RitaHeaders, artifactId: "rita-threats", acq: EvidenceAcquisition.LiveVolume));
        rita.Should().NotContain(a => a.Id == "ez-utc" || a.Id == "live-generic");
        Advisor.GetAdvisories(Ctx(PecmdHeaders) with { RitaDataSpanHours = 1 }).Should().NotContain(a => a.Id.StartsWith("rita-"));
    }
}
