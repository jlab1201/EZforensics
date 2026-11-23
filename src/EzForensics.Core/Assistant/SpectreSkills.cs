using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json.Nodes;

namespace EzForensics.Core.Assistant;

/// <summary>The page of the app a skill belongs to. <see cref="Any"/> = available everywhere.</summary>
public enum SpectrePage { Any, Analyze, Explore, Ingest, ProcessTree, Tools, Options, Findings }

/// <summary>
/// One thing EZ Forensics can do. <paramref name="Summary"/> is one line (listed in the index sent to the model);
/// <paramref name="Help"/> is the short how-to <c>skill_help</c> returns; <paramref name="ToolNames"/> are the assistant tools that carry out its actions.
/// </summary>
public sealed record Skill(string Id, string Name, SpectrePage Page, string Summary, string Help, params string[] ToolNames);

/// <summary>
/// What Spectre knows about the app's features. The model sees only the compact <see cref="Index"/>; the how-to text is fetched on
/// demand with <c>skill_help</c>, so a small local model is not loaded with every feature up front.
/// </summary>
public static class SpectreSkills
{
    public const string HelpTool = "skill_help";

    public static IReadOnlyList<Skill> All { get; } = new Skill[]
    {
        new("analyze-filter", "Filter the table", SpectrePage.Analyze,
            "Show only rows that match a condition (filter box, Filter actions, or ask me).",
            AssistantGrid.FilterSyntaxHelp + "\n" +
            "In the app: type words in 'Search all columns' for a quick text filter; use Filter actions > Edit filter... below the table for column conditions; a column header's filter button filters by values, dates or a custom condition.\n" +
            "Clear: 'Clear all filters' (or ask me to clear it). Copy the filter text from the Filter actions menu.",
            AssistantGridTools.GetContext, AssistantGridTools.ApplyFilter, AssistantGridTools.ClearFilter),
        new("analyze-flags", "Flag rows", SpectrePage.Analyze,
            "Mark rows as Informational, Interesting, Suspicious, Malicious or Benign / Ruled out.",
            "Select rows and press Space (or Ctrl+F2) to flag them with the default flag (Suspicious); the Flag button picks another flag.\n" +
            "Flag ids: informational, interesting, suspicious, malicious, benign (labels: Informational, Interesting, Suspicious, Malicious, Benign / Ruled out). The flags can be changed in Options.\n" +
            "To see only flagged rows use the flag filter ('Flagged with any of these flags'). Export or copy flagged rows from the Export / Copy menus.\n" +
            "Ask me: 'flag rows 12 and 40 as malicious' or 'unflag row 12'.",
            AssistantGridTools.FlagRows, AssistantGridTools.UnflagRows),
        new("analyze-sort", "Sort the table", SpectrePage.Analyze,
            "Order the rows by a column, ascending or descending.",
            "Click a column header to sort by it; click again to reverse. Or ask me: 'sort by TimeCreated descending'.",
            AssistantGridTools.Sort),
        new("analyze-go-to-row", "Go to a row", SpectrePage.Analyze,
            "Select a row by its row id and scroll to it.",
            "Ask me: 'go to row 812'. Row ids are the source row numbers of the CSV (the first row is 0) and stay the same when filtered or sorted.",
            AssistantGridTools.GoToRow),
        new("analyze-open-file", "Open another file", SpectrePage.Analyze,
            "Open another CSV of the case in the table, optionally filtered.",
            "Pick a CSV in the case tree on the left of Analyze and press Enter (several files open as a combined view). Or ask me: 'open the Security events filtered to 4688'; I list the case files first.",
            AssistantCaseTools.OpenFile),
        new("analyze-saved-filters", "Saved filters", SpectrePage.Analyze,
            "Save a filter under a name and re-apply it on other CSVs of the same kind.",
            "1. Apply the filter you want.\n2. Filter actions > Save filter as... and give it a name.\n3. Later: Filter actions > Edit filter... (Saved tab) or Manage saved filters... to apply, edit, export or import them.\n" +
            "Saved filters belong to the kind of CSV (its category), so they show up on every CSV of that kind."),
        new("analyze-views", "Saved views (columns)", SpectrePage.Analyze,
            "Save the column layout (visible columns, order, widths) of a CSV kind and reuse it.",
            "Use the Views menu in Analyze: Save view, Save view as..., Manage views..., Clear view. A saved view applies to every CSV of the same kind."),
        new("analyze-pivot", "Pivot (group by)", SpectrePage.Analyze,
            "Group the rows by one or more columns, with counts.",
            "Drag a column header into the band 'Drag a column header here to group by that column'. Drag more headers to nest groups (outermost first). " +
            "Groups can be sorted by count or value and expanded or collapsed (Full Expand / Full Collapse); remove them with Ungroup or Clear Grouping."),
        new("column-values", "Column values", SpectrePage.Any,
            "Most common values of a column, with counts (what stands out).",
            "Ask me: 'what are the most common EventId values?'. I can restrict it to rows matching a filter. In the table, a column header's filter button lists the values too.",
            AssistantCaseTools.ColumnValues),
        new("group-by", "Group by a column", SpectrePage.Any,
            "Group a CSV's rows by a column (e.g. PID) with row counts and a few values of other columns.",
            "Ask me: 'group the processes by PID'. I answer with a short table: each value, its row count, its first row and a few values of the columns you name. " +
            "To group the Analyze table itself, use Pivot (drag a column header into the group band).",
            AssistantCaseTools.GroupBy),
        new("keyword-search", "Search the case", SpectrePage.Any,
            "Find which case CSV files mention a keyword, and look inside them.",
            "Ask me: 'which files mention mimikatz?'. I answer with files and match counts, then look inside one with a narrow query. In Analyze, 'Search all columns' filters the open table; in Explore, Advanced search looks at files on disk.",
            AssistantCaseTools.Search, AssistantCaseTools.Query, AssistantCaseTools.GetRows, AssistantCaseTools.ListFiles),
        new("process-tree", "Process tree", SpectrePage.Analyze,
            "Parent / child graph of a host's processes (memory image and/or process events).",
            "In Analyze, right-click the host's folder in the case tree > Process tree. It needs Volatility pslist / psscan / pstree (Tools) or ingested event logs (Security 4688 / Sysmon 1). " +
            "'Rebuild process tree data' in the same menu correlates the evidence again. Back returns to the table."),
        new("explore-triage", "Browse and triage files", SpectrePage.Explore,
            "Browse folders and evidence files with preview, hex, strings, PE and metadata tabs.",
            "Explore shows the case folder. The add button (Add a folder or a file to browse) opens another folder or file; you can paste a path or drop it. Select a file for the Overview, Preview, Hex, Strings, Metadata and (executables) PE tabs. " +
            "Right-click a file for Copy SHA-256, Show in Explorer or Send to CyberChef.\n" +
            "Ask me: 'open C:\\Evidence\\file.exe in Explore' (I open it for you).",
            SpectrePageTools.ExploreOpen),
        new("explore-search", "Advanced search", SpectrePage.Explore,
            "Search files by name or by text inside them, across several folders.",
            "Click Advanced search in Explore. 'Look in' takes several folders separated by ;. Name match: wildcards (*.log;*.txt), regex or literal. Text match: words are AND'ed; use AND, OR, NOT (uppercase), parentheses and \"quotes\" " +
            "(example: password AND NOT example). You can also filter by modified date.\n" +
            "Ask me: 'find PowerShell scripts that mention Invoke-WebRequest' (I open Advanced search, fill it in, run it and tell you what it found).",
            SpectrePageTools.ExploreSearch),
        new("explore-cyberchef", "CyberChef (decode data)", SpectrePage.Explore,
            "Decode base64, hex, XOR and other encodings with CyberChef.",
            "Select a file in Explore and use Send to CyberChef (or the CyberChef button to open it in its own window), then pick the operation, e.g. From Base64. " +
            "CyberChef needs the WebView2 runtime; the page says if it is not available."),
        new("findings-review", "Review findings and key events", SpectrePage.Findings,
            "Review Spectre's findings, ask me to check one, and get suggested edits you accept or dismiss.",
            "The Review page has three tabs. Overview shows the summary, the review status, hosts by tactic, the timeline (UTC) and a card per key event; Story goes through the key events one slide at a time; Details has two lists, Findings and Key events, with the editor beside them. Select a finding (on the timeline, a card or the list) for its details, evidence and history. " +
            "Keys: J / K next / previous key event, A confirm, R reject, E edit. Confirm or Reject are your decisions (you give a rationale); Save keeps your own edits; Add to timeline or Group into key event in Details makes a key event from findings.\n" +
            "Ask me about the selected finding ('is this a false positive?', 'what MITRE id fits?') or press Ask Spectre. I can suggest a change: it appears as a yellow bar on the finding and you accept, edit or dismiss it. I never approve, reject or save.\n" +
            "Each finding comes from You (the examiner), Spectre or Both, and may sit in one key event; a key event holds one or more findings.",
            SpectrePageTools.FindingsSelected, SpectrePageTools.FindingsListView, SpectrePageTools.FindingsSelect, SpectrePageTools.FindingsProposeEdit,
            SpectrePageTools.FindingsShowEvidence, SpectrePageTools.KeyEventsList, SpectrePageTools.KeyEventsGet),
        new("findings-timeline", "Walk through the timeline", SpectrePage.Findings,
            "Explain the case's key events in time order, selecting each one as we talk.",
            "Open the Story tab on the Review page, or ask me 'walk me through the timeline'. I read the timeline, then take the key events in order: I select each one " +
            "(its card opens and the timeline follows), say what it shows and how sure it is, and cite the finding ids. All times are UTC. The order is when things were observed, " +
            "not proof that one caused another.",
            SpectrePageTools.FindingsTimelineView, SpectrePageTools.FindingsSelectMoment),
        new("ingest", "Ingest evidence", SpectrePage.Ingest,
            "Turn evidence (event logs, registry, memory, network captures, archives) into CSVs in the case.",
            "On the Ingest page pick an evidence file or open a folder (drive, KAPE output, mounted image) with Browse, review the detected artifacts and planned jobs (Custom selection changes them), then press Start. " +
            "Results appear in the case folder and in Analyze. Archives with a password ask for it. Progress is in the Jobs list on Ingest: each job shows its Status and Result.\n" +
            "Ask me: 'scan D:\\Evidence' (I set it as the evidence and scan it) or 'ingest D:\\Evidence with all jobs' (I also press Start; without auto-mode you click Apply).",
            SpectrePageTools.IngestSelectSource, SpectrePageTools.IngestStart),
        new("jobs", "Jobs", SpectrePage.Ingest,
            "See running and finished jobs; stop, retry or resume them.",
            "Jobs live on Ingest in one Jobs list: Ingest rows first, then jobs started elsewhere (Zeek, RITA, plaso, Computer details). Select a row to see its Command and Log. Right-click for Cancel (running or queued jobs), Retry (finished jobs), Open output folder and Open log file. Stop stops only the current ingest run; Resume cancelled jobs re-queues the cancelled ones.\n" +
            "Ask me: 'retry job 3' or 'stop job 2' (you click Apply), or 'why did job 3 fail?'.",
            SpectrePageTools.JobsRetry, SpectrePageTools.JobsCancel),
        new("tools", "Tools", SpectrePage.Tools,
            "Install, update and configure the forensic tools EZ Forensics runs.",
            "The Tools page (in the settings drawer) lists each tool with its status, install / update, configuration and usage. Open setup starts the wizard that downloads the EZ tools and what they need.\n" +
            "To install or update: tick the tool's row in Tools > Manage and press the button under the table. Ask me: 'install Volatility' (you click Apply; it may ask for Administrator).",
            SpectrePageTools.ToolsInstall),
        new("options-ai", "Options: AI and auto-mode", SpectrePage.Options,
            "Choose my model (Local Ollama, Anthropic, OpenAI) and turn on auto-mode.",
            "Options > AI: pick the provider and model; Local (Ollama) needs no key and keeps case data on this PC. Cloud keys are stored encrypted for your Windows account. Provider and model apply after Save. " +
            "Auto-mode (also a switch in my panel header) lets me apply filters, flags, sorts and open-file suggestions without asking; each can be undone from the chat. " +
            "I never change the provider, keys or consent, and only you turn auto-mode on; I can turn it off."),
        new("options-general", "Options: theme, hints, tool logging", SpectrePage.Options,
            "Theme (Dark/Light), Analyze hints, tool logging (Debug adds full command lines to job logs), flags and hot keys.",
            "Options page: Theme picks Dark or Light; 'Show Analyze hints' brings the hint bar back; Tool logging = Debug writes the command line, working folder, exit code and output tails to every job log; Flags and Hot keys have their own sections. Press Save.\n" +
            "Ask me: 'switch to the light theme' or 'turn on debug logging' (I propose the change; Apply saves it, Undo puts it back).",
            SpectrePageTools.OptionsSet),
        new("navigate", "Go to a page", SpectrePage.Any,
            "Open a page: Ingest, Jobs, Analyze, Explore, Review, AI (or its To review tab), Tools or Options.",
            "Use the navigation bar on the left (AI, Tools and Options are at the bottom). Ask me: 'take me to Jobs'.",
            SpectrePageTools.GoToPage),
        new("learn-page", "AI page: Graph, Intake, To review, Library", SpectrePage.Any,
            "What I know: the AI page has a knowledge Graph, Intake that checks saved sources for new files, a To review tab where new documents wait to be accepted or rejected, and a Library of sources.",
            "AI page (left navigation, bottom): Graph, Intake, To review, Library. Graph: a 3D graph of the accepted notes and how they link, with a hub per category; search it, click a note to read it, and ask me about the selected notes. " +
            "Intake checks saved sources for new files and sends them to review with AI labels (summary, category, type, tags). " +
            "To review (the Inbox in older versions): documents you add (files, folders, an Obsidian vault, .ezkb packs; drop them on the page) wait here. Nothing is used in my answers until you Accept it; Reject keeps it out, and a rejected document can be put back. Title and tags can be edited in place. " +
            "Distill into notes splits a long document into single-idea notes. " +
            "Library: the sources the notes came from. Everything stays on this PC.\n" +
            "Ask me: 'open To review' or 'take me to AI'. I only open the page; adding, accepting and rejecting are your clicks.",
            SpectrePageTools.GoToPage),
    };

    /// <summary>Extra words examiners use for a skill, beyond its id, name and summary.</summary>
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["analyze-filter"] = "filter show only where condition",
        ["analyze-flags"] = "flag mark tag highlight unflag",
        ["analyze-sort"] = "sort order newest oldest ascending descending",
        ["analyze-go-to-row"] = "jump row scroll goto",
        ["analyze-open-file"] = "open file csv another",
        ["analyze-saved-filters"] = "save saved filter reuse",
        ["analyze-views"] = "view views column columns hide layout",
        ["analyze-pivot"] = "pivot drag band nest collapse expand",
        ["group-by"] = "group grouping grouped per each by summary count",
        ["column-values"] = "values common frequent count distinct",
        ["keyword-search"] = "search find mention keyword grep",
        ["process-tree"] = "process tree parent child",
        ["explore-triage"] = "browse preview hex strings metadata hash sha256 folder",
        ["explore-search"] = "search name text folders wildcard regex",
        ["explore-cyberchef"] = "cyberchef base64 decode encode hex xor deobfuscate encoded",
        ["ingest"] = "ingest import evidence kape parse image memory pcap archive",
        ["jobs"] = "job jobs progress stop retry cancel failed resume",
        ["tools"] = "tool tools install update setup",
        ["options-ai"] = "model provider ollama anthropic openai claude key auto-mode automode",
        ["options-general"] = "theme dark light hints logging debug settings options",
        ["navigate"] = "go page open navigate take",
        ["learn-page"] = "learn brain inbox library knowledge notes documents accept reject",
    };

    private static readonly System.Text.RegularExpressions.Regex HowTo = new(
        @"\b(how\s+(do|can|to|does|should|would)|how's|where\s+(is|are|do|can)|what('s| is) the way|steps? to|way to|can i|is there a way)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "how", "the", "and", "can", "this", "that", "with", "for", "into", "from", "what", "where", "way", "use", "does", "should", "would", "there", "are", "you", "your", "its", "about" };

    private static IEnumerable<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]+").Select(m => m.Value)
            .SelectMany(w => w.Contains('-') ? w.Split('-').Append(w) : new[] { w })
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w);

    // The verb starts the message or a clause of it ("alright, from here, filter powershell"), after optional filler words.
    private static readonly Regex ActionVerb = new(
        @"(?:^\W*|[,;:.!]\s*|\s(?:and|then)\s+)" +
        @"(?:(?:please|pls|can you|could you|would you|now|then|ok|okay|alright|all right|yes|so|just|also|next|and|let's|lets|let us|go ahead and|i said|i asked you to|i want you to|i'd like you to)[\s,]+)*" +
        @"(?:flag|mark|unflag|unmark|filter|show only|only show|show me only|sort|order|clear the filter|clear filters?|reset the filter|remove the filter|go to|jump to|switch to|take me to|select row|open|scan|ingest|start|run|install|retry|cancel|set|enable|disable|turn on|turn off)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageName = new(@"\b(?:" + string.Join("|", SpectrePageTools.Pages) + @")\b(?!\s*(?:row|file))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IngestWords = new(@"\b(?:ingest|jobs?|scan)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the examiner asks for an action: the message, or a clause of it, starts with an action verb ("flag them",
    /// "please sort by time", "alright, from here, filter powershell"). A how-to question is not one ("how do I flag, then sort?").
    /// </summary>
    public static bool IsActionRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ActionVerb.Match(text.Trim()) is not { Success: true } m) return false;
        return m.Index == 0 || !HowTo.IsMatch(text);
    }

    /// <summary>
    /// The tool an action request's verb points to ("filter powershell" = grid_apply_filter), or null when it is not clear.
    /// With <paramref name="offered"/>, the first matching tool offered on this page ("open" = explore_open on Explore).
    /// </summary>
    public static string? ActionToolFor(string? text, IReadOnlyCollection<string>? offered = null)
    {
        if (!IsActionRequest(text) || ActionVerb.Match(text!.Trim()) is not { Success: true } m) return null;
        var verb = m.Value.Trim().ToLowerInvariant();
        bool Ends(params string[] words) => words.Any(w => verb.EndsWith(w, StringComparison.Ordinal));
        string[] candidates =
              Ends("unflag", "unmark") ? new[] { AssistantGridTools.UnflagRows }
            : Ends("flag", "mark") ? new[] { AssistantGridTools.FlagRows }
            : Ends("clear the filter", "clear filter", "clear filters", "reset the filter", "remove the filter") ? new[] { AssistantGridTools.ClearFilter }
            : Ends("filter", "show only", "only show", "show me only") ? new[] { AssistantGridTools.ApplyFilter }
            : Ends("sort", "order") ? new[] { AssistantGridTools.Sort }
            : Ends("switch to", "take me to") ? new[] { SpectrePageTools.GoToPage }
            : Ends("go to", "jump to") ? (PageName.IsMatch(text) ? new[] { SpectrePageTools.GoToPage } : new[] { AssistantGridTools.GoToRow })
            : Ends("select row") ? new[] { AssistantGridTools.GoToRow }
            : Ends("scan", "ingest") ? new[] { SpectrePageTools.IngestSelectSource }
            : Ends("start", "run") ? (IngestWords.IsMatch(text) ? new[] { SpectrePageTools.IngestStart } : Array.Empty<string>())
            : Ends("retry") ? new[] { SpectrePageTools.JobsRetry }
            : Ends("cancel") ? new[] { SpectrePageTools.JobsCancel }
            : Ends("install") ? new[] { SpectrePageTools.ToolsInstall }
            : Ends("set", "enable", "disable", "turn on", "turn off") ? new[] { SpectrePageTools.OptionsSet }
            : Ends("open") ? (PageName.IsMatch(text) ? new[] { SpectrePageTools.GoToPage } : new[] { SpectrePageTools.ExploreOpen, AssistantCaseTools.OpenFile })
            : Array.Empty<string>();
        if (offered == null) return candidates.Length == 1 ? candidates[0] : null;
        return candidates.FirstOrDefault(offered.Contains);
    }

    // A reply that says something was done ("I applied a filter", "Filtered on Name ...") or names an action tool.
    private static readonly Regex ClaimWords = new(
        @"\bI(?:'ve| have)?\s+(?:now\s+|just\s+|also\s+)?(?:applied|filtered|flagged|unflagged|marked|sorted|cleared|opened|started|selected|scanned|installed|retried|cancell?ed|switched|navigated|jumped|moved|enabled|disabled|set|went)\b" +
        @"|^\W*(?:Applied|Filtered|Flagged|Unflagged|Sorted|Cleared)\b" +
        @"|\b(?:" + string.Join("|", new[] { AssistantGridTools.ApplyFilter, AssistantGridTools.FlagRows, AssistantGridTools.UnflagRows, AssistantGridTools.ClearFilter,
            AssistantGridTools.Sort, AssistantGridTools.GoToRow, AssistantCaseTools.OpenFile, SpectrePageTools.GoToPage, SpectrePageTools.JobsRetry, SpectrePageTools.JobsCancel,
            SpectrePageTools.ToolsInstall, SpectrePageTools.OptionsSet, SpectrePageTools.IngestStart, SpectrePageTools.IngestSelectSource, SpectrePageTools.ExploreSearch,
            SpectrePageTools.ExploreOpen, SpectrePageTools.FindingsSelect }) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The action tool a reply names ("I applied grid_apply_filter ..."), if it is offered.</summary>
    public static string? ToolNamedIn(string? reply, IReadOnlyCollection<string> offered) =>
        string.IsNullOrEmpty(reply) ? null : offered.FirstOrDefault(n => n.Contains('_') && reply.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a reply claims an action was taken (it must then have called a tool; otherwise nothing changed).</summary>
    public static bool ClaimsAction(string? reply) => !string.IsNullOrWhiteSpace(reply) && ClaimWords.IsMatch(reply);

    /// <summary>
    /// For how-to questions, the skills whose help should be attached to the message (best first, at most two), so a small model
    /// answers from the real help instead of inventing buttons. Empty when the question is not a how-to or nothing matches well.
    /// </summary>
    public static IReadOnlyList<Skill> MatchHowTo(string question, SpectrePage page)
    {
        if (string.IsNullOrWhiteSpace(question) || !HowTo.IsMatch(question)) return Array.Empty<Skill>();
        var q = Words(question).ToHashSet();
        var scored = All.Select(s =>
        {
            var strong = Words(s.Id + " " + s.Name + " " + Keywords.GetValueOrDefault(s.Id, "")).ToHashSet();
            var weak = Words(s.Summary).ToHashSet();
            var score = 2 * q.Count(strong.Contains) + q.Count(w => !strong.Contains(w) && weak.Contains(w));
            if (score > 0 && (s.Page == page || s.Page == SpectrePage.Any)) score++;
            return (Skill: s, Score: score);
        }).Where(x => x.Score >= 2).OrderByDescending(x => x.Score).ToList();
        if (scored.Count == 0) return Array.Empty<Skill>();
        return scored.Take(2).Where(x => x.Score >= scored[0].Score - 1).Select(x => x.Skill).ToList();
    }

    /// <summary>Replaces skill ids a model leaked into its reply ("use `analyze-go-to-row`") with the feature's name.</summary>
    public static string ReplaceIds(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return reply;
        foreach (var s in All.Where(s => s.Id.Contains('-')))
            reply = System.Text.RegularExpressions.Regex.Replace(reply, $@"`?\b{System.Text.RegularExpressions.Regex.Escape(s.Id)}\b`?", s.Name,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return reply;
    }

    /// <summary>The attached help block for <see cref="MatchHowTo"/> results ("" when none).</summary>
    public static string HelpBlock(IReadOnlyList<Skill> skills) =>
        string.Concat(skills.Select(s => $"<skill_help name=\"{s.Name}\" page=\"{s.Page}\">\n{s.Help}\n</skill_help>\n"));

    /// <summary>The skill with this id or name (case-insensitive), or null.</summary>
    public static Skill? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var n = idOrName.Trim();
        return All.FirstOrDefault(s => string.Equals(s.Id, n, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One line per skill ("id: summary"), the current page's skills first, then the general ones, then the other pages (tagged).</summary>
    public static string Index(SpectrePage page)
    {
        var sb = new StringBuilder("What EZ Forensics can do (skill_help(id) gives the how-to):\n");
        foreach (var s in All.Where(s => s.Page == page && page != SpectrePage.Any).Concat(All.Where(s => s.Page == SpectrePage.Any))
                     .Concat(All.Where(s => s.Page != page && s.Page != SpectrePage.Any)))
            sb.Append("- ").Append(s.Id).Append(s.Page == SpectrePage.Any || s.Page == page ? "" : $" [{s.Page}]").Append(": ").Append(s.Summary)
              .AppendLine(s.ToolNames.Length > 0 && (s.Page == page || s.Page == SpectrePage.Any) ? " (I can do this: " + string.Join(", ", s.ToolNames) + ")" : "");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Keeps the tools that belong on <paramref name="page"/>: a tool owned only by skills of other pages (the grid actions on Explore)
    /// is dropped; general read tools, <c>skill_help</c> and tools no skill owns (findings tools) stay.
    /// </summary>
    public static IReadOnlyList<IAssistantTool> ForPage(SpectrePage page, IEnumerable<IAssistantTool> tools)
    {
        bool Offered(string name)
        {
            var owners = All.Where(s => s.ToolNames.Contains(name)).ToList();
            return owners.Count == 0 || SpectrePageTools.CrossPage.Contains(name) || owners.Any(s => s.Page == SpectrePage.Any || s.Page == page);
        }
        return tools.Where(t => Offered(t.Spec.Name)).ToList();
    }

    /// <summary>
    /// The Spectre engine skill (<c>src/spectre/knowledge/ezforensics/SKILL.md</c> in the Spectre repo): what EZ Forensics can do, page by page, which
    /// actions Spectre can propose there and what it never does. Generated from this registry so the engine and the app agree; a test
    /// fails when the file in the Spectre repo is out of date.
    /// </summary>
    public static string SkillMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("---\nname: ezforensics\n");
        sb.Append("description: \"What EZ Forensics can do and which actions Spectre can propose in it, page by page (generated by EZ Forensics from its skills registry).\"\n");
        sb.Append("version: 1.0.0\nauthor: EZ Forensics\nlicense: MIT\ntags: [ezforensics, dfir, assistant]\n---\n\n");
        sb.Append("# EZ Forensics (generated, do not edit)\n\n");
        sb.Append("Spectre runs inside EZ Forensics as the examiner's assistant. It sees which page is open (the Page: line of <app_state>) and that page's state. ");
        sb.Append("Every action is a proposal card: the examiner clicks Apply, or auto-mode applies it with Undo. Some actions always wait for the click.\n\n");
        sb.Append("Spectre never changes the AI provider or API keys, gives cloud consent, turns auto-mode on, uninstalls or deletes, or approves or rejects findings. ");
        sb.Append("For those it explains the clicks and can open the page.\n");
        foreach (var page in Enum.GetValues<SpectrePage>())
        {
            var skills = All.Where(x => x.Page == page).ToList();
            if (skills.Count == 0) continue;
            sb.Append("\n## ").Append(page == SpectrePage.Any ? "Every page" : page.ToString()).Append('\n');
            foreach (var k in skills)
            {
                sb.Append("\n### ").Append(k.Name).Append(" (").Append(k.Id).Append(")\n\n").Append(k.Summary).Append("\n\n").Append(k.Help.Trim()).Append('\n');
                if (k.ToolNames.Length > 0) sb.Append("\nSpectre tools: ").Append(string.Join(", ", k.ToolNames)).Append('\n');
            }
        }
        sb.Append("\n## Safe settings Spectre may change (options_set)\n\n");
        foreach (var (k, v) in SpectrePageTools.SafeSettings) sb.Append("- ").Append(k).Append(": ").Append(string.Join(" / ", v)).Append('\n');
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The read tool that returns a skill's how-to.</summary>
    public static IAssistantTool CreateHelpTool() => new SkillHelpTool();

    private sealed class SkillHelpTool : IAssistantTool
    {
        public ToolSpec Spec { get; } = AssistantGridTools.Spec(HelpTool,
            "How to use a feature of EZ Forensics (steps, syntax, examples). name = a skill id from the feature index.", ToolKind.Read,
            new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Skill id from the index, e.g. analyze-saved-filters." } }, "name");

        public Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
        {
            var name = call.Input["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return Task.FromResult(Find(name) is { } skill
                ? new ToolOutcome($"{skill.Name} ({skill.Page})\n{skill.Help}", false)
                : new ToolOutcome($"Unknown skill '{name}'. Valid ids: {string.Join(", ", All.Select(x => x.Id))}", true));
        }
    }
}
