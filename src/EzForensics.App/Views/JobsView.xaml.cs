using System.Windows;
using System.Windows.Controls;
using EzForensics.App.ViewModels;

namespace EzForensics.App.Views;

/// <summary>
/// Jobs page. The grid is in the live run order (JobsViewModel.Rows) and never scrolls by itself; only a job the user picks (Result link
/// on Ingest, Retry, the selection) is scrolled into view.
/// </summary>
public partial class JobsView : UserControl
{
    private JobsViewModel? _vm;

    public JobsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) =>
        {
            Hook();
            Scroll(_vm?.Selected);
        };
    }

    private void Hook()
    {
        if (_vm != null) _vm.ScrollRequested -= Scroll;
        _vm = DataContext as JobsViewModel;
        if (_vm != null)
        {
            _vm.ScrollRequested -= Scroll;
            _vm.ScrollRequested += Scroll;
        }
    }

    private void Scroll(JobRow? row)
    {
        if (row == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Grid.SelectedItem = row;
            Grid.UpdateLayout();
            Grid.ScrollIntoView(row);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
