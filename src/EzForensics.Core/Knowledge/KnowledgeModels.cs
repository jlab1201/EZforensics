namespace EzForensics.Core.Knowledge;

/// <summary>Link kinds stored in the <c>links</c> table.</summary>
public static class LinkKinds
{
    public const string Wikilink = "wikilink", Moc = "moc", Tag = "tag", Structure = "structure", Mention = "mention", Semantic = "semantic";
}

/// <summary>One link written in a note: the target title as written (alias and heading removed) and its kind (wikilink or moc).</summary>
public sealed record VaultLink(string Target, string Kind);

/// <summary>A Markdown note read from a vault. <see cref="Body"/> is the text without the frontmatter.</summary>
public sealed record VaultNote(string RelPath, string Title, string Topic, IReadOnlyList<string> Tags, string Hash, string Body, IReadOnlyList<VaultLink> Links);

/// <summary>A piece of a note. <see cref="EmbedText"/> is the text that is embedded (title and heading prefix included).</summary>
public sealed record ChunkPiece(int Ord, string Heading, string Text, string EmbedText);

/// <summary>A search result chunk.</summary>
public sealed record KnowledgeHit(long NoteId, string NoteTitle, string NotePath, string Heading, string Text, double Score, bool Strong = false);

/// <summary>Search result: the best chunks and the titles of notes linked to the best notes.</summary>
public sealed record KnowledgeResult(IReadOnlyList<KnowledgeHit> Hits, IReadOnlyList<string> Related);

/// <summary>Where the knowledge engine writes its log lines (set by the app; null in tests).</summary>
public static class KnowledgeLog
{
    public static Action<string>? Sink { get; set; }
    internal static void Info(string message) => Sink?.Invoke(message);
}
