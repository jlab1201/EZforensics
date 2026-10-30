using System.Windows;
using EzForensics.App.Services;
using EzForensics.Core.Triage;

namespace EzForensics.App.Views;

/// <summary>Small modal window of the Ingest page: the details of one Detected artifacts card (one bordered box per system or artifact).</summary>
public partial class IngestDetailWindow : Window
{
    /// <summary>One box: the lines and its AutomationId (<c>Ingest.Detail.Box.&lt;n&gt;</c>).</summary>
    public sealed record BoxItem(string AutomationId, IReadOnlyList<KeyValuePair<string, string>> Rows);

    public IngestDetailWindow(string heading, string iconGlyph, IReadOnlyList<DetailBox> boxes)
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Appearance.Attach(this);
        Title = heading;
        HeadingText.Text = heading;
        IconText.Text = iconGlyph;
        Boxes.ItemsSource = boxes.Select((b, i) => new BoxItem("Ingest.Detail.Box." + (i + 1), b.Rows)).ToList();
    }

    public static void Show(Window? owner, string heading, string iconGlyph, IReadOnlyList<DetailBox> boxes)
    {
        if (boxes.Count == 0) return;
        new IngestDetailWindow(heading, iconGlyph, boxes) { Owner = owner }.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
