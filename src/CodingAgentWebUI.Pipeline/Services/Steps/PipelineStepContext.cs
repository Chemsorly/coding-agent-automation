using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Bundles all shared state, providers, orchestrators, and callbacks needed by pipeline steps.
/// Constructed once per pipeline run and passed to each step in sequence.
/// </summary>
internal sealed class PipelineStepContext
{
    public required PipelineRun Run { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public required IRepositoryProvider? BrainProvider { get; init; }
    public required IPipelineProvider? PipelineProvider { get; init; }
    public required CancellationTokenSource? Cts { get; init; }
    public required IConfigurationStore ConfigStore { get; init; }

    /// <summary>
    /// The issue provider for fetching issue data. Null on the agent side
    /// (where issue data is pre-populated from the job assignment).
    /// </summary>
    public IIssueProvider? IssueProvider { get; init; }

    // Callbacks
    public required Action<PipelineStep> TransitionTo { get; init; }
    public required Action<string> EmitOutputLine { get; init; }
    public required Action NotifyChange { get; init; }
    public required Action<PipelineRun> AddRunToHistory { get; init; }
    public required Func<PipelineRun, Task> UpdateFileChangeStats { get; init; }
    public required Func<string, string, CancellationToken, Task> SwapAgentLabel { get; init; }
    public required Func<string, CancellationToken, Task> RemoveAllAgentLabels { get; init; }
    public required Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> CreatePullRequest { get; init; }

    // Issue operations (narrow interface for label swaps and comments)
    public required IAgentIssueOperations IssueOps { get; init; }

    // Orchestrators (steps delegate to these)
    public required AgentExecutionOrchestrator AgentExecution { get; init; }
    public required QualityGateOrchestrator QualityGates { get; init; }
    public required BrainSyncOrchestrator? BrainSync { get; init; }
    public required PullRequestOrchestrator PrOrchestrator { get; init; }

    /// <summary>
    /// Pre-resolved reviewer configurations. When non-null, <see cref="ReviewCodeStep"/>
    /// uses these directly instead of resolving from the config store.
    /// </summary>
    public IReadOnlyList<ReviewerConfiguration>? PreResolvedReviewerConfigs { get; set; }

    /// <summary>
    /// Pre-resolved quality gate configurations. When non-null, <see cref="RunQualityGatesStep"/>
    /// uses these directly instead of loading from the config store.
    /// </summary>
    public IReadOnlyList<QualityGateConfiguration>? PreResolvedQualityGateConfigs { get; set; }

    // Mutable state set by earlier steps, read by later steps
    public IssueDetail? Issue { get; set; }
    public ParsedIssue? ParsedIssue { get; set; }
    public IReadOnlyList<IssueComment>? IssueComments { get; set; }

    // Logger
    public required Serilog.ILogger Logger { get; init; }

    /// <summary>
    /// Fails the run with the given reason, swaps label to error, and transitions to Failed.
    /// </summary>
    public async Task FailRunAsync(string reason, CancellationToken ct = default)
    {
        Run.FailureReason = reason;
        Run.CompletedAt = DateTime.UtcNow;
        await SwapAgentLabel(Run.IssueIdentifier, AgentLabels.Error, ct);
        EmitOutputLine($"❌ Pipeline failed: {reason}");
        TransitionTo(PipelineStep.Failed);
        AddRunToHistory(Run);
    }
}
