using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

// Advanced search is a popup window (ExploreSearchWindow). Results are only revealed here; nothing is opened or run.
public sealed partial class ExploreViewModel
{
    public ExploreSearchViewModel Search { get; private set; } = null!;

    /// <summary>Raised after <see cref="RevealAsync"/> selected a row, so the view can scroll it into sight.</summary>
    public event Action<FileRow>? RowRevealed;

    private FileRow? _viewerRow;

    /// <summary>Plugged in by the view: shows (or activates) the Advanced search window for <see cref="Search"/>.</summary>
    public Action<ExploreSearchViewModel>? AdvancedSearchOpener { get; set; }

    [RelayCommand]
    private void OpenAdvancedSearch()
    {
        Search.OnEnter(CurrentPath);
        AdvancedSearchOpener?.Invoke(Search);
    }

    /// <summary>
    /// Back to Browse at a search result: lists its folder (the tree follows), selects the file and opens the viewer on it.
    /// With <paramref name="intoFolder"/> a folder result is entered instead of selected.
    /// </summary>
    public async Task RevealAsync(string path, bool intoFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string? folder = intoFolder ? path : Path.GetDirectoryName(path);
        // A single file added as a source is listed as itself.
        if (!intoFolder && _sources?.Owner(path) is { } owner && SamePath(owner.Path, path)) { folder = path; intoFolder = true; }
        if (string.IsNullOrEmpty(folder)) return;

        NameFilter = "";
        _history.Push(folder);
        UpdateNavState();
        await LoadFolderAsync(folder);
        if (intoFolder && !File.Exists(path)) return;

        FileRow? row = null;
        int last = -1;
        while (true)
        {
            row = Rows.FirstOrDefault(r => SamePath(r.FullPath, path));
            if (row != null || !HasMore || Rows.Count == last) break;
            last = Rows.Count;
            await LoadMoreAsync();
        }
        if (row == null) return;
        SelectedRow = row;
        RowRevealed?.Invoke(row);
    }
}
