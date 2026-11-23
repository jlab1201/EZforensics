using EzForensics.Core.EventLogs;
using FluentAssertions;
using Xunit;

namespace EzForensics.Core.Tests;

public class EvtxMapTests
{
    private static string MapPath(string name) => Path.Combine(TriageTestData.Root, "evtx-maps", name);

    private const string Sample7040 = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Service Control Manager" Guid="{555908d1-a6d7-4695-8e1e-26931d2012f4}" EventSourceName="Service Control Manager"/>
            <EventID Qualifiers="16384">7040</EventID>
            <EventRecordID>4242</EventRecordID>
            <Channel>System</Channel>
            <Computer>WKS1</Computer>
          </System>
          <EventData>
            <Data Name="param1">Background Intelligent Transfer Service</Data>
            <Data Name="param2">demand start</Data>
            <Data Name="param3">auto start</Data>
            <Data Name="param4">BITS</Data>
          </EventData>
        </Event>
        """;

    [Theory]
    [InlineData("System_Service-Control-Manager_7040.map")]
    [InlineData("System_Microsoft-Windows-Kernel-General_1.map")]
    [InlineData("Application_Application-Error_1000.map")]
    [InlineData("Microsoft-Windows-Ntfs-Operational_Microsoft-Windows-Ntfs_146.map")]
    public void Stock_map_round_trips(string file)
    {
        var a = EvtxMapYaml.Load(MapPath(file));
        a.Maps.Should().NotBeEmpty();
        a.Validate().Should().BeEmpty();
        var b = EvtxMapYaml.Parse(EvtxMapYaml.Write(a));
        EvtxMapYaml.Write(b).Should().Be(EvtxMapYaml.Write(a));
        b.Should().BeEquivalentTo(a);
    }

    [Fact]
    public void Parses_header_refine_and_lookups()
    {
        var m = EvtxMapYaml.Load(MapPath("Application_Application-Error_1000.map"));
        m.EventId.Should().Be(1000);
        m.Channel.Should().Be("Application");
        m.Provider.Should().Be("Application Error");
        m.Maps[0].Values[0].Refine.Should().Be(@"([a-zA-Z0-9_\\.\-\(\):])+(.exe|.dll)");
        var k = EvtxMapYaml.Load(MapPath("System_Microsoft-Windows-Kernel-General_1.map"));
        if (k.Lookups.Count > 0) k.Lookups[0].Values.Should().NotBeEmpty();
    }

    [Fact]
    public void Writer_escapes_quotes_and_backslashes()
    {
        var m = new EvtxMap
        {
            Author = "me", Description = "d", EventId = 4624, Channel = "Security", Provider = "Microsoft-Windows-Security-Auditing",
            Maps = { new EvtxMapEntry { Property = "UserName", PropertyValue = "%d%\\%u%", Values =
            {
                new EvtxMapValue { Name = "d", Value = "/Event/EventData/Data[@Name=\"SubjectDomainName\"]" },
                new EvtxMapValue { Name = "u", Value = "/Event/EventData/Data[@Name=\"SubjectUserName\"]", Refine = @"\w+" },
            } } },
        };
        var yaml = EvtxMapYaml.Write(m);
        yaml.Should().Contain("PropertyValue: \"%d%\\\\%u%\"");
        EvtxMapYaml.Parse(yaml).Should().BeEquivalentTo(m);
    }

    [Fact]
    public void Validate_flags_bad_columns_and_xpath()
    {
        var m = new EvtxMap { EventId = 1, Channel = "System", Provider = "P",
            Maps = { new EvtxMapEntry { Property = "Payload9", PropertyValue = "%a%", Values = { new EvtxMapValue { Name = "a", Value = "/x" } } },
                     new EvtxMapEntry { Property = "PayloadData1", PropertyValue = "%a%", Values = { new EvtxMapValue { Name = "a", Value = "/Event[" } } } } };
        var p = m.Validate();
        p.Should().Contain(s => s.Contains("Payload9"));
        p.Should().Contain(s => s.Contains("XPath"));
    }

    [Fact]
    public void File_name_follows_EvtxECmd_convention()
    {
        EvtxMapFiles.FileName("Microsoft-Windows-TaskScheduler/Operational", "Microsoft-Windows-TaskScheduler", 201)
            .Should().Be("Microsoft-Windows-TaskScheduler-Operational_Microsoft-Windows-TaskScheduler_201.map");
        EvtxMapFiles.FileName("System", "Service Control Manager", 7040).Should().Be("System_Service-Control-Manager_7040.map");
        EvtxMapFiles.ReadKey(MapPath("Microsoft-Windows-Ntfs-Operational_Microsoft-Windows-Ntfs_146.map"))
            .Should().Be(new EvtxEventKey("Microsoft-Windows-Ntfs/Operational", "Microsoft-Windows-Ntfs", 146));
    }

    [Fact]
    public void Evaluates_stock_map_on_sample_xml()
    {
        var m = EvtxMapYaml.Load(MapPath("System_Service-Control-Manager_7040.map"));
        var r = EvtxMapEvaluator.Evaluate(m, Sample7040);
        r["PayloadData1"].Should().Be("The start type of the Background Intelligent Transfer Service was changed from demand start to auto start");
        r["ExecutableInfo"].Should().Be("BITS");
        EvtxMapEvaluator.RecordId(Sample7040).Should().Be(4242);
    }

    [Fact]
    public void Evaluate_handles_missing_refine_lookup_and_expressions()
    {
        var m = new EvtxMap { EventId = 7040, Channel = "System", Provider = "Service Control Manager",
            Maps =
            {
                new EvtxMapEntry { Property = "PayloadData1", PropertyValue = "x=%missing%", Values = { new EvtxMapValue { Name = "missing", Value = "/Event/EventData/Data[@Name=\"nope\"]" } } },
                new EvtxMapEntry { Property = "PayloadData2", PropertyValue = "%s%", Values = { new EvtxMapValue { Name = "s", Value = "/Event/EventData/Data[@Name=\"param1\"]", Refine = "Intelligent|Service" } } },
                new EvtxMapEntry { Property = "PayloadData3", PropertyValue = "%code%", Values = { new EvtxMapValue { Name = "code", Value = "/Event/EventData/Data[@Name=\"param4\"]" } } },
                new EvtxMapEntry { Property = "PayloadData4", PropertyValue = "%n%", Values = { new EvtxMapValue { Name = "n", Value = "count(/Event/EventData/Data)" } } },
            },
            Lookups = { new EvtxMapLookup { Name = "code", Default = "other", Values = { ["BITS"] = "bits service" } } } };
        var r = EvtxMapEvaluator.Evaluate(m, Sample7040);
        r["PayloadData1"].Should().Be("x=");
        r["PayloadData2"].Should().Be("Intelligent | Service");
        r["PayloadData3"].Should().Be("bits service");
        r["PayloadData4"].Should().Be("4");
    }

    [Fact]
    public void Normalize_renders_values_like_EvtxECmd()
    {
        // checked against EvtxECmd 2026 CSV output (System 1 / 105 / 6013, Application RestartManager 10002)
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventRecordID>1</EventRecordID></System>
            <EventData><Data></Data><Data>16</Data><Data>60</Data><Data>-480 Taipei Standard Time</Data></EventData>
            <UserData><Rm xmlns="urn:x"><Ok>true</Ok><T>2026-09-11T10:33:50.5Z</T><Files><File>a</File><File>b</File></Files></Rm></UserData></Event>
            """;
        var n = EvtxMapEvaluator.Normalize(xml);
        var nav = new System.Xml.XPath.XPathDocument(new StringReader(n)).CreateNavigator();
        EvtxMapEvaluator.Select(nav, "/Event/EventData/Data").Should().Be("16, 60, -480 Taipei Standard Time");
        EvtxMapEvaluator.Select(nav, "/Event/UserData/Rm/Ok").Should().Be("True");
        EvtxMapEvaluator.Select(nav, "/Event/UserData/Rm/T").Should().Be("2026-09-11 10:33:50.5000000");
        EvtxMapEvaluator.Select(nav, "/Event/UserData/Rm/Files/File").Should().Be("a, b");
        EvtxMapEvaluator.Normalize(n).Should().Be(n);
    }

    [Fact]
    public void Lookup_unknown_code_and_empty_value()
    {
        var m = new EvtxMap { EventId = 1, Channel = "System", Provider = "P",
            Maps = { new EvtxMapEntry { Property = "PayloadData1", PropertyValue = "R %Reason%", Values = { new EvtxMapValue { Name = "Reason", Value = "/Event/EventData/Data[@Name=\"Reason\"]" } } } },
            Lookups = { new EvtxMapLookup { Name = "Reason", Default = "Unknown code", Values = { ["1"] = "one" } } } };
        string X(string v) => $"<Event><System/><EventData><Data Name=\"Reason\">{v}</Data></EventData></Event>";
        EvtxMapEvaluator.Evaluate(m, X("1"))["PayloadData1"].Should().Be("R one");
        EvtxMapEvaluator.Evaluate(m, X("13"))["PayloadData1"].Should().Be("R Unknown code (13)");
        EvtxMapEvaluator.Evaluate(m, X(""))["PayloadData1"].Should().Be("R ");
    }

    [Fact]
    public void Fields_lists_data_by_name_with_xpaths()
    {
        var f = EvtxMapEvaluator.Fields(Sample7040);
        f.Should().Contain(x => x.Label == "param1" && x.XPath == "/Event/EventData/Data[@Name=\"param1\"]" && x.Value == "Background Intelligent Transfer Service");
        f.Should().Contain(x => x.Label == "@Name" && x.XPath == "/Event/System/Provider/@Name");
        f.Should().Contain(x => x.Label == "EventData" && !x.IsLeaf);
        // every leaf XPath selects its own value
        var nav = new System.Xml.XPath.XPathDocument(new StringReader(EvtxMapEvaluator.Normalize(Sample7040))).CreateNavigator();
        foreach (var x in f.Where(x => x.IsLeaf)) EvtxMapEvaluator.Select(nav, x.XPath).Should().Be(x.Value);
    }

    [Fact]
    public void Custom_maps_are_copied_with_prefix_and_stale_copies_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezf-maps-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new EvtxCustomMaps(Path.Combine(root, "custom"));
            var tool = Path.Combine(root, "tool");
            var maps = Path.Combine(tool, "Maps");
            Directory.CreateDirectory(maps);
            var stock = Path.Combine(maps, "System_Service-Control-Manager_7040.map");
            File.Copy(MapPath("System_Service-Control-Manager_7040.map"), stock);
            var stockBytes = File.ReadAllBytes(stock);

            var m = EvtxMapYaml.Load(stock);
            m.Description = "custom";
            var saved = store.Save(m);
            Path.GetFileName(saved).Should().Be("System_Service-Control-Manager_7040.map");
            store.Has(new EvtxEventKey("system", "service control manager", 7040)).Should().BeTrue();

            var lines = EvtxCustomMaps.ApplyForRun(new[] { "-d", "x", "--csv", "y" }, tool, store.Folder);
            lines.Should().ContainSingle(l => l.Contains("1 in use"));
            var copy = Path.Combine(maps, EvtxCustomMaps.Prefix + "System_Service-Control-Manager_7040.map");
            File.Exists(copy).Should().BeTrue();
            File.ReadAllBytes(stock).Should().Equal(stockBytes);   // stock map untouched
            // the copy sorts before the stock map (EvtxECmd: first map per key wins)
            Directory.GetFiles(maps, "*.map").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First().Should().Be(copy);
            Directory.GetFiles(maps, "*.map").OrderBy(f => f, StringComparer.Ordinal).First().Should().Be(copy);
            EvtxMapFiles.Find(maps, m.Key, EvtxCustomMaps.Prefix).Should().Be(stock);

            // --maps elsewhere: nothing written there
            var other = Path.Combine(root, "mine");
            EvtxCustomMaps.ApplyForRun(new[] { "--maps", other }, tool, store.Folder).Should().ContainSingle(l => l.Contains("not copied"));
            Directory.Exists(other).Should().BeFalse();

            // reset to stock removes the custom map and its copy
            store.Delete(m.Key, maps).Should().BeTrue();
            File.Exists(copy).Should().BeFalse();
            File.WriteAllText(copy, "stale");
            EvtxCustomMaps.SyncInto(store.Folder, maps).Removed.Should().Be(1);
            File.Exists(stock).Should().BeTrue();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
