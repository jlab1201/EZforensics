using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Memory;
using EzForensics.Core.Triage;

namespace EzForensics.App.ViewModels;

/// <summary>
/// Volatility Overview, "Command": the memory image, the case (output) folder, one command per ticked Ingest plugin as it will run (each
/// with its own saved additional arguments) and Run, which plans the image like Ingest does (<see cref="MemoryTriage.Plan"/>) and queues it.
/// </summary>
public sealed partial class VolatilityCardViewModel
{
    private SpecialCommandTileViewModel? _command;
    private readonly Dictionary<string, SpecialCommandViewModel> _commandRows = new(StringComparer.OrdinalIgnoreCase);

    public SpecialCommandTileViewModel Command => _command ??= CreateCommand();

    /// <summary>Stand-in for the previews before Volatility is set up.</summary>
    private static readonly VolatilityInstall PlaceholderInstall = new("vol", "", null, "", "<symbols>", "<cache>");

    private SpecialCommandTileViewModel CreateCommand()
    {
        var tile = new SpecialCommandTileViewModel("special:volatility", "memory", RunCommandAsync,
            () => Install == null ? "Volatility is not set up yet: install it from the Tools table on the Overview tab." : null)
        {
            AutomationId = "ToolManager.Volatility.Command",
            SourceLabel = "Memory image",
            SourcePlaceholder = "No image selected",
            SourceHelp = "A raw / .mem / .vmem image, a Windows crash dump (MEMORY.DMP), hiberfil.sys or a VMware / LiME image.",
            AllowFile = true,
            FileFilter = "Memory images|*.raw;*.mem;*.vmem;*.dmp;*.bin;*.lime;*.img;*.vmss;*.vmsn;hiberfil.sys|All files|*.*",
            OutputHelp = "The case folder: the Memory_*.csv files go to <case>\\<Host>\\Memory. Auto uses a new folder under the case folder for every run.",
            Caption = "One command per plugin ticked under Ingest plugins (plugins for another OS than the image's are skipped when it runs).",
        };
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Offline)) tile.Refresh(); };
        _command = tile;
        RefreshCommandRows();
        return tile;
    }

    /// <summary>One row per ticked plugin (rows keep their typed text while the selection changes).</summary>
    private void RefreshCommandRows()
    {
        if (_command == null) return;
        var rows = new List<SpecialCommandViewModel>();
        foreach (var p in AllPluginItems.Where(p => p.IsSelected || p.IsRequired))
        {
            if (!_commandRows.TryGetValue(p.Id, out var row))
            {
                var def = p.Def;
                row = new SpecialCommandViewModel(ExtraArgsStore.Volatility(def.Id), $"{def.Plugin}  ({def.Label})", a => Preview(def, a))
                {
                    ArgsHelp = "Plugin arguments, added after the plugin name; saved for the Tools page and Ingest.",
                };
                row.Saved += _ => S.RaiseMemoryPluginsChanged();   // Ingest plans again with the new arguments
                _commandRows[p.Id] = row;
            }
            rows.Add(row);
        }
        _command.SetRows(rows);
    }

    private string Preview(MemoryPluginDef plugin, IReadOnlyList<string> extra)
    {
        var image = Command.HasSource ? Command.Source : "<memory image>";
        return VolatilityCommandBuilder.ToDisplayString(VolatilityCommandBuilder.Build(Install ?? PlaceholderInstall, image, plugin, Offline,
            extra.Count > 0 ? ExtraArgsStore.Join(extra) : null));
    }

    private async Task RunCommandAsync()
    {
        var install = Install;
        var tile = Command;
        if (install == null) { _onMessage("Volatility is not set up yet. Install it from the Tools table on the Overview tab, then Run.", InfoSeverity.Warning); return; }
        var path = tile.Source;
        var caseDir = tile.OutputDir;
        var selected = tile.Rows.Select(r => r.Id[ExtraArgsStore.VolatilityPrefix.Length..]).ToList();
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in tile.Rows)
            if (r.CurrentText is { Length: > 0 } t) extra[r.Id[ExtraArgsStore.VolatilityPrefix.Length..]] = t;
        bool offline = Offline;
        try
        {
            var notes = new List<string>();
            var plan = await Task.Run(() =>
            {
                var image = MemoryImageDetector.Detect(path);
                return image == null ? null : MemoryTriage.Plan(new[] { image }, caseDir, install, selectedPluginIds: selected, offline: offline,
                    notes: notes, pluginExtraArgs: extra);
            });
            if (plan == null) { _onMessage($"{path} is not a memory image Volatility can read (raw, crash dump, hibernation file, VMware or LiME).", InfoSeverity.Warning); return; }
            if (plan.Count == 0) { _onMessage("No plugin applies to this image" + (notes.Count > 0 ? ": " + string.Join(" ", notes) : "."), InfoSeverity.Warning); return; }
            Directory.CreateDirectory(caseDir);
            S.Queue.SourcePath = path;
            foreach (var p in plan) S.Queue.Enqueue(p.Spec, caseDir);
            S.LastCaseDir = caseDir;
            tile.RollAutoOutputDir();
            _onMessage($"Queued {plan.Count} Volatility job(s) on {Path.GetFileName(path)}. Follow them in the Jobs list on Ingest; the CSVs go to {caseDir}."
                       + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""), InfoSeverity.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _onError("Could not start Volatility", ex);
        }
    }
}
