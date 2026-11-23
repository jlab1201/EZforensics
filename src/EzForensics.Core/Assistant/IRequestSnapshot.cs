namespace EzForensics.Core.Assistant;

/// <summary>A model client that keeps the body of its last request, so a troubleshooter can see exactly what the model was sent.</summary>
public interface IRequestSnapshot
{
    /// <summary>JSON of the last request (system prompt, tool schemas, messages); null before the first one. Never holds the API key.</summary>
    string? LastRequestJson { get; }
}
