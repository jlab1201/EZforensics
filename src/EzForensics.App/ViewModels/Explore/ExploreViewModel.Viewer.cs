using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EzForensics.Core.Explore;

namespace EzForensics.App.ViewModels.Explore;

public sealed record MetaRow(string Label, string Value, bool Mono = false);
public sealed record MetaSection(string Title, IReadOnlyList<MetaRow> Rows, bool Expanded = true);
public sealed record PeSectionRow(string Name, string VirtualAddress, string VirtualSize, string RawSize, double Entropy, string EntropyText, string Flags, bool WriteExecute, bool HighEntropy);

public sealed record PeImportGroup(string Dll, IReadOnlyList<string> Functions)
{
    public string Header => $"{Dll}  ({Functions.Count})";
}

public sealed record PeExportRow(string Ordinal, string Name, string Address, string Forward);

public sealed partial class ExploreViewModel
{
    public const int TabOverview = 0, TabPreview = 1, TabHex = 2, TabStrings = 3, TabMetadata = 4, TabPe = 5;
    public const long PreviewImageMaxBytes = 25L * 1024 * 1024;
    public const int PreviewTextMaxBytes = 256 * 1024;

    private CancellationTokenSource? _viewerCts;
    private FileFacts? _facts;
    private DieResult? _die;
    private string? _metadataFor, _stringsFor;
    private int _stringsMinLoaded;
    private CancellationTokenSource? _stringsCts;
    private IReadOnlyList<ExtractedString> _allStrings = Array.Empty<ExtractedString>();

    /// <summary>Rows selected in the list (kept by the view); the context-menu commands act on them.</summary>
    public IReadOnlyList<FileRow> SelectedRows { get; set; } = Array.Empty<FileRow>();

    [ObservableProperty] private FileRow? _selectedRow;
    [ObservableProperty] private bool _hasFile;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _viewerBusy;
    [ObservableProperty] private double _factsProgress;
    [ObservableProperty] private string? _viewerError;

    // Overview
    [ObservableProperty] private string _fileTitle = "";
    [ObservableProperty] private string _typeText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _modifiedText = "";
    [ObservableProperty] private string _accessedText = "";
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private string _md5 = "";
    [ObservableProperty] private string _sha1 = "";
    [ObservableProperty] private string _sha256 = "";
    [ObservableProperty] private double _entropyFraction;
    [ObservableProperty] private string _entropyText = "";
    [ObservableProperty] private string _signerText = "";
    [ObservableProperty] private string _dieText = "";
    [ObservableProperty] private string? _dieNote;
    public ObservableCollection<VerdictChip> Chips { get; } = new();
    public ObservableCollection<DieDetection> DieDetections { get; } = new();

    // Hex: the tab opens its own read-only stream on this path
    [ObservableProperty] private string? _hexPath;

    // Preview
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string? _previewText;
    [ObservableProperty] private bool _hasPreview;

    // Strings
    public IReadOnlyList<int> StringsMinLengths { get; } = new[] { 4, 5, 6, 8 };
    [ObservableProperty] private int _stringsMinLength = 5;
    [ObservableProperty] private string _stringsFilter = "";
    [ObservableProperty] private IReadOnlyList<ExtractedString> _filteredStrings = Array.Empty<ExtractedString>();
    [ObservableProperty] private string _stringsStatus = "";
    [ObservableProperty] private bool _stringsBusy;
    [ObservableProperty] private ExtractedString? _selectedString;

    // Metadata
    [ObservableProperty] private IReadOnlyList<ExifGroup> _exifGroups = Array.Empty<ExifGroup>();
    [ObservableProperty] private string? _exifNote;
    [ObservableProperty] private bool _metadataBusy;
    [ObservableProperty] private string _attributesText = "";
    [ObservableProperty] private IReadOnlyList<AdsStream> _streams = Array.Empty<AdsStream>();
    [ObservableProperty] private bool _hasZone;
    [ObservableProperty] private string _zoneText = "";
    [ObservableProperty] private string _zoneHost = "";
    [ObservableProperty] private string _zoneReferrer = "";

    // Metadata tab: summary strip short hashes + sections built from what the VM already holds (empty values dropped)
    [ObservableProperty] private string _md5Short = "";
    [ObservableProperty] private string _sha1Short = "";
    [ObservableProperty] private string _sha256Short = "";
    [ObservableProperty] private IReadOnlyList<MetaSection> _metaSections = Array.Empty<MetaSection>();

    // PE
    [ObservableProperty] private bool _isPeTab;
    [ObservableProperty] private IReadOnlyList<InspectorValue> _peHeader = Array.Empty<InspectorValue>();
    [ObservableProperty] private IReadOnlyList<PeSectionRow> _peSections = Array.Empty<PeSectionRow>();
    [ObservableProperty] private IReadOnlyList<PeImportGroup> _peImports = Array.Empty<PeImportGroup>();
    [ObservableProperty] private IReadOnlyList<PeExportRow> _peExports = Array.Empty<PeExportRow>();
    [ObservableProperty] private string _peWarnings = "";

    public bool DieInstalled => Tools.IsInstalled(ExploreTool.Die);
    public bool ExifInstalled => Tools.IsInstalled(ExploreTool.ExifTool);

    partial void OnSelectedRowChanged(FileRow? value) => StartViewer(value);

    partial void OnSelectedTabChanged(int value) => _ = EnsureTabLoadedAsync();

    partial void OnStringsMinLengthChanged(int value) { if (SelectedTab == TabStrings) _ = EnsureTabLoadedAsync(); }

    partial void OnStringsFilterChanged(string value) => ApplyStringsFilter();
    partial void OnDieTextChanged(string value) => RebuildMeta();
    partial void OnPeHeaderChanged(IReadOnlyList<InspectorValue> value) => RebuildMeta();
    partial void OnExifGroupsChanged(IReadOnlyList<ExifGroup> value) => RebuildMeta();

    private static string ShortHash(string? h) => string.IsNullOrEmpty(h) ? "" : h.Length <= 16 ? h : h[..8] + "…" + h[^6..];

    private void RebuildMeta()
    {
        var list = new List<MetaSection>();
        void Add(string title, bool expanded, params MetaRow[] rows)
        {
            var kept = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
            if (kept.Count > 0) list.Add(new MetaSection(title, kept, expanded));
        }
        var path = _facts?.Entry.FullPath ?? "";
        Add("File", true,
            new("Path", path, true), new("Type", TypeText), new("Size", SizeText),
            new("Modified", ModifiedText), new("Accessed", AccessedText), new("Created", CreatedText),
            new("Attributes", AttributesText),
            new("Zone", ZoneText), new("HostUrl", ZoneHost, true), new("ReferrerUrl", ZoneReferrer, true));
        if (Streams.Count > 0)
            Add("Alternate data streams", true, Streams.Select(a => new MetaRow(":" + a.Name, $"{a.Size:N0} bytes", true)).ToArray());
        Add("Hashes", true, new("MD5", Md5, true), new("SHA-1", Sha1, true), new("SHA-256", Sha256, true));
        Add("Type / DIE", true, new("Entropy", EntropyText), new("Signature", SignerText),
            new("Detect It Easy", DieText == "Detecting..." ? "" : DieText));
        if (PeHeader.Count > 0)
            Add("PE", false, PeHeader.Select(v => new MetaRow(v.Label, v.Value, v.Label is "Image base" or "Entry point" or "Imphash")).ToArray());
        foreach (var g in ExifGroups)
            Add("EXIF / " + g.Name, false, g.Tags.Select(t => new MetaRow(t.Name, t.Value?.ToString() ?? "")).ToArray());
        MetaSections = list;
    }

    private void ClearViewer()
    {
        _viewerCts?.Cancel();
        _stringsCts?.Cancel();
        SelectedRow = null;
        StartViewer(null);
    }

    private void ResetViewerState()
    {
        _facts = null; _die = null; _metadataFor = null; _stringsFor = null; _allStrings = Array.Empty<ExtractedString>();
        ViewerError = null; FactsProgress = 0;
        TypeText = SizeText = ModifiedText = AccessedText = CreatedText = Md5 = Sha1 = Sha256 = EntropyText = SignerText = DieText = "";
        EntropyFraction = 0; DieNote = null;
        Chips.Clear(); DieDetections.Clear();
        HexPath = null;
        PreviewImage = null; PreviewText = null; HasPreview = false;
        FilteredStrings = Array.Empty<ExtractedString>(); StringsStatus = ""; SelectedString = null;
        ExifGroups = Array.Empty<ExifGroup>(); ExifNote = null; AttributesText = ""; Streams = Array.Empty<AdsStream>();
        HasZone = false; ZoneText = ZoneHost = ZoneReferrer = "";
        Md5Short = Sha1Short = Sha256Short = ""; StringsFilter = "";
        IsPeTab = false; PeHeader = Array.Empty<InspectorValue>(); PeSections = Array.Empty<PeSectionRow>();
        PeImports = Array.Empty<PeImportGroup>(); PeExports = Array.Empty<PeExportRow>(); PeWarnings = "";
    }

    private void StartViewer(FileRow? row)
    {
        _viewerCts?.Cancel();
        _stringsCts?.Cancel();
        var cts = _viewerCts = new CancellationTokenSource();
        _viewerRow = row;
        ResetViewerState();
        if (row == null || row.IsDirectory) { HasFile = false; FileTitle = row?.Name ?? ""; ViewerBusy = false; SelectedTab = TabOverview; return; }
        HasFile = true;
        FileTitle = row.Name;
        HexPath = row.FullPath;
        SizeText = row.SizeText;
        _ = RunViewerAsync(row, cts);
    }

    private async Task RunViewerAsync(FileRow row, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        ViewerBusy = true;
        try
        {
            var progress = new Progress<double>(p => { if (!ct.IsCancellationRequested) FactsProgress = p; });
            var facts = await GetFactsAsync(row.Entry, ct, progress, background: false);
            ct.ThrowIfCancellationRequested();
            row.Apply(facts);
            _facts = facts;
            ApplyFacts(facts);
            await Task.WhenAll(LoadPeAsync(facts, ct), LoadDieAsync(facts, ct), LoadPreviewAsync(facts, ct));
            ct.ThrowIfCancellationRequested();
            if (SelectedTab == TabPe && !IsPeTab || SelectedTab == TabPreview && !HasPreview) SelectedTab = TabOverview;
            await EnsureTabLoadedAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested) { ViewerError = ex.Message; }
        finally { if (_viewerCts == cts) ViewerBusy = false; }
    }

    // ------------------------------------------------------------------------------ overview

    private void ApplyFacts(FileFacts f)
    {
        var exp = f.Type.ExpectedExtensions.FirstOrDefault(e => !string.IsNullOrEmpty(e));
        TypeText = f.Type.Description + (exp != null ? $"  (expected .{exp.TrimStart('.')})" : "");
        SizeText = $"{Fmt.Size(f.Hashes.Length)}  ({f.Hashes.Length:N0} bytes)";
        ModifiedText = Fmt.Utc(f.Entry.ModifiedUtc);
        AccessedText = Fmt.Utc(f.Entry.AccessedUtc);
        CreatedText = Fmt.Utc(f.Entry.CreatedUtc);
        Md5 = f.Hashes.Md5; Sha1 = f.Hashes.Sha1; Sha256 = f.Hashes.Sha256;
        EntropyFraction = Math.Clamp(f.Entropy / 8.0, 0, 1);
        EntropyText = $"{f.Entropy:0.00} / 8" + (f.Entropy >= 7.2 ? "  (compressed or encrypted)" : f.Entropy < 1 ? "  (mostly constant)" : "");
        SignerText = !f.IsPe ? "Not a PE file"
            : f.Signature == null ? "Not checked"
            : f.Signature.Valid ? "Signed: " + (f.Signature.Signer ?? "unknown signer") + (f.Signature.IsMicrosoft ? " (Microsoft)" : "")
            : "Unsigned or invalid  (" + f.Signature.Detail + ")";
        AttributesText = f.Entry.Attributes.ToString().Replace(", ", "  ");
        Streams = f.Streams;
        HasZone = f.Zone != null;
        if (f.Zone != null)
        {
            ZoneText = $"{f.Zone.ZoneName} (zone {f.Zone.ZoneId?.ToString() ?? "?"})";
            ZoneHost = f.Zone.HostUrl ?? "";
            ZoneReferrer = f.Zone.ReferrerUrl ?? "";
        }
        Md5Short = ShortHash(Md5); Sha1Short = ShortHash(Sha1); Sha256Short = ShortHash(Sha256);
        RefreshChips();
        RebuildMeta();
    }

    private void RefreshChips()
    {
        Chips.Clear();
        if (_facts == null) return;
        foreach (var c in VerdictChips.From(_facts, _die)) Chips.Add(c);
    }

    private async Task LoadDieAsync(FileFacts f, CancellationToken ct)
    {
        if (!DieInstalled) { DieNote = "Detect It Easy is not installed. Install it in Tools to see packers and compilers."; return; }
        if (_cache == null || f.Hashes.Length == 0 || f.Type.Id == "text") { DieText = "n/a"; return; }
        DieText = "Detecting...";
        try
        {
            var (output, result) = await new DieRunner(Tools).GetAsync(_cache, f.Entry.FullPath, f.Hashes.Sha256, false, ct);
            ct.ThrowIfCancellationRequested();
            if (result == null) { DieText = "n/a"; DieNote = "Detect It Easy: " + (output.Error ?? "no result"); return; }
            _die = result;
            DieText = result.Verdict ?? "No detections";
            DieDetections.Clear();
            foreach (var d in result.Known) DieDetections.Add(d);
            RefreshChips();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { DieText = "n/a"; DieNote = "Detect It Easy failed: " + ex.Message; }
    }

    // ------------------------------------------------------------------------------ PE

    private async Task LoadPeAsync(FileFacts f, CancellationToken ct)
    {
        if (!f.IsPe) return;
        PeInfo? pe;
        try { pe = await Task.Run(() => PeInfo.Read(f.Entry.FullPath), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return; }
        ct.ThrowIfCancellationRequested();
        if (pe == null) return;
        var header = new List<InspectorValue>
        {
            new("Machine", pe.Machine + (pe.Is64Bit ? " (64-bit)" : " (32-bit)")),
            new("Kind", (pe.IsDriver ? "Driver" : pe.IsDll ? "DLL" : "Executable") + (pe.IsDotNet ? ", .NET" : "")),
            new("Subsystem", pe.Subsystem),
            new("Compile time", pe.TimestampUtc is { } t ? Fmt.Utc(t) : "0 (not set)"),
            new("Image base", "0x" + pe.ImageBase.ToString("X")),
            new("Entry point", "0x" + pe.EntryPoint.ToString("X")),
            new("Imphash", pe.ImpHash ?? "(none)"),
            new("Overlay", pe.OverlayOffset is { } o ? $"{Fmt.Size(pe.OverlaySize)} at 0x{o:X}" : "none"),
            new("Certificate", pe.CertificateSize > 0 ? Fmt.Size(pe.CertificateSize) : "none"),
        };
        var warn = new List<string>();
        if (pe.HasWriteExecuteSection) warn.Add("A section is both writable and executable.");
        if (pe.TimestampZero) warn.Add("Compile time is zero.");
        if (pe.TimestampInFuture) warn.Add("Compile time is in the future.");
        if (pe.OverlaySize > 0) warn.Add("Data after the last section (overlay).");
        if (pe.ImportedFunctionCount < 5 && !pe.IsDotNet) warn.Add("Very small import table.");
        PeHeader = header;
        PeWarnings = string.Join("  ", warn);
        PeSections = pe.Sections.Select(s => new PeSectionRow(s.Name, "0x" + s.VirtualAddress.ToString("X"), "0x" + s.VirtualSize.ToString("X"),
            "0x" + s.RawSize.ToString("X"), s.Entropy, s.Entropy.ToString("0.00"),
            (s.Executable ? "X" : "-") + (s.Writable ? "W" : "-"), s.WriteExecute, s.Entropy >= 7.2)).ToList();
        PeImports = pe.Imports.OrderBy(i => i.Dll, StringComparer.OrdinalIgnoreCase).Select(i => new PeImportGroup(i.Dll, i.Functions)).ToList();
        PeExports = pe.Exports.Select(e => new PeExportRow(e.Ordinal.ToString(), e.Name ?? "", "0x" + e.Address.ToString("X"), e.ForwardName ?? "")).ToList();
        IsPeTab = true;
    }

    // ------------------------------------------------------------------------------ preview (images and plain text only)

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff" };

    private async Task LoadPreviewAsync(FileFacts f, CancellationToken ct)
    {
        bool image = f.Type.Id is "png" or "jpeg" or "gif" || ImageExts.Contains(f.Extension) && f.Type.Id is "unknown";
        bool text = f.Type.Id == "text";
        if (!image && !text) return;
        try
        {
            if (image)
            {
                if (f.Hashes.Length > PreviewImageMaxBytes) return;
                var img = await Task.Run(() => DecodeImage(f.Entry.FullPath), ct);
                ct.ThrowIfCancellationRequested();
                if (img == null) return;
                PreviewImage = img;
                HasPreview = true;
            }
            else
            {
                var t = await Task.Run(() => ReadText(f.Entry.FullPath), ct);
                ct.ThrowIfCancellationRequested();
                PreviewText = t;
                HasPreview = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* no preview */ }
    }

    private static BitmapSource? DecodeImage(string path)
    {
        try
        {
            using var fs = FileFactsReader.OpenEvidence(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
            if ((long)w * h > 80_000_000) return null;
            fs.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (w > 2400) bi.DecodePixelWidth = 2400;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException) { return null; }
    }

    private static string ReadText(string path)
    {
        using var fs = FileFactsReader.OpenEvidence(path);
        var buf = new byte[PreviewTextMaxBytes];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        string s;
        if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) s = Encoding.Unicode.GetString(buf, 2, n - 2);
        else if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(buf, 2, n - 2);
        else s = new UTF8Encoding(false).GetString(buf, 0, n);
        return fs.Length > n ? s + $"\n\n[... first {Fmt.Size(n)} of {Fmt.Size(fs.Length)} shown]" : s;
    }

    // ------------------------------------------------------------------------------ lazy tabs

    private async Task EnsureTabLoadedAsync()
    {
        var f = _facts;
        var cts = _viewerCts;
        if (f == null || cts == null) return;
        var ct = cts.Token;
        try
        {
            if (SelectedTab == TabStrings) await LoadStringsAsync(f, ct);
            else if (SelectedTab == TabMetadata) await LoadMetadataAsync(f, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested) { ViewerError = ex.Message; }
    }

    private async Task LoadStringsAsync(FileFacts f, CancellationToken ct)
    {
        int min = StringsMinLength;
        if (_stringsFor == f.Entry.FullPath && _stringsMinLoaded == min) return;
        _stringsCts?.Cancel();
        var cts = _stringsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stringsFor = f.Entry.FullPath; _stringsMinLoaded = min;
        StringsBusy = true;
        StringsStatus = "Scanning...";
        try
        {
            var path = f.Entry.FullPath;
            var res = await Task.Run(() =>
            {
                using var fs = FileFactsReader.OpenEvidence(path);
                return StringsExtractor.Extract(fs, min, StringsExtractor.DefaultMaxCount, StringsExtractor.DefaultMaxBytes, cts.Token);
            }, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _allStrings = res.Items;
            ApplyStringsFilter(res.Truncated ? $"  (capped at {StringsExtractor.DefaultMaxCount:N0})" : res.BytesScanned < f.Hashes.Length ? $"  (first {Fmt.Size(res.BytesScanned)} scanned)" : "");
        }
        catch (OperationCanceledException) { _stringsFor = null; throw; }
        finally { if (_stringsCts == cts) StringsBusy = false; }
    }

    private const int StringsDisplayCap = 20_000;
    private string _stringsNote = "";

    private void ApplyStringsFilter(string? note = null)
    {
        if (note != null) _stringsNote = note;
        var q = StringsFilter?.Trim();
        IEnumerable<ExtractedString> src = _allStrings;
        if (!string.IsNullOrEmpty(q)) src = src.Where(s => s.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        var matches = src.ToList();
        var list = matches.Count > StringsDisplayCap ? matches.GetRange(0, StringsDisplayCap) : matches;
        FilteredStrings = list;
        StringsStatus = _allStrings.Count == 0 && _stringsFor == null ? "" :
            matches.Count > list.Count
                ? $"{list.Count:N0} shown of {matches.Count:N0} matching (display limit) · {_allStrings.Count:N0} total{_stringsNote}"
                : $"{list.Count:N0} of {_allStrings.Count:N0} strings{_stringsNote}";
    }

    private async Task LoadMetadataAsync(FileFacts f, CancellationToken ct)
    {
        if (_metadataFor == f.Entry.FullPath) return;
        _metadataFor = f.Entry.FullPath;
        if (!ExifInstalled || _cache == null)
        {
            ExifNote = "ExifTool is not installed. Install it in Tools to see file metadata.";
            return;
        }
        MetadataBusy = true;
        ExifNote = null;
        try
        {
            var (output, result) = await new ExifRunner(Tools).GetAsync(_cache, f.Entry.FullPath, f.Hashes.Sha256, false, ct);
            ct.ThrowIfCancellationRequested();
            if (result == null) { ExifNote = "ExifTool: " + (output.Error ?? "no metadata"); ExifGroups = Array.Empty<ExifGroup>(); }
            else ExifGroups = result.Groups.Where(g => g.Tags.Count > 0).ToList();
        }
        catch (OperationCanceledException) { _metadataFor = null; throw; }
        finally { MetadataBusy = false; }
    }

    /// <summary>Re-run ExifTool/DIE ignoring the cache for the open file.</summary>
    [RelayCommand]
    private void Rescan()
    {
        if (_facts is not { } f || _cache == null) return;
        _cache.Invalidate(f.Hashes.Sha256);
        var row = _viewerRow ?? SelectedRow;
        if (row != null) { _factsCache.Remove(KeyOf(row.Entry), out _); row.State = FactsState.None; StartViewer(row); }
    }
}
