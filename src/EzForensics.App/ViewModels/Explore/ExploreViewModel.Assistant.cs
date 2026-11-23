using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EzForensics.App.Services;
using EzForensics.Core.Assistant;
using EzForensics.Core.Explore.Search;

namespace EzForensics.App.ViewModels.Explore;

public sealed partial class ExploreViewModel
{
    private const int AssistantMaxHits = 10;

    public override SpectrePage AssistantPage => SpectrePage.Explore;
    public override bool ShowsAssistant => true;

    /// <summary>Where the examiner is in Explore (current folder and selected files), sent with the chat message after "Page: Explore".</summary>
    public override string? AssistantState()
    {
        var sb = new System.Text.StringBuilder();
        var dir = CaseDir;
        if (dir != null) sb.Append("Case: ").Append(Path.GetFileName(dir.TrimEnd('\\', '/'))).Append(" (").Append(dir).AppendLine(")");
        if (!string.IsNullOrEmpty(CurrentPath)) sb.Append("Current folder: ").AppendLine(CurrentPath);
        var sel = SelectedRows;
        if (sel.Count > 0)
            sb.Append("Selected: ").Append(string.Join("; ", sel.Take(5).Select(r => r.FullPath))).AppendLine(sel.Count > 5 ? $" (+{sel.Count - 5} more)" : "");
        if (dir != null) sb.Append(CaseInventory.AppStateText(dir));
        return sb.ToString().TrimEnd();
    }

    /// <summary>explore_search (fills in and runs Advanced search, the examiner sees the window) and explore_open.</summary>
    public override IEnumerable<IAssistantTool> AssistantTools(IAssistantHost host)
    {
        foreach (var t in base.AssistantTools(host)) yield return t;
        yield return new SearchTool(this);
        yield return SpectrePageTools.Create(SpectrePageTools.ExploreOpenSpec, host,
            (id, input) =>
            {
                var path = input["path"]?.ToString().Trim().Trim('"') ?? "";
                return new PageActionProposal(id, "Open in Explore", path, async () =>
                {
                    await OnUi(() => OpenForAssistantAsync(path));
                    return null;
                });
            },
            input => input["path"]?.ToString().Trim().Trim('"') is { Length: > 0 } p && (File.Exists(p) || Directory.Exists(p))
                ? null : $"No such file or folder: {input["path"]}. Ask the examiner for the exact path.");
    }

    /// <summary>Shows <paramref name="path"/>: inside a browsed source it is revealed (a file selected, a folder entered); otherwise it is added as a source.</summary>
    private async Task OpenForAssistantAsync(string path)
    {
        if (_sources?.Owner(path) == null) { AddSource(path); return; }
        await RevealAsync(path, Directory.Exists(path));
    }

    /// <summary>The criteria explore_search asks for, checked; null criteria = an error for the model.</summary>
    internal static (string? Error, string LookIn, NameMatchMode NameMode, string Name, string Text, DateTime? After, DateTime? Before) ParseSearch(JsonObject input, string? fallbackLookIn)
    {
        string S(string n) => input[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var lookIn = S("look_in");
        if (lookIn.Length == 0) lookIn = fallbackLookIn ?? "";
        var roots = ExploreSearchLogic.ParseLookIn(lookIn);
        if (roots.Count == 0) return Fail("Nothing to search: give look_in (a folder) or open a case first.");
        var missing = roots.FirstOrDefault(r => !Directory.Exists(r) && !File.Exists(r));
        if (missing != null) return Fail($"No such folder: {missing}. Ask the examiner for the exact path.");
        var name = S("name");
        var mode = S("name_mode").ToLowerInvariant() switch
        {
            "regex" => NameMatchMode.Regex,
            "contains" => NameMatchMode.Contains,
            "wildcard" => NameMatchMode.Wildcard,
            _ => name.IndexOfAny(new[] { '*', '?' }) >= 0 ? NameMatchMode.Wildcard : NameMatchMode.Contains,
        };
        if (mode == NameMatchMode.Regex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(name); }
            catch (ArgumentException) { return Fail($"name is not a valid regular expression: {name}"); }
        }
        var text = S("text");
        if (name.Length == 0 && text.Length == 0) return Fail("Give a name pattern or text to look for.");
        DateTime? after = null, before = null;
        foreach (var (key, set) in new (string, Action<DateTime>)[] { ("modified_after", d => after = d), ("modified_before", d => before = d) })
        {
            var v = S(key);
            if (v.Length == 0) continue;
            if (!DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return Fail($"{key} is not a date (use yyyy-MM-dd): {v}");
            set(d.Date);
        }
        return (null, string.Join("; ", roots), mode, name, text, after, before);

        static (string?, string, NameMatchMode, string, string, DateTime?, DateTime?) Fail(string e) => (e, "", NameMatchMode.Contains, "", "", null, null);
    }

    /// <summary>
    /// explore_search: a Read tool (it only reads files) that runs in the open: Explore and the Advanced search window come up filled in, the
    /// search runs to the end, and the model gets the count and the first paths.
    /// </summary>
    private sealed class SearchTool : IAssistantTool
    {
        private readonly ExploreViewModel _vm;
        public SearchTool(ExploreViewModel vm) { _vm = vm; }
        public ToolSpec Spec => SpectrePageTools.ExploreSearchSpec;

        public async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var q = OnUi(() => ParseSearch(call.Input, string.IsNullOrEmpty(_vm.CurrentPath) ? _vm.CaseDir : _vm.CurrentPath));
            if (q.Error != null) return new ToolOutcome(q.Error, true);
            var search = _vm.Search;
            var run = OnUi(() =>
            {
                search.Cancel();
                var c = search.Criteria;
                c.NameText = q.Name; c.NameMode = q.NameMode;
                c.ContentText = q.Text; c.ContentMode = ContentMatchMode.Boolean;
                c.ModifiedAfter = q.After; c.ModifiedBefore = q.Before;
                search.LookIn = q.LookIn;
                _vm.AdvancedSearchOpener?.Invoke(search);   // the examiner watches it fill in and run
                return search.RunSearchAsync();
            });
            using (ct.Register(() => AppServices.OnUi(search.Cancel))) await run.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return OnUi(() =>
            {
                if (search.Errors is { Length: > 0 } err) return new ToolOutcome("The search could not run: " + err.ReplaceLineEndings(" "), true);
                var sb = new StringBuilder();
                sb.Append($"Advanced search in {q.LookIn}: {search.FoundCount:N0} found, {search.CheckedCount:N0} checked ({search.ElapsedText}). The window shows them.");
                foreach (var r in search.Results.Take(AssistantMaxHits)) sb.Append('\n').Append(r.FullPath);
                if (search.Results.Count > AssistantMaxHits) sb.Append($"\n+{search.Results.Count - AssistantMaxHits} more in the window.");
                return new ToolOutcome(sb.ToString(), false);
            });
        }
    }
}
