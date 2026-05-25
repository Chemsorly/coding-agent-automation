namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A persisted template defining which providers to use when polling a repository
/// for issues and dispatching pipeline jobs. Multiple templates enable round-robin
/// polling across repositories.
/// </summary>
public sealed record PipelineJobTemplate
{
    /// <summary>Unique identifier (GUID), generated on creation.</summary>
    public required string Id { get; init; }

    /// <summary>Operator-assigned display name (e.g., "DotNet Main Repo").</summary>
    public required string Name { get; init; }

    /// <summary>Issue provider config ID — used to poll for agent:next issues.</summary>
    public required string IssueProviderId { get; init; }

    /// <summary>Repository provider config ID — used for cloning and PR creation.</summary>
    public required string RepoProviderId { get; init; }

    /// <summary>Optional brain repository provider config ID.</summary>
    public string? BrainProviderId { get; init; }

    /// <summary>Optional CI/pipeline provider config ID.</summary>
    public string? PipelineProviderId { get; init; }

    /// <summary>
    /// When true, the brain repository is read-only for this template — context is
    /// injected but no write-back occurs. Default false.
    /// </summary>
    public bool BrainReadOnly { get; init; } = false;

    /// <summary>Whether this template is active for round-robin polling. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether this template dispatches implementation jobs from issues. Default true.</summary>
    public bool ImplementationEnabled { get; init; } = true;

    /// <summary>Whether this template dispatches PR review jobs. Default true.</summary>
    public bool ReviewEnabled { get; init; } = true;
}
