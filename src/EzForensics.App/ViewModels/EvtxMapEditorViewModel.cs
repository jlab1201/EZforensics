using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.App.Services;
using EzForensics.Core.EventLogs;

namespace EzForensics.App.ViewModels;

/// <summary>A field of the sample record (left list of the map editor).</summary>
public sealed class EvtxFieldItem
{
    public EvtxFieldItem(EvtxXmlField f) => Field = f;
    public EvtxXmlField Field { get; }
    public string Label => Field.Label;
    public string Value => Field.Value;
    public bool IsLeaf => Field.IsLeaf;
    public System.Windows.Thickness Indent => new(Field.Depth * 14, 0, 0, 0);
}

/// <summary>A variable of a map column: name, XPath, optional Refine regex.</summary>
public sealed partial class EvtxMapValueViewModel : ObservableObject
{
    private readonly EvtxMapColumnViewModel _owner;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _xPath;
    [ObservableProperty] private string _refine;

    public EvtxMapValueViewModel(EvtxMapColumnViewModel owner, EvtxMapValue v)
    {
        _owner = owner; _name = v.Name; _xPath = v.Value; _refine = v.Refine ?? "";
    }

    partial void OnNameChanged(string value) => _owner.Changed();
    partial void OnXPathChanged(string value) => _owner.Changed();
    partial void OnRefineChanged(string value) => _owner.Changed();

    [RelayCommand] private void Remove() => _owner.RemoveValue(this);

    public EvtxMapValue ToModel() => new() { Name = Name.Trim(), Value = XPath.Trim(), Refine = Refine.Length == 0 ? null : Refine };
}

/// <summary>One output column of the map: column name, format (<c>%Name%</c> per value), values.</summary>
public sealed partial class EvtxMapColumnViewModel : ObservableObject
{
    private readonly EvtxMapEditorViewModel _owner;
    [ObservableProperty] private string _property;
    [ObservableProperty] private string _propertyValue;

    public EvtxMapColumnViewModel(EvtxMapEditorViewModel owner, EvtxMapEntry e)
    {
        _owner = owner; _property = e.Property; _propertyValue = e.PropertyValue;
        Values = new ObservableCollection<EvtxMapValueViewModel>(e.Values.Select(v => new EvtxMapValueViewModel(this, v)));
    }

    public ObservableCollection<EvtxMapValueViewModel> Values { get; }
    public IReadOnlyList<string> ColumnNames => EvtxMap.Columns;

    partial void OnPropertyChanged(string value) => Changed();
    partial void OnPropertyValueChanged(string value) => Changed();

    public void Changed() => _owner.Refresh();

    public void AddValue(EvtxMapValue v)
    {
        Values.Add(new EvtxMapValueViewModel(this, v));
        Changed();
    }

    public void RemoveValue(EvtxMapValueViewModel v) { Values.Remove(v); Changed(); }

    [RelayCommand] private void Remove() => _owner.RemoveColumn(this);

    public EvtxMapEntry ToModel() => new() { Property = Property, PropertyValue = PropertyValue, Values = Values.Select(v => v.ToModel()).ToList() };
}

/// <summary>
/// Map editor for one event: sample records from the evidence, their fields, the map's columns, a live preview on the samples, and Save
/// (custom map, after an accuracy check with the installed EvtxECmd).
/// </summary>
public sealed partial class EvtxMapEditorViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly EvtxCustomMaps _store;
    private EvtxEventKey _key;
    private List<EvtxSampleRecord> _samples = new();
    private List<EvtxMapLookup> _lookups = new();
    private string _evidence;
    private bool _loading;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _mapSource = "";
    [ObservableProperty] private string _sampleStatus = "";
    [ObservableProperty] private string _previewNote = "";
    [ObservableProperty] private string _checkStatus = "";
    [ObservableProperty] private string _problems = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedSample = -1;
    [ObservableProperty] private EvtxFieldItem? _selectedField;
    [ObservableProperty] private string _assignColumn = "PayloadData1";
    [ObservableProperty] private DataView? _preview;

    public EvtxMapEditorViewModel(AppServices s, EvtxEventKey key, string title, string? evidence, EvtxCustomMaps? store = null)
    {
        _s = s;
        _store = store ?? new EvtxCustomMaps();
        _key = key;
        Title = title;
        _evidence = evidence ?? "";
        SampleStatus = evidence == null ? "Pick the evidence folder or an .evtx file to load sample records." : "";
        LoadMap();
    }

    public string Title { get; }
    public string Header => $"{_key.EventId}  ·  {_key.Channel}" + (_key.Provider.Length > 0 ? "  ·  " + _key.Provider : "");
    public EvtxEventKey Key => _key;
    public ObservableCollection<string> SampleLabels { get; } = new();
    public ObservableCollection<EvtxFieldItem> Fields { get; } = new();
    public ObservableCollection<EvtxMapColumnViewModel> Columns { get; } = new();
    public IReadOnlyList<string> ColumnNames => EvtxMap.Columns;
    public bool HasProblems => Problems.Length > 0;
    public bool HasCheckStatus => CheckStatus.Length > 0;
    public string LookupNote => _lookups.Count == 0 ? "" : $"{_lookups.Count} lookup table(s) from the loaded map are kept.";

    partial void OnProblemsChanged(string value) => OnPropertyChanged(nameof(HasProblems));
    partial void OnCheckStatusChanged(string value) => OnPropertyChanged(nameof(HasCheckStatus));
    partial void OnSelectedSampleChanged(int value) => ShowFields();
    partial void OnAuthorChanged(string value) => CheckStatus = "";
    partial void OnDescriptionChanged(string value) => CheckStatus = "";

    private string? ExePath => _s.ResolveNonBlocking("EvtxECmd.exe")?.Path;
    private string? ToolMapsDir => ExePath is { } p ? Path.Combine(Path.GetDirectoryName(p)!, "Maps") : null;

    private void LoadMap()
    {
        _loading = true;
        try
        {
            EvtxMap? map = null;
            if (_store.Find(_key) is { } custom)
            {
                map = TryLoad(custom);
                MapSource = map != null ? "Custom map: " + custom : "Custom map could not be read: " + custom;
            }
            if (map == null && ToolMapsDir is { } dir && EvtxMapFiles.Find(dir, _key, EvtxCustomMaps.Prefix) is { } stock)
            {
                map = TryLoad(stock);
                MapSource = map != null ? "Stock map: " + Path.GetFileName(stock) : "Stock map could not be read: " + stock;
            }
            if (map == null && MapSource.Length == 0) MapSource = ExePath == null ? "No map (EvtxECmd is not installed)." : "No map for this event yet.";
            if (map != null && map.Provider.Length > 0 && _key.Provider.Length == 0) _key = _key with { Provider = map.Provider };
            Author = map?.Author ?? "";
            Description = map?.Description is { Length: > 0 } d ? d : Title;
            _lookups = map?.Lookups ?? new();
            Columns.Clear();
            foreach (var e in map?.Maps ?? new()) Columns.Add(new EvtxMapColumnViewModel(this, e));
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(LookupNote));
        Refresh();
    }

    private static EvtxMap? TryLoad(string path)
    {
        try { return EvtxMapYaml.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { AppLog.Warn($"Could not read the map {path}: {ex.Message}"); return null; }
    }

    /// <summary>Loads the sample records from the remembered evidence path (called when the window opens).</summary>
    public Task StartAsync() => Directory.Exists(_evidence) || File.Exists(_evidence) ? LoadSamplesFromAsync(_evidence) : Task.CompletedTask;

    [RelayCommand]
    private async Task PickEvidenceAsync()
    {
        var dir = Dialogs.PickFolder("Select the folder with the .evtx files (searched recursively)", Directory.Exists(_evidence) ? _evidence : _s.LastCaseDir ?? _s.Settings.CaseRoot);
        if (string.IsNullOrEmpty(dir)) return;
        await LoadSamplesFromAsync(dir);
    }

    [RelayCommand]
    private async Task PickEvidenceFileAsync()
    {
        var f = Dialogs.PickFile("Select an .evtx file", "Event logs (*.evtx)|*.evtx|All files (*.*)|*.*", Directory.Exists(_evidence) ? _evidence : _s.LastCaseDir ?? _s.Settings.CaseRoot);
        if (string.IsNullOrEmpty(f)) return;
        await LoadSamplesFromAsync(f);
    }

    private async Task LoadSamplesFromAsync(string path)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _evidence = path;
        IsBusy = true;
        SampleStatus = $"Reading event {_key.EventId} from {path}…";
        var errors = new List<string>();
        try
        {
            var key = _key;
            var found = await Task.Run(() => EvtxSamples.Load(path, key, new EventLogSampleSource(), EvtxSamples.DefaultMax, errors, ct), ct);
            UseSamples(found);
            if (found.Count == 0)
                SampleStatus = $"No event {_key.EventId} ({_key.Channel}) found in {path}." + (errors.Count > 0 ? $" {errors.Count} file(s) could not be read ({errors[0]})." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SampleStatus = "Could not read the event logs: " + ex.Message; AppLog.Error("Map editor sample load failed", ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Shows these records as the samples (field list, preview).</summary>
    public void UseSamples(IReadOnlyList<EvtxSampleRecord> samples)
    {
        _samples = samples.ToList();
        if (_samples.Count > 0 && _key.Provider.Length == 0 && ProviderOf(_samples[0].Xml) is { Length: > 0 } p)
        {
            _key = _key with { Provider = p };
            OnPropertyChanged(nameof(Header));
        }
        SampleLabels.Clear();
        foreach (var r in _samples) SampleLabels.Add($"#{r.RecordId}  ·  {Path.GetFileName(r.SourceFile)}");
        SampleStatus = $"{_samples.Count} sample record(s) from {string.Join(", ", _samples.Select(r => Path.GetFileName(r.SourceFile)).Distinct())}.";
        SelectedSample = _samples.Count > 0 ? 0 : -1;
        ShowFields();
        Refresh();
    }

    private static string ProviderOf(string xml)
    {
        try { return XElement.Parse(xml).Element("System")?.Element("Provider")?.Attribute("Name")?.Value ?? ""; }
        catch (System.Xml.XmlException) { return ""; }
    }

    private void ShowFields()
    {
        Fields.Clear();
        if (SelectedSample < 0 || SelectedSample >= _samples.Count) return;
        foreach (var f in EvtxMapEvaluator.Fields(_samples[SelectedSample].Xml)) Fields.Add(new EvtxFieldItem(f));
    }

    /// <summary>Adds the selected field to <see cref="AssignColumn"/> (the column is created when missing; its format gets <c>%Name%</c>).</summary>
    [RelayCommand]
    private void Assign()
    {
        if (SelectedField is not { IsLeaf: true } f) return;
        var col = Columns.FirstOrDefault(c => c.Property.Equals(AssignColumn, StringComparison.OrdinalIgnoreCase));
        var name = f.Field.SuggestedName;
        if (col != null)
        {
            var used = new HashSet<string>(col.Values.Select(v => v.Name), StringComparer.Ordinal);
            var baseName = name; int i = 2;
            while (used.Contains(name)) name = baseName + i++;
            col.PropertyValue = col.PropertyValue.Length == 0 ? $"%{name}%" : $"{col.PropertyValue} %{name}%";
            col.AddValue(new EvtxMapValue { Name = name, Value = f.Field.XPath });
        }
        else
        {
            col = new EvtxMapColumnViewModel(this, new EvtxMapEntry { Property = AssignColumn, PropertyValue = $"{name}: %{name}%", Values = { new EvtxMapValue { Name = name, Value = f.Field.XPath } } });
            // keep the column order of EvtxECmd's CSV
            int at = 0, want = IndexOf(AssignColumn);
            while (at < Columns.Count && IndexOf(Columns[at].Property) < want) at++;
            Columns.Insert(at, col);
            Refresh();
        }
    }

    private static int IndexOf(string column) { var i = EvtxMap.Columns.ToList().FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase)); return i < 0 ? 99 : i; }

    public void RemoveColumn(EvtxMapColumnViewModel c) { Columns.Remove(c); Refresh(); }

    public EvtxMap BuildMap() => new()
    {
        Author = Author.Trim(), Description = Description.Trim(), EventId = _key.EventId, Channel = _key.Channel, Provider = _key.Provider,
        Maps = Columns.Select(c => c.ToModel()).ToList(),
        Lookups = _lookups,
    };

    /// <summary>Recomputes the preview table and the problems line.</summary>
    public void Refresh()
    {
        if (_loading) return;
        CheckStatus = "";
        var map = BuildMap();
        var problems = map.Validate();
        Problems = string.Join("  ", problems);
        var t = new DataTable();
        t.Columns.Add("Record");
        var cols = map.Maps.Select(m => m.Property).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var c in cols) t.Columns.Add(c);
        var empty = cols.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var r in _samples)
        {
            var vals = EvtxMapEvaluator.Evaluate(map, r.Xml);
            var row = t.NewRow();
            row["Record"] = "#" + r.RecordId;
            foreach (var c in cols)
            {
                var v = vals.TryGetValue(c, out var x) ? x : "";
                if (IsEmptyResult(map, c, v)) { empty[c]++; v = "(empty) " + v; }
                row[c] = v;
            }
            t.Rows.Add(row);
        }
        Preview = t.DefaultView;
        var emptyCols = empty.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} in {kv.Value} of {_samples.Count}").ToList();
        PreviewNote = _samples.Count == 0 ? "Load sample records to see the preview."
            : emptyCols.Count == 0 ? $"All mapped columns have values in the {_samples.Count} sample(s)."
            : "Empty: " + string.Join(", ", emptyCols) + ".";
    }

    /// <summary>True when every value of the column came back empty (only the format's fixed text is left).</summary>
    private static bool IsEmptyResult(EvtxMap map, string column, string value)
    {
        var e = map.Maps.FirstOrDefault(m => m.Property.Equals(column, StringComparison.OrdinalIgnoreCase));
        if (e == null) return value.Length == 0;
        var bare = e.PropertyValue;
        foreach (var v in e.Values) bare = bare.Replace("%" + v.Name + "%", "", StringComparison.Ordinal);
        return value.Trim() == bare.Trim();
    }

    [RelayCommand]
    private async Task CheckAsync() => await RunCheckAsync();

    private async Task<EvtxCheckResult?> RunCheckAsync()
    {
        var map = BuildMap();
        if (map.Validate() is { Count: > 0 } p) { CheckStatus = "Fix the map first: " + p[0]; return null; }
        if (ExePath is not { } exe) { CheckStatus = "EvtxECmd is not installed: the check was skipped."; return new EvtxCheckResult(false, CheckStatus, 0, Array.Empty<EvtxCheckMismatch>(), Array.Empty<string>()); }
        IsBusy = true;
        CheckStatus = "Running EvtxECmd on the sample…";
        try
        {
            var r = await EvtxMapCheck.RunAsync(exe, map, _samples, TimeSpan.FromMinutes(5), CancellationToken.None);
            CheckStatus = Describe(r);
            return r;
        }
        catch (Exception ex) { CheckStatus = "Check failed: " + ex.Message; AppLog.Error("Map check failed", ex); return null; }
        finally { IsBusy = false; }
    }

    private static string Describe(EvtxCheckResult r)
    {
        var sb = new StringBuilder(r.Note);
        foreach (var m in r.Mismatches.Take(8)) sb.Append($"\n#{m.RecordId} {m.Column}: preview \"{m.Preview}\", EvtxECmd \"{m.Tool}\"");
        if (r.Mismatches.Count > 8) sb.Append($"\n… {r.Mismatches.Count - 8} more.");
        foreach (var t in r.ToolMessages.Where(t => !t.StartsWith("Maps loaded", StringComparison.OrdinalIgnoreCase)).Take(3)) sb.Append("\nEvtxECmd: ").Append(t);
        if (r.Ran && r.ToolMessages.FirstOrDefault(t => t.StartsWith("Maps loaded", StringComparison.OrdinalIgnoreCase)) is { } loaded && loaded.EndsWith(": 0")) sb.Append("\nEvtxECmd did not load the map.");
        return sb.ToString();
    }

    /// <summary>Validates, checks with EvtxECmd, asks when the check disagrees, then saves the custom map. True when saved.</summary>
    public async Task<bool> SaveAsync(System.Windows.Window owner)
    {
        var map = BuildMap();
        if (map.Validate() is { Count: > 0 } p)
        {
            Dialogs.Warn("Map not saved", "Fix these first:\n" + string.Join("\n", p), owner: owner);
            return false;
        }
        var r = await RunCheckAsync();
        if (r == null) return false;
        if (!r.Ok && r.Ran)
        {
            if (!Dialogs.Confirm("Check did not pass", "EvtxECmd output differs from the preview. Save the map anyway?", "Save anyway", "Fix", Views.EzDialogSeverity.Warning, details: CheckStatus, defaultNo: true, owner: owner)) return false;
        }
        try
        {
            var path = _store.Save(map, "Custom map made in EZ Forensics. EvtxECmd uses it instead of the stock map for this event.");
            if (ToolMapsDir is { } dir) EvtxCustomMaps.SyncInto(_store.Folder, dir);
            AppLog.Info("Saved custom event map " + path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Map not saved", ex.Message, owner: owner);
            return false;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
