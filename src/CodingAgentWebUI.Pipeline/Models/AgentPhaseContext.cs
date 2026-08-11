using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Bundles the common parameters needed by all agent phase execution methods.
/// Replaces the 10-13 positional parameters on <see cref="IAgentPhaseExecutor"/> methods.
/// </summary>
public sealed record AgentPhaseContext : PipelineContextBase
{
    /// <summary>The issue being worked on.</summary>
    public required IssueDetail Issue { get; init; }

    /// <summary>Parsed issue with structured requirements/acceptance criteria.</summary>
    public required ParsedIssue ParsedIssue { get; init; }

    /// <summary>Downloaded issue/PR images for native vision delivery to agents.</summary>
    public IReadOnlyList<DownloadedImage>? DownloadedImages { get; init; }

    /// <summary>
    /// Project secrets to inject into the child agent process via
    /// <see cref="AgentRequest.EnvironmentVariables"/>. Populated from
    /// <see cref="PipelineStepContext.InjectedSecrets"/> by
    /// <see cref="PipelineStepContext.BuildAgentPhaseContext"/>. Null when no secrets
    /// were configured for this pipeline run.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InjectedSecrets { get; init; }
}
