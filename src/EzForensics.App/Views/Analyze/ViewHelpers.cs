using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EzForensics.App.ViewModels;
using EzForensics.Core.Results;

namespace EzForensics.App.Views.Analyze;

/// <summary>Visual-tree and popup helpers shared by the Analyze controls.</summary>
internal static class ViewHelpers
{
    /// <summary>Popups live outside the window's visual tree, so they get the UI zoom explicitly.</summary>
    public static T Scaled<T>(T child) where T : FrameworkElement
    {
        Services.Appearance.ApplyTo(child);
        return child;
    }

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var g in FindVisualChildren<T>(child)) yield return g;
        }
    }

    private static FilterBuilderWindow? _filterBuilder;

    /// <summary>
    /// Opens the (modeless) filter builder over the current expression, or activates the open one (one instance); savedTab selects the Saved filters tab,
    /// otherwise the Builder tab. The window applies through its Apply / OK buttons.
    /// </summary>
    public static void EditFilter(ResultsViewModel? vm, DependencyObject owner, bool savedTab = false)
    {
        if (vm == null) return;
        if (_filterBuilder is { } open)
        {
            if (open.DataContext is FilterBuilderViewModel { Results: { } r } && ReferenceEquals(r, vm))
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                ((FilterBuilderViewModel)open.DataContext).SelectedTabIndex = savedTab ? 1 : 0;
                open.Activate();
                return;
            }
            open.Close();
        }
        if (vm.Headers.Count == 0)
        {
            vm.ShowMessage("Open a CSV first.", InfoSeverity.Warning);
            return;
        }
        var bvm = new FilterBuilderViewModel(vm.Headers, vm.BuildExpression(), vm.GlobalText, vm.ActiveSavedFilter,
            vm.CurrentSignature, vm.CurrentCategoryName, vm);
        if (savedTab) bvm.SelectedTabIndex = 1;
        var win = new FilterBuilderWindow { DataContext = bvm, Owner = Window.GetWindow(owner) };
        win.Closed += (_, _) => { if (ReferenceEquals(_filterBuilder, win)) _filterBuilder = null; };
        _filterBuilder = win;
        win.Show();
    }
}
