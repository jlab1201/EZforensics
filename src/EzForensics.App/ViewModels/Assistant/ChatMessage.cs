namespace EzForensics.App.ViewModels;

/// <summary><see cref="Step"/>: one thing Spectre did (a tool call), shown as a muted line under the reply.</summary>
public enum ChatRole { You, Assistant, Status, Step }

/// <summary>One line of the chat list: examiner text, assistant text, a status line, or (with <see cref="Card"/>) a proposal.</summary>
public sealed class ChatMessage
{
    public ChatMessage(ChatRole role, string text, ProposalCardViewModel? card = null) { Role = role; Text = text; Card = card; }

    public ChatRole Role { get; }
    public string Text { get; }
    public ProposalCardViewModel? Card { get; }
    public bool IsProposal => Card != null;
    public string RoleLabel => Role switch { ChatRole.You => "You", ChatRole.Assistant => "Spectre", _ => "" };
}
