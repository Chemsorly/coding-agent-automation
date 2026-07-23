using CodingAgentWebUI.Agent.KiroCli;
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
    private readonly AgentIdentity _agentIdentity;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Test seam: when set, invoked inside the try block after CTS creation to simulate a failure.
    /// Enables unit testing of the catch block's cleanup logic. Null (no-op) in production.
    /// </summary>
    internal Action? _testThrowAfterCtsCreation;

    // TODO: Add ArgumentNullException.ThrowIfNull for required parameters (qualityGateValidator,
    // reporterFactory, feedbackService, agentIdentity, logger) to fail fast instead of NRE in Build().
    public PipelineExecutionContextBuilder(
        IQualityGateValidator qualityGateValidator,
        IPipelineReporterFactory reporterFactory,
        FeedbackService feedbackService,
        AgentIdentity agentIdentity,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        PullRequestFinalizationService? finalization = null)
    {
        _qualityGateValidator = qualityGateValidator;
        _reporterFactory = reporterFactory;
        _feedbackService = feedbackService;
        _agentIdentity = agentIdentity;
        _logger = logger;
        _brainUpdateService = brainUpdateService;
        _historyService = historyService;
        _finalization = finalization;
    }

    /// <summary>
    /// Constructs all orchestrators, the reporter, delegates, and parameter objects needed
    /// for a single pipeline execution.
    /// </summary>
    public async Task<PipelineExecutionBuildResult> Build(
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
    {
        var run = PipelineRun.Create(
            runId: job.JobId,
            issueIdentifier: job.IssueIdentifier,
            issueTitle: job.IssueDetail.Title,
            issueProviderConfigId: string.Empty, // Agent doesn't have issue provider
            repoProviderConfigId: job.RepoProviderConfigId,
            runType: job.RunType,
            initiatedBy: job.InitiatedBy,
            agentId: _agentIdentity.Id,
            brainProviderConfigId: brainProvider is not null ? job.BrainProviderConfigId : null,
            reviewPrBranchName: job.LinkedPullRequest?.BranchName,
            reviewPrTargetBranch: job.ReviewPrTargetBranch,
            reviewPrDescription: job.ReviewPrDescription,
            reviewPrAuthor: job.ReviewPrAuthor,
            linkedIssueContexts: job.LinkedIssueContexts);
        run.RepositoryName = repoProvider.RepositoryFullName;
        run.ModelName = agentProvider is KiroCliAgentProvider kp ? kp.Model : null;
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
            inputs.TransitionTo,
            inputs.EmitOutputLine,
            inputs.IssueOps,
            inputs.Run,
            inputs.PrOrchestrator,
            inputs.RepoProvider,
            inputs.ReportQualityGateResult,
            (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, inputs.PrContext, token),
            async (contextLoaded, fileCount) => await reporter.ReportBrainSyncResultAsync(contextLoaded, fileCount, ct));

        var ctx = PipelineStepContext.ForAgent(
            run: inputs.Run,
            config: inputs.Config,
            repoProvider: inputs.RepoProvider,
            agentProvider: inputs.AgentProvider,
            brainProvider: inputs.BrainProvider,
            pipelineProvider: inputs.PipelineProvider,
            cts: inputs.LocalCts,
            configStore: new NullConfigurationStore(),
            callbacks: callbacks,
            issueOps: inputs.IssueOps,
            agentExecution: inputs.AgentExecution,
            qualityGates: inputs.QualityGates,
            brainSync: inputs.BrainSync,
            prOrchestrator: inputs.PrOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator,
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

    // TODO: _finalization! uses null-forgiving operator on a nullable field. Either make the constructor
    // parameter required or add a null guard (e.g., ArgumentNullException.ThrowIfNull) to prevent
    // NullReferenceException if this method is called when finalization was not provided.
    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft,
        PullRequestCreationContext context, CancellationToken ct)
    {
        await _finalization!.RunFullPrCreationAsync(
            run, report, isDraft,
            context.PrOrchestrator, context.RepoProvider, context.AgentProvider,
            context.BrainProvider, context.BrainSync, context.Config,
            context.Job.IssueDetail, context.Job.IssueComments,
            _feedbackService, _historyService,
            context.EmitOutputLine,
            step => context.ReportStepTransition?.Invoke(step, ct) ?? Task.CompletedTask,
            ct);
    }

    /// <summary>
    /// Adapts the agent executor's callback methods to <see cref="IPipelineCallbacks"/>.
    /// Routes label swaps based on <see cref="PipelineRun.LabelTargetKind"/>:
    /// Implementation runs swap labels on issues, Review runs swap labels on PRs.
    /// </summary>
    private sealed class AgentCallbacks(
        Action<PipelineStep> transitionTo,
        Action<string> emitOutputLine,
        OrchestratorProxy orchestratorProxy,
        PipelineRun run,
        PullRequestOrchestrator prOrchestrator,
        IRepositoryProvider repoProvider,
        Action<QualityGateReport> reportQualityGateResult,
        Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> createPullRequest,
        Func<bool, int, Task> reportBrainSyncResult) : PipelineCallbacksBase
    {
        protected override PipelineRun Run => run;
        public override void TransitionTo(PipelineStep step) => transitionTo(step);
        public override void EmitOutputLine(string line) => emitOutputLine(line);
        public override void NotifyChange() { }
        public override Task AddRunToHistoryAsync(PipelineRun run) => Task.CompletedTask;
        public override Task UpdateFileChangeStats(PipelineRun run)
            => prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
        public override Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
            => orchestratorProxy.SwapLabelAsync(issueIdentifier, label, GetLabelTargetKind(), ct);
        public override Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
            => orchestratorProxy.SwapLabelAsync(issueIdentifier, string.Empty, GetLabelTargetKind(), ct);
        public override Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            reportQualityGateResult(report);
            return createPullRequest(run, report, isDraft, ct);
        }
        protected override Task CreateDraftPrCoreAsync(PipelineRun run, CancellationToken ct)
            => prOrchestrator.CreateDraftPrIfNotExistsAsync(run, repoProvider, ct);
        protected override void LogDraftPrFailure(PipelineRun run, Exception ex)
        {
            Serilog.Log.Warning(ex, "Agent {RunId} failed to create draft PR, continuing", run.RunId);
        }
        public override Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            reportQualityGateResult(report);
            return createPullRequest(run, report, isDraft, ct);
        }
        public override Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
            => reportBrainSyncResult(contextLoaded, knowledgeFileCount);
    }
}
