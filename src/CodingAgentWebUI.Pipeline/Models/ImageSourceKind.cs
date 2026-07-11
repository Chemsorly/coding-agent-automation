namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminates whether images were extracted from an issue or a pull request.
/// Used by IssueImageExtractor to generate distinct filenames per source type.
/// </summary>
public enum ImageSourceKind
{
    Issue,
    PullRequest
}
