using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Plaso;

namespace EzForensics.App.ViewModels;

/// <summary>
/// plaso Overview, "Command": evidence folder or image, output (case) folder, the log2timeline and psort commands as they will run (Timeline
/// options + the user's additional arguments) and Run, which builds one super timeline through <see cref="PlasoTimelineRunner"/>.
/// </summary>
public sealed partial class ToolManagerViewModel
{
    private SpecialCommandTileViewModel? _plasoCommand;

    public SpecialCommandTileViewModel PlasoCommand => _plasoCommand ??= CreatePlasoCommand();

    /// <summary>Stand-in for the previews before plaso is set up.</summary>
    private static readonly PlasoInstall PlaceholderPlaso = new(PlasoOrigin.Path, PlasoEngineKind.Executable,
        Core.Plaso.PlasoCommand.Of("log2timeline"), Core.Plaso.PlasoCommand.Of("psort"), null, "");

    private SpecialCommandTileViewModel CreatePlasoCommand()
    {
        var tile = new SpecialCommandTileViewModel("special:plaso", "Timeline", RunPlasoCommandAsync,
            () => Plaso == null ? "plaso is not set up yet: install it from the Tools table on the Overview tab." : null)
        {
            AutomationId = "ToolManager.Plaso.Command",
            SourceLabel = "Evidence folder or image",
            SourcePlaceholder = "No evidence selected",
            SourceHelp = "A KAPE target output, mounted drive or extracted triage folder, or a disk image (E01, raw / dd, VHD(X), VMDK).",
            AllowFile = true, AllowFolder = true,
            FileFilter = "Disk images|*.e01;*.ex01;*.raw;*.dd;*.img;*.001;*.vhd;*.vhdx;*.vmdk|All files|*.*",
            OutputHelp = "Auto uses a new folder under the case folder for every run; the timeline goes to its Timeline folder.",
            RunText = "Build timeline",
            Caption = "Run builds one super timeline: log2timeline, then psort. Timeline options above apply to both.",
        };
        tile.SetRows(new[]
        {
            new SpecialCommandViewModel(ExtraArgsStore.PlasoLog2Timeline, "log2timeline", a => PlasoPreview(tile, a, null)),
            new SpecialCommandViewModel(ExtraArgsStore.PlasoPsort, "psort", a => PlasoPreview(tile, null, a)),
        });
        S.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } n && n.StartsWith("Plaso", StringComparison.Ordinal)) AppServices.OnUi(tile.Refresh);
        };
        return tile;
    }

    /// <summary>The options of a run from the tile (Timeline options applied; sources as typed, "a; b" for several).</summary>
    private static PlasoRunOptions PlasoCommandOptions(SpecialCommandTileViewModel tile, string? stamp)
    {
        var sources = tile.HasSource ? PlasoRunOptions.ParseSources(tile.Source) : Array.Empty<string>();
        var o = new PlasoRunOptions
        {
            Source = sources.Count > 0 ? sources[0] : "<evidence>",
            AdditionalSources = sources.Skip(1).ToList(),
            CaseDir = tile.OutputDir,
        };
        if (stamp != null) o.Stamp = stamp;
        PlasoOptionsViewModel.Apply(o);
        o.ExtraLog2TimelineArgs = tile.Rows.FirstOrDefault(r => r.Id == ExtraArgsStore.PlasoLog2Timeline)?.CurrentArgs ?? Array.Empty<string>();
        o.ExtraPsortArgs = tile.Rows.FirstOrDefault(r => r.Id == ExtraArgsStore.PlasoPsort)?.CurrentArgs ?? Array.Empty<string>();
        return o;
    }

    /// <summary>log2timeline (<paramref name="l2t"/> set) or psort (<paramref name="psort"/> set) as it will run.</summary>
    private string PlasoPreview(SpecialCommandTileViewModel tile, IReadOnlyList<string>? l2t, IReadOnlyList<string>? psort)
    {
        var o = PlasoCommandOptions(tile, "<stamp>");
        var install = Plaso ?? PlaceholderPlaso;
        if (l2t != null)
        {
            o.ExtraLog2TimelineArgs = l2t;
            var text = PlasoCommandBuilder.BuildLog2Timeline(install, o).Display;
            return o.AdditionalSources.Count > 0 ? text + $"\n(and one more log2timeline run per further source: {o.AdditionalSources.Count})" : text;
        }
        o.ExtraPsortArgs = psort ?? Array.Empty<string>();
        return PlasoCommandBuilder.BuildPsort(install, o).Display;
    }

    private async Task RunPlasoCommandAsync()
    {
        var install = Plaso;
        var tile = PlasoCommand;
        if (install == null) { ShowMessage("plaso is not set up yet. Install it from the Tools table on the Overview tab, then Run.", InfoSeverity.Warning); return; }
        var o = PlasoCommandOptions(tile, null);
        var missing = o.AllSources.FirstOrDefault(p => !Directory.Exists(p) && !File.Exists(p));
        if (missing != null) { ShowMessage($"The plaso source does not exist: {missing}", InfoSeverity.Warning); return; }
        try
        {
            Directory.CreateDirectory(o.CaseDir);
            S.Queue.SourcePath = o.Source;
            S.LastCaseDir = o.CaseDir;
            tile.RollAutoOutputDir();
            ShowMessage($"Queued the super timeline of {o.Source}. Follow it in the Jobs list on Ingest; the CSV goes to {o.TimelineDir}.", InfoSeverity.Info);
            var r = await PlasoTimelineRunner.RunAsync(S.Queue, install, o);
            if (r.Succeeded) ShowMessage($"Super timeline built: {r.Events:N0} event(s) in {r.OutputCsv}. Open it on the Analyze page.", InfoSeverity.Success);
            else if (r.Cancelled) ShowMessage("The super timeline was cancelled.", InfoSeverity.Warning);
            else ShowMessage($"The super timeline failed: {r.Error}. See the Jobs list on Ingest for the log.", InfoSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ShowError("Could not start the super timeline", ex);
        }
    }
}
