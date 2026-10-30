using EzForensics.Core.Archives;
using EzForensics.Core.Memory;
using EzForensics.Core.Network;
using EzForensics.Core.Platforms;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class IngestSessionSnapshotTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static IngestSessionSnapshot Sample()
    {
        var outer = new ArchiveOrigin(@"D:\ev\outer.zip", "aa", ArchiveFormat.Zip, false, null, @"D:\case\.ezstaging\extracted\aa",
            new[] { @"D:\ev\outer.zip" }, TimestampCoverage.ModifiedOnly);
        var inner = new ArchiveOrigin(@"D:\ev\outer.zip|data.7z", "bb", ArchiveFormat.SevenZip, true, "AES-256", @"D:\case\.ezstaging\extracted\bb",
            new[] { @"D:\ev\outer.zip", "data.7z" }, TimestampCoverage.All, outer);
        var sys = new DiscoveredSystem
        {
            RootPath = @"C:\", HostName = "WKS-01", HostNameSource = HostDiscovery.FromSystemHive,
            OsFamily = OsFamily.Windows, AcquisitionKind = AcquisitionKind.LiveVolume, Archive = inner,
        };
        var scan = new TriageScanResult(@"C:\", new SourceLayout(new[] { @"C:\" }, true, new[] { "live volume" }), new[] { sys },
            new List<DetectedArtifact>
            {
                new("mft", "$MFT", "MFTECmd", "FileSystem", @"C:\$MFT", false, 1, @"C:\", true, true, sys),
                new("evtx", "Event logs", "EvtxECmd", "EventLogs", @"C:\Windows\System32\winevt\Logs", false, 42, @"C:\", false, true, sys),
            })
        {
            MemoryImages = new[] { new MemoryImage(@"D:\mem.raw", 1024, MemoryFormat.Raw, MemoryOs.Windows, DumpKind.Full, 256, "note") { CompanionPath = @"D:\mem.vmem" } },
        };
        return new IngestSessionSnapshot
        {
            SavedUtc = Now,
            Source = @"C:\", CaseName = "case 7", CaseFolder = @"D:\cases\case7", CaseFolderEdited = true,
            Scan = scan,
            Pcaps = new PcapScanResult(new[] { new PcapFile(@"D:\net\a.pcapng", 99, PcapFormat.PcapNg) }, 10, 3, true, "stopped after 10 entries"),
            PcapRoot = @"C:\", SummarySource = @"C:\", SummaryIsFile = false,
            EffectiveRoots = new[] { @"D:\case\.ezstaging\extracted\bb" }, ArchiveOrigins = new[] { inner }, ArchiveSource = @"D:\ev\outer.zip",
            ArchiveCaseDir = @"D:\case", Archives = new[] { new IngestArchiveCard(inner, new[] { "CRC mismatch" }) },
            EvidenceArchives = new[] { @"C:\Users\x\Downloads\a.zip" }, EvidenceArchivesTruncated = true, ContainerArchives = new[] { @"D:\ev\b.zip" },
            Systems = new[] { new IngestSystemState(@"C:\", true, "WKS-01-renamed") },
            MemoryImages = new[] { new IngestPathState(@"D:\mem.raw", false) },
            PcapFiles = new[] { new IngestPathState(@"D:\net\a.pcapng", true) },
            IncludePagefile = true,
            Rows = new[] { new IngestRowTick(@"mft|C:\$MFT|$MFT", false), new IngestRowTick("evtx|x|Event logs", true) },
        };
    }

    [Fact]
    public void Round_trips_the_whole_page_state()
    {
        var s = Sample();
        var back = IngestSessionStore.Deserialize(IngestSessionStore.Serialize(s));
        back.Should().NotBeNull();
        back.Should().BeEquivalentTo(s, o => o.RespectingRuntimeTypes());
        back!.Scan!.Artifacts[0].System.Should().BeSameAs(back.Scan.Systems[0]);   // re-linked, as after a scan
        back.Scan.Artifacts[0].Host.Should().Be("WKS-01");
        back.Scan.MemoryImages[0].CompanionPath.Should().Be(@"D:\mem.vmem");
        back.ArchiveOrigins[0].Parent!.ArchivePath.Should().Be(@"D:\ev\outer.zip");
    }

    private static string Key(PlannedJob p) => $"{p.Artifact.RuleId}|{p.Artifact.MatchedPath}|{p.Spec.Label}";

    [Fact]
    public void Ticks_of_several_supertimeline_rows_survive_the_round_trip_and_the_replan()
    {
        DiscoveredSystem Sys(string root, string host) => new() { RootPath = root, HostName = host, HostNameSource = HostDiscovery.FromSystemHive, OsFamily = OsFamily.Windows };
        var ws = Sys(@"E:\src\WS01\C", "WS01");
        var dc = Sys(@"E:\src\DC01\C", "DC01");
        var scan = new TriageScanResult(@"E:\src", new SourceLayout(new[] { ws.RootPath, dc.RootPath }, false, Array.Empty<string>()), new[] { ws, dc },
            new List<DetectedArtifact>
            {
                new("prefetch", "Prefetch", "PECmd", "Prefetch", @"E:\src\WS01\C\Windows\Prefetch", false, 3, ws.RootPath, false, true, ws),
                new("evtx", "Event logs", "EvtxECmd", "EventLogs", @"E:\src\DC01\C\Windows\System32\winevt\Logs", false, 9, dc.RootPath, false, true, dc),
            });
        List<PlannedJob> Plan(TriageScanResult sc) =>
            IngestSteps.Compose(TriagePlanner.PlanJobs(sc.Artifacts, TriageRules.LoadEmbedded(), @"D:\case"), @"D:\case", new[] { @"E:\src" }, plasoAvailable: true);

        var timelines = Plan(scan).Where(IngestSteps.IsSupertimeline).ToList();
        timelines.Should().HaveCount(2);
        var s = new IngestSessionSnapshot
        {
            SavedUtc = Now, Source = @"E:\src", CaseFolder = @"D:\case", Scan = scan, SummarySource = @"E:\src",
            Rows = new[] { new IngestRowTick(Key(timelines[0]), true), new IngestRowTick(Key(timelines[1]), false) },
        };

        var back = IngestSessionStore.Deserialize(IngestSessionStore.Serialize(s))!;
        var ticks = back.Rows.ToDictionary(r => r.Key, r => r.Selected);
        var replanned = Plan(back.Scan!).Where(IngestSteps.IsSupertimeline).ToList();
        replanned.Select(Key).Should().Equal(timelines.Select(Key));
        replanned.Select(p => ticks[Key(p)]).Should().Equal(true, false);
        replanned.Select(p => p.Spec.OutputDir).Should().Equal(@"D:\case\WS01\Timeline", @"D:\case\DC01\Timeline");
    }

    [Fact]
    public void Enums_are_written_as_names()
    {
        IngestSessionStore.Serialize(Sample()).Should().Contain("\"liveVolume\"").And.Contain("\"pcapNg\"");
    }

    [Fact]
    public void Invalid_json_or_other_version_gives_null()
    {
        IngestSessionStore.Deserialize("{ not json").Should().BeNull();
        IngestSessionStore.Deserialize(IngestSessionStore.Serialize(Sample() with { Version = 99 })).Should().BeNull();
    }

    [Fact]
    public void Freshness_is_one_hour()
    {
        var s = Sample();
        IngestSessionStore.IsFresh(s, Now.AddMinutes(59)).Should().BeTrue();
        IngestSessionStore.IsFresh(s, Now.AddMinutes(61)).Should().BeFalse();
        IngestSessionStore.IsFresh(s, Now.AddHours(-1)).Should().BeFalse();   // saved "in the future": clock changed
    }

    [Fact]
    public void Take_restores_once_and_deletes_the_file()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "sub", IngestSessionStore.FileName);
        IngestSessionStore.Save(Sample(), path);
        File.Exists(path).Should().BeTrue();
        IngestSessionStore.Take(Now.AddMinutes(5), path).Should().NotBeNull();
        File.Exists(path).Should().BeFalse();
        IngestSessionStore.Take(Now.AddMinutes(5), path).Should().BeNull();
    }

    [Fact]
    public void Take_ignores_and_deletes_an_expired_snapshot()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, IngestSessionStore.FileName);
        IngestSessionStore.Save(Sample(), path);
        IngestSessionStore.Take(Now.AddHours(2), path).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }
}

public class IngestSessionLaunchTests
{
    [Fact]
    public void Args_round_trip_to_the_snapshot_path()
    {
        var p = @"C:\Users\u\AppData\Local\EzForensicsData\ingest-session.json";
        IngestSessionLaunch.SnapshotPath(IngestSessionLaunch.Args(p)).Should().Be(p);
        IngestSessionLaunch.HasSwitch(IngestSessionLaunch.Args(p)).Should().BeTrue();
    }

    [Fact]
    public void Other_files_or_no_switch_are_never_used()
    {
        IngestSessionLaunch.SnapshotPath(new[] { "--resume-setup" }).Should().BeNull();
        IngestSessionLaunch.SnapshotPath(new[] { IngestSessionLaunch.Switch, @"C:\Windows\win.ini" }).Should().Be(IngestSessionStore.DefaultPath);
        IngestSessionLaunch.SnapshotPath(new[] { IngestSessionLaunch.Switch }).Should().Be(IngestSessionStore.DefaultPath);
    }
}
