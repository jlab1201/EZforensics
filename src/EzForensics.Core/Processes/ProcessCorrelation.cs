namespace EzForensics.Core.Processes;

/// <summary>
/// The process graph of one host with every index the details card and the <see cref="ProcessCorrelator"/> read: <see cref="ProcessGraphBuilder"/> (process lists,
/// event logs, Prefetch), the <see cref="ProcessFacetIndex"/> (memory plugins), the <see cref="ProcessEvidenceIndex"/> (catalog CSVs, attached to the nodes) and the
/// <see cref="ZeekSocketIndex"/> (attached through the memory and event sockets). Shared by the Analyze process tree and the Ingest correlator. Dispose to release the CSVs.
/// </summary>
public sealed class ProcessCorrelation : IDisposable
{
    private bool _disposed;

    private ProcessCorrelation(string hostFolder) => HostFolder = hostFolder;

    public string HostFolder { get; }
    public ProcessGraph Graph { get; private set; } = new();
    /// <summary>Null until built (or when it could not be built, see <see cref="Errors"/>).</summary>
    public ProcessFacetIndex? Facets { get; private set; }
    public ZeekSocketIndex? Zeek { get; private set; }
    public ProcessEvidenceIndex? Evidence { get; private set; }
    /// <summary>Warnings of the indexes (the graph's own warnings stay in <see cref="ProcessGraph.Warnings"/>).</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>Indexes that failed (what, exception): the rest of the correlation still works without them.</summary>
    public List<(string What, Exception Error)> Errors { get; } = new();

    /// <summary>A snapshot over the graph and the indexes built so far.</summary>
    public ProcessActivity Activity() => new(Graph, Facets, Zeek, Evidence);

    /// <summary>
    /// Builds the graph (off the caller's thread) while the indexes load in the background, then attaches them: the catalog CSVs, the memory facets, then Zeek (its socket
    /// join needs the memory sockets). <paramref name="changed"/> is called after the graph and after each index, on the caller's synchronisation context (no
    /// ConfigureAwait(false) here, so a UI caller can update its view from it). A failing index is recorded in <see cref="Errors"/> and left null; a failing graph throws.
    /// Cancelled: everything built so far is released and <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public static async Task<ProcessCorrelation> BuildAsync(string hostFolder, CancellationToken ct, Action<ProcessCorrelation>? changed = null)
    {
        var c = new ProcessCorrelation(hostFolder);
        var facetTask = ProcessFacetIndex.BuildAsync(hostFolder, ct);
        var zeekTask = ZeekSocketIndex.BuildAsync(hostFolder, ct);
        var evidenceTask = ProcessEvidenceIndex.BuildAsync(hostFolder, ct);
        try
        {
            c.Graph = await Task.Run(() => ProcessGraphBuilder.Build(hostFolder), ct);
            ct.ThrowIfCancellationRequested();
            changed?.Invoke(c);

            var evidenceAttach = c.AttachEvidenceAsync(evidenceTask, ct, changed);
            try
            {
                var idx = await facetTask;
                ct.ThrowIfCancellationRequested();
                c.Facets = idx;
                c.Warnings.AddRange(idx.Warnings);
                changed?.Invoke(c);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { c.Errors.Add(("the per-process Volatility CSVs", ex)); }

            try
            {
                var zeek = await zeekTask;
                ct.ThrowIfCancellationRequested();
                var (graph, facets) = (c.Graph, c.Facets);
                c.Zeek = zeek;   // owned (and disposed) by c from here on
                await Task.Run(() => zeek.Attach(graph, facets), ct);
                c.Warnings.AddRange(zeek.Warnings);
                changed?.Invoke(c);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                c.Zeek?.Dispose();
                c.Zeek = null;
                c.Errors.Add(("the Zeek logs", ex));
            }
            await evidenceAttach;
            ct.ThrowIfCancellationRequested();
            return c;
        }
        catch
        {
            c.Dispose();
            // indexes still being built: released when they finish
            foreach (var t in new Task<IDisposable>[] { Up(facetTask), Up(zeekTask), Up(evidenceTask) })
                _ = t.ContinueWith(x =>
                {
                    if (x.Status == TaskStatus.RanToCompletion && !c.Owns(x.Result)) x.Result.Dispose();
                }, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<IDisposable> Up<T>(Task<T> t) where T : IDisposable => await t.ConfigureAwait(false);

    private bool Owns(IDisposable d) => ReferenceEquals(d, Facets) || ReferenceEquals(d, Zeek) || ReferenceEquals(d, Evidence);

    private async Task AttachEvidenceAsync(Task<ProcessEvidenceIndex> task, CancellationToken ct, Action<ProcessCorrelation>? changed)
    {
        try
        {
            var idx = await task;
            ct.ThrowIfCancellationRequested();
            Evidence = idx;
            idx.Attach(Graph.AllNodes);
            Warnings.AddRange(idx.Warnings);
            changed?.Invoke(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Errors.Add(("the process evidence CSVs", ex)); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Facets?.Dispose();
        Zeek?.Dispose();
        Evidence?.Dispose();
    }
}
