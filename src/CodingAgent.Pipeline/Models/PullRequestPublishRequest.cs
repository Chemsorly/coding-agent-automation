using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Groups the inputs shared by <see cref="Services.PullRequestOrchestrator.CreatePullRequestAsync"/> and
/// <see cref="Services.PullRequestOrchestrator.FinalizePullRequestAsync"/> into a single parameter
/// object to satisfy S107.
/// </summary>
public sealed record PullRequestPublishRequest
{
    /// <summary>The run whose branch is committed, pushed and published as a pull request.</summary>
    public required PipelineRun Run { get; init; }

    /// <summary>Whether the PR is (or stays) a draft — true when quality gates failed.</summary>
    public required bool IsDraft { get; init; }

    /// <summary>Repository provider used to commit, push and create/update the PR.</summary>
    public required IRepositoryProvider RepoProvider { get; init; }

    /// <summary>The issue being implemented, used for the PR body; may be null.</summary>
    public IssueDetail? Issue { get; init; }

    /// <summary>The issue's comments, used for the PR body; may be null.</summary>
    public IReadOnlyList<IssueComment>? IssueComments { get; init; }

    /// <summary>Pipeline configuration (blacklisted and injected paths).</summary>
    public required PipelineConfiguration Config { get; init; }

    /// <summary>Receives progress output lines; may be null.</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>
    /// Issue reference used in the PR title and body (e.g. <c>org/repo#42</c>). When null,
    /// <c>#{run.IssueIdentifier}</c> is used.
    /// </summary>
    public string? IssueReference { get; init; }
}
