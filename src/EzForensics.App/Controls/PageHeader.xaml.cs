using System.Windows;
using System.Windows.Controls;

namespace EzForensics.App.Controls;

/// <summary>Page title with a Back button beside it (history lives in <c>ShellViewModel</c>).</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public PageHeader() { InitializeComponent(); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
}
