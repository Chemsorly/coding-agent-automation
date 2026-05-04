namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for commit blacklisting behavior.
/// </summary>
public sealed record CommitConfiguration
{
    public IReadOnlyList<string> BlacklistedPaths { get; init; } = new[] { ".kiro", ".github", ".brain" };
    public BlacklistMode BlacklistMode { get; init; } = BlacklistMode.WarnAndExclude;
}
