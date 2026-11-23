using System.IO;
using EzForensics.App.ViewModels;
using EzForensics.Core.Assistant;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Flags proposed for a case file other than the open one: Apply opens that file, then flags.</summary>
public sealed class ProposalCardCrossFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezcard-" + Guid.NewGuid().ToString("N"));
    private readonly string _open, _other;

    public ProposalCardCrossFileTests()
    {
        Directory.CreateDirectory(_dir);
        _open = Path.Combine(_dir, "Processes.csv"); File.WriteAllText(_open, "a\n1\n");
        _other = Path.Combine(_dir, "WindowsEventLogs.csv"); File.WriteAllText(_other, "a\n1\n2\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Grid : IAssistantGridTarget
    {
        public string? CurrentCsv { get; set; }
        public List<string> Opened = new();
        public List<(string? Csv, IReadOnlyList<long> Rows, string? Flag)> Flags = new();
        public Task<string?> OpenFileAsync(string path, FilterGroup? filter) { Opened.Add(path); CurrentCsv = path; return Task.FromResult<string?>(null); }
        public GridContext GetContext() => new(CurrentCsv, new[] { "a" }, 2, 2, "", Array.Empty<GridSampleRow>());
        public IReadOnlyList<string> FlagIds { get; } = new[] { "suspicious" };
        public string FlagLabel(string flagId) => "Suspicious";
        public void ApplyFilter(FilterGroup filter) { }
        public string? ApplyFlag(IReadOnlyList<long> rows, string? flagId) { Flags.Add((CurrentCsv, rows, flagId)); return null; }
        public object CaptureFilter() => Shown!;
        public void RestoreFilter(object snapshot) => Shown = (string?)snapshot;
        public string? Shown;
        public void ShowFlag(string flagId) => Shown = flagId;
        public void ClearFilter() { }
        public IReadOnlyList<string?> GetFlags(IReadOnlyList<long> rows) => rows.Select(_ => (string?)null).ToList();
        public (string? Column, bool Descending) GetSort() => (null, false);
        public Task<string?> SortAsync(string? column, bool descending) => Task.FromResult<string?>(null);
        public string? GoToRow(long row) => null;
    }

    [Fact]
    public async Task Apply_opens_the_other_file_then_flags_it()
    {
        var grid = new Grid { CurrentCsv = _open };
        var card = new ProposalCardViewModel(new FlagProposal("t1", _other, new long[] { 0, 1 }, "suspicious", "encoded"), grid);
        card.Title.Should().Be("Open WindowsEventLogs.csv and flag 2 rows as Suspicious and show them");
        await card.ApplyCommand.ExecuteAsync(null);
        card.State.Should().Be(ProposalState.Applied);
        grid.Opened.Should().Equal(_other);
        var f = grid.Flags.Single();
        f.Csv.Should().Be(_other);
        f.Rows.Should().Equal(0, 1);
        f.Flag.Should().Be("suspicious");
        grid.Shown.Should().Be("suspicious");
    }

    [Fact]
    public async Task Flag_shows_the_flag_and_undo_brings_the_old_filter_back()
    {
        var grid = new Grid { CurrentCsv = _open, Shown = null };
        var card = new ProposalCardViewModel(new FlagProposal("t1", _open, new long[] { 0 }, "suspicious", ""), grid);
        (await card.AutoApplyAsync()).Should().BeNull();
        grid.Shown.Should().Be("suspicious");
        await card.UndoCommand.ExecuteAsync(null);
        card.State.Should().Be(ProposalState.Undone);
        grid.Shown.Should().BeNull();
        grid.Flags.Last().Flag.Should().BeNull();
    }

    [Fact]
    public async Task Auto_mode_does_the_same_and_a_flag_on_the_open_file_opens_nothing()
    {
        var grid = new Grid { CurrentCsv = _open };
        (await new ProposalCardViewModel(new UnflagProposal("t1", _other, new long[] { 1 }, ""), grid).AutoApplyAsync()).Should().BeNull();
        grid.Opened.Should().Equal(_other);

        var same = new Grid { CurrentCsv = _open };
        var card = new ProposalCardViewModel(new FlagProposal("t2", _open, new long[] { 0 }, "suspicious", ""), same);
        card.Title.Should().StartWith("Flag 1 row");
        await card.ApplyCommand.ExecuteAsync(null);
        same.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_left_the_case_is_not_opened()
    {
        var grid = new Grid { CurrentCsv = _open };
        var gone = Path.Combine(_dir, "gone.csv");
        var card = new ProposalCardViewModel(new FlagProposal("t1", gone, new long[] { 0 }, "suspicious", ""), grid);
        await card.ApplyCommand.ExecuteAsync(null);
        card.State.Should().Be(ProposalState.Pending);
        card.Note.Should().Contain("gone.csv");
        grid.Opened.Should().BeEmpty();
    }
}
