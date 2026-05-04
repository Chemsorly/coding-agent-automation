using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Bundles all shared state, providers, orchestrators, and callbacks needed by pipeline steps.
/// Constructed once per pipeline run and passed to each step in sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Intentionally mutable:</b> This class is not a record or immutable object because it
/// accumulates state across pipeline steps during execution. Early steps (e.g., <c>FetchIssueStep</c>)
/// populate properties that later steps (e.g., <c>AnalyzeCodeStep</c>, <c>GenerateCodeStep</c>) consume.
/// The mutable design avoids reconstructing the entire context after each step completes.
/// </para>
/// </remarks>
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

    /// <summary>Pipeline lifecycle callbacks (transitions, output, history, labels, PR creation).</summary>
    public required IPipelineCallbacks Callbacks { get; init; }

    // Issue operations (narrow interface for label swaps and comments)
    public required IAgentIssueOperations IssueOps { get; init; }

    // Orchestrators (steps delegate to these via interfaces)
    public required IAgentPhaseExecutor AgentExecution { get; init; }
    public required IQualityGateExecutor QualityGates { get; init; }
    public required IBrainSyncService? BrainSync { get; init; }
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

    /// <summary>
    /// The fetched issue detail. Set by <c>FetchIssueStep</c> during pipeline execution.
    /// Read by <c>AnalyzeCodeStep</c>, <c>GenerateCodeStep</c>, <c>ReviewCodeStep</c>, and <c>RunQualityGatesStep</c>.
    /// </summary>
    public IssueDetail? Issue { get; set; }

    /// <summary>
    /// The parsed issue (extracted acceptance criteria, description sections, etc.).
    /// Set by <c>FetchIssueStep</c> during pipeline execution.
    /// Read by <c>AnalyzeCodeStep</c>, <c>GenerateCodeStep</c>, and <c>ReviewCodeStep</c>.
    /// </summary>
    public ParsedIssue? ParsedIssue { get; set; }

    /// <summary>
    /// Comments on the issue, capped at 50. Set by <c>FetchIssueStep</c> during pipeline execution.
    /// Read by <c>AnalyzeCodeStep</c> (to detect existing analysis) and <c>CreatePullRequest</c> operations.
    /// </summary>
    public IReadOnlyList<IssueComment>? IssueComments { get; set; }

    // Logger
    public required Serilog.ILogger Logger { get; init; }

    /// <summary>
    /// Builds an <see cref="AgentPhaseContext"/> from the current step context.
    /// Throws if <see cref="Issue"/> or <see cref="ParsedIssue"/> has not been set by a prior step.
    /// </summary>
    internal AgentPhaseContext BuildAgentPhaseContext()
    {
        if (Issue is null)
            throw new InvalidOperationException("Cannot build AgentPhaseContext: Issue has not been set by a prior step.");
        if (ParsedIssue is null)
            throw new InvalidOperationException("Cannot build AgentPhaseContext: ParsedIssue has not been set by a prior step.");

        return new AgentPhaseContext
        {
            Run = Run,
            Config = Config,
            AgentProvider = AgentProvider,
            IssueOps = IssueOps,
            Callbacks = Callbacks,
            OrchestratorCts = Cts,
            Issue = Issue,
            ParsedIssue = ParsedIssue
        };
    }

    /// <summary>
    /// Fails the run with the given reason, swaps label to error, and transitions to Failed.
    /// </summary>
    public async Task FailRunAsync(string reason, CancellationToken ct = default)
    {
        Run.FailureReason = reason;
        Run.CompletedAt = DateTime.UtcNow;
        await Callbacks.SwapAgentLabel(Run.IssueIdentifier, AgentLabels.Error, ct);
        Callbacks.EmitOutputLine($"❌ Pipeline failed: {reason}");
        Callbacks.TransitionTo(PipelineStep.Failed);
        Callbacks.AddRunToHistory(Run);
    }
}
