using EzForensics.Core.Knowledge;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace EzForensics.Core.Tests.Knowledge;

public class KnowledgeLabelerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf-klab-" + Guid.NewGuid().ToString("N"));

    public KnowledgeLabelerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private KnowledgeStore NewStore() => new(Path.Combine(_dir, "k.db"));

    private static long Add(KnowledgeStore s, long src, string name, string[]? tags = null) =>
        s.InsertNote(src, new VaultNote(name + ".md", name, "t", tags ?? Array.Empty<string>(), "h-" + name, "body of " + name, Array.Empty<VaultLink>()),
            new[] { new ChunkPiece(0, "", "body of " + name, "body of " + name) }, null);

    private static List<long> AddMany(KnowledgeStore s, int n)
    {
        var src = s.UpsertSource("src", "vault", "/x");
        return Enumerable.Range(0, n).Select(i => Add(s, src, "n" + i)).ToList();
    }

    /// <summary>A reply labelling every [i] item of the prompt.</summary>
    private static string ReplyFor(int count, string category = "Memory", string type = "Reference", string tag = "ai-tag") =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"{{\"index\":{i},\"summary\":\"sum {i}\",\"category\":\"{category}\",\"type\":\"{type}\",\"tags\":[\"{tag}\"]}}")) + "]";

    private static int ItemCount(string prompt) => System.Text.RegularExpressions.Regex.Matches(prompt, @"^\[\d+\]", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    [Fact]
    public async Task Prose_with_index_marker_before_fenced_array_is_parsed()
    {
        using var s = NewStore();
        var ids = AddMany(s, 1);
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult("For [0] here is the result:" + Environment.NewLine + "```json" + Environment.NewLine + ReplyFor(1) + Environment.NewLine + "```"), "m1");
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        res.Should().Be(new LabelRunResult(1, 0));
        s.Labels(ids[0]).AiState.Should().Be("labelled");
    }

    [Fact]
    public async Task Labels_batch_of_six_with_one_call()
    {
        using var s = NewStore();
        var ids = AddMany(s, 7);
        var sizes = new List<int>();
        var lab = new KnowledgeLabeler((p, ct) => { var n = ItemCount(p); sizes.Add(n); return Task.FromResult(ReplyFor(n)); }, "m1");
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        sizes.Should().Equal(6, 1);
        res.Should().Be(new LabelRunResult(7, 0));
        foreach (var id in ids)
        {
            var l = s.Labels(id);
            l.AiState.Should().Be("labelled");
            l.Category.Should().Be("Memory");
            l.AiModel.Should().Be("m1");
            DateTime.TryParse(l.AiAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Out_of_list_category_and_type_fall_back()
    {
        using var s = NewStore();
        var ids = AddMany(s, 1);
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult(ReplyFor(1, "Nonsense", "Weird")), "m");
        await lab.LabelAsync(s, ids, null, CancellationToken.None);
        var l = s.Labels(ids[0]);
        l.Category.Should().Be("General");
        l.Type.Should().Be("Reference");
    }

    [Fact]
    public async Task Garbage_reply_marks_batch_failed_and_continues()
    {
        using var s = NewStore();
        var ids = AddMany(s, 7);
        var call = 0;
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult(++call == 1 ? "sorry" : ReplyFor(ItemCount(p))), "m");
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        res.Should().Be(new LabelRunResult(1, 6));
        s.Labels(ids[0]).AiState.Should().Be("failed");
        s.Labels(ids[6]).AiState.Should().Be("labelled");
    }

    [Fact]
    public async Task Fenced_or_wrapped_json_is_parsed()
    {
        using var s = NewStore();
        var ids = AddMany(s, 2);
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult("Here you go:\n```json\n" + ReplyFor(2) + "\n```\nHope it helps [1]."), "m");
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        res.Labelled.Should().Be(2);
    }

    [Fact]
    public async Task Short_array_fails_only_missing_items()
    {
        using var s = NewStore();
        var ids = AddMany(s, 3);
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult(ReplyFor(2)), "m");
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        res.Should().Be(new LabelRunResult(2, 1));
        s.Labels(ids[2]).AiState.Should().Be("failed");
        s.Labels(ids[0]).AiState.Should().Be("labelled");
    }

    [Fact]
    public async Task Timeout_marks_failed_without_throwing()
    {
        using var s = NewStore();
        var ids = AddMany(s, 2);
        var lab = new KnowledgeLabeler(async (p, ct) => { await Task.Delay(5000, ct); return "[]"; }, "m", TimeSpan.FromMilliseconds(50));
        var res = await lab.LabelAsync(s, ids, null, CancellationToken.None);
        res.Should().Be(new LabelRunResult(0, 2));
        s.Labels(ids[0]).AiState.Should().Be("failed");
    }

    [Fact]
    public async Task Cancel_throws_OperationCanceled_and_keeps_done_batches()
    {
        using var s = NewStore();
        var ids = AddMany(s, 7);
        using var cts = new CancellationTokenSource();
        var call = 0;
        var lab = new KnowledgeLabeler((p, ct) =>
        {
            if (++call == 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(ReplyFor(ItemCount(p)));
        }, "m");
        var act = () => lab.LabelAsync(s, ids, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        s.Labels(ids[0]).AiState.Should().Be("labelled");
        s.Labels(ids[6]).AiState.Should().Be("none");
    }

    [Fact]
    public async Task Tags_merge_existing_note_tags_first()
    {
        using var s = NewStore();
        var src = s.UpsertSource("src", "vault", "/x");
        var id = Add(s, src, "a", new[] { "mine", "Shared" });
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult(ReplyFor(1, tag: "shared")), "m");
        await lab.LabelAsync(s, new[] { id }, null, CancellationToken.None);
        s.Labels(id).Tags.Should().Equal("mine", "Shared");
        // add another AI tag via a different reply
        var lab2 = new KnowledgeLabeler((p, ct) => Task.FromResult("[{\"index\":0,\"summary\":\"x\",\"category\":\"Memory\",\"type\":\"Reference\",\"tags\":[\"a\",\"b\",\"c\",\"d\",\"e\"]}]"), "m");
        await lab2.LabelAsync(s, new[] { id }, null, CancellationToken.None);
        s.Labels(id).Tags.Should().Equal("mine", "Shared", "a", "b", "c", "d");
    }

    [Fact]
    public async Task Progress_is_reported_once_per_batch()
    {
        using var s = NewStore();
        var ids = AddMany(s, 7);
        var seen = new List<(int, int)>();
        var lab = new KnowledgeLabeler((p, ct) => Task.FromResult(ReplyFor(ItemCount(p))), "m");
        await lab.LabelAsync(s, ids, new SyncProgress(seen.Add), CancellationToken.None);
        seen.Should().Equal((6, 7), (7, 7));
    }

    private sealed class SyncProgress(Action<(int, int)> a) : IProgress<(int done, int total)>
    {
        public void Report((int done, int total) v) => a(v);
    }
}
