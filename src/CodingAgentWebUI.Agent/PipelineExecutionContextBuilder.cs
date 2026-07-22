using CodingAgentWebUI.Agent.KiroCli;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
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
        IPipelineRunHistoryService? historyService = null)
    {
        _qualityGateValidator = qualityGateValidator;
        _reporterFactory = reporterFactory;
        _feedbackService = feedbackService;
        _agentIdentity = agentIdentity;
        _logger = logger;
        _brainUpdateService = brainUpdateService;
        _historyService = historyService;
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
            // TODO: Wrap reporter.DisposeAsync() in a nested try/catch so that if it throws
            // (e.g., ObjectDisposedException from the internal SemaphoreSlim), the original
            // exception is not lost. Currently PipelineSignalRReporter.DisposeAsync leaves
            // _signalrLock.Dispose() unprotected — a faulted semaphore would replace the
            // original exception with a disposal exception.
            await reporter.DisposeAsync();
            throw;
        }
    }
}
