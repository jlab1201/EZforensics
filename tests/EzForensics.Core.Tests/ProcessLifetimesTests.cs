using EzForensics.Core.Processes;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The facts a link may rest on: PID inside the lifetime, full path + fitting time + a single fitting instance.</summary>
public class ProcessLifetimesTests
{
    private static DateTime T(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static ProcessNode N(int pid, string path, string? create, string? exit = null) =>
        new() { Pid = pid, Name = System.IO.Path.GetFileName(path), Path = path, CreateTime = create == null ? null : T(create), ExitTime = exit == null ? null : T(exit) };

    private static readonly TimeFit Start = new(TimeRole.Start, -2, 15);

    [Fact]
    public void StartRole_FitsOnlyInsideTheTolerance()
    {
        var a = N(1, @"C:\x\a.exe", "2024-01-01 10:00:00");
        var life = new ProcessLifetimes(new[] { a });
        life.IsUniquePathTime(a, T("2024-01-01 10:00:10"), Start).Should().BeTrue();
        life.IsUniquePathTime(a, T("2024-01-01 10:00:15"), Start).Should().BeTrue();
        life.IsUniquePathTime(a, T("2024-01-01 10:00:15.5"), Start).Should().BeFalse();
        life.IsUniquePathTime(a, T("2024-01-01 09:59:57"), Start).Should().BeFalse();
        life.IsUniquePathTime(N(2, @"C:\x\a.exe", null), T("2024-01-01 10:00:10"), Start).Should().BeFalse();   // no start time, nothing to fit
    }

    [Fact]
    public void BareNames_HaveNoPathKey_AndNeverFit()
    {
        var a = N(1, "a.exe", "2024-01-01 10:00:00");
        new ProcessLifetimes(new[] { a }).IsUniquePathTime(a, T("2024-01-01 10:00:05"), Start).Should().BeFalse();
    }

    [Fact]
    public void TwoInstancesBothFitting_AreAmbiguous_OneFittingIsUnique()
    {
        var a = N(1, @"C:\x\a.exe", "2024-01-01 10:00:00");
        var b = N(2, @"C:\X\A.EXE", "2024-01-01 10:00:05");
        var life = new ProcessLifetimes(new[] { a, b });
        life.IsUniquePathTime(a, T("2024-01-01 10:00:08"), Start).Should().BeFalse();
        life.IsUniquePathTime(b, T("2024-01-01 10:00:08"), Start).Should().BeFalse();
        life.IsUniquePathTime(a, T("2024-01-01 09:59:59"), Start).Should().BeTrue();    // only a fits
        life.IsUniquePathTime(b, T("2024-01-01 10:00:18"), Start).Should().BeTrue();    // only b fits
    }

    [Fact]
    public void DuringRole_EndsAtExit_OrAtTheNextInstanceOfThePath()
    {
        var exited = N(1, @"C:\x\a.exe", "2024-01-01 10:00:00", "2024-01-01 10:05:00");
        var open = N(2, @"C:\x\b.exe", "2024-01-01 10:00:00");
        var open2 = N(3, @"C:\x\b.exe", "2024-01-01 11:00:00");
        var life = new ProcessLifetimes(new[] { exited, open, open2 });
        life.IsUniquePathTime(exited, T("2024-01-01 10:04:59"), TimeFit.During).Should().BeTrue();
        life.IsUniquePathTime(exited, T("2024-01-01 10:05:01"), TimeFit.During).Should().BeFalse();     // after the exit
        life.IsUniquePathTime(exited, T("2024-01-01 09:59:59"), TimeFit.During).Should().BeFalse();     // before the start
        life.IsUniquePathTime(open, T("2024-01-01 10:30:00"), TimeFit.During).Should().BeTrue();
        life.IsUniquePathTime(open, T("2024-01-01 11:30:00"), TimeFit.During).Should().BeFalse();       // the later instance took over
        life.IsUniquePathTime(open2, T("2024-01-01 11:30:00"), TimeFit.During).Should().BeTrue();
    }

    [Fact]
    public void DuringRole_UsesTheEvidenceEnd_WhenKnown()
    {
        var open = N(2, @"C:\x\b.exe", "2024-01-01 10:00:00");
        var life = new ProcessLifetimes(new[] { open }, T("2024-01-01 10:10:00"));
        life.IsUniquePathTime(open, T("2024-01-01 10:09:00"), TimeFit.During).Should().BeTrue();
        life.IsUniquePathTime(open, T("2024-01-01 10:11:00"), TimeFit.During).Should().BeFalse();
    }

    [Fact]
    public void PidReuse_OutsideTheLifetime_DoesNotFit()
    {
        var first = N(500, @"C:\x\a.exe", "2024-01-01 10:00:00", "2024-01-01 10:10:00");
        var second = N(500, @"C:\x\b.exe", "2024-01-01 11:00:00");
        var life = new ProcessLifetimes(new[] { first, second });
        life.PidFits(first, T("2024-01-01 10:05:00")).Should().BeTrue();
        life.PidFits(first, T("2024-01-01 11:05:00")).Should().BeFalse();
        life.PidFits(second, T("2024-01-01 10:05:00")).Should().BeFalse();
        life.PidFits(second, T("2024-01-01 11:05:00")).Should().BeTrue();
        life.PidFits(second, null).Should().BeTrue();   // no row time, the PID stands
    }

    [Fact]
    public void PidWithoutExit_EndsAtTheNextInstanceOfThePid()
    {
        var first = N(500, @"C:\x\a.exe", "2024-01-01 10:00:00");
        var second = N(500, @"C:\x\b.exe", "2024-01-01 11:00:00");
        var life = new ProcessLifetimes(new[] { first, second });
        life.PidFits(first, T("2024-01-01 10:59:00")).Should().BeTrue();
        life.PidFits(first, T("2024-01-01 11:00:30")).Should().BeFalse();
    }
}
