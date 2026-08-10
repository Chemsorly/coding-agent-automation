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
    /// Key→value pairs of secrets injected by <see cref="RunEnvironmentSetupStep"/>,
    /// forwarded from <see cref="Services.Steps.PipelineStepContext.InjectedSecrets"/>.
    /// Passed as <see cref="AgentRequest.AdditionalEnv"/> to scope secrets to each child process
    /// rather than mutating the parent process environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InjectedSecrets { get; init; }
}
