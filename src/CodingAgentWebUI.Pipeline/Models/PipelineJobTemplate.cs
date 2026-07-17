using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A persisted template defining which providers to use when polling a repository
/// for issues and dispatching pipeline jobs. Multiple templates enable round-robin
/// polling across repositories.
/// </summary>
[MessagePackObject]
public sealed record PipelineJobTemplate
{
    /// <summary>Optional brain repository provider config ID.</summary>
    [Key(0)]
    public ProviderConfigId? BrainProviderId { get; init; }

    /// <summary>
    /// When true, the brain repository is read-only for this template — context is
    /// injected but no write-back occurs. Default false.
    /// </summary>
    [Key(1)]
    public bool BrainReadOnly { get; init; } = false;

    /// <summary>Whether this template dispatches decomposition jobs from epics. Default false.</summary>
    [Key(2)]
    public bool DecompositionEnabled { get; init; } = false;

    /// <summary>Whether this template is active for round-robin polling. Default true.</summary>
    [Key(3)]
    public bool Enabled { get; init; } = true;

    /// <summary>Unique identifier (GUID), generated on creation.</summary>
    [Key(4)]
    public required string Id { get; init; }

    /// <summary>Whether this template dispatches implementation jobs from issues. Default true.</summary>
    [Key(5)]
    public bool ImplementationEnabled { get; init; } = true;

    /// <summary>Issue provider config ID — used to poll for agent:next issues.</summary>
    [Key(6)]
    public required ProviderConfigId IssueProviderId { get; init; }

    /// <summary>Operator-assigned display name (e.g., "DotNet Main Repo").</summary>
    [Key(7)]
    public required string Name { get; init; }

    /// <summary>Optional CI/pipeline provider config ID.</summary>
    [Key(8)]
    public ProviderConfigId? PipelineProviderId { get; init; }

    /// <summary>Repository provider config ID — used for cloning and PR creation.</summary>
    [Key(9)]
    public required ProviderConfigId RepoProviderId { get; init; }

    /// <summary>Whether this template dispatches PR review jobs. Default true.</summary>
    [Key(10)]
    public bool ReviewEnabled { get; init; } = true;
}
