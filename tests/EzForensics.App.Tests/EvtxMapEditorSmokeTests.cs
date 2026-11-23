using System.Diagnostics;
using System.IO;
using System.Windows;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views;
using EzForensics.Core.EventLogs;
using EzForensics.Core.Settings;
using FluentAssertions;
using Xunit;

namespace EzForensics.App.Tests;

/// <summary>The map editor window binds and lays out; assigning a field fills the preview.</summary>
[Collection("WpfApp")]
public sealed class EvtxMapEditorSmokeTests
{
    private sealed class Collector : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message != null) lock (Lines) Lines.Add(message); }
    }

    private const string Xml = """
        <Event><System><Provider Name="Test-Provider"/><EventID>65001</EventID><EventRecordID>7</EventRecordID><Channel>Test/Operational</Channel></System>
        <EventData><Data Name="User">alice</Data><Data Name="Host">ws1</Data><Data Name="Empty"></Data></EventData></Event>
        """;

    [Fact]
    public void Editor_binds_assigns_fields_and_previews()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezf-mapeditor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var listener = new Collector();
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new App().InitializeComponent();
                foreach (var d in Application.Current!.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var s = new AppServices(new JsonSettingsStore(Path.Combine(dir, "settings.json")), new AppSettings());
                var key = new EvtxEventKey("Test/Operational", "", 65001);
                var vm = new EvtxMapEditorViewModel(s, key, "Test event", null, new EvtxCustomMaps(Path.Combine(dir, "maps")));
                var win = new EvtxMapEditorWindow { DataContext = vm };
                var root = (FrameworkElement)win.Content;
                void Layout() { root.Measure(new Size(1100, 760)); root.Arrange(new Rect(0, 0, 1100, 760)); root.UpdateLayout(); }
                Layout();

                vm.UseSamples(new[] { new EvtxSampleRecord(7, Xml, Path.Combine(dir, "Test%4Operational.evtx")) });
                vm.Key.Provider.Should().Be("Test-Provider");
                vm.Fields.Should().Contain(f => f.Label == "User");

                vm.SelectedField = vm.Fields.First(f => f.Label == "User");
                vm.AssignColumn = "UserName";
                vm.AssignCommand.Execute(null);
                vm.SelectedField = vm.Fields.First(f => f.Label == "Empty");
                vm.AssignColumn = "PayloadData1";
                vm.AssignCommand.Execute(null);
                Layout();

                vm.Columns.Select(c => c.Property).Should().Equal("UserName", "PayloadData1");
                vm.Preview!.Table.Rows[0]["UserName"].Should().Be("User: alice");
                ((string)vm.Preview.Table.Rows[0]["PayloadData1"]).Should().StartWith("(empty)");
                vm.PreviewNote.Should().Contain("PayloadData1 in 1 of 1");
                vm.HasProblems.Should().BeFalse();
                var map = vm.BuildMap();
                map.Validate().Should().BeEmpty();
                EvtxMapYaml.Parse(EvtxMapYaml.Write(map)).Maps.Should().HaveCount(2);

                vm.Columns[0].PropertyValue = "%User%@%Host%";
                vm.Preview!.Table.Rows[0]["UserName"].Should().Be("alice@%Host%");
                win.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        try { Directory.Delete(dir, true); } catch (IOException) { }
        if (failure != null) throw new Exception("Map editor smoke failed: " + failure, failure);
        listener.Lines.Should().BeEmpty("binding errors: " + string.Join("\n", listener.Lines));
    }
}
