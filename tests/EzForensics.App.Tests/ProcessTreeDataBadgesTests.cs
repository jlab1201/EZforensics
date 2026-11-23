using System.IO;
using EzForensics.App.ViewModels;
using EzForensics.Core.Processes;
using EzForensics.Core.Results;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Process tree data chips (linked rows per card tab), the details card's tab pick / total, and Flag all / Flag rows on a hand-built graph.</summary>
public sealed class ProcessTreeDataBadgesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ezf_badges_" + Guid.NewGuid().ToString("N"));
    public ProcessTreeDataBadgesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Counts per (pid, tab); Network rows of PID 20 point at a netscan CSV.</summary>
    private sealed class FakeSource : IProcessDetailSource
    {
        public Dictionary<int, Dictionary<ProcessTab, int>> CountsByPid { get; } = new();
        public string? NetCsv { get; set; }
        public IReadOnlyList<ProcessRowSet> SetsFor(ProcessNode node, ProcessTab tab)
        {
            if (tab != ProcessTab.Network || NetCsv == null || !CountsByPid.TryGetValue(node.Pid, out var c) || c.GetValueOrDefault(ProcessTab.Network) == 0)
                return Array.Empty<ProcessRowSet>();
            var origin = new ProcessRowOrigin(NetCsv, new FilterCondition("PID", ColumnFilterMode.Equals, node.Pid.ToString()));
            return new[] { new ProcessRowSet { Id = "net", Tab = tab, Rows = new[] { new ProcessActivityRow(null, null, "netscan", new[] { "x" }, origin) }, TotalRows = 1 } };
        }
        public ProcessRowSet Timeline(ProcessNode node) => new();
        public ProcessRowSet Sources(ProcessNode node) => new();
        public Dictionary<ProcessTab, int> Counts(ProcessNode node)
        {
            var d = Enum.GetValues<ProcessTab>().ToDictionary(t => t, _ => 0);
            if (CountsByPid.TryGetValue(node.Pid, out var c)) foreach (var (t, v) in c) d[t] = v;
            return d;
        }
        public string? SignatureText(ProcessNode node) => null;
        public IReadOnlyList<string> ServiceNames(ProcessNode node, int cap = 20) => Array.Empty<string>();
    }

    // roots: a(1); a: b(11); b: c(20) with an indicator, d(21)
    private static async Task<(ProcessTreeViewModel Vm, FakeSource Src)> LoadAsync(FakeSource? src = null)
    {
        var g = new ProcessGraph();
        ProcessNode N(int pid, string name, ProcessNode? parent)
        {
            var n = new ProcessNode { Pid = pid, Ppid = parent?.Pid ?? 0, Name = name, Parent = parent };
            parent?.Children.Add(n);
            g.AllNodes.Add(n);
            if (parent == null) g.Roots.Add(n);
            return n;
        }
        var a = N(1, "a.exe", null);
        var b = N(11, "b.exe", a);
        var c = N(20, "powershell.exe", b);
        c.CmdLine = "powershell.exe -enc " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("whoami"));   // an EncodedCommand indicator
        N(21, "d.exe", b);
        src ??= new FakeSource();
        src.CountsByPid[20] = new() { [ProcessTab.Network] = 3, [ProcessTab.Modules] = 12, [ProcessTab.Threats] = 4 };
        src.CountsByPid[21] = new() { [ProcessTab.Files] = 2 };
        var vm = new ProcessTreeViewModel("host", "HOST", (_, _) => { }, () => { }, (_, _) => Task.FromResult<string?>(null));
        vm.UseDetailSource(src);
        await vm.ShowGraphAsync(g, CancellationToken.None);
        await vm.DataCountsTask;
        return (vm, src);
    }

    private static IEnumerable<ProcessNodeViewModel> All(IEnumerable<ProcessNodeViewModel> nodes) => nodes.SelectMany(r => All(r.AllChildren).Prepend(r));
    private static ProcessNodeViewModel Node(ProcessTreeViewModel vm, int pid) => All(vm.VisibleRoots).First(n => n.Pid == pid);

    [Fact]
    public async Task Count_pass_puts_one_chip_per_rail_tab_with_rows_on_each_node()
    {
        var (vm, _) = await LoadAsync();
        var c = Node(vm, 20);
        c.DataChips.Select(x => (x.Tab, x.Count)).Should().Equal((ProcessTab.Network, 3), (ProcessTab.Modules, 12));   // Threats is not on the rail
        c.DataChips[0].Glyph.Should().Be(ProcessDetailViewModel.RailTabs.Single(r => r.Tab == ProcessTab.Network).Glyph);
        c.DataChips[0].AutomationId.Should().Be("Results.ProcessTree.Node.20.Data.Network");
        Node(vm, 1).HasDataChips.Should().BeFalse();
    }

    [Fact]
    public async Task Details_open_on_the_first_tab_with_data_a_chip_opens_its_tab_and_the_title_has_the_total()
    {
        var (vm, _) = await LoadAsync();
        vm.SelectNode(Node(vm, 20));
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Network);
        vm.Detail.TotalCount.Should().Be(15);
        vm.Detail.Tabs.Single(t => t.Tab == ProcessTab.Modules).HasData.Should().BeTrue();
        vm.Detail.Tabs.Single(t => t.Tab == ProcessTab.Files).IsDim.Should().BeTrue();

        vm.SelectNode(Node(vm, 1));
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Overview, "no tab has rows");
        vm.Detail.TotalCount.Should().Be(0);

        // a chip of another node opens that node's card on the chip's tab
        var c = Node(vm, 20);
        c.OpenDataTabCommand.Execute(c.DataChips[1]);
        vm.Selected.Should().BeSameAs(c);
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Modules);
        // a chip of the selected node switches the tab in place
        c.OpenDataTabCommand.Execute(c.DataChips[0]);
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Network);
    }

    [Fact]
    public async Task A_rail_pick_is_remembered_when_the_next_process_has_rows_there()
    {
        var (vm, _) = await LoadAsync();
        vm.SelectNode(Node(vm, 20));
        vm.Detail!.SelectedTab = vm.Detail.Tabs.Single(t => t.Tab == ProcessTab.Overview);   // the user picks Overview
        vm.SelectNode(Node(vm, 21));
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Overview);
        vm.Detail.SelectedTab = vm.Detail.Tabs.Single(t => t.Tab == ProcessTab.Files);
        vm.SelectNode(Node(vm, 20));
        vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Network, "20 has no Files rows: the first tab with data");
    }

    private static IEnumerable<System.Windows.DependencyObject> VisualTree(System.Windows.DependencyObject root)
    {
        yield return root;
        if (root is not System.Windows.Media.Visual) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in VisualTree(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    [Fact]
    [Trait("Category", "Flaky")]   // raw mouse input / shown window: timing-sensitive under load
    public async Task Pane_renders_the_chips_the_total_and_the_flag_buttons_and_a_chip_click_opens_its_tab()
    {
        var (vm, _) = await LoadAsync();
        vm.IsNavigatorOpen = true;
        vm.FlagRowsSink = files => 0;
        vm.SelectNode(Node(vm, 21));
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null) new App().InitializeComponent();
                var pane = new EzForensics.App.Views.Analyze.ProcessTreePane { DataContext = vm };
                var w = new System.Windows.Window { Width = 1100, Height = 760, Content = pane, ShowActivated = false, WindowStyle = System.Windows.WindowStyle.None, Left = -3000, Top = -3000, ShowInTaskbar = false };
                w.Show();
                w.UpdateLayout();
                System.Windows.FrameworkElement? Find(string id) => VisualTree(w).OfType<System.Windows.FrameworkElement>()
                    .FirstOrDefault(f => System.Windows.Automation.AutomationProperties.GetAutomationId(f) == id);
                Find("Results.ProcessTree.Detail.Total").Should().NotBeNull();
                Find("Results.ProcessTree.Detail.FlagRows").Should().NotBeNull();
                var flagAll = Find("Results.ProcessTree.Navigator.FlagAll");
                flagAll.Should().NotBeNull();
                flagAll!.IsVisible.Should().BeTrue();
                var chip = Find("Results.ProcessTree.Node.20.Data.Modules") as System.Windows.Controls.Button;
                chip.Should().NotBeNull("the chips render at the default zoom");
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(chip!)).Invoke();
                w.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
                vm.Selected!.Pid.Should().Be(20);
                vm.Detail!.SelectedTab.Tab.Should().Be(ProcessTab.Modules);
                w.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull(failure?.ToString());
    }

    [Fact]
    public async Task Flag_rows_and_Flag_all_send_the_rows_per_csv_and_confirm_large_batches()
    {
        var net = Path.Combine(_dir, "Memory_NetScan.csv");
        File.WriteAllText(net, "PID,Owner\r\n" + string.Join("\r\n", Enumerable.Range(0, 1200).Select(i => i % 2 == 0 ? "20,powershell.exe" : "21,d.exe")) + "\r\n");
        var src = new FakeSource { NetCsv = net };
        var (vm, _) = await LoadAsync(src);
        src.CountsByPid[21][ProcessTab.Network] = 1;
        var sent = new List<IReadOnlyList<ProcessCsvRows>>();
        var asked = new List<string>();
        vm.FlagRowsSink = files => { sent.Add(files); return files.Sum(f => f.Rows.Count); };
        vm.ConfirmFlag = m => { asked.Add(m); return false; };

        for (int i = 0; i < 1000 && !vm.HasIndicators; i++) await Task.Delay(10);
        vm.FlagAllIndicatorsCommand.CanExecute(null).Should().BeTrue();
        vm.IndicatorNodes.Select(n => n.Pid).Should().Equal(20);
        await vm.FlagAllIndicatorsCommand.ExecuteAsync(null);
        sent.Should().ContainSingle().Which.Single().Rows.Should().HaveCount(600).And.OnlyContain(r => r % 2 == 0);
        asked.Should().BeEmpty();
        vm.StatusText.Should().Contain("Flagged 600 rows in 1 file");

        // both processes: 1,200 rows > FlagConfirmRows asks first; No = nothing flagged
        await vm.FlagProcessesAsync(new[] { Node(vm, 20), Node(vm, 21) }, "both");
        asked.Should().ContainSingle().Which.Should().Contain("1,200 rows");
        sent.Should().HaveCount(1);
        vm.StatusText.Should().Be("Flagging cancelled.");

        // the card's Flag rows
        vm.SelectNode(Node(vm, 21));
        await vm.Detail!.FlagRowsCommand.ExecuteAsync(null);
        sent.Should().HaveCount(2);
        sent[1].Single().Rows.Should().HaveCount(600).And.OnlyContain(r => r % 2 == 1);
    }
}
