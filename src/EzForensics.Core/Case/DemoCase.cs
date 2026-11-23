using System.Globalization;
using EzForensics.Core.Processes;

namespace EzForensics.Core.Case;

/// <summary>What <see cref="DemoCase.CreateAsync"/> made: the new case folder and anything that did not work (the case opens anyway).</summary>
public sealed record DemoCaseResult(string CaseDir, IReadOnlyList<string> Warnings);

/// <summary>
/// "Load demo case": copies the synthetic case shipped next to the exe (<c>demo-case\</c>, from <c>samples\demo-case</c>) to a new folder
/// <c>&lt;case root&gt;\Demo-yyyyMMdd_HHmmss</c> and builds each host's process tree data, as Ingest does at the end of a run. Its findings and key
/// events are in <c>.ezforensics\analysis.json</c>, so the Review page shows them without Spectre or a local AI.
/// </summary>
public static class DemoCase
{
    public const string FolderName = "demo-case";
    public const string CasePrefix = "Demo-";

    /// <summary>The demo shipped with the app (<c>&lt;exe folder&gt;\demo-case</c>).</summary>
    public static string DefaultSource => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>The demo folder exists and has its case marker.</summary>
    public static bool IsAvailable(string? source = null) => File.Exists(CaseLayout.MarkerPath(source ?? DefaultSource));

    /// <summary>A folder name under <paramref name="caseRoot"/> that does not exist yet: <c>Demo-yyyyMMdd_HHmmss</c>, then <c>-2</c>, <c>-3</c>...</summary>
    public static string NewCaseFolder(string caseRoot, DateTime now)
    {
        var name = CasePrefix + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(caseRoot, name);
        for (int i = 2; Directory.Exists(dir) || File.Exists(dir); i++) dir = Path.Combine(caseRoot, $"{name}-{i}");
        return dir;
    }

    /// <summary>Copies the demo into a new case folder under <paramref name="caseRoot"/> and builds the process tree data of its hosts.</summary>
    public static async Task<DemoCaseResult> CreateAsync(string caseRoot, string? source = null, DateTime? now = null, CancellationToken ct = default)
    {
        source ??= DefaultSource;
        if (!IsAvailable(source)) throw new DirectoryNotFoundException($"The demo case is missing: {source}");
        var dir = NewCaseFolder(caseRoot, now ?? DateTime.Now);
        await Task.Run(() => Copy(source, dir, ct), ct).ConfigureAwait(false);

        var warnings = new List<string>();
        foreach (var host in Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')).Where(ProcessCorrelator.HasProcessSource))
        {
            var o = await ProcessCorrelator.RunAsync(host, force: true, ct: ct).ConfigureAwait(false);
            if (o.State != ProcessCorrelationState.Built) warnings.Add(ProcessCorrelator.Describe(o));
        }
        return new DemoCaseResult(dir, warnings);
    }

    private static void Copy(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(to, Path.GetFileName(f));
            File.Copy(f, dest);
            File.SetAttributes(dest, FileAttributes.Normal);   // the installed copy may be read-only; the case must be writable
        }
        foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)), ct);
    }
}
