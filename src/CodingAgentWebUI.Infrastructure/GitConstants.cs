namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Constants for Git operations performed by the pipeline.
/// </summary>
public static class GitConstants
{
    /// <summary>Git commit author/committer name used by the pipeline.</summary>
    public const string CommitAuthorName = "CodingAgentWebUI Pipeline";

    /// <summary>Git commit author/committer email used by the pipeline.</summary>
    public const string CommitAuthorEmail = "pipeline@kiro.dev";

    /// <summary>Username used when constructing authenticated Git clone URLs.</summary>
    public const string TokenUsername = "x-access-token";
}
