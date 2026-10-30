using EzForensics.App.ViewModels;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Rule behind "a CSV without PID links closes the process graph": host with process data and headers the grid links.</summary>
public sealed class ProcessTreeAutoCloseTests
{
    [Fact]
    public void A_file_with_a_process_host_and_headers_has_PID_links() =>
        ResultsViewModel.HasPidLinks(@"C:\case\HOST", new[] { "TimeCreated", "PayloadData1" }).Should().BeTrue();

    [Fact]
    public void A_file_without_a_process_host_has_no_PID_links() =>
        ResultsViewModel.HasPidLinks(null, new[] { "PID", "Name" }).Should().BeFalse();

    [Fact]
    public void A_file_without_headers_has_no_PID_links() =>
        ResultsViewModel.HasPidLinks(@"C:\case\HOST", Array.Empty<string>()).Should().BeFalse();
}
