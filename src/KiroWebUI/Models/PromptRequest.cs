namespace KiroWebUI.Models;

/// <summary>
/// Request body for the POST /api/prompt endpoint.
/// </summary>
public sealed class PromptRequest
{
    public required string Prompt { get; init; }
    public bool UseResume { get; init; }
}
