using CommunityToolkit.Mvvm.ComponentModel;
using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Plaso;
using EzForensics.Core.Settings;

namespace EzForensics.App.ViewModels;

/// <summary>A choice of a combo box: the value and its label.</summary>
public sealed record ChoiceItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The plaso options (parser preset, output format, workers, UTC, VSS, unattended, date range) as a view over <see cref="AppSettings"/>:
/// edits are saved at once, and every instance sees the same values; the Ingest super timeline row builds with them.</summary>
public sealed class PlasoOptionsViewModel : ObservableObject
{
    private static AppServices S => AppServices.Current;

    public PlasoOptionsViewModel()
    {
        S.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } n && n.StartsWith("Plaso", StringComparison.Ordinal))
            {
                AppServices.OnUi(() =>
                {
                    OnPropertyChanged(n);
                    if (n == nameof(AppSettings.PlasoFormat)) OnPropertyChanged(nameof(Format));
                    if (n == nameof(AppSettings.PlasoFromDate)) OnPropertyChanged(nameof(FromDate));
                    if (n == nameof(AppSettings.PlasoToDate)) OnPropertyChanged(nameof(ToDate));
                    if (n is nameof(AppSettings.PlasoParsers) or nameof(AppSettings.PlasoFormat)) OnPropertyChanged(nameof(OptionsHeader));
                });
            }
        };
    }

    public IReadOnlyList<string> ParserPresets { get; } = new[] { "", "win7", "win_gen", "win7_slow", "winxp", "webhist", "linux", "macos", "android", "ios" };

    public IReadOnlyList<ChoiceItem<PlasoOutputFormat>> Formats { get; } = new[]
    {
        new ChoiceItem<PlasoOutputFormat>(PlasoOutputFormat.Dynamic, "dynamic (ISO datetime + MACB)"),
        new ChoiceItem<PlasoOutputFormat>(PlasoOutputFormat.L2tCsv, "l2tcsv (legacy 17 columns)"),
    };

    public string PlasoParsers { get => S.Settings.PlasoParsers; set => Edit(() => S.Settings.PlasoParsers = value); }
    public string PlasoWorkers { get => S.Settings.PlasoWorkers; set => Edit(() => S.Settings.PlasoWorkers = value); }
    public bool PlasoTimezoneUtc { get => S.Settings.PlasoTimezoneUtc; set => Edit(() => S.Settings.PlasoTimezoneUtc = value); }
    public bool PlasoVss { get => S.Settings.PlasoVss; set => Edit(() => S.Settings.PlasoVss = value); }
    public bool PlasoUnattended { get => S.Settings.PlasoUnattended; set => Edit(() => S.Settings.PlasoUnattended = value); }
    public DateTime? FromDate { get => S.Settings.PlasoFromDate; set => Edit(() => S.Settings.PlasoFromDate = value); }
    public DateTime? ToDate { get => S.Settings.PlasoToDate; set => Edit(() => S.Settings.PlasoToDate = value); }

    public ChoiceItem<PlasoOutputFormat> Format
    {
        get => Formats.First(f => f.Value == S.Settings.PlasoFormat);
        set { if (value != null) Edit(() => S.Settings.PlasoFormat = value.Value); }
    }

    public string OptionsHeader =>
        "plaso options" + (string.IsNullOrWhiteSpace(PlasoParsers) ? "  ·  parsers: auto" : $"  ·  parsers: {PlasoParsers}") + $"  ·  {Format.Label}";

    /// <summary>
    /// The settings as psort / log2timeline options, the additional arguments saved on the Tools page included (the evidence and case
    /// folder are set by the caller).
    /// </summary>
    public static PlasoRunOptions Apply(PlasoRunOptions o)
    {
        var st = S.Settings;
        o.Parsers = string.IsNullOrWhiteSpace(st.PlasoParsers) ? null : st.PlasoParsers.Trim();
        o.TimezoneUtc = st.PlasoTimezoneUtc;
        o.Workers = int.TryParse(st.PlasoWorkers?.Trim(), out var w) && w > 0 ? w : null;
        o.Vss = st.PlasoVss ? PlasoVssMode.All : PlasoVssMode.None;
        o.Unattended = st.PlasoUnattended;
        o.Format = st.PlasoFormat;
        o.FromUtc = st.PlasoFromDate is { } f ? DateTime.SpecifyKind(f.Date, DateTimeKind.Utc) : null;
        o.ToUtc = st.PlasoToDate is { } t ? DateTime.SpecifyKind(t.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc) : null;
        o.ExtraLog2TimelineArgs = ExtraArgsStore.Args(st, ExtraArgsStore.PlasoLog2Timeline);
        o.ExtraPsortArgs = ExtraArgsStore.Args(st, ExtraArgsStore.PlasoPsort);
        return o;
    }

    private static void Edit(Action set)
    {
        set();
        try { S.SaveSettings(); } catch (Exception ex) { AppLog.Error("Could not save settings", ex); }
    }
}
