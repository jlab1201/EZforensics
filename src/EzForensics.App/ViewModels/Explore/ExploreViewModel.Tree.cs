using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

/// <summary>A folder (or single-file source) in the Sources tree. Children load lazily on first expand and list folders only.</summary>
public sealed partial class ExploreNode : ObservableObject
{
    private readonly ExploreViewModel _owner;
    private bool _loaded;
    private Task _loadTask = Task.CompletedTask;

    public ExploreNode(ExploreViewModel owner, string name, string path, ExploreSource? source, bool isRoot, bool isFile, bool placeholder = false)
    {
        _owner = owner; Name = name; FullPath = path; Source = source; IsRoot = isRoot; IsFile = isFile; IsPlaceholder = placeholder;
        if (!placeholder && !isFile) Children.Add(new ExploreNode(owner, "", "", null, false, false, placeholder: true));
        else _loaded = true;
    }

    public string Name { get; }
    public string FullPath { get; }
    public ExploreSource? Source { get; }
    public bool IsRoot { get; }
    public bool IsFile { get; }
    public bool IsPlaceholder { get; }
    public bool CanRemove => IsRoot;
    public string Glyph => IsFile ? "" : IsRoot && Source?.IsCase == true ? "" : "";
    public ObservableCollection<ExploreNode> Children { get; } = new();

    // GuideTreeItem style contract (Themes/TreeStyles.xaml)
    public bool IsTopLevel => IsRoot;
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private bool _isOpenHighlighted;
    [ObservableProperty] private bool _isMultiSelected;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureChildrenAsync();
    }

    /// <summary>Loads the folder children once (also what expanding does); awaitable so the tree can walk down to a path.</summary>
    public Task EnsureChildrenAsync()
    {
        if (!_loaded) { _loaded = true; _loadTask = LoadChildrenAsync(); }
        return _loadTask;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        // The highlight does not depend on keyboard focus, so the explored folder stays marked when the tree loses focus.
        IsOpenHighlighted = value;
        if (value) _owner.OnNodeSelected(this);
    }

    private async Task LoadChildrenAsync()
    {
        var path = FullPath;
        var opts = _owner.OptionsFor(path);
        List<(string Name, string Path)> dirs;
        try
        {
            dirs = await Task.Run(() => FolderEnumerator.Enumerate(path, opts).Where(e => e.IsDirectory)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => (e.Name, e.FullPath)).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dirs = new(); }
        Children.Clear();
        for (int i = 0; i < dirs.Count; i++)
            Children.Add(new ExploreNode(_owner, dirs[i].Name, dirs[i].Path, null, false, false) { IsFirst = i == 0, IsLast = i == dirs.Count - 1 });
    }
}

public sealed partial class ExploreViewModel
{
    public ObservableCollection<ExploreNode> RootNodes { get; } = new();

    [ObservableProperty] private ExploreNode? _selectedNode;

    internal FolderEnumerationOptions OptionsFor(string path) =>
        _caseDir != null && SamePath(path, _caseDir) ? FolderEnumerationOptions.CaseRoot : FolderEnumerationOptions.Default;

    private void BuildTree()
    {
        RootNodes.Clear();
        if (_sources == null) return;
        var all = _sources.All;
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            var node = new ExploreNode(this, s.Label, s.Path, s, true, s.Kind == ExploreSourceKind.File) { IsFirst = i == 0, IsLast = i == all.Count - 1 };
            RootNodes.Add(node);
        }
    }

    private bool _syncingTree;
    private int _syncVersion;

    internal void OnNodeSelected(ExploreNode node)
    {
        if (node.IsPlaceholder) return;
        SelectedNode = node;
        if (_syncingTree) return;   // selection set by SyncTreeAsync: the list is already showing this folder
        NavigateTo(node.FullPath);
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects the deepest folder on the way (the tree lists folders only).</summary>
    private async Task SyncTreeAsync(string path)
    {
        int version = ++_syncVersion;
        if (SelectedNode != null && SamePath(SelectedNode.FullPath, path)) return;
        var node = RootNodes.FirstOrDefault(n => IsUnder(n.FullPath, path));
        if (node == null) return;
        try
        {
            while (!SamePath(node.FullPath, path))
            {
                node.IsExpanded = true;
                await node.EnsureChildrenAsync();
                if (version != _syncVersion) return;
                var next = node.Children.FirstOrDefault(c => !c.IsPlaceholder && IsUnder(c.FullPath, path));
                if (next == null) break;
                node = next;
            }
        }
        catch (Exception) { return; }
        if (version != _syncVersion || node.IsSelected) return;
        var old = SelectedNode;
        _syncingTree = true;
        try { node.IsSelected = true; if (old != null && old != node) old.IsSelected = false; }
        finally { _syncingTree = false; }
    }

    private static bool IsUnder(string root, string path)
    {
        if (SamePath(root, path)) return true;
        try
        {
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    [RelayCommand]
    private void AddFolderSource()
    {
        if (_sources == null) return;
        var dir = Dialogs.PickFolder("Add a folder to Explore", _caseDir);
        if (dir != null) AddSource(dir);
    }

    [RelayCommand]
    private void AddFileSource()
    {
        if (_sources == null) return;
        var file = Dialogs.PickFile("Add a file to Explore", "All files (*.*)|*.*", _caseDir);
        if (file != null) AddSource(file);
    }

    private void AddSource(string path) => Guard(() =>
    {
        var src = _sources!.Add(path);
        BuildTree();
        OnPropertyChanged(nameof(IsCaseHidden));
        var node = RootNodes.FirstOrDefault(n => n.Source?.Id == src.Id);
        if (node != null) node.IsSelected = true;
    }, "Could not add the source");

    [RelayCommand]
    private void RemoveSource(ExploreNode? node)
    {
        node ??= SelectedNode;
        if (_sources == null || node is not { CanRemove: true } || node.Source == null) return;
        if (!Dialogs.Confirm("Remove from the list", $"Remove \"{node.Name}\" from the Sources list? This only takes it out of the list; nothing on disk is touched.", "Remove")) return;
        // Only the entry in sources.json goes; the folder or file on disk is never touched.
        Guard(() =>
        {
            _sources.Remove(node.Source.Id);
            AfterSourcesChanged();
        }, "Could not remove the source");
    }

    /// <summary>True while the case folder is hidden from the Sources tree (Browse › Add folder with the case folder path brings it back).</summary>
    public bool IsCaseHidden => _sources?.CaseHidden == true;

    [RelayCommand]
    private void ShowCaseFolder()
    {
        if (_sources == null) return;
        Guard(() =>
        {
            _sources.ShowCase();
            BuildTree();
            OnPropertyChanged(nameof(IsCaseHidden));
            if (RootNodes.FirstOrDefault(n => n.Source?.IsCase == true) is { } caseNode) caseNode.IsSelected = true;
        }, "Could not show the case folder");
    }

    private void AfterSourcesChanged()
    {
        BuildTree();
        OnPropertyChanged(nameof(IsCaseHidden));
        if (RootNodes.FirstOrDefault() is { } first) first.IsSelected = true;
    }
}
