using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>Options > Hot Keys: one editable shortcut row.</summary>
public sealed partial class HotKeyEditItem : ObservableObject
{
    public HotKeyEditItem(HotKeyAction action, string gesture)
    {
        Action = action;
        _gesture = gesture;
    }

    public HotKeyAction Action { get; }
    public string Id => Action.Id;
    public string Name => Action.Name;
    public string DefaultText => HotKeys.Normalize(Action.Default) ?? "";

    /// <summary>Effective gesture (canonical text); "" = unassigned.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyBoxText), nameof(IsDefault))] private string _gesture;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyBoxText))] private bool _isCapturing;
    /// <summary>Inline warning (conflict with another action); empty when fine.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWarning))] private string _warning = "";

    public bool HasWarning => Warning.Length > 0;
    public bool IsDefault => string.Equals(Gesture, DefaultText, StringComparison.OrdinalIgnoreCase);
    public string KeyBoxText => IsCapturing ? "Press a key combination…" : Gesture.Length == 0 ? "Unassigned" : Gesture;
}

/// <summary>A group of rows in the Hot Keys card.</summary>
public sealed class HotKeyGroup
{
    public HotKeyGroup(string name, IEnumerable<HotKeyEditItem> items) { Name = name; Items = items.ToList(); }
    public string Name { get; }
    public IReadOnlyList<HotKeyEditItem> Items { get; }
}

public sealed partial class SettingsViewModel
{
    private readonly List<HotKeyEditItem> _hotKeyItems = new();
    public ObservableCollection<HotKeyGroup> HotKeyGroups { get; } = new();
    /// <summary>Hard-coded shortcuts, listed read-only under the editable ones.</summary>
    public static IReadOnlyList<FixedHotKey> FixedHotKeys => HotKeys.Fixed;

    private void LoadHotKeys(IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var i in _hotKeyItems) i.PropertyChanged -= OnHotKeyChanged;
        _hotKeyItems.Clear();
        HotKeyGroups.Clear();
        var eff = HotKeys.Resolve(overrides);
        foreach (var a in HotKeys.Actions)
        {
            var item = new HotKeyEditItem(a, eff[a.Id]);
            item.PropertyChanged += OnHotKeyChanged;
            _hotKeyItems.Add(item);
        }
        foreach (var g in _hotKeyItems.GroupBy(i => i.Action.Group)) HotKeyGroups.Add(new HotKeyGroup(g.Key, g));
    }

    private void OnHotKeyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HotKeyEditItem.Gesture)) IsDirty = true;
    }

    private Dictionary<string, string> EffectiveHotKeys() => _hotKeyItems.ToDictionary(i => i.Id, i => i.Gesture);

    /// <summary>Overrides to save (only differences from the defaults).</summary>
    private Dictionary<string, string> HotKeyOverrides() => HotKeys.ToOverrides(EffectiveHotKeys());

    /// <summary>
    /// Assigns a captured chord. A chord already used by another action (or a fixed key) is refused with an inline warning
    /// naming it, so nothing conflicting can be saved; free the other action first.
    /// </summary>
    public bool AssignHotKey(HotKeyEditItem item, string gesture)
    {
        var norm = HotKeys.Normalize(gesture);
        if (norm == null) return false;
        var other = HotKeys.FindConflict(EffectiveHotKeys(), item.Id, norm);
        if (other != null)
        {
            item.Warning = $"{norm} is already used by \"{other}\". Clear or change that one first.";
            return false;
        }
        item.Warning = "";
        item.Gesture = norm;
        return true;
    }

    [RelayCommand]
    private void ClearHotKey(HotKeyEditItem? item)
    {
        if (item == null) return;
        item.IsCapturing = false;
        item.Warning = "";
        item.Gesture = "";
    }

    [RelayCommand]
    private void ResetHotKey(HotKeyEditItem? item)
    {
        if (item == null) return;
        item.IsCapturing = false;
        item.Warning = "";
        if (!item.IsDefault) AssignHotKey(item, item.DefaultText);
    }

    [RelayCommand]
    private void ResetAllHotKeys()
    {
        foreach (var i in _hotKeyItems) { i.IsCapturing = false; i.Warning = ""; i.Gesture = i.DefaultText; }
        IsDirty = true;
    }

    /// <summary>Only one key box captures at a time.</summary>
    public void BeginHotKeyCapture(HotKeyEditItem item)
    {
        foreach (var i in _hotKeyItems) i.IsCapturing = ReferenceEquals(i, item);
        item.Warning = "";
    }
}
