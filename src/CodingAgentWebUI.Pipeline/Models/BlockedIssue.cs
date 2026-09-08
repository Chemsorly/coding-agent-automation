namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// An open provider issue that cannot be dispatched yet because it depends on other issues that
/// are still open. Surfaced on the Attention screen so an operator sees what is waiting and why.
/// </summary>
/// <param name="Identifier">Issue identifier (e.g. "123").</param>
/// <param name="Title">Issue title.</param>
/// <param name="BlockedBy">Issue numbers that are still open and block this one.</param>
/// <param name="Url">Web URL of the issue on the provider, or null if unknown.</param>
public sealed record BlockedIssue(
    string Identifier,
    string Title,
    IReadOnlyList<int> BlockedBy,
    string? Url);
