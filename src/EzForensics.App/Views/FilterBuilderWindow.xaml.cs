using System.Windows;
using System.Windows.Input;
using EzForensics.App.Services;
using EzForensics.App.ViewModels;
using EzForensics.App.Views.Analyze;

namespace EzForensics.App.Views;

public partial class FilterBuilderWindow : Window
{
    public FilterBuilderWindow()
    {
        InitializeComponent();
        Services.WindowChrome.HideIcon(this);
        Services.Appearance.Attach(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        SyntaxBox.Loaded += (_, _) => AlignSyntaxOverlay();
        SyntaxBox.SizeChanged += (_, _) => AlignSyntaxOverlay();
        SyntaxBox.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent, new System.Windows.Controls.ScrollChangedEventHandler((_, _) => AlignSyntaxOverlay()));
    }

    /// <summary>
    /// Lays the coloured overlay exactly over the syntax box's text: same left/top as its content host, the host's viewport width (so wrapping
    /// matches, with or without the scrollbar), shifted by the scroll offset and clipped to the viewport.
    /// </summary>
    private void AlignSyntaxOverlay()
    {
        if (SyntaxBox.Template?.FindName("PART_ContentHost", SyntaxBox) is not System.Windows.Controls.ScrollViewer sv || !sv.IsLoaded) return;
        Point o;
        try { o = sv.TransformToAncestor(SyntaxOverlayHost).Transform(new Point(0, 0)); }
        catch (InvalidOperationException) { return; }
        var w = Math.Max(0, sv.ViewportWidth);
        SyntaxOverlayHost.Clip = new System.Windows.Media.RectangleGeometry(new Rect(o.X, o.Y, w, Math.Max(0, sv.ViewportHeight)));
        SyntaxOverlay.Width = w;
        SyntaxOverlay.Margin = new Thickness(o.X, o.Y - sv.VerticalOffset, 0, 0);
    }

    private FilterBuilderViewModel? Vm => DataContext as FilterBuilderViewModel;

    private void Apply_Click(object sender, RoutedEventArgs e) => Vm?.Apply();

    /// <summary>Builder tab: apply, then close (stays open when the apply failed). Saved tab: Close.</summary>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedTabIndex: 0 } vm && !vm.Apply()) return;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    // ---- saving

    private void Save_Click(object sender, RoutedEventArgs e) => Vm?.Save();

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm == null || !vm.CanSave) return;
        var name = Analyze.SavedFilterUi.PromptSaveName(this, vm.CategorySignature, vm.CategoryName, SavedFilters.Instance.NextNumberedName(vm.CategorySignature));
        if (name == null) return;
        vm.Save(name);
    }

    // ---- saved filters tab

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterUi.Import(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SavedFilterUi.Export(this) is { } msg && Vm != null) Vm.Notice = msg;
    }

    private static SavedFilterItemViewModel? Item(object sender) => (sender as FrameworkElement)?.DataContext as SavedFilterItemViewModel;
    private static SavedCategoryViewModel? Category(object sender) => (sender as FrameworkElement)?.DataContext as SavedCategoryViewModel;

    private void SavedRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Item(sender) is { CanLoad: true } item) { Vm?.LoadSaved(item); e.Handled = true; }
    }

    private void EditSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { CanLoad: true } item) Vm?.LoadSaved(item);
    }

    // ---- field box: editable, drop-down narrows to the typed text, commits only a valid field

    private bool _fieldBusy;   // our own Text / list changes are not typing

    private static BuilderConditionViewModel? Cond(object sender) => (sender as FrameworkElement)?.DataContext as BuilderConditionViewModel;

    private static System.Windows.Controls.TextBox? InnerBox(System.Windows.Controls.ComboBox cb) =>
        cb.Template?.FindName("PART_EditableTextBox", cb) as System.Windows.Controls.TextBox;

    private void Field_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && Cond(cb) is { } c && !cb.IsDropDownOpen && c.ColumnChoices.Count == 0) c.RefreshChoices("");
    }

    private void Field_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_fieldBusy || sender is not System.Windows.Controls.ComboBox cb || Cond(cb) is not { } c || !cb.IsKeyboardFocusWithin) return;
        if (e.OriginalSource is not System.Windows.Controls.TextBox box) return;
        var typed = box.Text;
        var caret = box.CaretIndex;
        _fieldBusy = true;
        try
        {
            c.RefreshChoices(typed);   // replacing the list can reset the text: put it back
            cb.SelectedIndex = -1;
            cb.Text = typed;
            box.CaretIndex = Math.Min(caret, typed.Length);
            cb.IsDropDownOpen = c.ColumnChoices.Count > 0;
            box.CaretIndex = Math.Min(caret, typed.Length);
        }
        finally { _fieldBusy = false; }
    }

    private void Field_DropDownOpened(object sender, EventArgs e)
    {
        if (_fieldBusy || sender is not System.Windows.Controls.ComboBox cb || Cond(cb) is not { } c) return;
        // opened with the arrow (not by typing): show every field
        if (c.ColumnChoices.Count != c.Root.Columns.Count)
        {
            _fieldBusy = true;
            try
            {
                var text = cb.Text;
                c.RefreshChoices(string.Equals(text, c.ColumnText, StringComparison.Ordinal) ? "" : text);
                cb.Text = text;
            }
            finally { _fieldBusy = false; }
        }
    }

    private void Field_DropDownClosed(object sender, EventArgs e)
    {
        if (!_fieldBusy && sender is System.Windows.Controls.ComboBox cb) CommitField(cb);
    }

    private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && !cb.IsKeyboardFocusWithin) CommitField(cb);
    }

    private void Field_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb || Cond(cb) is not { } c) return;
        if (e.Key == Key.Escape)
        {
            bool dirty = cb.IsDropDownOpen || !string.Equals(cb.Text, c.ColumnText, StringComparison.Ordinal);
            if (!dirty) return;   // nothing to cancel: Esc closes the window as usual
            _fieldBusy = true;
            try { cb.IsDropDownOpen = false; cb.SelectedIndex = -1; cb.Text = c.ColumnText; }
            finally { _fieldBusy = false; }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (cb.IsDropDownOpen && cb.SelectedIndex < 0 && c.ColumnChoices.Count > 0 && !c.Root.TryResolveColumn(cb.Text, out _))
            {
                // no row highlighted and the text is not a field: take the best match
                _fieldBusy = true;
                try { cb.Text = c.ColumnChoices[0]; }
                finally { _fieldBusy = false; }
            }
            CommitField(cb);
            cb.IsDropDownOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>Typed/selected text becomes the field when it names one; otherwise it reverts.</summary>
    private void CommitField(System.Windows.Controls.ComboBox cb)
    {
        if (Cond(cb) is not { } c) return;
        _fieldBusy = true;
        try
        {
            c.CommitColumnText(cb.Text);
            cb.Text = c.ColumnText;
            c.RefreshChoices("");
        }
        finally { _fieldBusy = false; }
    }

    // ---- syntax box

    private readonly System.Windows.Threading.DispatcherTimer _syntaxTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _syntaxTimerWired;

    private void SyntaxBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SyntaxOverlay != null) Views.Controls.FilterHighlighter.SetText(SyntaxOverlay, SyntaxBox.Text);
        Dispatcher.BeginInvoke(AlignSyntaxOverlay, System.Windows.Threading.DispatcherPriority.Loaded);
        // Only keyboard / paste edits count; the binding pushing builder text into the box happens while the box is not focused.
        if (Vm is not { } vm || !SyntaxBox.IsKeyboardFocusWithin) return;
        vm.SyntaxEditing = true;
        if (!_syntaxTimerWired)
        {
            _syntaxTimerWired = true;
            _syntaxTimer.Tick += (_, _) =>
            {
                _syntaxTimer.Stop();
                Vm?.ApplySyntax(SyntaxBox.Text);
            };
        }
        _syntaxTimer.Stop();
        _syntaxTimer.Start();
    }

    private void SyntaxBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (_syntaxTimer.IsEnabled) { _syntaxTimer.Stop(); vm.ApplySyntax(SyntaxBox.Text); }
        vm.EndSyntaxEditing();
    }

    private void CopySyntax_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetDataObject(SyntaxBox.Text, true); if (Vm != null) Vm.Notice = "Syntax copied"; }
        catch { /* clipboard busy */ }
    }

    private void RenameSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        var name = TextPromptWindow.Ask(this, "Rename saved filter", "Filter name:", item.Name, t =>
        {
            if (string.IsNullOrWhiteSpace(t)) return "Enter a name.";
            var same = string.Equals(t.Trim(), item.Name, StringComparison.OrdinalIgnoreCase);
            return !same && SavedFilters.Instance.Find(item.CategorySignature, t.Trim()) != null ? "A filter with this name already exists in the category." : null;
        });
        if (name != null) vm.Rename(item, name);
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Item(sender) is not { } item) return;
        if (Dialogs.Confirm("Delete saved filter", $"Delete the saved filter '{item.Name}'?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
            vm.Delete(item);
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Category(sender) is not { } cat) return;
        var name = TextPromptWindow.Ask(this, "Rename category", "Category name:", cat.Name, t => string.IsNullOrWhiteSpace(t) ? "Enter a name." : null);
        if (name != null) vm.RenameCategory(cat, name.Trim());
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Category(sender) is not { } cat) return;
        if (Dialogs.Confirm("Delete category", $"Delete the category '{cat.Name}' and its {cat.Count} saved filter(s)?", yes: "Delete", no: "Cancel", defaultNo: true, owner: this))
            vm.DeleteCategory(cat);
    }
}
