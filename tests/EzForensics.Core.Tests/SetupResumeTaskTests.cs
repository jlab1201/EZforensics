using System.Xml.Linq;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>The logon task that reopens the setup right at sign-in (faster than Explorer's RunOnce processing).</summary>
public class SetupResumeTaskTests
{
    [Fact]
    public void Task_xml_runs_the_resume_at_this_users_logon_at_normal_priority()
    {
        var xml = SetupResumeTask.BuildXml(@"VM\Tanjiro & Co", @"C:\Users\T\AppData\Local\EzForensics\current\EzForensics.exe", new[] { SetupResumeLaunch.Switch });
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value.Should().Be(@"VM\Tanjiro & Co", "the trigger is for this user only (no admin needed)");
        doc.Descendants(ns + "LogonType").Single().Value.Should().Be("InteractiveToken");
        doc.Descendants(ns + "RunLevel").Single().Value.Should().Be("LeastPrivilege");
        doc.Descendants(ns + "Priority").Single().Value.Should().Be("4");
        doc.Descendants(ns + "Command").Single().Value.Should().EndWith("EzForensics.exe");
        doc.Descendants(ns + "Arguments").Single().Value.Should().Be("--resume-setup");
    }

    [Fact]
    public void Dev_build_arguments_with_spaces_are_quoted()
    {
        var xml = SetupResumeTask.BuildXml(@"PC\me", @"C:\Program Files\dotnet\dotnet.exe", new[] { @"D:\My Work\EzForensics.dll", "--resume-setup" });
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XDocument.Parse(xml).Descendants(ns + "Arguments").Single().Value.Should().Be("\"D:\\My Work\\EzForensics.dll\" --resume-setup");
    }
}
