using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EzForensics.App.Services;
using EzForensics.Core.Settings;

namespace EzForensics.App.Views.Analyze;

/// <summary>
/// Analyze "Keyboard shortcuts" list: groups with a monospace gesture column next to the action name, so the columns line up.
/// AutomationIds: <c>Results.Shortcuts.Window</c>, <c>Results.Shortcuts.Ok</c>.
/// </summary>
public sealed class ShortcutsWindow : Window
{
    public ShortcutsWindow(IReadOnlyDictionary<string, string> map)
    {
        Services.WindowChrome.HideIcon(this);
        Title = "Keyboard shortcuts";
        Width = 560;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        AutomationProperties.SetAutomationId(this, "Results.Shortcuts.Window");

        var grid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Header(string text)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = text.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, grid.RowDefinitions.Count > 1 ? 12 : 0, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetRow(t, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(t, 2);
            grid.Children.Add(t);
        }
        void Row(string gesture, string name, bool dim)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int r = grid.RowDefinitions.Count - 1;
            var g = new TextBlock { Text = gesture, Margin = new Thickness(8, 2, 20, 2), TextWrapping = TextWrapping.NoWrap };
            g.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            if (dim) g.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            var n = new TextBlock { Text = name, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(g, r); Grid.SetRow(n, r); Grid.SetColumn(n, 1);
            grid.Children.Add(g); grid.Children.Add(n);
        }

        foreach (var grp in HotKeys.Actions.GroupBy(a => a.Group))
        {
            Header(grp.Key);
            foreach (var a in grp)
            {
                bool has = map.TryGetValue(a.Id, out var gs) && gs.Length > 0;
                Row(has ? gs! : "(unassigned)", a.Name, !has);
            }
        }
        Header("Fixed");
        foreach (var f in HotKeys.Fixed) Row(f.Display, f.Name, false);

        var note = new TextBlock
        {
            Text = "Change the editable ones in Options > Hot Keys. Keys typed in a filter or search box are never intercepted.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(note);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 84, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        AutomationProperties.SetAutomationId(ok, "Results.Shortcuts.Ok");
        ok.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520, Content = body });
        Content = root;
        Appearance.Attach(this);
    }

    public static void ShowFor(DependencyObject owner, IReadOnlyDictionary<string, string> map) =>
        new ShortcutsWindow(map) { Owner = GetWindow(owner) }.ShowDialog();
}
