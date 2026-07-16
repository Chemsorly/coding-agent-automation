using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

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
public sealed class PipelineStepContext
{
    // ──────────────────────────────────────────────────────────────────────────
    // Factory Methods
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="PipelineStepContext"/> for orchestrator-side execution.
    /// Issue data is NOT pre-populated — <c>FetchIssueStep</c> populates it later.
    /// </summary>
    public static PipelineStepContext ForOrchestrator(
        PipelineRun run,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        CancellationTokenSource? cts,
        IConfigurationStore configStore,
        IPipelineCallbacks callbacks,
        IAgentIssueOperations issueOps,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        IBrainSyncService? brainSync,
        PullRequestOrchestrator prOrchestrator,
        Serilog.ILogger logger,
        IQualityGateValidator? qualityGateValidator,
        IIssueProvider issueProvider)
    {
        return CreateBase(run, config, repoProvider, agentProvider, brainProvider,
            pipelineProvider, cts, configStore, callbacks, issueOps, agentExecution,
            qualityGates, brainSync, prOrchestrator, logger, qualityGateValidator,
            issueProvider: issueProvider, projectContext: null);
    }

    /// <summary>
    /// Creates a <see cref="PipelineStepContext"/> for agent-side execution.
    /// Issue data is pre-populated from the job assignment (no <c>IssueProvider</c> needed).
    /// </summary>
    public static PipelineStepContext ForAgent(
        PipelineRun run,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        CancellationTokenSource? cts,
        IConfigurationStore configStore,
        IPipelineCallbacks callbacks,
        IAgentIssueOperations issueOps,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        IBrainSyncService? brainSync,
        PullRequestOrchestrator prOrchestrator,
        Serilog.ILogger logger,
        IQualityGateValidator? qualityGateValidator,
        IssueDetail? issue,
        ParsedIssue? parsedIssue,
        IReadOnlyList<IssueComment>? issueComments,
        IReadOnlyList<ReviewerConfiguration>? preResolvedReviewerConfigs,
        IReadOnlyList<QualityGateConfiguration>? preResolvedQualityGateConfigs,
        DecompositionProjectContext? projectContext)
    {
        var ctx = CreateBase(run, config, repoProvider, agentProvider, brainProvider,
            pipelineProvider, cts, configStore, callbacks, issueOps, agentExecution,
            qualityGates, brainSync, prOrchestrator, logger, qualityGateValidator,
            issueProvider: null, projectContext: projectContext);
        // These are get/set properties — safe to assign after construction
        ctx.Issue = issue;
        ctx.ParsedIssue = parsedIssue;
        ctx.IssueComments = issueComments;
        ctx.PreResolvedReviewerConfigs = preResolvedReviewerConfigs;
        ctx.PreResolvedQualityGateConfigs = preResolvedQualityGateConfigs;
        return ctx;
    }

    private static PipelineStepContext CreateBase(
        PipelineRun run,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        CancellationTokenSource? cts,
        IConfigurationStore configStore,
        IPipelineCallbacks callbacks,
        IAgentIssueOperations issueOps,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        IBrainSyncService? brainSync,
        PullRequestOrchestrator prOrchestrator,
        Serilog.ILogger logger,
        IQualityGateValidator? qualityGateValidator,
        IIssueProvider? issueProvider,
        DecompositionProjectContext? projectContext)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = config,
            RepoProvider = repoProvider,
            AgentProvider = agentProvider,
            BrainProvider = brainProvider,
            PipelineProvider = pipelineProvider,
            Cts = cts,
            ConfigStore = configStore,
            Callbacks = callbacks,
            IssueOps = issueOps,
            AgentExecution = agentExecution,
            QualityGates = qualityGates,
            BrainSync = brainSync,
            PrOrchestrator = prOrchestrator,
            Logger = logger,
            QualityGateValidator = qualityGateValidator,
            IssueProvider = issueProvider,
            ProjectContext = projectContext
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Properties
    // ──────────────────────────────────────────────────────────────────────────

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
    /// Direct access to the quality gate validator for baseline checks (no retry/CI orchestration).
    /// Null on the orchestrator side when not wired up.
    /// </summary>
    public IQualityGateValidator? QualityGateValidator { get; init; }

    /// <summary>
    /// Project context for cross-repo decomposition, populated when the epic
    /// originates from a project-level EpicIssueProviderId.
    /// </summary>
    public DecompositionProjectContext? ProjectContext { get; init; }

    /// <summary>
    /// Pre-resolved repository providers for additional project repos (cross-repo decomposition).
    /// Keyed by template name. Used by <see cref="CloneProjectRepositoriesStep"/> to clone
    /// secondary repos into workspace subdirectories for agent exploration.
    /// Null for non-decomposition runs or per-template decomposition.
    /// </summary>
    public IReadOnlyList<(string TemplateName, IRepositoryProvider Provider)>? AdditionalRepoProviders { get; set; }

    /// <summary>
    /// Pre-resolved reviewer configurations. When non-null, <see cref="ReviewCodeStep"/>
    /// uses these directly instead of resolving from the config store.
    /// </summary>
    public IReadOnlyList<ReviewerConfiguration>? PreResolvedReviewerConfigs { get; set; }

    /// <summary>
    /// Resolved reviewer configurations from the review phase. Populated by <see cref="ReviewCodeStep"/>
    /// after resolving configs, so that <c>PostReviewFindingsStep</c> can access them for per-agent retry
    /// via <see cref="IAgentPhaseExecutor.ExecuteFollowUpAsync"/>. Null when review step hasn't run.
    /// </summary>
    public IReadOnlyList<ReviewerConfiguration>? ResolvedReviewerConfigs { get; set; }

    /// <summary>
    /// Pre-resolved quality gate configurations. When non-null, <see cref="RunQualityGatesStep"/>
    /// uses these directly instead of loading from the config store.
    /// </summary>
    public IReadOnlyList<QualityGateConfiguration>? PreResolvedQualityGateConfigs { get; set; }

    /// <summary>
    /// Keys of environment variables injected by <see cref="RunEnvironmentSetupStep"/> as process-wide
    /// secrets. Tracked so that the executor's finally block can unset them after the run completes.
    /// </summary>
    public List<string>? InjectedSecretKeys { get; set; }

    /// <summary>
    /// Key→value pairs of secrets injected by <see cref="RunEnvironmentSetupStep"/>.
    /// Used by the executor to mask secret values in ALL pipeline output (not just the setup step).
    /// Values shorter than 4 characters are not masked to avoid excessive false-positive redaction.
    /// </summary>
    public Dictionary<string, string>? InjectedSecrets { get; set; }

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

    /// <summary>
    /// Downloaded issue/PR images on the local filesystem.
    /// Set by <c>DownloadIssueImagesStep</c> during pipeline execution.
    /// Read by prompt builders and agent providers to deliver images to the coding agent.
    /// </summary>
    public IReadOnlyList<DownloadedImage>? DownloadedImages { get; set; }

    /// <summary>Whether the dispatch layer determined that analysis should be force-refreshed.</summary>
    public bool ForceRefreshAnalysis { get; set; }

    /// <summary>Which staleness signal triggered analysis force-refresh (null if not triggered).</summary>
    public string? StalenessSignal { get; set; }

    /// <summary>Number of prior analysis refreshes for this issue (for OpenTelemetry).</summary>
    public int AnalysisRefreshCount { get; set; }

    // Logger
    public required Serilog.ILogger Logger { get; init; }

    /// <summary>
    /// Builds an <see cref="AgentPhaseContext"/> from the current step context.
    /// Throws if <see cref="Issue"/> or <see cref="ParsedIssue"/> has not been set by a prior step.
    /// </summary>
    internal AgentPhaseContext BuildAgentPhaseContext()
    {
        if (Issue is null)
        {
            Logger.Error("Cannot build AgentPhaseContext: Issue has not been set by a prior step (RunId={RunId})", Run.RunId);
            throw new InvalidOperationException("Cannot build AgentPhaseContext: Issue has not been set by a prior step.");
        }
        if (ParsedIssue is null)
        {
            Logger.Error("Cannot build AgentPhaseContext: ParsedIssue has not been set by a prior step (RunId={RunId})", Run.RunId);
            throw new InvalidOperationException("Cannot build AgentPhaseContext: ParsedIssue has not been set by a prior step.");
        }

        return new AgentPhaseContext
        {
            Run = Run,
            Config = Config,
            AgentProvider = AgentProvider,
            IssueOps = IssueOps,
            Callbacks = Callbacks,
            OrchestratorCts = Cts,
            Issue = Issue,
            ParsedIssue = ParsedIssue,
            DownloadedImages = DownloadedImages
        };
    }

    /// <summary>
    /// Fails the run with the given reason, swaps label to error, and transitions to Failed.
    /// </summary>
    public async Task FailRunAsync(string reason, CancellationToken ct = default)
    {
        Run.FailureReason = reason;
        Run.MarkCompleted();
        Run.FinalLabel = AgentLabels.Error;
        Logger.Information(
            "Pipeline {RunId} FailRunAsync swapping label to agent:error for issue {IssueIdentifier} (reason={Reason}, step={CurrentStep})",
            Run.RunId, Run.IssueIdentifier, reason, Run.CurrentStep);
        await Callbacks.SwapAgentLabel(Run.IssueIdentifier, AgentLabels.Error, ct);
        Callbacks.EmitOutputLine($"❌ Pipeline failed: {reason}");
        Callbacks.TransitionTo(PipelineStep.Failed);
        await Callbacks.AddRunToHistoryAsync(Run);
    }

    /// <summary>
    /// Fails the run with the given reason and typed failure category.
    /// </summary>
    public Task FailRunAsync(string reason, FailureReason failureCategory, CancellationToken ct = default)
    {
        Run.FailureCategory = failureCategory;
        return FailRunAsync(reason, ct);
    }

    /// <summary>
    /// Executes a critical async action. On failure (non-cancellation), logs an error,
    /// fails the run, and returns <see cref="StepResult.Stop"/>.
    /// Returns <see cref="StepResult.Continue"/> on success.
    /// </summary>
    public async Task<StepResult> TryCriticalAsync(Func<Task> action, string actionDescription, CancellationToken ct = default)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex);
            Logger.Error(ex, "Pipeline {RunId} {ActionDescription} failed", Run.RunId, actionDescription);
            await FailRunAsync($"{actionDescription} failed: {ex.Message}", ct);
            return StepResult.Stop;
        }
        return StepResult.Continue;
    }

    /// <summary>
    /// Executes a non-critical async action. On failure (non-cancellation), logs a warning
    /// and invokes the optional <paramref name="onFailure"/> callback.
    /// Always returns <see cref="StepResult.Continue"/>.
    /// </summary>
    public async Task<StepResult> TryNonCriticalAsync(Func<Task> action, string actionDescription, CancellationToken ct = default, Action? onFailure = null)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex);
            Logger.Warning(ex, "Pipeline {RunId} {ActionDescription} failed, continuing", Run.RunId, actionDescription);
            onFailure?.Invoke();
        }
        return StepResult.Continue;
    }
}
