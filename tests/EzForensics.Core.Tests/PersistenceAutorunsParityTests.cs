using System.Text;
using EzForensics.Core.Persistence;
using EzForensics.Core.SystemInfo;
using FluentAssertions;

namespace EzForensics.Core.Tests;

/// <summary>Locations and columns added so the built-in collector covers what <c>autorunsc -a * -c -h -s -t</c> reported.</summary>
public class PersistenceAutorunsParityTests
{
    private const string CV = @"Microsoft\Windows\CurrentVersion";

    private static PersistenceResult Sw(Action<InMemoryHive> build)
    {
        var h = new InMemoryHive("SOFTWARE");
        build(h);
        var r = new PersistenceResult();
        RegistryAseps.FromSoftware(h, r);
        return r;
    }

    private static PersistenceResult Sys(Action<InMemoryHive> build)
    {
        var h = new InMemoryHive("SYSTEM");
        h.Key("Select").With(RegValue.Dword("Current", 1));
        build(h);
        var r = new PersistenceResult();
        RegistryAseps.FromSystem(h, r);
        return r;
    }

    private static void Clsid(InMemoryHive h, string clsid, string dll) =>
        h.Key($@"Classes\CLSID\{clsid}\InprocServer32").With(RegValue.Sz("", dll));

    [Fact]
    public void Shell_handlers_overlays_and_credential_providers()
    {
        var r = Sw(h =>
        {
            Clsid(h, "{OV1}", @"C:\Program Files\Dropbox\ov.dll");
            h.Key($@"{CV}\Explorer\ShellIconOverlayIdentifiers\  DropboxExt1").With(RegValue.Sz("", "{OV1}"));
            Clsid(h, "{CM1}", @"C:\Program Files\Evil\cm.dll");
            h.Key(@"Classes\*\shellex\ContextMenuHandlers\EvilMenu").With(RegValue.Sz("", "{CM1}"));
            Clsid(h, "{PS1}", @"C:\x\ps.dll");
            h.Key(@"Classes\Directory\shellex\PropertySheetHandlers\{PS1}");
            h.Key(@"Classes\Folder\shellex\ContextMenuHandlers\Unresolved").With(RegValue.Sz("", "{NOPE}"));
            Clsid(h, "{CP1}", @"C:\x\cp.dll");
            h.Key($@"{CV}\Authentication\Credential Providers\{{CP1}}").With(RegValue.Sz("", "My provider"));
        });
        var ex = r.Entries.Where(e => e.Category == PersistenceCategory.Explorer).ToList();
        ex.Single(e => e.EntryName == "DropboxExt1 (icon overlay)").ImagePath.Should().Be(@"C:\Program Files\Dropbox\ov.dll");
        var cm = ex.Single(e => e.EntryName == "EvilMenu (context menu handler)");
        cm.Command.Should().Be(@"C:\Program Files\Evil\cm.dll");
        cm.Location.Should().Be(@"HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers");
        ex.Single(e => e.EntryName == "{PS1} (property sheet handler)").ImagePath.Should().Be(@"C:\x\ps.dll");
        ex.Should().NotContain(e => e.EntryName.StartsWith("Unresolved"));
        var cp = r.Entries.Single(e => e.EntryName.Contains("credential provider"));
        cp.Category.Should().Be(PersistenceCategory.Winlogon);
        cp.ImagePath.Should().Be(@"C:\x\cp.dll");
    }

    [Fact]
    public void Internet_explorer_toolbars_extensions_and_search_hooks()
    {
        var r = Sw(h =>
        {
            Clsid(h, "{TB1}", @"C:\tb\toolbar.dll");
            h.Key($@"{CV}\Explorer\Toolbar").With(RegValue.Bin("{TB1}", new byte[] { 1, 2 }));
            Clsid(h, "{UH1}", @"C:\tb\hook.dll");
            h.Key(@"Microsoft\Internet Explorer\URLSearchHooks").With(RegValue.Sz("{UH1}", "hook"));
            h.Key(@"Microsoft\Internet Explorer\Extensions\{E1}").With(RegValue.Sz("ButtonText", "Send to X"), RegValue.Sz("Exec", @"C:\tb\send.exe"));
            Clsid(h, "{E2CLS}", @"C:\tb\ext.dll");
            h.Key(@"Microsoft\Internet Explorer\Extensions\{E2}").With(RegValue.Sz("CLSIDExtension", "{E2CLS}"));
        });
        var ie = r.Entries.Where(e => e.Category == PersistenceCategory.InternetExplorer).ToList();
        ie.Should().HaveCount(4);
        ie.Single(e => e.EntryName == "{TB1} (toolbar)").ImagePath.Should().Be(@"C:\tb\toolbar.dll");
        ie.Single(e => e.EntryName == "{UH1} (URL search hook)").ImagePath.Should().Be(@"C:\tb\hook.dll");
        ie.Single(e => e.EntryName == "Send to X (IE extension)").Command.Should().Be(@"C:\tb\send.exe");
        ie.Single(e => e.EntryName == "{E2} (IE extension)").ImagePath.Should().Be(@"C:\tb\ext.dll");
    }

    [Fact]
    public void Codecs_drivers32_directshow_and_media_foundation()
    {
        var r = Sw(h =>
        {
            h.Key(@"Microsoft\Windows NT\CurrentVersion\Drivers32").With(RegValue.Sz("vidc.XVID", "xvid.dll"), RegValue.Sz("midi", "wdmaud.drv"));
            Clsid(h, "{F1}", @"C:\codec\f.ax");
            h.Key(@"Classes\Filter\{F1}");
            Clsid(h, "{DS1}", @"C:\codec\ds.ax");
            h.Key(@"Classes\CLSID\{083863F1-70DE-11d0-BD40-00A0C911CE86}\Instance\{DS1}").With(RegValue.Sz("CLSID", "{DS1}"), RegValue.Sz("FriendlyName", "My filter"));
            Clsid(h, "{MFT1}", @"C:\codec\mft.dll");
            h.Key(@"Microsoft\Windows Media Foundation\Transforms\{MFT1}").With(RegValue.Sz("MFTName", "My MFT"));
            Clsid(h, "{BSH1}", @"C:\codec\bsh.dll");
            h.Key(@"Microsoft\Windows Media Foundation\ByteStreamHandlers\.xyz").With(RegValue.Sz("{BSH1}", "xyz handler"));
        });
        var c = r.Entries.Where(e => e.Category == PersistenceCategory.Codecs).ToList();
        c.Single(e => e.EntryName == "vidc.XVID").ImagePath.Should().Be("xvid.dll");
        c.Should().Contain(e => e.EntryName == "midi");
        c.Single(e => e.EntryName == "{F1} (filter)").ImagePath.Should().Be(@"C:\codec\f.ax");
        c.Single(e => e.EntryName == "My filter (DirectShow)").ImagePath.Should().Be(@"C:\codec\ds.ax");
        c.Single(e => e.EntryName == "My MFT (Media Foundation transform)").ImagePath.Should().Be(@"C:\codec\mft.dll");
        c.Single(e => e.EntryName == ".xyz (ByteStreamHandlers)").ImagePath.Should().Be(@"C:\codec\bsh.dll");
    }

    [Fact]
    public void Office_addins_machine_and_wow_with_load_behavior()
    {
        var r = Sw(h =>
        {
            Clsid(h, "{OA1}", @"C:\Program Files\Office addin\a.dll");
            h.Key(@"Classes\My.Addin\CLSID").With(RegValue.Sz("", "{OA1}"));
            h.Key(@"Microsoft\Office\Excel\Addins\My.Addin").With(RegValue.Dword("LoadBehavior", 3), RegValue.Sz("FriendlyName", "My Addin"), RegValue.Sz("Description", "does things"));
            h.Key(@"Microsoft\Office\16.0\Word\Addins\Vsto.Addin").With(RegValue.Dword("LoadBehavior", 2), RegValue.Sz("Manifest", @"file:///C:/vsto/a.vsto|vstolocal"));
            h.Key(@"WOW6432Node\Microsoft\Office\Outlook\Addins\Wow.Addin").With(RegValue.Dword("LoadBehavior", 3), RegValue.Sz("Manifest", @"C:\w\w.vsto"));
        });
        var o = r.Entries.Where(e => e.Category == PersistenceCategory.OfficeAddins).ToList();
        o.Should().HaveCount(3);
        var a = o.Single(e => e.EntryName == "My Addin");
        a.ImagePath.Should().Be(@"C:\Program Files\Office addin\a.dll");
        a.Enabled.Should().Be("Yes");
        a.Notes.Should().Contain("ProgID My.Addin").And.Contain("does things");
        var v = o.Single(e => e.EntryName == "Vsto.Addin");
        v.Enabled.Should().Be("No");
        v.Location.Should().Be(@"HKLM\SOFTWARE\Microsoft\Office\16.0\Word\Addins");
        o.Single(e => e.EntryName == "Wow.Addin").Location.Should().Contain("WOW6432Node");
    }

    [Fact]
    public void Group_policy_scripts_machine_and_user()
    {
        var r = Sw(h =>
        {
            h.Key($@"{CV}\Group Policy\Scripts\Startup\0\0").With(RegValue.Sz("Script", @"\\dc\sysvol\boot.bat"), RegValue.Sz("Parameters", "/q"));
            h.Key($@"{CV}\Group Policy\State\Machine\Scripts\Shutdown\0\0").With(RegValue.Sz("Script", @"C:\x\down.cmd"));
        });
        var g = r.Entries.Where(e => e.Category == PersistenceCategory.GroupPolicyScripts).ToList();
        g.Should().HaveCount(2);
        g.Single(e => e.Command.StartsWith(@"\\dc")).Command.Should().Be(@"\\dc\sysvol\boot.bat /q");
        g.Single(e => e.Command.StartsWith(@"\\dc")).Flags.Should().Contain(PersistenceHeuristics.NetworkPath);
        g.Single(e => e.ImagePath == @"C:\x\down.cmd").Notes.Should().Be("Shutdown script");

        var nt = new InMemoryHive("NTUSER.DAT");
        nt.Key($@"Software\{CV}\Group Policy\Scripts\Logon\0\0").With(RegValue.Sz("Script", @"C:\x\logon.vbs"));
        nt.Key($@"Software\{CV}\Group Policy\State\S-1-5-21\Scripts\Logoff\0\0").With(RegValue.Sz("Script", @"C:\x\logoff.vbs"));
        var ur = new PersistenceResult();
        RegistryAseps.FromUser(nt, "bob", ur);
        var ug = ur.Entries.Where(e => e.Category == PersistenceCategory.GroupPolicyScripts).ToList();
        ug.Should().HaveCount(2).And.OnlyContain(e => e.User == "bob" && e.Location.StartsWith("HKCU\\"));
    }

    [Fact]
    public void Image_hijack_file_associations()
    {
        var r = Sw(h =>
        {
            h.Key(@"Classes\exefile\shell\open\command").With(RegValue.Sz("", "\"%1\" %*"));
            h.Key(@"Classes\batfile\shell\open\command").With(RegValue.Sz("", @"C:\Users\Public\wrap.exe ""%1"""));
            h.Key(@"Classes\.com").With(RegValue.Sz("", "evilfile"));
        });
        var ih = r.Entries.Where(e => e.Category == PersistenceCategory.ImageHijacks).ToList();
        ih.Single(e => e.EntryName == @"exefile\shell\open\command").Flags.Should().BeEmpty();
        ih.Single(e => e.EntryName == @"batfile\shell\open\command").Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue).And.Contain(PersistenceHeuristics.UserWritable);
        ih.Single(e => e.EntryName == ".com class mapping").Notes.Should().Be("Expected comfile");

        var uc = new InMemoryHive("UsrClass.dat");
        uc.Key(@"exefile\shell\open\command").With(RegValue.Sz("", @"C:\Users\bob\h.exe ""%1"""));
        var ur = new PersistenceResult();
        RegistryAseps.FromUserClasses(uc, "bob", ur);
        var e1 = ur.Entries.Single(e => e.Category == PersistenceCategory.ImageHijacks);
        e1.Location.Should().Be(@"HKCU\Software\Classes\exefile\shell\open\command");
        e1.Flags.Should().Contain(PersistenceHeuristics.NonDefaultValue);
    }

    [Fact]
    public void KnownDlls_network_providers_and_winsock()
    {
        var r = Sys(h =>
        {
            h.Key(@"ControlSet001\Control\Session Manager\KnownDLLs").With(RegValue.Sz("kernel32", "kernel32.dll"), RegValue.Sz("evil", "evil.dll"), RegValue.Sz("DllDirectory", @"%SystemRoot%\system32"));
            h.Key(@"ControlSet001\Control\NetworkProvider\Order").With(RegValue.Sz("ProviderOrder", "RDPNP,Evil"));
            h.Key(@"ControlSet001\Services\RDPNP\NetworkProvider").With(RegValue.Sz("Name", "Microsoft Terminal Services"), RegValue.Sz("ProviderPath", @"%SystemRoot%\System32\drprov.dll", RegType.ExpandSz));
            h.Key(@"ControlSet001\Services\Evil\NetworkProvider").With(RegValue.Sz("ProviderPath", @"C:\ProgramData\np.dll"));

            // Real layout (checked against a live Windows 11 catalog, 888 bytes): ANSI CHAR[260] path, then WSAPROTOCOL_INFOW with szProtocol at +116.
            var packed = new byte[260 + 628];
            Encoding.ASCII.GetBytes(@"C:\ProgramData\lsp.dll").CopyTo(packed, 0);
            Encoding.Unicode.GetBytes("Evil LSP").CopyTo(packed, 376);
            h.Key(@"ControlSet001\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries\000000000001").With(RegValue.Bin("PackedCatalogItem", packed));
            h.Key(@"ControlSet001\Services\WinSock2\Parameters\NameSpace_Catalog5\Catalog_Entries64\000000000002")
                .With(RegValue.Sz("LibraryPath", @"%SystemRoot%\System32\nlasvc.dll", RegType.ExpandSz), RegValue.Sz("DisplayString", "NLA"), RegValue.Dword("Enabled", 0));
        });
        var kd = r.Entries.Where(e => e.Category == PersistenceCategory.KnownDlls).ToList();
        kd.Select(e => e.EntryName).Should().BeEquivalentTo("kernel32", "evil");
        kd.Single(e => e.EntryName == "evil").ImagePath.Should().Be("evil.dll");

        var np = r.Entries.Where(e => e.Category == PersistenceCategory.NetworkProviders).ToList();
        np.Should().HaveCount(2);
        np.Single(e => e.EntryName == "Microsoft Terminal Services").ImagePath.Should().Be(@"%SystemRoot%\System32\drprov.dll");
        np.Single(e => e.EntryName == "Evil").Flags.Should().Contain(PersistenceHeuristics.UserWritable);

        var ws = r.Entries.Where(e => e.Category == PersistenceCategory.Winsock).ToList();
        ws.Should().HaveCount(2);
        var lsp = ws.Single(e => e.EntryName == "Evil LSP");
        lsp.ImagePath.Should().Be(@"C:\ProgramData\lsp.dll");
        lsp.Flags.Should().Contain(PersistenceHeuristics.UserWritable);
        ws.Single(e => e.EntryName == "NLA").Enabled.Should().Be("No");
    }

    [Fact]
    public void User_hive_ie_and_office_addins()
    {
        var sw = new InMemoryHive("SOFTWARE");
        Clsid(sw, "{OA9}", @"C:\o\hk.dll");
        var nt = new InMemoryHive("NTUSER.DAT");
        nt.Key(@"Software\Classes\Mine.Addin\CLSID").With(RegValue.Sz("", "{OA9}"));
        nt.Key(@"Software\Microsoft\Office\Excel\Addins\Mine.Addin").With(RegValue.Dword("LoadBehavior", 3));
        nt.Key(@"Software\Microsoft\Internet Explorer\Extensions\{X}").With(RegValue.Sz("Script", @"C:\s\x.js"), RegValue.Sz("MenuText", "Run x"));
        var r = new PersistenceResult();
        RegistryAseps.FromUser(nt, "bob", r, null, sw);
        var o = r.Entries.Single(e => e.Category == PersistenceCategory.OfficeAddins);
        o.ImagePath.Should().Be(@"C:\o\hk.dll");
        o.User.Should().Be("bob");
        o.Location.Should().StartWith(@"HKCU\Software\Microsoft\Office");
        r.Entries.Single(e => e.Category == PersistenceCategory.InternetExplorer).EntryName.Should().Be("Run x (IE extension)");
    }

    [Fact]
    public void Headers_include_autoruns_columns_and_hash_columns_stay_empty()
    {
        // Same names and order as autorunsc's CSV hash columns: MD5, SHA-1, PESHA-1, PESHA-256, SHA-256, IMP.
        PersistenceCollector.Headers.Should().ContainInOrder("Signer", "MD5", "SHA-1", "PESHA-1", "PESHA-256", "SHA-256", "IMP");
        PersistenceCollector.Headers.Should().Contain(new[] { "Command", "ImagePath", "User", "Enabled", "Description", "Company", "Version" });
        var r = new PersistenceResult();
        r.Entries.Add(new PersistenceEntry { Category = "Run keys", EntryName = "x", Command = "c.exe", ImagePath = "c.exe", Description = "Desc", Company = "Co", Version = "1.2" });
        using var t = new TempDir();
        var f = PersistenceCollector.Write(r, t.Path).Single();
        var lines = File.ReadAllLines(f);
        var h = lines[0].Split(',');
        var row = lines[1].Split(',');
        row.Length.Should().Be(h.Length);
        row[Array.IndexOf(h, "Description")].Should().Be("Desc");
        row[Array.IndexOf(h, "Company")].Should().Be("Co");
        row[Array.IndexOf(h, "Version")].Should().Be("1.2");
        foreach (var col in new[] { "Signer", "MD5", "SHA-1", "PESHA-1", "PESHA-256", "SHA-256", "IMP" })
            row[Array.IndexOf(h, col)].Should().BeEmpty(col);
    }

    [Fact]
    public void Thirty_two_bit_classes_are_read_from_Classes_Wow6432Node()
    {
        var r = Sw(h =>
        {
            h.Key(@"Classes\Wow6432Node\CLSID\{W32}\InprocServer32").With(RegValue.Sz("", @"C:\Program Files (x86)\x\menu32.dll"));
            h.Key(@"Classes\Wow6432Node\*\shellex\ContextMenuHandlers\Menu32").With(RegValue.Sz("", "{W32}"));
            h.Key(@"Classes\Wow6432Node\CLSID\{B32}\InprocServer32").With(RegValue.Sz("", @"C:\Program Files (x86)\x\bho32.dll"));
            h.Key($@"WOW6432Node\{CV}\Explorer\Browser Helper Objects\{{B32}}");
        });
        var menu = r.Entries.Single(e => e.EntryName == "Menu32 (context menu handler)");
        menu.ImagePath.Should().Be(@"C:\Program Files (x86)\x\menu32.dll");
        menu.Location.Should().Be(@"HKLM\SOFTWARE\Classes\Wow6432Node\*\shellex\ContextMenuHandlers");
        r.Entries.Single(e => e.EntryName == "{B32} (BHO)").ImagePath.Should().Be(@"C:\Program Files (x86)\x\bho32.dll");
        RegistryAseps.ClsidServer(new InMemoryHive("SOFTWARE"), "{none}", "WOW6432Node\\").Should().BeNull();
    }

    [Fact]
    public void Shell_service_objects_machine_and_user_and_user_overlays()
    {
        var r = Sw(h =>
        {
            Clsid(h, "{SSO1}", @"C:\ProgramData\sso.dll");
            h.Key($@"{CV}\Explorer\ShellServiceObjects\{{SSO1}}").With(RegValue.Dword("AutoStart", 1));
        });
        r.Entries.Single(e => e.EntryName == "{SSO1} (shell service object)").ImagePath.Should().Be(@"C:\ProgramData\sso.dll");

        var sw = new InMemoryHive("SOFTWARE");
        Clsid(sw, "{UO1}", @"C:\Users\bob\AppData\Local\ov.dll");
        Clsid(sw, "{USSO}", @"C:\Users\bob\AppData\Local\usso.dll");
        var nt = new InMemoryHive("NTUSER.DAT");
        nt.Key($@"Software\{CV}\Explorer\ShellIconOverlayIdentifiers\ UserOverlay").With(RegValue.Sz("", "{UO1}"));
        nt.Key($@"Software\{CV}\Explorer\ShellServiceObjects\{{USSO}}");
        var ur = new PersistenceResult();
        RegistryAseps.FromUser(nt, "bob", ur, null, sw);
        var ov = ur.Entries.Single(e => e.EntryName == "UserOverlay (icon overlay)");
        ov.User.Should().Be("bob");
        ov.Location.Should().Be($@"HKCU\Software\{CV}\Explorer\ShellIconOverlayIdentifiers");
        ur.Entries.Single(e => e.EntryName == "{USSO} (shell service object)").ImagePath.Should().Be(@"C:\Users\bob\AppData\Local\usso.dll");
    }

    [Fact]
    public void User_classes_shell_handlers_resolve_user_then_machine_clsids()
    {
        var sw = new InMemoryHive("SOFTWARE");
        Clsid(sw, "{HKLMCLS}", @"C:\Program Files\m\m.dll");
        var uc = new InMemoryHive("UsrClass.dat");
        uc.Key(@"CLSID\{UCLS}\InprocServer32").With(RegValue.Sz("", @"C:\Users\bob\AppData\Roaming\u.dll"));
        uc.Key(@"*\shellex\ContextMenuHandlers\UserMenu").With(RegValue.Sz("", "{UCLS}"));
        uc.Key(@"Directory\shellex\CopyHookHandlers\MachineHook").With(RegValue.Sz("", "{HKLMCLS}"));
        var r = new PersistenceResult();
        RegistryAseps.FromUserClasses(uc, "bob", r, null, sw);
        var um = r.Entries.Single(e => e.EntryName == "UserMenu (context menu handler)");
        um.ImagePath.Should().Be(@"C:\Users\bob\AppData\Roaming\u.dll");
        um.Location.Should().Be(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers");
        um.User.Should().Be("bob");
        um.Flags.Should().Contain(PersistenceHeuristics.UserWritable);
        r.Entries.Single(e => e.EntryName == "MachineHook (copy hook)").ImagePath.Should().Be(@"C:\Program Files\m\m.dll");
    }

    [Fact]
    public void Group_policy_state_scripts_per_user_sid_in_software()
    {
        var r = Sw(h =>
        {
            h.Key($@"{CV}\Group Policy\State\S-1-5-21-1-2-3-1001\Scripts\Logon\0\0").With(RegValue.Sz("Script", @"\\dc\netlogon\map.cmd"));
            h.Key($@"{CV}\Group Policy\State\Machine\Scripts\Startup\0\0").With(RegValue.Sz("Script", @"C:\x\start.cmd"));
        });
        var g = r.Entries.Where(e => e.Category == PersistenceCategory.GroupPolicyScripts).ToList();
        g.Should().HaveCount(2);
        var logon = g.Single(e => e.ImagePath == @"\\dc\netlogon\map.cmd");
        logon.User.Should().Be("S-1-5-21-1-2-3-1001");
        logon.Notes.Should().Be("Logon script");
        g.Single(e => e.ImagePath == @"C:\x\start.cmd").User.Should().BeEmpty();
    }

    [Fact]
    public void Pe_version_info_is_read_from_the_evidence_file_and_blank_when_absent()
    {
        using var t = new TempDir();
        var sys32 = Path.Combine(t.Path, "Windows", "System32");
        Directory.CreateDirectory(sys32);
        var real = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(real)) real = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(real)) return; // needs a real PE with a version resource
        File.Copy(real, Path.Combine(sys32, "copy.exe"));
        var entries = new[]
        {
            new PersistenceEntry { ImagePath = @"C:\Windows\System32\copy.exe" },
            new PersistenceEntry { ImagePath = "copy.exe" },
            new PersistenceEntry { ImagePath = @"%SystemRoot%\system32\copy.exe" },
            new PersistenceEntry { ImagePath = @"C:\Windows\System32\missing.exe" },
            new PersistenceEntry { ImagePath = "" },
        };
        PeVersionInfo.Enrich(entries, t.Path);
        foreach (var e in entries.Take(3))
        {
            e.Company.Should().Contain("Microsoft");
            e.Version.Should().NotBeEmpty();
            e.Description.Should().NotBeEmpty();
        }
        entries[3].Company.Should().BeEmpty();
        entries[4].Version.Should().BeEmpty();
    }
}
