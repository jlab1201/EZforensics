using EzForensics.App.Services;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The shared async init behind the Learn page's WebView2 environment: one run for concurrent callers, retry after a failure.</summary>
public sealed class SharedInitTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_run()
    {
        var runs = 0;
        var gate = new TaskCompletionSource();
        var init = new SharedInit<int>(async () => { runs++; await gate.Task; return 7; });
        var a = init.GetAsync();
        var b = init.GetAsync();
        gate.SetResult();
        (await a).Should().Be(7);
        (await b).Should().Be(7);
        (await init.GetAsync()).Should().Be(7);
        runs.Should().Be(1);
    }

    [Fact]
    public async Task Failure_is_forgotten_and_the_next_call_retries()
    {
        var runs = 0;
        var init = new SharedInit<string>(async () =>
        {
            await Task.Yield();
            if (++runs == 1) throw new InvalidOperationException("boom");
            return "ok";
        });
        await FluentActions.Awaiting(() => init.GetAsync()).Should().ThrowAsync<InvalidOperationException>();
        (await init.GetAsync()).Should().Be("ok");
        runs.Should().Be(2);
    }

    [Fact]
    public async Task Reset_creates_again()
    {
        var runs = 0;
        var init = new SharedInit<int>(() => Task.FromResult(++runs));
        (await init.GetAsync()).Should().Be(1);
        init.Reset();
        (await init.GetAsync()).Should().Be(2);
    }
}
