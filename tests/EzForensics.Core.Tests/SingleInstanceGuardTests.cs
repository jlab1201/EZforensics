using EzForensics.Core.Setup;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class SingleInstanceGuardTests
{
    private static (string m, string e) Names() { var id = Guid.NewGuid().ToString("N"); return (@"Local\EzTest.M." + id, @"Local\EzTest.E." + id); }

    [Fact]
    public void Only_the_first_guard_becomes_the_instance()
    {
        var (m, e) = Names();
        using var a = new SingleInstanceGuard(m, e);
        a.TryBecomeFirst().Should().BeTrue();
        // A mutex is owned per thread, so the second claimant runs on another thread (like another process would).
        bool second = true;
        var t = new Thread(() => { using var b = new SingleInstanceGuard(m, e); second = b.TryBecomeFirst(); });
        t.Start(); t.Join();
        second.Should().BeFalse();
    }

    [Fact]
    public void Signal_reaches_the_listening_instance()
    {
        var (m, e) = Names();
        using var a = new SingleInstanceGuard(m, e);
        a.TryBecomeFirst().Should().BeTrue();
        using var got = new ManualResetEventSlim();
        a.StartListening(got.Set);
        using var b = new SingleInstanceGuard(m, e);
        b.SignalExisting().Should().BeTrue();
        got.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void A_mutex_owned_by_an_elevated_instance_is_reported_not_thrown()
    {
        var (m, e) = Names();
        using var g = new SingleInstanceGuard(m, e, _ => throw new UnauthorizedAccessException("Access to the path is denied."),
            _ => throw new UnauthorizedAccessException("Access to the path is denied."));
        g.TryBecomeFirst().Should().BeFalse();
        g.TryBecomeFirst(TimeSpan.FromMilliseconds(50)).Should().BeFalse();
        g.SignalExisting().Should().BeFalse();
        g.OwnedByElevatedInstance.Should().BeTrue();
    }

    [Fact]
    public void An_activate_event_that_cannot_be_opened_is_reported_not_thrown()
    {
        var (m, e) = Names();
        using var g = new SingleInstanceGuard(m, e, n => new Mutex(false, n), _ => throw new UnauthorizedAccessException("Access to the path is denied."));
        g.SignalExisting().Should().BeFalse();
        g.OwnedByElevatedInstance.Should().BeTrue();
    }

    [Fact]
    public void Signal_without_a_running_instance_returns_false()
    {
        var (m, e) = Names();
        using var b = new SingleInstanceGuard(m, e);
        b.SignalExisting().Should().BeFalse();
    }

    [Fact]
    public void Disposing_the_first_releases_the_mutex()
    {
        var (m, e) = Names();
        var a = new SingleInstanceGuard(m, e);
        a.TryBecomeFirst().Should().BeTrue();
        a.Dispose();
        using var b = new SingleInstanceGuard(m, e);
        b.TryBecomeFirst().Should().BeTrue();
    }

    [Fact]
    public void A_relaunch_waits_for_the_old_instance_to_exit()
    {
        var (m, e) = Names();
        using var owned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // the old instance (own thread: a mutex is owned per thread) holds the mutex, then exits shortly after the relaunch started
        var old = new Thread(() =>
        {
            using var a = new SingleInstanceGuard(m, e);
            a.TryBecomeFirst();
            owned.Set();
            release.Wait();
        });
        old.Start();
        owned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        using var b = new SingleInstanceGuard(m, e);
        b.TryBecomeFirst().Should().BeFalse();
        var t = new Thread(() => { Thread.Sleep(200); release.Set(); });
        t.Start();
        b.TryBecomeFirst(TimeSpan.FromSeconds(10)).Should().BeTrue();
        old.Join();
        t.Join();
    }
}
