using System.Windows;
using System.Windows.Controls;

namespace EzForensics.App.Views;

/// <summary>The tools folder / downloads / updates block (<see cref="SectionProperty">Section</see> = "General") or the install +
/// configuration card of one special tool (Plaso, Zeek, Rita, Volatility). Hosted by the Tools page.</summary>
public partial class ToolManagerView : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section), typeof(string), typeof(ToolManagerView), new PropertyMetadata("General", (d, _) => ((ToolManagerView)d).ApplySection()));

    /// <summary>Zeek run controls (input, options, command, output) supplied by the Tools page, whose view model runs the job.</summary>
    public static readonly DependencyProperty ZeekRunProperty = DependencyProperty.Register(
        nameof(ZeekRun), typeof(object), typeof(ToolManagerView), new PropertyMetadata(null));

    /// <summary>Zeek set-up prompt shown in the Release tab while Zeek is not ready (supplied by the Tools page).</summary>
    public static readonly DependencyProperty ZeekReleaseProperty = DependencyProperty.Register(
        nameof(ZeekRelease), typeof(object), typeof(ToolManagerView), new PropertyMetadata(null));

    public object? ZeekRun
    {
        get => GetValue(ZeekRunProperty);
        set => SetValue(ZeekRunProperty, value);
    }

    public object? ZeekRelease
    {
        get => GetValue(ZeekReleaseProperty);
        set => SetValue(ZeekReleaseProperty, value);
    }

    public ToolManagerView()
    {
        InitializeComponent();
        ApplySection();
    }

    /// <summary>"General" or the name of a <see cref="Services.ToolsSection"/>.</summary>
    public string Section
    {
        get => (string)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private FrameworkElement? CardOf(string section) => section switch
    {
        "Plaso" => PlasoCard,
        "Zeek" => ZeekCard,
        "Rita" => RitaCard,
        "Volatility" => VolatilityCard,
        _ => null,
    };

    private void ApplySection()
    {
        if (RootPanel == null) return;
        var cards = new FrameworkElement[] { PlasoCard, PlasoOptionsCard, ZeekCard, RitaCard, VolatilityCard };
        var wanted = CardOf(Section);
        foreach (UIElement child in RootPanel.Children)
        {
            if (child is not FrameworkElement fe || fe.Tag as string == "always") continue;
            bool isCard = Array.IndexOf(cards, fe) >= 0;
            // General mode only hides the cards (the other blocks keep their own Visibility bindings); a section instance
            // never goes back to General, so overwriting those bindings there is fine.
            if (wanted == null) { if (isCard) fe.Visibility = Visibility.Collapsed; }
            else fe.Visibility = ReferenceEquals(fe, wanted) || (ReferenceEquals(wanted, PlasoCard) && ReferenceEquals(fe, PlasoOptionsCard)) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
