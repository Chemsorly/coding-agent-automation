namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// An open provider issue with its dispatch readiness, shown on the Work screen's provider backlog.
/// Ready issues can be dispatched now; blocked issues wait on the <see cref="BlockedBy"/> issues.
/// </summary>
/// <param name="Identifier">Issue identifier (e.g. "123").</param>
/// <param name="Title">Issue title.</param>
/// <param name="Url">Web URL of the issue on the provider, or null if unknown.</param>
/// <param name="IsReady">True when no open dependencies block it.</param>
/// <param name="BlockedBy">Still-open issue numbers that block this one (empty when ready).</param>
public sealed record BacklogIssue(
    string Identifier,
    string Title,
    string? Url,
    bool IsReady,
    IReadOnlyList<int> BlockedBy);
