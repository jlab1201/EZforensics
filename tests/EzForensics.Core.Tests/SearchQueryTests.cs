using System.Text;
using EzForensics.Core.Results;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class SearchQueryTests
{
    private static Func<string[], bool> P(string text)
    {
        SearchQuery.TryParse(text, out var q, out var err).Should().BeTrue(err);
        q.Should().NotBeNull();
        return q!.Compile();
    }

    private static string Err(string text)
    {
        SearchQuery.TryParse(text, out var q, out var err).Should().BeFalse();
        q.Should().BeNull();
        err.Should().NotBeNullOrWhiteSpace();
        return err!;
    }

    private static string[] R(params string[] cells) => cells;

    [Fact]
    public void Implicit_and()
    {
        var f = P("alpha beta");
        f(R("Alpha one", "two BETA")).Should().BeTrue();
        f(R("alpha")).Should().BeFalse();
    }

    [Fact]
    public void Or_and_not()
    {
        P("a OR b")(R("b")).Should().BeTrue();
        P("a OR b")(R("c")).Should().BeFalse();
        P("NOT a")(R("b")).Should().BeTrue();
        P("NOT a")(R("xax")).Should().BeFalse();
        P("a AND b")(R("a", "b")).Should().BeTrue();
    }

    [Fact]
    public void Precedence_and_over_or()
    {
        var f = P("a OR b c"); // a OR (b AND c)
        f(R("a")).Should().BeTrue();
        f(R("b c")).Should().BeTrue();
        f(R("b")).Should().BeFalse();
    }

    [Fact]
    public void Precedence_not_over_and()
    {
        var f = P("NOT a b"); // (NOT a) AND b
        f(R("b")).Should().BeTrue();
        f(R("a b")).Should().BeFalse();
        f(R("c")).Should().BeFalse();
    }

    [Fact]
    public void Nested_parentheses()
    {
        var f = P("(a OR (b AND c)) AND NOT d");
        f(R("a")).Should().BeTrue();
        f(R("b", "c")).Should().BeTrue();
        f(R("b")).Should().BeFalse();
        f(R("a d")).Should().BeFalse();
        P("(a OR b) c")(R("b c")).Should().BeTrue();
    }

    [Fact]
    public void Quoted_phrases()
    {
        P("\"hello world\"")(R("say Hello World now")).Should().BeTrue();
        P("\"hello world\"")(R("hello", "world")).Should().BeFalse();
        P("\"OR\"")(R("x or y")).Should().BeTrue();
        P("\"a OR b\"")(R("a OR b")).Should().BeTrue();
        P("\"(x)\"")(R("(x)")).Should().BeTrue();
        P("\"say \"\"hi\"\"\"")(R("say \"hi\"")).Should().BeTrue();
    }

    [Fact]
    public void Lowercase_operators_are_literal_terms()
    {
        var f = P("a or b");   // a AND or AND b
        f(R("a or b")).Should().BeTrue();
        f(R("a b")).Should().BeFalse();
        P("not x")(R("not x")).Should().BeTrue();
        P("not x")(R("x")).Should().BeFalse();
        P("and")(R("sand")).Should().BeTrue();
    }

    [Fact]
    public void Operator_inside_a_word_is_not_an_operator()
    {
        var f = P("organ");
        f(R("organ")).Should().BeTrue();
        f(R("or")).Should().BeFalse();
        P("ORGAN")(R("organ")).Should().BeTrue();
        P("NOTE")(R("a note")).Should().BeTrue();
    }

    [Fact]
    public void Dash_is_literal()
    {
        SearchQuery.TryParse("-encodedcommand", out var q, out _).Should().BeTrue();
        q!.IsSingleTerm.Should().BeTrue();
        q.SingleTerm.Should().Be("-encodedcommand");
        q.Compile()(R("powershell -EncodedCommand AAA")).Should().BeTrue();
    }

    [Fact]
    public void Single_term_flags()
    {
        SearchQuery.TryParse("  foo ", out var q, out _).Should().BeTrue();
        q!.IsSingleTerm.Should().BeTrue();
        q.SingleTerm.Should().Be("foo");
        SearchQuery.TryParse("\"a b\"", out q, out _);
        q!.IsSingleTerm.Should().BeTrue();
        q.SingleTerm.Should().Be("a b");
        SearchQuery.TryParse("a b", out q, out _);
        q!.IsSingleTerm.Should().BeFalse();
        q.SingleTerm.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void Empty_input_is_no_constraint(string? text)
    {
        SearchQuery.TryParse(text, out var q, out var err).Should().BeTrue();
        q.Should().BeNull();
        err.Should().BeNull();
    }

    [Theory]
    [InlineData("(a")]
    [InlineData("a)")]
    [InlineData("((a)")]
    [InlineData(")")]
    [InlineData("()")]
    [InlineData("a ()")]
    [InlineData("OR a")]
    [InlineData("a OR")]
    [InlineData("AND a")]
    [InlineData("a AND")]
    [InlineData("a OR OR b")]
    [InlineData("a AND OR b")]
    [InlineData("a OR AND b")]
    [InlineData("NOT")]
    [InlineData("a NOT")]
    [InlineData("NOT AND a")]
    [InlineData("(NOT)")]
    [InlineData("\"abc")]
    [InlineData("a \"b c")]
    public void Errors(string text) => Err(text);

    [Theory]
    [InlineData("a b", "a AND b")]
    [InlineData("a OR b c", "a OR b AND c")]
    [InlineData("(psexec OR wmic) AND NOT admin", "(psexec OR wmic) AND NOT admin")]
    [InlineData("(psexec OR wmic) NOT admin", "(psexec OR wmic) AND NOT admin")]
    [InlineData("NOT (a OR b)", "NOT (a OR b)")]
    [InlineData("\"hello world\" x", "\"hello world\" AND x")]
    [InlineData("\"OR\"", "\"OR\"")]
    [InlineData("or not", "or AND not")]
    public void Display_string(string input, string display)
    {
        SearchQuery.TryParse(input, out var q, out _).Should().BeTrue();
        q!.ToDisplayString().Should().Be(display);
    }

    [Theory]
    [InlineData("a OR b c")]
    [InlineData("NOT a b")]
    [InlineData("(a OR b) AND NOT (c d)")]
    [InlineData("\"x y\" OR \"say \"\"hi\"\"\" z")]
    [InlineData("\"OR\" \"AND\" or and")]
    [InlineData("NOT NOT a")]
    [InlineData("\"(\" \")\" -x")]
    public void Display_round_trips_to_same_semantics(string input)
    {
        SearchQuery.TryParse(input, out var q, out _).Should().BeTrue();
        var d = q!.ToDisplayString();
        SearchQuery.TryParse(d, out var q2, out var err).Should().BeTrue(err);
        q2!.ToDisplayString().Should().Be(d);
        var f1 = q.Compile(); var f2 = q2.Compile();
        var words = new[] { "a", "b", "c", "d", "x y", "say \"hi\"", "z", "OR", "AND", "or", "and", "(", ")", "-x" };
        // every subset of cells (bitmask over a small word list) must give the same answer
        for (var m = 0; m < 1 << words.Length; m += 7)
        {
            var row = words.Where((_, i) => (m & (1 << i)) != 0).ToArray();
            f2(row).Should().Be(f1(row), string.Join("|", row));
        }
    }
}

public class SearchQueryFilterEngineTests : IDisposable
{
    private readonly string _csv = Path.Combine(Path.GetTempPath(), "ezsq_" + Guid.NewGuid().ToString("N") + ".csv");

    public SearchQueryFilterEngineTests() => File.WriteAllText(_csv,
        "Source,Message,Id\r\n" +
        "MFT,psexec started,1\r\n" +       // 0
        "EVTX,wmic process call,2\r\n" +   // 1
        "EVTX,psexec admin,3\r\n" +        // 2
        "Prefetch,notepad,4\r\n" +         // 3
        "EVTX,hello world,5\r\n",          // 4
        new UTF8Encoding(false));

    public void Dispose() { try { File.Delete(_csv); } catch { } }

    private async Task<int[]> Run(FilterSpec spec)
    {
        using var idx = await CsvIndex.OpenAsync(_csv, null, default);
        await idx.IndexingCompleted;
        return await FilterEngine.FilterAsync(idx, spec, null, default);
    }

    [Fact]
    public async Task Single_term_is_plain_contains()
    {
        (await Run(new FilterSpec { GlobalText = "PSEXEC" })).Should().Equal(0, 2);
        (await Run(new FilterSpec { GlobalText = "\"hello world\"" })).Should().Equal(4);
    }

    [Fact]
    public async Task Compound_query()
    {
        (await Run(new FilterSpec { GlobalText = "(psexec OR wmic) AND NOT admin" })).Should().Equal(0, 1);
        (await Run(new FilterSpec { GlobalText = "evtx psexec" })).Should().Equal(2);
        (await Run(new FilterSpec { GlobalText = "hello world" })).Should().Equal(4);
    }

    [Fact]
    public async Task Invalid_query_throws_ArgumentException()
    {
        var act = () => Run(new FilterSpec { GlobalText = "(psexec" });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Display_string_keeps_contains_for_single_term()
    {
        new FilterSpec { GlobalText = "x" }.ToDisplayString().Should().Be("Contains(*, 'x')");
        new FilterSpec { GlobalText = "a OR b" }.ToDisplayString().Should().Be("Search(*, 'a OR b')");
        new FilterSpec { GlobalText = "a b" }.ToDisplayString().Should().Be("Search(*, 'a AND b')");
    }
}
