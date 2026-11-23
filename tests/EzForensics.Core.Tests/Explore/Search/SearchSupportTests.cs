using System.Text;
using EzForensics.Core.Explore.Search;
using FluentAssertions;

namespace EzForensics.Core.Tests.Explore.Search;

public sealed class SearchSupportTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    // ---------------- Expression ----------------

    [Theory]
    [InlineData("a b", "a b", true)]
    [InlineData("a b", "a", false)]
    [InlineData("a OR b", "b", true)]
    [InlineData("a AND NOT b", "a b", false)]
    [InlineData("a AND NOT b", "a", true)]
    [InlineData("(a OR b) c", "b c", true)]
    [InlineData("(a OR b) c", "a b", false)]
    [InlineData("a OR b c", "a", true)]          // AND binds tighter than OR
    [InlineData("\"x y\" z", "x y|z", true)]
    [InlineData("a or b", "a or b", true)]        // lowercase is a term, AND'ed
    public void Expression_with_default_and(string expr, string present, bool expected)
    {
        SearchExpression.TryParse(expr, false, false, out var e, out var err).Should().BeTrue(err);
        var set = present.Contains('|') ? present.Split('|') : present.Split(' ');
        e!.Evaluate(t => set.Contains(t)).Should().Be(expected);
    }

    [Fact]
    public void Expression_with_default_or_and_semicolons()
    {
        SearchExpression.TryParse("*.log;*.txt *.md", true, true, out var e, out _).Should().BeTrue();
        e!.Terms.Should().Equal("*.log", "*.txt", "*.md");
        e.Evaluate(t => t == "*.md").Should().BeTrue();
        SearchExpression.TryParse("a NOT b", true, false, out var n, out _).Should().BeTrue();
        n!.PositiveTerms.Should().Equal("a");
        n.Evaluate(_ => false).Should().BeTrue();      // a OR (NOT b)
        n.Evaluate(t => t == "b").Should().BeFalse();
    }

    [Theory]
    [InlineData("(a")]
    [InlineData("a)")]
    [InlineData("a AND")]
    [InlineData("NOT")]
    [InlineData("\"open")]
    [InlineData("()")]
    [InlineData("OR a")]
    [InlineData("   ")]
    public void Expression_errors(string text)
    {
        SearchExpression.TryParse(text, false, false, out var e, out var err).Should().BeFalse();
        e.Should().BeNull();
        err.Should().NotBeNullOrWhiteSpace();
    }

    // ---------------- Validation ----------------

    [Fact]
    public void Validation_reports_readable_errors()
    {
        SearchValidation.Validate(new SearchOptions()).Should().ContainSingle().Which.Should().Contain("at least one folder");

        var root = _tmp.Path;
        var ok = new SearchOptions { Roots = new[] { root } };
        SearchValidation.Validate(ok).Should().BeEmpty();

        SearchValidation.Validate(ok with { Roots = new[] { Path.Combine(root, "missing") } }).Should().ContainSingle(e => e.Contains("does not exist"));
        SearchValidation.Validate(ok with { Roots = new[] { Path.Combine(root, "missing") } }, checkRootsExist: false).Should().BeEmpty();

        var badRx = SearchValidation.Validate(ok with { NameText = "a(", NameMode = NameMatchMode.Regex });
        badRx.Should().ContainSingle().Which.Should().StartWith("File name regular expression:").And.Contain("a(");
        SearchValidation.Validate(ok with { ContentText = "[z", ContentMode = ContentMatchMode.Regex }).Should().ContainSingle(e => e.StartsWith("Containing text regular expression"));
        SearchValidation.Validate(ok with { ContentText = "a AND", ContentMode = ContentMatchMode.Boolean }).Should().ContainSingle(e => e.StartsWith("Containing text:"));
        SearchValidation.Validate(ok with { NameText = "(x", NameMode = NameMatchMode.Wildcard }).Should().ContainSingle(e => e.StartsWith("File name:"));
        SearchValidation.Validate(ok with { MinSize = 10, MaxSize = 5 }).Should().ContainSingle(e => e.Contains("Minimum size is larger"));
        SearchValidation.Validate(ok with { ModifiedAfter = DateTime.UtcNow, ModifiedBefore = DateTime.UtcNow.AddDays(-1) })
            .Should().ContainSingle(e => e.StartsWith("Modified after"));
        SearchValidation.Validate(ok with { Target = SearchTarget.Folders, ContentText = "x" }).Should().ContainSingle(e => e.Contains("only applies to files"));
        SearchValidation.Validate(ok with { AttributesMustHave = FileAttributes.Hidden, AttributesMustNotHave = FileAttributes.Hidden })
            .Should().ContainSingle(e => e.Contains("both required and excluded"));
        SearchValidation.Validate(ok with { MaxResults = 0, MaxHitsPerFile = 0, ContextLines = 99 }).Should().HaveCount(3);
        // Literal content is never parsed as an expression or regex.
        SearchValidation.Validate(ok with { ContentText = "a( AND" }).Should().BeEmpty();
    }

    // ---------------- Saved searches ----------------

    [Fact]
    public void Saved_searches_roundtrip_all_options()
    {
        var path = Path.Combine(_tmp.Path, "explore", "saved-searches.json");
        var store = new SavedSearches(path);
        store.Load().Should().BeEmpty();
        var o = new SearchOptions
        {
            Roots = new[] { @"C:\Evidence", @"D:\x.bin" },
            IncludeSubfolders = false,
            ExcludeFolders = new[] { "node_modules", @"C:\Windows\WinSxS" },
            NameText = "*.log;*.txt",
            NameMode = NameMatchMode.Wildcard,
            NameMatchCase = true,
            Target = SearchTarget.Both,
            ContentText = "pass AND NOT example",
            ContentMode = ContentMatchMode.Boolean,
            ContentWholeWord = true,
            Encoding = ContentEncoding.Utf16LE,
            SkipBinary = false,
            ContextLines = 4,
            MinSize = 1, MaxSize = 99,
            ModifiedAfter = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            AttributesMustHave = FileAttributes.Hidden | FileAttributes.System,
            AttributesMustNotHave = FileAttributes.ReparsePoint,
            IncludeHidden = true,
            MaxResults = 50, MaxHitsPerFile = 7,
        };
        store.Save("My search", o);
        store.Save("Other", new SearchOptions());
        store.Save("my SEARCH", o with { ContextLines = 3 });   // replaces, case-insensitive

        var loaded = new SavedSearches(path).Load();
        loaded.Select(s => s.Name).Should().Equal("my SEARCH", "Other");
        var back = loaded[0].Options;
        back.Should().BeEquivalentTo(o with { ContextLines = 3 });
        File.ReadAllText(path).Should().Contain("\"Wildcard\"").And.Contain("Hidden, System");
        Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().BeEmpty();

        store.Delete("other").Should().BeTrue();
        store.Delete("other").Should().BeFalse();
        store.Get("MY search").Should().NotBeNull();
    }

    [Fact]
    public void Corrupt_saved_searches_file_is_backed_up_and_starts_empty()
    {
        var path = Path.Combine(_tmp.Path, "saved-searches.json");
        File.WriteAllText(path, "{ not json ");
        var store = new SavedSearches(path);
        store.Load().Should().BeEmpty();
        store.CorruptBackupPath.Should().NotBeNull();
        File.ReadAllText(store.CorruptBackupPath!).Should().Be("{ not json ");
        File.Exists(path).Should().BeFalse();
        store.Save("a", new SearchOptions { Roots = new[] { "C:\\" } });
        new SavedSearches(path).Load().Should().ContainSingle();
    }

    [Fact]
    public void Default_path_is_under_the_data_folder()
    {
        SavedSearches.DefaultPath.Should().EndWith(Path.Combine("explore", "saved-searches.json"));
    }

    // ---------------- CSV ----------------

    [Fact]
    public void Csv_export_quotes_and_writes_a_bom()
    {
        var t = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var file = new SearchResult(@"C:\a\b, c.txt", "b, c.txt", @"C:\a", false, 42, t, t, t, FileAttributes.Hidden | FileAttributes.Archive)
        {
            HitCount = 2,
            Hits = new[] { new ContentHit(3, "say \"hi\"", 0, new[] { new MatchSpan(5, 2) }, Array.Empty<string>(), Array.Empty<string>()) },
        };
        var dir = new SearchResult(@"C:\a\=cmd", "=cmd", @"C:\a", true, 0, t, t, t, FileAttributes.Directory);
        var path = Path.Combine(_tmp.Path, "out", "results.csv");
        SearchCsvExport.Write(path, new[] { file, dir }).Should().Be(2);

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var lines = File.ReadAllText(path, Encoding.UTF8).Split("\r\n");
        lines[0].Should().Be("Path,Name,Folder,Type,Size,Modified (UTC),Created (UTC),Accessed (UTC),Attributes,Hits,First hit line");
        lines[1].Should().Be("\"C:\\a\\b, c.txt\",\"b, c.txt\",C:\\a,File,42,2025-05-06 07:08:09,2025-05-06 07:08:09,2025-05-06 07:08:09,\"Hidden, Archive\",2,\"3: say \"\"hi\"\"\"");
        lines[2].Should().StartWith("C:\\a\\=cmd,=cmd,C:\\a,Folder,,");

        var sb = new StringWriter();
        SearchCsvExport.Write(sb, new[] { dir }, escapeFormulas: true);
        sb.ToString().Split("\r\n")[1].Should().StartWith("C:\\a\\=cmd,'=cmd,");
    }
}
