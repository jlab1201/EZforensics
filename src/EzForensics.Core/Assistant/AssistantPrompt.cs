namespace EzForensics.Core.Assistant;

public static class AssistantPrompt
{
    /// <summary>
    /// System prompt, read from Resources/prompts/system.md (or the user's override in the data folder's prompts folder).
    /// Short and plain so small local models follow it; the answer-style rules come first and are repeated last.
    /// </summary>
    public static string System => PromptStore.Get("system");
}
