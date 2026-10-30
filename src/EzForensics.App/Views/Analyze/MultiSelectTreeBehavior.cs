using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using static EzForensics.App.Views.Analyze.ViewHelpers;

namespace EzForensics.App.Views.Analyze;

/// <summary>Multi-selection model behind a tree (the TreeView's DataContext implements it).</summary>
public interface IMultiSelectTree
{
    /// <summary>Plain click / keyboard selection: <paramref name="item"/> becomes the only selected item.</summary>
    void SelectSingle(object? item);
    /// <summary>Ctrl+click: toggles <paramref name="item"/> (the current single selection joins the set first).</summary>
    void ToggleSelection(object item);
    /// <summary>Shift+click: anchor..item in visible order (Ctrl+Shift adds to the set).</summary>
    void SelectRange(object item, bool additive);
    /// <summary>Ctrl+A: every visible item.</summary>
    void SelectAllVisible();
    /// <summary>True when <paramref name="item"/> is part of the multi-selection.</summary>
    bool IsInSelection(object item);
    /// <summary>False for items a plain click only expands/collapses (the selection stays where it is).</summary>
    bool SelectsOnClick(object item);
}

/// <summary>
/// Attached behaviour for a WPF TreeView (single-select by design): Ctrl+click toggles, Shift+click selects a range in visible order,
/// Ctrl+A selects all visible items, a right-click on an unselected item selects only it (without opening it). Plain clicks on files keep the
/// TreeView's own selection (which opens files) and reset the set; a plain click on a folder only expands/collapses it. Usage: <c>an:MultiSelectTreeBehavior.IsEnabled="True"</c>.
/// </summary>
public static class MultiSelectTreeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MultiSelectTreeBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree) return;
        if ((bool)e.NewValue)
        {
            tree.PreviewMouseLeftButtonDown += OnPreviewLeftDown;
            tree.PreviewMouseRightButtonDown += OnPreviewRightDown;
            tree.PreviewKeyDown += OnPreviewKeyDown;
            tree.SelectedItemChanged += OnSelectedItemChanged;
        }
        else
        {
            tree.PreviewMouseLeftButtonDown -= OnPreviewLeftDown;
            tree.PreviewMouseRightButtonDown -= OnPreviewRightDown;
            tree.PreviewKeyDown -= OnPreviewKeyDown;
            tree.SelectedItemChanged -= OnSelectedItemChanged;
        }
    }

    private static IMultiSelectTree? Host(TreeView tree) => tree.DataContext as IMultiSelectTree;

    /// <summary>The item row under the mouse, or null for the expander chevron, scroll bars and empty space.</summary>
    private static TreeViewItem? ItemAt(MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<ToggleButton>(src) != null || FindAncestor<ScrollBar>(src) != null) return null;
        return FindAncestor<TreeViewItem>(src);
    }

    private static void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree || Host(tree) is not { } host || ItemAt(e) is not { } tvi || tvi.DataContext is not { } item) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            host.SelectRange(item, (mods & ModifierKeys.Control) != 0);
            e.Handled = true; // keep the TreeView's own selection (= the open file)
            if (!tree.IsKeyboardFocusWithin) tree.Focus();
        }
        else if ((mods & ModifierKeys.Control) != 0)
        {
            host.ToggleSelection(item);
            e.Handled = true;
            if (!tree.IsKeyboardFocusWithin) tree.Focus();
        }
        else if (!host.SelectsOnClick(item))
        {
            // folders: toggle open/closed and keep the selection on the open file (the set resets to it)
            if (e.ClickCount == 1) tvi.IsExpanded = !tvi.IsExpanded;
            host.SelectSingle(tree.SelectedItem);
            e.Handled = true;
            if (!tree.IsKeyboardFocusWithin) tree.Focus();
        }
        else if (e.ClickCount == 1)
            host.SelectSingle(item); // also when the clicked item already is the TreeView's selected item
    }

    private static void OnPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree || Host(tree) is not { } host || ItemAt(e) is not { } tvi || tvi.DataContext is not { } item) return;
        if (!host.IsInSelection(item)) host.SelectSingle(item);
        if (!tree.IsKeyboardFocusWithin) tree.Focus();
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TreeView tree || Host(tree) is not { } host) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            host.SelectAllVisible();
            e.Handled = true;
        }
    }

    private static void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView tree || Host(tree) is not { } host) return;
        // keyboard navigation and programmatic selection (reveal, sort) reset the set to the selected item,
        // unless the item already is part of a larger selection (a refresh re-selecting the open file)
        if (e.NewValue != null && host.IsInSelection(e.NewValue)) return;
        host.SelectSingle(e.NewValue);
    }
}
