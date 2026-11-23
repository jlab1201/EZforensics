using System.ComponentModel;
using System.Windows;
using EzForensics.App.Services;

namespace EzForensics.App.Views.Explore;

/// <summary>The CyberChef popup. The user closing it only hides it (the host keeps the panel, so the recipe and input survive); the host closes it for real on app exit.</summary>
public partial class CyberChefWindow : Window
{
    public CyberChefWindow()
    {
        InitializeComponent();
        EzForensics.App.Controls.WindowSizing.FitToWorkArea(this);
        WindowChrome.HideIcon(this);
        Appearance.Attach(this);
    }

    public CyberChefPanel? Panel
    {
        get => PanelSlot.Content as CyberChefPanel;
        set => PanelSlot.Content = value;
    }

    /// <summary>Set by the host when it closes the window for good (app exit or a rebuilt panel).</summary>
    public bool ClosingByHost { get; set; }

    /// <summary>Raised after the user closed the window and it was only hidden.</summary>
    public event Action? HiddenByUser;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ClosingByHost) return;
        e.Cancel = true;
        Hide();
        HiddenByUser?.Invoke();
    }
}
