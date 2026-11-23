using System.Diagnostics;
using System.Text;
using EzForensics.Core.Explore.Search;
using FluentAssertions;

namespace EzForensics.Core.Tests.Explore.Search;

public sealed class FileSearcherTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string Root => _tmp.Path;

    private string Put(string rel, string text, Encoding? enc = null)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, enc ?? new UTF8Encoding(false));
        return p;
    }

    private string PutBytes(string rel, byte[] bytes)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private SearchOptions Opt(Func<SearchOptions, SearchOptions>? f = null)
    {
        var o = new SearchOptions { Roots = new[] { Root } };
        return f == null ? o : f(o);
    }

    private static async Task<List<SearchResult>> Run(SearchOptions o, FileSearcher? s = null, List<SearchProgress>? progress = null)
    {
        var list = new List<SearchResult>();
        var p = progress == null ? null : new SyncProgress(progress);
        await foreach (var r in (s ?? new FileSearcher()).SearchAsync(o, p)) list.Add(r);
        return list;
    }

    private static List<string> Names(IEnumerable<SearchResult> r) => r.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

    private sealed class SyncProgress : IProgress<SearchProgress>
    {
        private readonly List<SearchProgress> _l;
        public SyncProgress(List<SearchProgress> l) => _l = l;
        public void Report(SearchProgress value) { lock (_l) _l.Add(value); }
    }

    private void NameTree()
    {
        Put("report.txt", "a");
        Put("Report-final.log", "a");
        Put("notes.md", "a");
        Put(@"sub\data.log", "a");
        Put(@"sub\deep\image.jpg", "a");
        Directory.CreateDirectory(Path.Combine(Root, "reports"));
    }

    // ---------------- Names ----------------

    [Fact]
    public async Task Contains_name_is_case_insensitive_by_default_and_case_sensitive_on_request()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = "report" }))).Should().Equal("Report-final.log", "report.txt");
        Names(await Run(Opt(o => o with { NameText = "report", NameMatchCase = true }))).Should().Equal("report.txt");
    }

    [Fact]
    public async Task Contains_name_supports_implicit_and_or_not_and_quotes()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = "report final" }))).Should().Equal("Report-final.log");
        Names(await Run(Opt(o => o with { NameText = "notes OR image" }))).Should().Equal("image.jpg", "notes.md");
        Names(await Run(Opt(o => o with { NameText = "report AND NOT final" }))).Should().Equal("report.txt");
        Names(await Run(Opt(o => o with { NameText = "(data OR notes) NOT md" }))).Should().Equal("data.log");
        Names(await Run(Opt(o => o with { NameText = "\"t-f\"" }))).Should().Equal("Report-final.log");
    }

    [Fact]
    public async Task Wildcard_terms_separated_by_semicolon_or_space_are_ored()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = "*.log;*.txt", NameMode = NameMatchMode.Wildcard })))
            .Should().Equal("Report-final.log", "data.log", "report.txt");
        Names(await Run(Opt(o => o with { NameText = "*.md *.jpg", NameMode = NameMatchMode.Wildcard }))).Should().Equal("image.jpg", "notes.md");
        Names(await Run(Opt(o => o with { NameText = "*.log AND NOT data*", NameMode = NameMatchMode.Wildcard }))).Should().Equal("Report-final.log");
        Names(await Run(Opt(o => o with { NameText = "r*.TXT", NameMode = NameMatchMode.Wildcard, NameMatchCase = true }))).Should().BeEmpty();
        Names(await Run(Opt(o => o with { NameText = "?otes.md", NameMode = NameMatchMode.Wildcard }))).Should().Equal("notes.md");
        // No wildcard in a term: substring, like Agent Ransack.
        Names(await Run(Opt(o => o with { NameText = "ima", NameMode = NameMatchMode.Wildcard }))).Should().Equal("image.jpg");
    }

    [Fact]
    public async Task Regex_name()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = @"^(report|data)\.\w+$", NameMode = NameMatchMode.Regex }))).Should().Equal("data.log", "report.txt");
        Names(await Run(Opt(o => o with { NameText = @"^R", NameMode = NameMatchMode.Regex, NameMatchCase = true }))).Should().Equal("Report-final.log");
    }

    [Fact]
    public async Task Target_files_folders_or_both()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = "report", Target = SearchTarget.Folders }))).Should().Equal("reports");
        Names(await Run(Opt(o => o with { NameText = "report", Target = SearchTarget.Both }))).Should().Equal("Report-final.log", "report.txt", "reports");
        var folders = await Run(Opt(o => o with { Target = SearchTarget.Folders }));
        folders.Should().OnlyContain(r => r.IsDirectory);
        Names(folders).Should().Equal("deep", "reports", "sub");
    }

    [Fact]
    public async Task Subfolders_off_searches_only_the_root_level()
    {
        NameTree();
        Names(await Run(Opt(o => o with { NameText = "*.log", NameMode = NameMatchMode.Wildcard, IncludeSubfolders = false })))
            .Should().Equal("Report-final.log");
    }

    [Fact]
    public async Task Exclude_folders_by_name_wildcard_or_full_path()
    {
        NameTree();
        Put(@"node_modules\pkg\index.log", "a");
        Names(await Run(Opt(o => o with { NameText = "*.log", NameMode = NameMatchMode.Wildcard, ExcludeFolders = new[] { "node_modules" } })))
            .Should().Equal("Report-final.log", "data.log");
        Names(await Run(Opt(o => o with { NameText = "*.jpg", NameMode = NameMatchMode.Wildcard, ExcludeFolders = new[] { Path.Combine(Root, "sub") + "\\" } })))
            .Should().BeEmpty();
        Names(await Run(Opt(o => o with { NameText = "*.jpg", NameMode = NameMatchMode.Wildcard, ExcludeFolders = new[] { "de*" } }))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_file_root_and_overlapping_roots_give_no_duplicates()
    {
        NameTree();
        var file = Path.Combine(Root, "notes.md");
        var r = await Run(new SearchOptions { Roots = new[] { Root, Path.Combine(Root, "sub"), Root + "\\", file } });
        r.Select(x => x.FullPath).Should().OnlyHaveUniqueItems();
        r.Should().HaveCount(5);
        (await Run(new SearchOptions { Roots = new[] { file } })).Should().ContainSingle().Which.FullPath.Should().Be(file);
    }

    // ---------------- Filters ----------------

    [Fact]
    public async Task Size_filters_apply_to_files()
    {
        Put("small.txt", "12345");
        Put("big.txt", new string('x', 5000));
        Names(await Run(Opt(o => o with { MinSize = 1000 }))).Should().Equal("big.txt");
        Names(await Run(Opt(o => o with { MaxSize = 5 }))).Should().Equal("small.txt");
        Names(await Run(Opt(o => o with { MinSize = 5, MaxSize = 5000 }))).Should().Equal("big.txt", "small.txt");
    }

    [Fact]
    public async Task Date_filters()
    {
        var old = Put("old.txt", "a");
        var mid = Put("mid.txt", "a");
        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, t0);
        File.SetLastWriteTimeUtc(mid, t0.AddDays(30));
        File.SetCreationTimeUtc(old, t0);
        Names(await Run(Opt(o => o with { ModifiedBefore = t0.AddDays(1) }))).Should().Equal("old.txt");
        Names(await Run(Opt(o => o with { ModifiedAfter = t0.AddDays(10), ModifiedBefore = t0.AddDays(40) }))).Should().Equal("mid.txt");
        Names(await Run(Opt(o => o with { ModifiedAfter = t0 }))).Should().Equal("mid.txt", "old.txt");     // inclusive
        Names(await Run(Opt(o => o with { CreatedBefore = t0.AddDays(1) }))).Should().Equal("old.txt");
        Names(await Run(Opt(o => o with { AccessedAfter = DateTime.UtcNow.AddYears(1) }))).Should().BeEmpty();
    }

    [Fact]
    public async Task Attribute_filters_and_hidden_exclusion()
    {
        var h = Put("hidden.txt", "a");
        var ro = Put("ro.txt", "a");
        Put("plain.txt", "a");
        Put(@"hid\inside.txt", "a");
        File.SetAttributes(h, FileAttributes.Hidden);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var hidDir = Path.Combine(Root, "hid");
        File.SetAttributes(hidDir, File.GetAttributes(hidDir) | FileAttributes.Hidden);
        try
        {
            Names(await Run(Opt()))
                .Should().Equal("hidden.txt", "inside.txt", "plain.txt", "ro.txt");                   // hidden included by default
            Names(await Run(Opt(o => o with { AttributesMustHave = FileAttributes.Hidden }))).Should().Equal("hidden.txt");
            Names(await Run(Opt(o => o with { AttributesMustNotHave = FileAttributes.ReadOnly | FileAttributes.Hidden })))
                .Should().Equal("inside.txt", "plain.txt");
            Names(await Run(Opt(o => o with { IncludeHidden = false }))).Should().Equal("plain.txt", "ro.txt"); // hidden folder not entered
        }
        finally
        {
            File.SetAttributes(ro, FileAttributes.Normal);
            File.SetAttributes(h, FileAttributes.Normal);
        }
    }

    // ---------------- Content ----------------

    [Fact]
    public async Task Literal_content_with_case_and_hits()
    {
        Put("a.txt", "first line\nThe Password is here\nnothing\npassword password\n");
        Put("b.txt", "no match here\n");
        var r = (await Run(Opt(o => o with { ContentText = "password" }))).Should().ContainSingle().Subject;
        r.Name.Should().Be("a.txt");
        r.HitCount.Should().Be(3);
        r.Hits.Select(h => h.LineNumber).Should().Equal(2, 4);
        r.Hits[1].Spans.Should().Equal(new MatchSpan(0, 8), new MatchSpan(9, 8));
        r.EncodingName.Should().Be("UTF-8");

        var cs = (await Run(Opt(o => o with { ContentText = "Password", ContentMatchCase = true }))).Should().ContainSingle().Subject;
        cs.HitCount.Should().Be(1);
        cs.Hits[0].Spans.Should().Equal(new MatchSpan(4, 8));
    }

    [Fact]
    public async Task Whole_word_literal_and_regex()
    {
        Put("a.txt", "admin administrator\nsysadmin\n_admin admin_x admin.\n");
        var r = (await Run(Opt(o => o with { ContentText = "admin", ContentWholeWord = true }))).Single();
        r.HitCount.Should().Be(2);
        r.Hits.Select(h => (h.LineNumber, h.Spans[0].Start)).Should().Equal((1, 0), (3, 15));

        var rx = (await Run(Opt(o => o with { ContentText = "adm[a-z]+", ContentMode = ContentMatchMode.Regex, ContentWholeWord = true }))).Single();
        rx.HitCount.Should().Be(3);   // admin, administrator, and the last "admin." ("_admin" / "admin_x" touch '_')
        rx.Hits[0].Spans.Should().Equal(new MatchSpan(0, 5), new MatchSpan(6, 13));

        var rx2 = (await Run(Opt(o => o with { ContentText = @"\d{3}-\d{4}", ContentMode = ContentMatchMode.Regex }))).Should().BeEmpty();
        Put("b.txt", "call 555-1234 now");
        var rx3 = (await Run(Opt(o => o with { ContentText = @"\d{3}-\d{4}", ContentMode = ContentMatchMode.Regex }))).Single();
        rx3.Hits[0].Spans.Should().Equal(new MatchSpan(5, 8));
    }

    [Fact]
    public async Task Boolean_content_is_evaluated_per_file()
    {
        Put("both.txt", "user=bob\nsomething\npassword=1\n");
        Put("one.txt", "password only\n");
        Put("example.txt", "password\nuser\nexample config\n");
        Names(await Run(Opt(o => o with { ContentText = "password user", ContentMode = ContentMatchMode.Boolean })))
            .Should().Equal("both.txt", "example.txt");
        var r = await Run(Opt(o => o with { ContentText = "password AND user AND NOT example", ContentMode = ContentMatchMode.Boolean }));
        r.Should().ContainSingle().Which.Name.Should().Be("both.txt");
        r[0].Hits.Select(h => h.LineNumber).Should().Equal(1, 3);
        Names(await Run(Opt(o => o with { ContentText = "\"password only\" OR example", ContentMode = ContentMatchMode.Boolean })))
            .Should().Equal("example.txt", "one.txt");
    }

    [Fact]
    public async Task Utf16LE_with_and_without_bom_and_utf16BE()
    {
        PutBytes("bom.txt", new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("hello\r\nsecret token\r\n")).ToArray());
        PutBytes("nobom.txt", Encoding.Unicode.GetBytes("line one\nthe secret\n"));
        PutBytes("be.txt", Encoding.BigEndianUnicode.GetBytes("a secret here"));
        var r = await Run(Opt(o => o with { ContentText = "secret" }));
        Names(r).Should().Equal("be.txt", "bom.txt", "nobom.txt");
        r.Single(x => x.Name == "bom.txt").Hits[0].Should().Match<ContentHit>(h => h.LineNumber == 2 && h.LineText == "secret token");
        r.Single(x => x.Name == "nobom.txt").EncodingName.Should().Be("UTF-16LE");
        r.Single(x => x.Name == "be.txt").EncodingName.Should().Be("UTF-16BE");

        // Forced encodings.
        Names(await Run(Opt(o => o with { ContentText = "secret", Encoding = ContentEncoding.Utf16LE })))
            .Should().Contain(new[] { "bom.txt", "nobom.txt" }).And.NotContain("be.txt");
    }

    [Fact]
    public async Task Ansi_1252_text_and_utf8_are_both_found()
    {
        var cp = EzForensics.Core.Explore.Search.TextDecoding.Windows1252;
        PutBytes("ansi.txt", cp.GetBytes("Le café coûte 5€\n"));
        Put("utf8.txt", "un café\n");
        var r = await Run(Opt(o => o with { ContentText = "café" }));
        Names(r).Should().Equal("ansi.txt", "utf8.txt");
        Names(await Run(Opt(o => o with { ContentText = "5€" }))).Should().Equal("ansi.txt");
        Names(await Run(Opt(o => o with { ContentText = "coûte", Encoding = ContentEncoding.Ansi }))).Should().Equal("ansi.txt");
    }

    [Fact]
    public async Task Binary_files_are_skipped_by_default_and_searched_on_request()
    {
        var bytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF }
            .Concat(Encoding.ASCII.GetBytes("xx needle yy")).Concat(new byte[] { 0, 1, 2 }).ToArray();
        PutBytes("prog.bin", bytes);
        Put("t.txt", "needle");
        var s = new FileSearcher();
        var progress = new List<SearchProgress>();
        Names(await Run(Opt(o => o with { ContentText = "needle" }), s, progress)).Should().Equal("t.txt");
        s.Skipped.Should().ContainSingle(x => x.Reason == SkipReason.Binary);
        progress.Last().IsComplete.Should().BeTrue();
        progress.Last().SkippedByReason[SkipReason.Binary].Should().Be(1);

        var withBin = await Run(Opt(o => o with { ContentText = "needle", SkipBinary = false }));
        Names(withBin).Should().Equal("prog.bin", "t.txt");
    }

    [Fact]
    public async Task Context_lines_and_spans_and_long_line_window()
    {
        var lines = Enumerable.Range(1, 10).Select(i => i == 5 ? "xx MATCH yy match" : "line " + i).ToList();
        lines.Add(new string('a', 1000) + "match" + new string('b', 1000));
        Put("c.txt", string.Join("\r\n", lines));
        var r = (await Run(Opt(o => o with { ContentText = "match", ContextLines = 2 }))).Single();
        var h = r.Hits[0];
        h.LineNumber.Should().Be(5);
        h.Before.Should().Equal("line 3", "line 4");
        h.After.Should().Equal("line 6", "line 7");
        h.Spans.Should().Equal(new MatchSpan(3, 5), new MatchSpan(12, 5));
        h.LineText.Substring(h.Spans[1].Start, 5).Should().Be("match");

        var lng = r.Hits[1];
        lng.LineNumber.Should().Be(11);
        lng.LineText.Length.Should().Be(400);
        lng.LineTextOffset.Should().BeGreaterThan(0);
        lng.LineText.Substring(lng.Spans[0].Start, lng.Spans[0].Length).Should().Be("match");
        lng.After.Should().BeEmpty();
        lng.Before.Should().HaveCount(2).And.OnlyContain(s => s.Length <= 400);

        var none = (await Run(Opt(o => o with { ContentText = "match", ContextLines = 0 }))).Single();
        none.Hits[0].Before.Should().BeEmpty();
        none.Hits[0].After.Should().BeEmpty();
    }

    [Fact]
    public async Task Max_hits_per_file_caps_and_flags_truncation()
    {
        Put("many.txt", string.Join("\n", Enumerable.Repeat("hit hit", 100)));
        var r = (await Run(Opt(o => o with { ContentText = "hit", MaxHitsPerFile = 5 }))).Single();
        r.HitCount.Should().Be(5);
        r.HitsTruncated.Should().BeTrue();
        r.Hits.Sum(h => h.Spans.Count).Should().Be(5);
        r.Hits.Should().HaveCount(3);
        var all = (await Run(Opt(o => o with { ContentText = "hit" }))).Single();
        all.HitCount.Should().Be(200);
        all.HitsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Max_results_stops_the_search()
    {
        for (int i = 0; i < 30; i++) Put($"f{i}.txt", "x");
        var progress = new List<SearchProgress>();
        var r = await Run(Opt(o => o with { MaxResults = 7 }), null, progress);
        r.Should().HaveCount(7);
        progress.Last().LimitReached.Should().BeTrue();
    }

    [Fact]
    public async Task Content_over_the_size_limit_is_skipped_with_a_reason()
    {
        Put("big.txt", new string('z', 3000) + " needle");
        var s = new FileSearcher();
        (await Run(Opt(o => o with { ContentText = "needle", MaxFileSizeForContent = 1000 }), s)).Should().BeEmpty();
        s.Skipped.Should().ContainSingle().Which.Reason.Should().Be(SkipReason.TooLarge);
    }

    [Fact]
    public async Task A_locked_file_is_skipped_and_the_search_goes_on()
    {
        var locked = Put("locked.txt", "needle");
        Put("open.txt", "needle");
        Put(@"sub\also.txt", "needle");
        var s = new FileSearcher();
        List<SearchResult> r;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = await Run(Opt(o => o with { ContentText = "needle" }), s);
        Names(r).Should().Equal("also.txt", "open.txt");
        s.Skipped.Should().ContainSingle().Which.Should().Match<SkippedItem>(x => x.Reason == SkipReason.FileUnreadable && x.Path == locked);
    }

    [Fact]
    public async Task Missing_root_among_valid_ones_fails_validation()
    {
        Put("a.txt", "x");
        var o = Opt(o => o with { Roots = new[] { Root, Path.Combine(Root, "nope") } });
        var act = async () => await Run(o);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task Junction_is_listed_but_not_followed()
    {
        Put(@"real\inner.txt", "x");
        var link = Path.Combine(Root, "loop");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Root}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) p.WaitForExit(10_000);
        if (!Directory.Exists(link)) return;   // junctions unavailable on this file system
        try
        {
            var s = new FileSearcher();
            var r = await Run(Opt(o => o with { Target = SearchTarget.Both }), s);
            Names(r).Should().Equal("inner.txt", "loop", "real");
            s.Skipped.Should().ContainSingle(x => x.Reason == SkipReason.LinkNotFollowed && x.Path == link);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Cancellation_stops_promptly_mid_file()
    {
        // ~20 MB of text per file and a linear but slow regex (no 'z' anywhere), so the scan is still running when cancelled.
        var p = Path.Combine(Root, "huge.txt");
        using (var w = new StreamWriter(p, false, new UTF8Encoding(false), 1 << 20))
        {
            var line = new string('q', 200);
            for (int i = 0; i < 100_000; i++) w.WriteLine(line);
        }
        for (int i = 0; i < 4; i++) File.Copy(p, Path.Combine(Root, $"huge{i}.txt"));
        using var cts = new CancellationTokenSource();
        var o = Opt(o => o with { ContentText = @"q.{0,50}?z", ContentMode = ContentMatchMode.Regex });
        var sw = new Stopwatch();
        var act = async () =>
        {
            cts.CancelAfter(300);
            await foreach (var _ in new FileSearcher().SearchAsync(o, null, cts.Token)) { }
        };
        var task = act.Should().ThrowAsync<OperationCanceledException>();
        sw.Start();
        await task;
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(300 + 4000, "a loaded test host needs slack; a full scan takes far longer");
    }

    [Fact]
    public async Task Progress_reports_counts()
    {
        NameTree();
        var progress = new List<SearchProgress>();
        await Run(Opt(), new FileSearcher { ProgressInterval = TimeSpan.Zero }, progress);
        var last = progress.Last();
        last.IsComplete.Should().BeTrue();
        last.FoldersScanned.Should().Be(4);
        last.FilesScanned.Should().Be(5);
        last.Matches.Should().Be(5);
        last.Cancelled.Should().BeFalse();
        progress.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Invalid_options_throw_argument_exception()
    {
        var act = async () => await Run(new SearchOptions());
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
