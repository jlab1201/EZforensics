using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views;
using EzForensics.Core.Catalog;
using EzForensics.Core.Settings;
using FluentAssertions;

namespace EzForensics.App.Tests;

/// <summary>Renders the Tools list sections (real ToolsView template, sample items) in light and dark to PNG under artifacts/snapshots.</summary>
[Collection("WpfApp")]
public sealed class ToolsSectionsSnapshotTests
{
    private static void NewAppWithoutStartup()
    {
        var posted = new List<System.Windows.Threading.DispatcherOperation>();
        var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
        void OnPosted(object? s, System.Windows.Threading.DispatcherHookEventArgs e) => posted.Add(e.Operation);
        hooks.OperationPosted += OnPosted;
        try { new App().InitializeComponent(); }
        finally { hooks.OperationPosted -= OnPosted; }
        foreach (var op in posted) op.Abort();
    }

    private static IEnumerable<ToolListItem> Sample()
    {
        ToolListItem Item(ToolGroup g, string name, bool installed)
        {
            var t = new ToolListItem(ToolsSection.Explore, g, name, "Sample tool");
            t.Installed = installed;
            return t;
        }
        for (int i = 0; i < 6; i++) yield return Item(ToolGroup.Disk, "Disk tool " + (char)('A' + i), i < 3);
        for (int i = 0; i < 4; i++) yield return Item(ToolGroup.Timeline, "Timeline tool " + (char)('A' + i), i < 1);
        for (int i = 0; i < 3; i++) yield return Item(ToolGroup.Memory, "Memory tool " + (char)('A' + i), i < 2);
        for (int i = 0; i < 3; i++) yield return Item(ToolGroup.Network, "Network tool " + (char)('A' + i), false);
        yield return Item(ToolGroup.Ai, "AI", true);
        yield return Item(ToolGroup.Spectre, "Spectre engine tools", false);
    }

    [Fact]
    public void Tools_list_sections_render()
    {
        var written = new List<string>();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) NewAppWithoutStartup();
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                foreach (var width in new[] { 1400, 900 })
                {
                    ThemeManager.Apply(theme);
                    var view = new ToolsView();
                    var items = Sample().ToList();
                    var cv = new ListCollectionView(items);
                    cv.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(ToolListItem.Header)));
                    cv.SortDescriptions.Add(new SortDescription(nameof(ToolListItem.HeaderSort), ListSortDirection.Ascending));
                    cv.SortDescriptions.Add(new SortDescription(nameof(ToolListItem.DisplayName), ListSortDirection.Ascending));
                    var cat = (DockPanel)FindById(view, "Tools.Category");
                    cat.Visibility = Visibility.Visible;
                    ((DockPanel)FindById(view, "Tools.Landing")).Visibility = Visibility.Collapsed;
                    var list = (ListBox)FindById(view, "Tools.List");
                    list.ItemsSource = cv;
                    list.SelectedIndex = 2;

                    var host = new Border { Child = view, Padding = new Thickness(16), Width = width, Height = 900 };
                    host.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                    host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");
                    for (int i = 0; i < 2; i++) { host.Measure(new Size(width, 900)); host.Arrange(new Rect(0, 0, width, 900)); host.UpdateLayout(); }
                    var bmp = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var path = Path.Combine(FindingsSnapshotTests.SnapshotDir(), $"tools-sections-{theme.ToString().ToLowerInvariant()}-{width}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var fs = File.Create(path)) enc.Save(fs);
                    written.Add(path);
                    host.Child = null;
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        failure.Should().BeNull();
        written.Should().OnlyContain(p => File.Exists(p));
    }

    private static FrameworkElement FindById(DependencyObject root, string id)
    {
        FrameworkElement? hit = null;
        void Walk(DependencyObject d)
        {
            if (hit != null) return;
            if (d is FrameworkElement fe && System.Windows.Automation.AutomationProperties.GetAutomationId(fe) == id) { hit = fe; return; }
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c);
        }
        Walk(root);
        return hit ?? throw new InvalidOperationException(id);
    }
}
