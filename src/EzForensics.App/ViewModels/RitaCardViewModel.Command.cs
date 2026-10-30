using EzForensics.App.Services;
using EzForensics.Core.Commands;
using EzForensics.Core.Rita;
using EzForensics.Core.Runtimes;

namespace EzForensics.App.ViewModels;

/// <summary>
/// RITA Overview, "Command": a folder of Zeek logs (e.g. an earlier Zeek run), the case (output) folder, the key lines of the pipeline
/// script as it will run (the <c>rita import</c> line takes the user's additional arguments) and Run, which queues one case-wide analysis.
/// </summary>
public sealed partial class RitaCardViewModel
{
    private SpecialCommandTileViewModel? _command;

    public SpecialCommandTileViewModel Command => _command ??= CreateCommand();

    private SpecialCommandTileViewModel CreateCommand()
    {
        var tile = new SpecialCommandTileViewModel("special:rita", "rita", RunCommandAsync,
            () => Installed ? null : "RITA is not set up yet: install it from the Tools table on the Overview tab.")
        {
            AutomationId = "ToolManager.Rita.Command",
            SourceLabel = "Zeek logs folder",
            SourcePlaceholder = "No folder selected",
            SourceHelp = "A folder with Zeek logs (conn, dns, http, ssl), e.g. the output of a Zeek run; every sub-folder is one capture.",
            AllowFolder = true,
            OutputHelp = "The case folder: the Rita_*.csv files go to its Network folder. Auto uses a new folder under the case root for every run.",
            Caption = "RITA runs as one pipeline script in the EZ Forensics Linux environment (WSL); its key lines:",
        };
        tile.SetRows(new[]
        {
            new SpecialCommandViewModel(ExtraArgsStore.RitaImport, null, Preview)
            {
                ArgsHelp = "Added to the rita import line; saved for the Tools page and Ingest.",
            },
        });
        return tile;
    }

    private static readonly string[] KeyLines = { "DB=", "OUT=", "\"$R/bin/clickhouse\" server", "\"$R/bin/rita\" import", "\"$R/bin/rita\" view" };

    /// <summary>The pipeline's key lines (database, output, ClickHouse, import, view) with the given import arguments.</summary>
    private string Preview(IReadOnlyList<string> extra)
    {
        var caseDir = Command.OutputDir;
        var db = RitaCommandBuilder.DatabaseName(caseDir, null);
        var work = RitaJobs.WorkDirFor(caseDir, null);
        var script = RitaCommandBuilder.PipelineScript(WslRuntime.ToWslPath(work) ?? work, db, RitaOptions.FromSettings(S.Settings) with { ExtraImportArgs = extra });
        var lines = script.Split('\n').Select(l => l.Trim()).Where(l => KeyLines.Any(k => l.StartsWith(k, StringComparison.Ordinal) || l.Contains(k, StringComparison.Ordinal)));
        return $"wsl.exe -d {EzDistro.Name} -u root -e sh -c <pipeline script>\n" + string.Join("\n", lines)
            + "\n# then ClickHouse enrichment queries (first / last seen, captures) and the Rita_*.csv files";
    }

    private async Task RunCommandAsync()
    {
        var install = S.Rita?.Selected;
        var tile = Command;
        if (install == null) { _onMessage("RITA is not set up yet. Install it from the Tools table on the Overview tab, then Run.", InfoSeverity.Warning); return; }
        var source = tile.Source;
        var caseDir = tile.OutputDir;
        try
        {
            var captures = await Task.Run(() => RitaJobs.CapturesIn(source));
            if (captures.Count == 0) { _onMessage($"No Zeek logs RITA can read (conn / dns / http / ssl) were found in {source}.", InfoSeverity.Warning); return; }
            Directory.CreateDirectory(caseDir);
            var bytes = captures.SelectMany(c => c.LogFiles).Where(f => RitaCommandBuilder.OriginalLogName(f) is "conn.log" or "open_conn.log")
                .Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
            var options = RitaOptions.FromSettings(S.Settings) with { ExtraImportArgs = tile.Rows[0].CurrentArgs };
            var spec = RitaJobs.CreateSpec(install, new RitaJobRequest { CaseDir = caseDir, Captures = captures, Options = options, InputBytesHint = bytes });
            S.Queue.SourcePath = source;
            S.Queue.Enqueue(spec, caseDir);
            S.LastCaseDir = caseDir;
            tile.RollAutoOutputDir();
            _onMessage($"Queued RITA on {captures.Count} capture(s). Follow it on the Jobs page; the Rita_*.csv files go to {RitaJobs.OutputDirFor(caseDir, null)}.", InfoSeverity.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            _onError("Could not start RITA", ex);
        }
    }
}
