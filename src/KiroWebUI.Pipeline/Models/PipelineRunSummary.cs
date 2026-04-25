namespace KiroWebUI.Pipeline.Models;

public sealed class PipelineRunSummary
{
    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueTitle { get; init; }
    public required PipelineStep FinalStep { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int RetryCount { get; init; }
    public string? PullRequestUrl { get; init; }

    /// <summary>Model configured for the agent provider used in this run.</summary>
    public string? ModelName { get; init; }

    /// <summary>Whether a brain repository was used for this run.</summary>
    public bool BrainRepoUsed { get; init; }

    /// <summary>Whether brain updates were pushed successfully.</summary>
    public bool BrainUpdatesPushed { get; init; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";
}
