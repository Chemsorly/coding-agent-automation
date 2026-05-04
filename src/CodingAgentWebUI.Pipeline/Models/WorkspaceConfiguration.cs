namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for workspace directory management and retention.
/// </summary>
public sealed record WorkspaceConfiguration
{
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";

    /// <summary>
    /// Number of days to retain workspace folders for failed or cancelled runs.
    /// Set to 0 to delete immediately. Set to -1 to retain indefinitely.
    /// </summary>
    public int FailedWorkspaceRetentionDays { get; init; } = 7;
}
