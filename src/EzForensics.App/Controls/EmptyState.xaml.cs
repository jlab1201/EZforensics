using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EzForensics.App.Controls;

/// <summary>Centred empty-state block: glyph, title, message and up to two actions (plus optional extra buttons).</summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = Reg<string>(nameof(Glyph), "", OnChanged);
    public static readonly DependencyProperty TitleProperty = Reg<string>(nameof(Title), "", OnChanged);
    public static readonly DependencyProperty MessageProperty = Reg<string>(nameof(Message), "", OnChanged);
    public static readonly DependencyProperty ActionTextProperty = Reg<string>(nameof(ActionText), "", OnChanged);
    public static readonly DependencyProperty ActionCommandProperty = Reg<ICommand?>(nameof(ActionCommand), null, null);
    public static readonly DependencyProperty SecondaryActionTextProperty = Reg<string>(nameof(SecondaryActionText), "", OnChanged);
    public static readonly DependencyProperty SecondaryActionCommandProperty = Reg<ICommand?>(nameof(SecondaryActionCommand), null, null);
    public static readonly DependencyProperty ExtraProperty = Reg<object?>(nameof(Extra), null, OnChanged);

    static DependencyProperty Reg<T>(string name, T def, PropertyChangedCallback? cb) =>
        DependencyProperty.Register(name, typeof(T), typeof(EmptyState), new PropertyMetadata(def, cb));

    public EmptyState() { InitializeComponent(); Loaded += (_, _) => Refresh(); Refresh(); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public string SecondaryActionText { get => (string)GetValue(SecondaryActionTextProperty); set => SetValue(SecondaryActionTextProperty, value); }
    public ICommand? SecondaryActionCommand { get => (ICommand?)GetValue(SecondaryActionCommandProperty); set => SetValue(SecondaryActionCommandProperty, value); }
    /// <summary>Optional extra content (more buttons) shown after the two standard actions.</summary>
    public object? Extra { get => GetValue(ExtraProperty); set => SetValue(ExtraProperty, value); }

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is EmptyState s) s.Refresh(); }

    void Refresh()
    {
        if (PrimaryButton is null) return;
        TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        GlyphText.Visibility = string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Visibility = string.IsNullOrEmpty(SecondaryActionText) ? Visibility.Collapsed : Visibility.Visible;
        ActionsPanel.Visibility = PrimaryButton.Visibility == Visibility.Visible || SecondaryButton.Visibility == Visibility.Visible || Extra is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
