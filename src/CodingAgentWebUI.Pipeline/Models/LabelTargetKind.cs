namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminates the target entity kind for label operations.
/// Used by ILabelSwapper to route to the correct provider.
/// </summary>
public enum LabelTargetKind
{
    Issue,
    PullRequest
}
