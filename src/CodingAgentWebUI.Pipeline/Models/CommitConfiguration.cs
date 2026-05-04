namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for commit behavior including blacklisted path handling.
/// </summary>
public sealed record CommitConfiguration
{
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github", ".brain" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
