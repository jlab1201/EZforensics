namespace EzForensics.App.ViewModels;

/// <summary>Display label and colour slot (Brush.Cat{n}Bg / Fg) of a tool's category badge.</summary>
public static class ToolCategoryBadge
{
    public static string Label(string category) => category switch
    {
        "EventLogs" => "Event logs",
        "Registry" => "Registry",
        "FileSystem" => "File system",
        "ProgramExecution" => "Execution",
        "FileFolderAccess" => "File access",
        "FileDeletion" => "Deletion",
        "SQLDatabases" => "Databases",
        "SRUMDatabase" => "System usage",
        "SUMDatabase" => "System usage",
        "Timeline" => "Timeline",
        "WebServers" => "Web servers",
        _ => category,
    };

    public static int Index(string category) => category switch
    {
        "EventLogs" => 1,
        "Registry" => 2,
        "FileSystem" => 3,
        "ProgramExecution" => 4,
        "FileFolderAccess" or "FileDeletion" => 5,
        "SQLDatabases" or "SRUMDatabase" or "SUMDatabase" => 6,
        "Timeline" => 7,
        _ => 8,
    };
}
