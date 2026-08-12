using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Constructs the pipeline execution context (orchestrators, reporter, delegates, and parameter objects)
/// from a <see cref="JobAssignmentMessage"/> and resolved providers. Extracted from
/// <see cref="LocalPipelineExecutor.ExecutePipelineStepsAsync"/> to enable isolated unit testing
/// of context construction without requiring a full pipeline execution.
/// </summary>
internal sealed class PipelineExecutionContextBuilder
{
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly IPipelineReporterFactory _reporterFactory;
    private readonly IBrainUpdateService? _brainUpdateService;
    private readonly IPipelineRunHistoryService? _historyService;
    private readonly FeedbackService _feedbackService;
    private readonly PullRequestFinalizationService? _finalization;
    private readonly AgentId _agentId;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Test seam: when set, invoked inside the try block after CTS creation to simulate a failure.
    /// Enables unit testing of the catch block's cleanup logic. Null (no-op) in production.
    /// </summary>
    internal Action? _testThrowAfterCtsCreation;

    public PipelineExecutionContextBuilder(PipelineExecutionContextBuilderDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.QualityGateValidator);
        ArgumentNullException.ThrowIfNull(deps.ReporterFactory);
        ArgumentNullException.ThrowIfNull(deps.FeedbackService);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _qualityGateValidator = deps.QualityGateValidator;
        _reporterFactory = deps.ReporterFactory;
        _feedbackService = deps.FeedbackService;
        _agentId = deps.AgentId;
        _logger = deps.Logger;
        _brainUpdateService = deps.BrainUpdateService;
        _historyService = deps.HistoryService;
        _finalization = deps.Finalization;
    }

    /// <summary>
    /// Constructs all orchestrators, the reporter, delegates, and parameter objects needed
    /// for a single pipeline execution.
    /// </summary>
    public async Task<PipelineExecutionBuildResult> Build(PipelineBuildRequest req)
    {
        var job = req.Job;
        var config = req.Config;
        var repoProvider = req.RepoProvider;
        var agentProvider = req.AgentProvider;
        var brainProvider = req.BrainProvider;
        var pipelineProvider = req.PipelineProvider;
        var issueOps = req.IssueOps;
        var connection = req.Connection;
        var outputBatcher = req.OutputBatcher;
        var onStepChanged = req.OnStepChanged;
        var ct = req.Ct;
        var run = job.RunType switch
        {
            PipelineRunType.Review => PipelineRun.CreateReview(new PipelineRunCreationParams
            {
                RunId = job.JobId,
                IssueIdentifier = job.IssueIdentifier,
                IssueTitle = job.IssueDetail.Title,
                IssueProviderConfigId = string.Empty,
                RepoProviderConfigId = job.RepoProviderConfigId,
                RunType = PipelineRunType.Review,
                InitiatedBy = job.InitiatedBy,
                AgentId = _agentId.Value,
                BrainProviderConfigId = brainProvider is not null ? job.BrainProviderConfigId : null,
                ReviewPrBranchName = job.LinkedPullRequest?.BranchName ?? string.Empty,
                ReviewPrTargetBranch = job.ReviewPrTargetBranch ?? string.Empty,
                ReviewPrDescription = job.ReviewPrDescription,
                ReviewPrAuthor = job.ReviewPrAuthor,
                LinkedIssueContexts = job.LinkedIssueContexts
            }),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
            {
                RunId = job.JobId,
                IssueIdentifier = job.IssueIdentifier,
                IssueTitle = job.IssueDetail.Title,
                IssueProviderConfigId = string.Empty,
                RepoProviderConfigId = job.RepoProviderConfigId,
                RunType = job.RunType,
                InitiatedBy = job.InitiatedBy,
                AgentId = _agentId.Value,
                BrainProviderConfigId = brainProvider is not null ? job.BrainProviderConfigId : null
            }),
            _ => PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = job.JobId,
                IssueIdentifier = job.IssueIdentifier,
                IssueTitle = job.IssueDetail.Title,
                IssueProviderConfigId = string.Empty,
                RepoProviderConfigId = job.RepoProviderConfigId,
                InitiatedBy = job.InitiatedBy,
                AgentId = _agentId.Value,
                BrainProviderConfigId = brainProvider is not null ? job.BrainProviderConfigId : null
            })
        };
        run.RepositoryName = repoProvider.RepositoryFullName;
        run.ModelName = agentProvider.Model;
        run.PipelineProviderConfigId = job.PipelineProviderConfigId;
        run.LinkedPullRequest = job.LinkedPullRequest;
        run.ProjectId = job.ProjectId;
        run.ProjectName = job.ProjectName;

        run.IssueLabels = job.IssueDetail.Labels;

        // Orchestrators
        var agentExecution = new AgentPhaseExecutor(_logger);
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var qualityGates = new QualityGateExecutor(_qualityGateValidator, prOrchestrator, new CiLogWriter(_logger), _feedbackService, _logger, _historyService);
        BrainSyncService? brainSync = _brainUpdateService is not null
            ? new BrainSyncService(_brainUpdateService, _logger)
            : null;

        // SignalR communication is encapsulated in PipelineSignalRReporter which owns the
        // serialization semaphore and all *InternalAsync methods. Manually disposed via
        // await reporter.DisposeAsync() in the finally block, which drains in-flight sends
        // before releasing the semaphore.
        var reporter = _reporterFactory.Create(connection, outputBatcher, job.JobId, run, onStepChanged);

        // Wrap all post-reporter construction in try/catch so that if anything throws
        // (including CreateLinkedTokenSource or record construction), the reporter and
        // any partially-created CTS are disposed before the exception propagates.
        CancellationTokenSource? localCts = null;
        try
        {
            localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Test seam: allows unit tests to inject a failure after CTS creation
            // to exercise the catch block's cleanup logic. No-op in production (null by default).
            _testThrowAfterCtsCreation?.Invoke();

            // Build result with a mutable StepContext reference for the EmitOutputLine delegate.
            // The delegate captures 'result' so that when StepContext is set later (after CreateStepContext),
            // subsequent calls to EmitOutputLine will use the populated context for secret masking.
            PipelineExecutionBuildResult? result = null;

            // Fire-and-forget wrappers delegating to the reporter.
            // context.InjectedSecrets is null until RunEnvironmentSetupStep populates it,
            // so output before that step passes through unmasked (no secrets exist yet).
            void TransitionTo(PipelineStep step) => reporter.TransitionTo(step, ct);
            void ReportQualityGateResult(QualityGateReport report) => reporter.ReportQualityGateResult(report, ct);
            void EmitOutputLine(string line) => reporter.EmitOutputLine(line, result?.StepContext, ct);

            var prContext = new PullRequestCreationContext
            {
                RepoProvider = repoProvider,
                AgentProvider = agentProvider,
                BrainProvider = brainProvider,
                BrainSync = brainSync,
                Config = config,
                IssueOps = issueOps,
                Job = job,
                PrOrchestrator = prOrchestrator,
                EmitOutputLine = EmitOutputLine,
                ReportStepTransition = (step, token) => reporter.ReportStepTransitionAsync(step, token)
            };

            // Build step context
            var executionContext = new PipelineExecutionContext
            {
                Job = job,
                Run = run,
                Config = config,
                RepoProvider = repoProvider,
                AgentProvider = agentProvider,
                BrainProvider = brainProvider,
                BrainSync = brainSync,
                PipelineProvider = pipelineProvider,
                IssueOps = issueOps,
                PrOrchestrator = prOrchestrator,
                AgentExecution = agentExecution,
                QualityGates = qualityGates,
                LocalCts = localCts,
                PrContext = prContext,
                TransitionTo = TransitionTo,
                EmitOutputLine = EmitOutputLine,
                ReportQualityGateResult = ReportQualityGateResult
            };

            result = new PipelineExecutionBuildResult
            {
                Run = run,
                ExecutionContext = executionContext,
                Reporter = reporter,
                LocalCts = localCts,
                EmitOutputLine = EmitOutputLine
            };

            return result;
        }
        catch
        {
            localCts?.Dispose();
            await reporter.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Backward-compatible overload accepting individual parameters.
    /// Used by tests and callers that do not yet use <see cref="PipelineBuildRequest"/>.
    /// </summary>
    public Task<PipelineExecutionBuildResult> Build( // NOSONAR S107 — convenience overload delegates to PipelineBuildRequest
        JobAssignmentMessage job,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        OrchestratorProxy issueOps,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct)
        => Build(new PipelineBuildRequest(job, config, repoProvider, agentProvider, brainProvider,
            pipelineProvider, issueOps, connection, outputBatcher, onStepChanged, ct));

    /// <summary>
    /// Creates the <see cref="PipelineStepContext"/> that carries all dependencies needed by
    /// individual pipeline steps. Wires up <see cref="AgentCallbacks"/> with the correct
    /// delegates for PR creation, label swaps, and brain sync reporting.
    /// </summary>
    internal PipelineStepContext CreateStepContext(
        PipelineExecutionContext inputs,
        PipelineSignalRReporter reporter,
        CancellationToken ct)
    {
        var callbacks = new AgentCallbacks(
            new AgentCallbacksContext(
                inputs.IssueOps,
                inputs.Run,
                inputs.PrOrchestrator,
                inputs.RepoProvider,
                inputs.ReportQualityGateResult,
                (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, inputs.PrContext, token)),
            inputs.TransitionTo,
            inputs.EmitOutputLine,
            async (contextLoaded, fileCount) => await reporter.ReportBrainSyncResultAsync(contextLoaded, fileCount, ct));

        var ctx = PipelineStepContext.ForAgent(
            services: new PipelineStepContextServices
            {
                Run = inputs.Run,
                Config = inputs.Config,
                RepoProvider = inputs.RepoProvider,
                AgentProvider = inputs.AgentProvider,
                BrainProvider = inputs.BrainProvider,
                PipelineProvider = inputs.PipelineProvider,
                Cts = inputs.LocalCts,
                ConfigStore = new NullConfigurationStore(),
                Callbacks = callbacks,
                IssueOps = inputs.IssueOps,
                AgentExecution = inputs.AgentExecution,
                QualityGates = inputs.QualityGates,
                BrainSync = inputs.BrainSync,
                PrOrchestrator = inputs.PrOrchestrator,
                Logger = _logger,
                QualityGateValidator = _qualityGateValidator
            },
            issue: inputs.Job.IssueDetail,
            parsedIssue: inputs.Job.ParsedIssue,
            issueComments: inputs.Job.IssueComments,
            preResolvedReviewerConfigs: inputs.Job.ReviewerConfigs,
            preResolvedQualityGateConfigs: inputs.Job.QualityGateConfigs,
            projectContext: inputs.Job.ProjectContext);

        // Propagate dispatch-level staleness detection results to the step context
        // so AnalyzeCodeStep can use ForceRefreshAnalysis and set OTel tags correctly.
        ctx.ForceRefreshAnalysis = inputs.Job.ForceRefreshAnalysis;
        ctx.StalenessSignal = inputs.Job.StalenessSignal;
        ctx.AnalysisRefreshCount = inputs.Job.AnalysisRefreshCount;

        return ctx;
    }

    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft,
        PullRequestCreationContext context, CancellationToken ct)
    {
        if (_finalization is null)
        {
            throw new InvalidOperationException(
                "CreatePullRequestAsync requires a PullRequestFinalizationService, but none was provided to the constructor. " +
                "Ensure the PipelineExecutionContextBuilder is constructed with a non-null 'finalization' parameter.");
        }

        await _finalization.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = isDraft,
                PrOrchestrator = context.PrOrchestrator,
                RepoProvider = context.RepoProvider,
                AgentProvider = context.AgentProvider,
                BrainProvider = context.BrainProvider,
                BrainSync = context.BrainSync,
                Config = context.Config,
                Issue = context.Job.IssueDetail,
                IssueComments = context.Job.IssueComments,
                FeedbackService = _feedbackService,
                HistoryService = _historyService,
                EmitOutputLine = context.EmitOutputLine,
                TransitionCallback = step => context.ReportStepTransition?.Invoke(step, ct) ?? Task.CompletedTask
            },
            ct);
    }

    /// <summary>
    /// Groups the context parameters for <see cref="AgentCallbacks"/> that are not delegates,
    /// reducing its primary constructor to ≤ 7 parameters (S107).
    /// </summary>
    private sealed record AgentCallbacksContext(
        OrchestratorProxy IssueOps,
        PipelineRun Run,
        PullRequestOrchestrator PrOrchestrator,
        IRepositoryProvider RepoProvider,
        Action<QualityGateReport> ReportQualityGateResult,
        Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> CreatePullRequest);

    /// <summary>
    /// Adapts the agent executor's callback methods to <see cref="IPipelineCallbacks"/>.
    /// Routes label swaps based on <see cref="PipelineRun.LabelTargetKind"/>:
    /// Implementation runs swap labels on issues, Review runs swap labels on PRs.
    /// </summary>
    private sealed class AgentCallbacks(
        AgentCallbacksContext context,
        Action<PipelineStep> transitionTo,
        Action<string> emitOutputLine,
        Func<bool, int, Task> reportBrainSyncResult) : PipelineCallbacksBase
    {
        protected override PipelineRun Run => context.Run;
        public override void TransitionTo(PipelineStep step) => transitionTo(step);
        public override void EmitOutputLine(string line) => emitOutputLine(line);
        public override void NotifyChange() { }
        public override Task AddRunToHistoryAsync(PipelineRun run) => Task.CompletedTask;
        public override Task UpdateFileChangeStats(PipelineRun run)
            => context.PrOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
        public override Task SwapAgentLabel(IssueIdentifier issueIdentifier, string label, CancellationToken ct)
            => context.IssueOps.SwapLabelAsync(issueIdentifier, label, GetLabelTargetKind(), ct);
        public override Task RemoveAllAgentLabels(IssueIdentifier issueIdentifier, CancellationToken ct)
            => context.IssueOps.SwapLabelAsync(issueIdentifier, string.Empty, GetLabelTargetKind(), ct);
        public override Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            context.ReportQualityGateResult(report);
            return context.CreatePullRequest(run, report, isDraft, ct);
        }
        protected override Task CreateDraftPrCoreAsync(PipelineRun run, CancellationToken ct)
            => context.PrOrchestrator.CreateDraftPrIfNotExistsAsync(run, context.RepoProvider, ct);
        protected override void LogDraftPrFailure(PipelineRun run, Exception ex)
        {
            Serilog.Log.Warning(ex, "Agent {RunId} failed to create draft PR, continuing", run.RunId);
        }
        public override Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
            => CreatePullRequest(run, report, isDraft, ct);
        public override Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
            => reportBrainSyncResult(contextLoaded, knowledgeFileCount);
    }
}

/// <summary>
/// Groups the parameters for <see cref="PipelineExecutionContextBuilder.Build"/>
/// to reduce method parameter count (S107).
/// </summary>
internal sealed record PipelineBuildRequest(
    JobAssignmentMessage Job,
    PipelineConfiguration Config,
    IRepositoryProvider RepoProvider,
    IAgentProvider AgentProvider,
    IRepositoryProvider? BrainProvider,
    IPipelineProvider? PipelineProvider,
    OrchestratorProxy IssueOps,
    Microsoft.AspNetCore.SignalR.Client.HubConnection Connection,
    OutputBatcher OutputBatcher,
    Action<PipelineStep?>? OnStepChanged,
    CancellationToken Ct);
