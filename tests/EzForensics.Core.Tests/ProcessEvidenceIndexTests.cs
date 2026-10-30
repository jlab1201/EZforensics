using System.Globalization;
using System.Text;
using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class ProcessEvidenceIndexTests : IDisposable
{
    private readonly string _host = Path.Combine(Path.GetTempPath(), "ezforensics_evidence_" + Guid.NewGuid().ToString("N"));

    public ProcessEvidenceIndexTests() => Directory.CreateDirectory(_host);
    public void Dispose() { try { Directory.Delete(_host, true); } catch { } }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_host, name), content, new UTF8Encoding(false));

    private Task<ProcessEvidenceIndex> Build(IReadOnlyList<EvidenceSourceDef>? catalog = null) => ProcessEvidenceIndex.BuildAsync(_host, CancellationToken.None, catalog);

    private static IReadOnlyList<EvidenceSourceDef> Catalog(string json) => ProcessEvidenceCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static DateTime Utc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static ProcessNode Node(int pid, string name, string path = "", string? create = null, string? exit = null, string guid = "") => new()
    {
        Pid = pid, Name = name, Path = path, Guid = guid,
        CreateTime = create == null ? null : Utc(create),
        ExitTime = exit == null ? null : Utc(exit),
    };

    private const string PrefetchHeader = "SourceFilename,ExecutableName,RunCount,LastRun,PreviousRun0,FilesLoaded\r\n";

    private static string Pf(string exe, string lastRun, string previous, string path) =>
        $"{exe.ToUpperInvariant()}-1.pf,{exe.ToUpperInvariant()},3,{lastRun},{previous},\"\\VOLUME{{x}}\\WINDOWS\\SYSTEM32\\NTDLL.DLL, {path}\"\r\n";

    [Fact]
    public void Catalog_HoldsOnlyFactSources_AndPrefetchIsPathTimeStart()
    {
        var all = ProcessEvidenceCatalog.Sources;
        all.Select(s => s.Id).Should().BeEquivalentTo(new[] { "prefetch", "eventlog", "zeek" });
        var zeek = ProcessEvidenceCatalog.Find("zeek")!;
        zeek.IsCsv.Should().BeFalse("ZeekSocketIndex reads it, only through Exact-linked sockets");
        zeek.JoinKind.Should().Be(EvidenceJoinKind.Socket);
        zeek.HowToGet.Should().Be("Ingest › Zeek on a pcap");
        ProcessEvidenceCatalog.ForFile("Zeek_Conn_cap.csv").Should().BeNull("not CSV-indexed by ProcessEvidenceIndex");
        foreach (var gone in new[] { "amcache", "shimcache", "srum", "srum-network", "mft", "usnjournal", "recentfilecache", "registry", "persistence", "lnk", "jumplists", "activitiescache", "recyclebin" })
            ProcessEvidenceCatalog.Find(gone).Should().BeNull();
        all.Should().OnlyContain(s => s.HowToGet.Length > 0);
        ProcessEvidenceCatalog.Find("eventlog")!.IsCsv.Should().BeFalse();
        var pf = ProcessEvidenceCatalog.Find("prefetch")!;
        pf.JoinKind.Should().Be(EvidenceJoinKind.PathTime);
        pf.Fit.Should().Be(new TimeFit(TimeRole.Start, -2, 15));
        pf.Columns.Time.Should().HaveCount(9).And.StartWith("lastrun", "previousrun0");
        ProcessEvidenceCatalog.ForFile("Prefetch_Timeline.csv").Should().BeNull();
        ProcessEvidenceCatalog.ForFile("Amcache_AssociatedFileEntries.csv").Should().BeNull();
    }

    [Fact]
    public void Catalog_RejectsEntriesWithoutHint_AndPathTimeWithoutTimeRole()
    {
        var act = () => Catalog("""{ "sources": [ { "id": "x", "fileRegex": "^x$", "joinKind": "pid-time", "tab": "Files" } ] }""");
        act.Should().Throw<InvalidDataException>();
        var noRole = () => Catalog("""{ "sources": [ { "id": "x", "fileRegex": "^x$", "joinKind": "path-time", "tab": "Files", "howToGet": "x" } ] }""");
        noRole.Should().Throw<InvalidDataException>();
        var noTolerance = () => Catalog("""{ "sources": [ { "id": "x", "fileRegex": "^x$", "joinKind": "path-time", "timeRole": "start", "tab": "Files", "howToGet": "x" } ] }""");
        noTolerance.Should().Throw<InvalidDataException>();
        var during = Catalog("""{ "sources": [ { "id": "x", "fileRegex": "^x$", "joinKind": "path-time", "timeRole": "during", "tab": "Files", "howToGet": "x" } ] }""");
        during[0].Fit.Should().Be(TimeFit.During);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"\windows\system32\cmd.exe")]
    [InlineData(@"\??\C:\Windows\System32\cmd.exe", @"\windows\system32\cmd.exe")]
    [InlineData(@"\\?\D:\Windows\System32\CMD.EXE", @"\windows\system32\cmd.exe")]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe", @"\windows\system32\cmd.exe")]
    [InlineData(@"\SystemRoot\System32\cmd.exe", @"\windows\system32\cmd.exe")]
    [InlineData(@"\VOLUME{01d6-abcd}\WINDOWS\SYSTEM32\CMD.EXE", @"\windows\system32\cmd.exe")]
    [InlineData("cmd.exe", "")]
    public void PathKey_NormalisesVariants(string path, string key) => ProcessLogText.PathKey(path).Should().Be(key);

    [Fact]
    public void JoinPath_StripsTheMftRootDot() => ProcessEvidenceIndex.JoinPath(@".\Windows\System32", "cmd.exe").Should().Be(@"\Windows\System32\cmd.exe");

    [Fact]
    public async Task PrefetchRun_InsideTheStartTolerance_IsPathTime_OutsideIsNot()
    {
        Write("Prefetch.csv", PrefetchHeader +
            Pf("tool.exe", "2024-01-01 10:00:10", "", @"\VOLUME{x}\USERS\A\TOOL.EXE") +      // 10 s after the start below
            Pf("late.exe", "2024-01-01 10:00:16", "", @"\VOLUME{x}\USERS\A\LATE.EXE") +      // 16 s: outside
            Pf("early.exe", "2024-01-01 09:59:57", "", @"\VOLUME{x}\USERS\A\EARLY.EXE"));    // 3 s before: outside [-2, 15]
        using var idx = await Build();
        var tool = Node(10, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 10:00:00");
        var farAway = Node(11, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 08:00:00");   // a second instance of the path, far from every run
        idx.Attach(new[] { tool, farAway });

        var r = idx.Lookup(tool, "prefetch");
        r.Status.Should().Be(EvidenceStatus.Available);
        r.Rows.Should().ContainSingle().Which.Tier.Should().Be(LinkTier.PathTime);
        idx.Lookup(farAway, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.Lookup(Node(12, "late.exe", @"C:\Users\a\late.exe", "2024-01-01 10:00:00"), "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.Lookup(Node(13, "early.exe", @"C:\Users\a\early.exe", "2024-01-01 10:00:00"), "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task PrefetchPreviousRun_LinksTheEarlierInstance()
    {
        Write("Prefetch.csv", PrefetchHeader + Pf("tool.exe", "2024-01-01 10:00:10", "2024-01-01 09:00:08", @"\VOLUME{x}\USERS\A\TOOL.EXE"));
        using var idx = await Build();
        var morning = Node(10, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 09:00:00");
        var later = Node(11, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 10:00:00");
        idx.Attach(new[] { morning, later });
        idx.Lookup(morning, "prefetch").Rows.Should().ContainSingle();
        idx.Lookup(later, "prefetch").Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task PrefetchPathWithTwoFittingInstances_IsAmbiguousAndNotLinked()
    {
        Write("Prefetch.csv", PrefetchHeader + Pf("tool.exe", "2024-01-01 10:00:08", "", @"\VOLUME{x}\USERS\A\TOOL.EXE"));
        using var idx = await Build();
        var a = Node(10, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 10:00:00");
        var b = Node(11, "tool.exe", @"C:\Users\a\tool.exe", "2024-01-01 10:00:05");
        var unknownStart = Node(12, "tool.exe", @"C:\Users\a\tool.exe");

        idx.Attach(new[] { a, b });
        idx.Lookup(a, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.Lookup(b, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);

        // one instance alone is linked
        idx.Attach(new[] { a });
        idx.Lookup(a, "prefetch").Rows.Should().ContainSingle();

        // an instance without a start time cannot be ruled out: still ambiguous
        idx.Attach(new[] { a, unknownStart });
        idx.Lookup(a, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task NameOnlyMatch_IsNeverLinked()
    {
        Write("Prefetch.csv", PrefetchHeader + Pf("tool.exe", "2024-01-01 10:00:08", "", @"\VOLUME{x}\USERS\A\TOOL.EXE"));
        using var idx = await Build();
        // same executable name at another path, and a node with no path at all
        var elsewhere = Node(10, "tool.exe", @"D:\Temp\tool.exe", "2024-01-01 10:00:00");
        var noPath = Node(11, "TOOL.EXE", "", "2024-01-01 10:00:00");
        idx.Attach(new[] { elsewhere, noPath });
        idx.Lookup(elsewhere, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.Lookup(noPath, "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task PrefetchRowWithoutAFilesLoadedEntryForTheProgram_HasNoPath()
    {
        Write("Prefetch.csv", PrefetchHeader + "TOOL.EXE-1.pf,TOOL.EXE,1,2024-01-01 10:00:08,,\"\\VOLUME{x}\\WINDOWS\\SYSTEM32\\NTDLL.DLL\"\r\n");
        using var idx = await Build();
        idx.Lookup(Node(4, "ntdll.dll", @"C:\Windows\System32\ntdll.dll", "2024-01-01 10:00:00"), "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.Lookup(Node(5, "tool.exe", @"C:\Windows\System32\tool.exe", "2024-01-01 10:00:00"), "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task PidIsExactOnlyInsideTheLifetime()
    {
        var catalog = Catalog("""
            { "sources": [ { "id": "ev", "fileRegex": "^Ev\\.csv$", "joinKind": "pid-time", "tab": "Timeline", "howToGet": "Ingest",
              "columns": { "pid": ["Pid"], "time": ["When"] } } ] }
            """);
        Write("Ev.csv", "Pid,When\r\n" +
            "500,2024-03-01 10:00:05\r\n" +     // inside the first life
            "500,2024-03-01 12:00:00\r\n" +     // the PID was reused later
            "500,\r\n");                        // no time: the PID is all there is
        using var idx = await Build(catalog);

        var first = idx.Lookup(Node(500, "a.exe", "", "2024-03-01 10:00:00", "2024-03-01 10:30:00"), "ev");
        first.Rows.Select(r => r.Cells[1]).Should().BeEquivalentTo(new[] { "2024-03-01 10:00:05", "" });
        first.Rows.Should().OnlyContain(r => r.Tier == LinkTier.Exact);
        first.TotalRows.Should().Be(2);

        var second = idx.Lookup(Node(500, "b.exe", "", "2024-03-01 11:59:59"), "ev");
        second.Rows.Select(r => r.Cells[1]).Should().BeEquivalentTo(new[] { "2024-03-01 12:00:00", "" });

        idx.Lookup(Node(501, "c.exe"), "ev").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task ReusedPidOutsideTheLifetime_IsNotLinked_AndNeitherIsTheName()
    {
        var catalog = Catalog("""
            { "sources": [ { "id": "ev", "fileRegex": "^Ev\\.csv$", "joinKind": "pid-time", "tab": "Timeline", "howToGet": "x",
              "columns": { "pid": ["Pid"], "name": ["Image"], "time": ["When"] } } ] }
            """);
        Write("Ev.csv", "Pid,Image,When\r\n500,old.exe,2024-03-01 08:00:00\r\n");
        using var idx = await Build(catalog);

        // exited node: the row is before its start
        idx.Lookup(Node(500, "other.exe", "", "2024-03-01 11:00:00", "2024-03-01 11:10:00"), "ev").Status.Should().Be(EvidenceStatus.NoRows);
        // an older instance of the PID without exit time is bounded by the next one
        var older = Node(500, "older.exe", "", "2024-03-01 07:00:00");
        var newer = Node(500, "newer.exe", "", "2024-03-01 09:00:00");
        idx.Attach(new[] { older, newer });
        idx.Lookup(older, "ev").Rows.Should().ContainSingle();
        idx.Lookup(newer, "ev").Status.Should().Be(EvidenceStatus.NoRows);
        // the same name with another PID is not a link
        idx.Lookup(Node(777, "old.exe", "", "2024-03-01 11:00:00"), "ev").Status.Should().Be(EvidenceStatus.NoRows);
    }

    [Fact]
    public async Task GuidMatchIsExactEvenWithAnotherPid()
    {
        var catalog = Catalog("""
            { "sources": [ { "id": "sys", "fileRegex": "^Sys\\.csv$", "joinKind": "guid", "tab": "Timeline", "howToGet": "x",
              "columns": { "pid": ["Pid"], "guid": ["Guid"], "time": ["When"] } } ] }
            """);
        Write("Sys.csv", "Pid,Guid,When\r\n" +
            "1,{aaaaaaaa-0000-0000-0000-000000000001},2024-03-01 10:00:05\r\n" +
            "2,{aaaaaaaa-0000-0000-0000-000000000002},2024-03-01 10:00:05\r\n");
        using var idx = await Build(catalog);

        var r = idx.Lookup(Node(999, "x.exe", "", "2024-03-01 10:00:00", null, "{AAAAAAAA-0000-0000-0000-000000000002}"), "sys");
        r.Rows.Should().ContainSingle().Which.Cells[0].Should().Be("2");
        r.Rows[0].Tier.Should().Be(LinkTier.Exact);
    }

    [Fact]
    public async Task Status_NotFoundCarriesTheHowToGetHint_AndEmptyFilesAreNoRows()
    {
        Write("Prefetch.csv", PrefetchHeader);
        using var idx = await Build();

        idx.Lookup(Node(1, "a.exe"), "prefetch").Status.Should().Be(EvidenceStatus.NoRows);
        idx.StatusOf("prefetch")!.Status.Should().Be(EvidenceStatus.NoRows);
        idx.StatusOf("eventlog")!.Status.Should().Be(EvidenceStatus.NotFound);
        idx.StatusOf("eventlog")!.HowToGet.Should().Be("Ingest › Event logs");
        idx.LookupAll(Node(1, "a.exe")).Should().HaveCount(ProcessEvidenceCatalog.Sources.Count);

        using var none = await ProcessEvidenceIndex.BuildAsync(Path.Combine(_host, "nothing"), CancellationToken.None);
        none.Lookup(Node(1, "a.exe"), "prefetch").Status.Should().Be(EvidenceStatus.NotFound);
        none.Lookup(Node(1, "a.exe"), "prefetch").HowToGet.Should().Be("Ingest › Prefetch");
    }

    [Fact]
    public async Task Rows_AreCappedAtMaxRows_WithTheRealTotal()
    {
        var sb = new StringBuilder(PrefetchHeader);
        for (int i = 0; i < ProcessFacetIndex.MaxRows + 25; i++) sb.Append(Pf("a.exe", "2024-01-01 10:00:05", "", @"\VOLUME{x}\X\A.EXE"));
        Write("Prefetch.csv", sb.ToString());
        using var idx = await Build();
        var r = idx.Lookup(Node(1, "a.exe", @"C:\x\a.exe", "2024-01-01 10:00:00"), "prefetch");
        r.Rows.Should().HaveCount(ProcessFacetIndex.MaxRows);
        r.TotalRows.Should().Be(ProcessFacetIndex.MaxRows + 25);
        r.Truncated.Should().BeTrue();
    }
}
