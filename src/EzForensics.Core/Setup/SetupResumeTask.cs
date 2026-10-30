using System.Diagnostics;
using System.Security;
using System.Text;

namespace EzForensics.Core.Setup;

/// <summary>
/// A faster trigger for the setup resume than <see cref="SetupResumeLaunch"/>'s RunOnce value: a per-user Task Scheduler task with
/// an "at log on of &lt;this user&gt;" trigger that starts <c>EzForensics.exe --resume-setup</c> as soon as the user signs in.
/// Explorer starts HKCU Run / RunOnce entries only after its start-up delay and once the machine is idle, which after the restart
/// that enables WSL (feature servicing, Defender, updates) can be minutes. RunOnce stays registered as the fallback (a second launch
/// just brings the first window forward: single instance). The task is deleted when the resumed wizard opens.
/// Trade-offs: a visible entry in Task Scheduler for one sign-in, schtasks.exe must be allowed (some policies block task
/// creation; then only RunOnce is used), and the task survives if EZ Forensics is uninstalled before the restart (it then fails
/// harmlessly once and is removed at the next resume or setup).
/// </summary>
public static class SetupResumeTask
{
    public const string TaskName = "EZ Forensics resume setup";

    /// <summary>Task Scheduler XML: logon trigger for <paramref name="userId"/> (DOMAIN\user), interactive token, normal priority, no time limit.</summary>
    public static string BuildXml(string userId, string exe, IReadOnlyList<string> arguments)
    {
        static string X(string s) => SecurityElement.Escape(s) ?? "";
        var args = string.Join(" ", arguments.Select(a => a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a));
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine("  <RegistrationInfo><Description>Opens EZ Forensics once after the restart to finish its setup. Removed when setup continues.</Description></RegistrationInfo>");
        sb.AppendLine($"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(userId)}</UserId></LogonTrigger></Triggers>");
        sb.AppendLine($"  <Principals><Principal id=\"Author\"><UserId>{X(userId)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        sb.AppendLine("    <Priority>4</Priority>"); // the default 7 runs the app below normal priority (CPU and I/O)
        sb.AppendLine("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>");
        sb.AppendLine("  </Settings>");
        sb.AppendLine($"  <Actions Context=\"Author\"><Exec><Command>{X(exe)}</Command>{(args.Length > 0 ? $"<Arguments>{X(args)}</Arguments>" : "")}</Exec></Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }

    /// <summary>Creates (or replaces) the task with schtasks.exe. Returns null on success, else why it failed (logged by the caller).</summary>
    public static string? Register(string exe, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) return "not Windows";
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var xml = Path.Combine(Path.GetTempPath(), "EzForensicsResumeTask_" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        try
        {
            File.WriteAllText(xml, BuildXml(user, exe, arguments), Encoding.Unicode);
            var (code, output) = Schtasks("/Create", "/TN", TaskName, "/XML", xml, "/F");
            return code == 0 ? null : $"schtasks exit code {code}: {output}";
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(xml); } catch { } }
    }

    /// <summary>Deletes the task (quietly when it does not exist).</summary>
    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Schtasks("/Delete", "/TN", TaskName, "/F"); } catch { }
    }

    private static (int Code, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe did not start");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (err.Result + " " + outp.Result).Trim());
    }
}
