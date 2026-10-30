namespace EzForensics.Core.Catalog;

public enum ToolKind { Cli, Gui }

public enum ParamType { File, Folder, FileOrFolder, OutputDir, Enum, Bool, String, Int, Date, Multi }

public enum GroupMode
{
    /// <summary>Exactly one member must be set (e.g. -f OR -d).</summary>
    ExactlyOne,
    /// <summary>One or more members must be set (e.g. at least one output).</summary>
    AtLeastOne,
    /// <summary>Either every member is set or none is (e.g. --body with --bdl).</summary>
    AllOrNone,
}

/// <summary>One CLI or GUI tool, loaded from Resources/tools/&lt;Id&gt;.json.</summary>
public sealed class ToolDefinition
{
    /// <summary>Stable id; equals the exe name without extension (also the --help file name).</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>KAPE-style category (FileSystem, ProgramExecution, EventLogs, Registry, ...).</summary>
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>One to three sentences: what the tool is for and when to use it (the "Usage" section of the tool page).</summary>
    public string Usage { get; set; } = "";
    /// <summary>Optional: what the tool cannot show (shown as "Limitation: ..." under Usage).</summary>
    public string Limitation { get; set; } = "";
    public ToolKind Kind { get; set; } = ToolKind.Cli;
    /// <summary>Exe file name, e.g. "EvtxECmd.exe".</summary>
    public string Exe { get; set; } = "";
    /// <summary>Zip name on download.ericzimmermanstools.com, e.g. "EvtxECmd.zip" (net9 builds live under /net9/).</summary>
    public string Zip { get; set; } = "";
    /// <summary>Available builds: "net9", "net4".</summary>
    public List<string> Builds { get; set; } = new();
    /// <summary>Heavy (I/O / memory), e.g. MFTECmd: at most one heavy job runs at a time; light jobs still run beside it within MaxParallel.</summary>
    public bool Exclusive { get; set; }
    public bool RequiresAdmin { get; set; }
    /// <summary>Artifacts / file masks the tool reads (informational, used by triage UI).</summary>
    public List<string> Consumes { get; set; } = new();
    public List<ParameterDef> Parameters { get; set; } = new();
    public List<ParamGroup> Groups { get; set; } = new();

    public ParameterDef? FindParameter(string id) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Id;
}

/// <summary>One command-line switch.</summary>
public sealed class ParameterDef
{
    /// <summary>Key used in the values dictionary passed to CommandBuilder.</summary>
    public string Id { get; set; } = "";
    /// <summary>The switch as typed, e.g. "-f" or "--csv".</summary>
    public string Flag { get; set; } = "";
    public ParamType Type { get; set; } = ParamType.String;
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The tool's own default (as a string; "true"/"false" for bools). Null = none.</summary>
    public string? Default { get; set; }
    public bool Advanced { get; set; }
    public bool Required { get; set; }
    /// <summary>Allowed values for Enum parameters.</summary>
    public List<string>? Values { get; set; }
    /// <summary>File dialog filter, e.g. "Event logs|*.evtx|All files|*.*".</summary>
    public string? Filter { get; set; }
    /// <summary>Id of another parameter that must be set for this one to be shown/emitted.</summary>
    public string? VisibleWhen { get; set; }
    public string? Placeholder { get; set; }

    public bool DefaultIsTrue => Type == ParamType.Bool && CommandValue.ParseBool(Default) == true;

    public override string ToString() => $"{Id} ({Flag})";
}

public sealed class ParamGroup
{
    public GroupMode Mode { get; set; }
    /// <summary>Parameter ids in the group.</summary>
    public List<string> Params { get; set; } = new();
    /// <summary>Optional custom error message.</summary>
    public string? Message { get; set; }
}

internal static class CommandValue
{
    public static bool? ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null,
        };
    }
}
