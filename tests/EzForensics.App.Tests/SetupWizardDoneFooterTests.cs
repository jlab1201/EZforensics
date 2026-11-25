using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.Core.Settings;
using EzForensics.Core.Setup;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>The setup wizard's Done page (also after the restart-resume) shows exactly one footer button, and no footer buttons overlap at small sizes.</summary>
[Collection("WpfApp")]
public sealed class SetupWizardDoneFooterTests
{
    private sealed class NoMachineProbe : EzForensics.Core.Runtimes.IRuntimeProbe
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public string? FindOnPath(string fileName) => null;
        public string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) => null;
    }

    private static void Pump(Func<bool> until, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (until()) return;
            Thread.Sleep(10);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !d.GetFiles("*.sln").Any()) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static FrameworkElement LoadWindowContent()
    {
        var x = File.ReadAllText(Path.Combine(RepoRoot(), "src", "EzForensics.App", "Views", "SetupWizardWindow.xaml")).TrimStart('\uFEFF');
        var rootEnd = x.IndexOf('>', x.IndexOf("<Window", StringComparison.Ordinal));
        var root = x[..(rootEnd + 1)];
        var keep = System.Text.RegularExpressions.Regex.Matches(root, @"\s(xmlns(:\w+)?|Background|Foreground|FontFamily|FontSize|TextOptions\.TextFormattingMode|UseLayoutRounding)=""[^""]*""").Select(m => m.Value);
        var assets = Path.Combine(RepoRoot(), "src", "EzForensics.App", "Assets");
        x = "<UserControl" + string.Concat(keep) + ">" + x[(rootEnd + 1)..];
        x = System.Text.RegularExpressions.Regex.Replace(x, @"\sTextChanged=""[^""]*""", "");
        x = System.Text.RegularExpressions.Regex.Replace(x, @"clr-namespace:([\w.]+)""", "clr-namespace:$1;assembly=" + typeof(App).Assembly.GetName().Name + "\"");
        x = x.Replace("<Window.Resources>", "<UserControl.Resources>").Replace("</Window.Resources>", "</UserControl.Resources>").Replace("</Window>", "</UserControl>");
        x = x.Replace("pack://application:,,,/Assets/", new Uri(assets + Path.DirectorySeparatorChar).AbsoluteUri);
        return (FrameworkElement)System.Windows.Markup.XamlReader.Parse(x);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); } catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("Setup footer test failed: " + failure, failure);
    }

    private static void EnsureApp()
    {
        if (Application.Current == null) new App().InitializeComponent();
        ThemeManager.Apply(AppTheme.Dark);
        foreach (var n in new[] { "Controls", "TreeStyles" })
            if (!Application.Current.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.EndsWith(n + ".xaml", StringComparison.OrdinalIgnoreCase) == true))
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/EzForensics;component/Themes/{n}.xaml", UriKind.Absolute) });
        foreach (var d in Application.Current.Resources.MergedDictionaries) foreach (var k in d.Keys.Cast<object>().ToList()) _ = d[k];
    }

    private static SetupWizardViewModel ResumeToDone(string tmp)
    {
        var s = new AppServices(new JsonSettingsStore(Path.Combine(tmp, "settings.json")), new AppSettings());
        // the resume path (opens on Install, field write) with nothing left to install: ends on the Done page by itself
        var vm = new SetupWizardViewModel(s, new SetupSelection(), true, null, new NoMachineProbe());
        vm.Page.Should().Be(SetupPage.Install);
        Pump(() => vm.Page == SetupPage.Done);
        return vm;
    }

    [Fact]
    public void Resume_ends_on_Done_with_a_single_footer_button_that_never_overlaps()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ezf-footer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        OnSta(() =>
        {
            EnsureApp();
            var sizes = new[] { (1024, 700), (980, 720), (760, 500) };
            var contents = sizes.Select(_ => LoadWindowContent()).ToList(); // parsed first: the app (and its resources) is gone after the wizard ran
            var vm = ResumeToDone(tmp);
            vm.Page.Should().Be(SetupPage.Done);
            vm.ShowOpenApp.Should().BeTrue();
            vm.ShowNext.Should().BeFalse();
            vm.ShowBack.Should().BeFalse();
            vm.ShowCancel.Should().BeFalse();

            for (int n = 0; n < sizes.Length; n++)
            {
                var (w, h) = sizes[n]; var content = contents[n];
                content.DataContext = vm;
                var host = new Border { Child = content, Width = w, Height = h };
                host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout();
                var footer = Descendants(host).OfType<Button>()
                    .Where(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) is "Setup.Next" or "Setup.Done.OpenEvidence" or "Setup.Back" or "Setup.Cancel")
                    .Where(b => b.Visibility == Visibility.Visible).ToList();
                footer.Select(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b)).Should().Equal(new[] { "Setup.Done.OpenEvidence" }, $"at {w}x{h}");
                var rects = footer.Select(b => b.TransformToAncestor(host).TransformBounds(new Rect(b.RenderSize))).ToList();
                for (int i = 0; i < rects.Count; i++)
                    for (int j = i + 1; j < rects.Count; j++) rects[i].IntersectsWith(rects[j]).Should().BeFalse($"at {w}x{h}");
                rects.Should().OnlyContain(r => r.Right <= w && r.Left >= 0, $"at {w}x{h}");
                content.DataContext = null;
            }
        });
        try { Directory.Delete(tmp, true); } catch (IOException) { }
    }
}
