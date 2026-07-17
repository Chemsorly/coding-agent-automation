using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Infrastructure.Persistence;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Test-only pipeline runner that executes the full step pipeline without requiring
/// <see cref="PipelineOrchestrationService"/>. Constructs a <see cref="PipelineStepContext"/>
/// directly from injected providers and calls <see cref="PipelineStepRunner.ExecuteAsync"/>.
/// </summary>
/// <remarks>
/// This replaces the former internal <c>StartPipelineAsync</c> test entry point.
/// Tests construct this runner with their mocked providers and call <see cref="RunAsync"/>
/// to exercise pipeline step logic end-to-end.
/// </remarks>
public sealed class TestPipelineRunner : IDisposable, IAsyncDisposable
{
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IssueDescriptionParser _issueParser;
    private readonly IAgentPhaseExecutor _agentExecution;
    private readonly IQualityGateExecutor _qualityGates;
    private readonly IQualityGateValidator? _qualityGateValidator;
    private readonly IBrainSyncService _brainSync;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly Serilog.ILogger _logger;

    private readonly PipelineRunLifecycleService _lifecycle;

    /// <summary>Provider manager for the active run â€” stored for cancel-time label swap.</summary>
    private PipelineProviderManager? _activeProviderManager;

    /// <summary>The currently active run, or null if idle.</summary>
    public PipelineRun? ActiveRun => _lifecycle.ActiveRun;

    /// <summary>Whether a pipeline run is currently in progress.</summary>
    public bool IsRunning => _lifecycle.IsRunning;

    /// <summary>Fired after each state transition.</summary>
    public event Action? OnChange
    {
        add => _lifecycle.OnChange += value;
        remove => _lifecycle.OnChange -= value;
    }

    /// <summary>Fired for each agent output line.</summary>
    public event Action<string>? OnOutputLine
    {
        add => _lifecycle.OnOutputLine += value;
        remove => _lifecycle.OnOutputLine -= value;
    }

    /// <summary>Returns run history (async).</summary>
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => _historyService.GetRunHistoryAsync(ct);

    public TestPipelineRunner(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IQualityGateValidator? qualityGateValidator = null,
        PullRequestOrchestrator? prOrchestrator = null)
    {
        _configStore = configStore;
        _providerFactory = providerFactory;
        _issueParser = issueParser;
        _agentExecution = agentExecution;
        _qualityGates = qualityGates;
        _qualityGateValidator = qualityGateValidator;
        _logger = logger;
        _prOrchestrator = prOrchestrator ?? new PullRequestOrchestrator(logger);
        _brainSync = new BrainSyncService(
            brainUpdateService ?? new NullBrainUpdateService(), logger);
        _historyService = historyService ?? new PipelineRunHistoryService(
            logger, Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));
        _lifecycle = new PipelineRunLifecycleService(_historyService, null, logger);
    }

    /// <summary>
    /// Executes the full pipeline step sequence. Drop-in replacement for the former
    /// <c>PipelineOrchestrationService.StartPipelineAsync</c> test entry point.
    /// </summary>
    public async Task<PipelineRun> RunAsync(
        string issueProviderId, string repoProviderId, string issueIdentifier,
        string agentProviderId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null)
    {
        _lifecycle.CreateLinkedCancellationToken(ct);
        var linkedCt = _lifecycle.CancellationTokenSource!.Token;

        var config = await _configStore.LoadPipelineConfigAsync(linkedCt);
        _historyService.CleanupExpiredWorkspaces(config, ActiveRun?.RunId);

        // Resolve provider configs
        var providerManager = new PipelineProviderManager(_configStore, _providerFactory, _logger);
        _activeProviderManager = providerManager;
        var issueProviderConfig = await providerManager.ResolveProviderConfigAsync(
            issueProviderId, ProviderKind.Issue, linkedCt);
        var repoProviderConfig = await providerManager.ResolveProviderConfigAsync(
            repoProviderId, ProviderKind.Repository, linkedCt);
        var agentProviderConfig = await providerManager.ResolveProviderConfigAsync(
            agentProviderId, ProviderKind.Agent, linkedCt);

        config = PipelineConfigurationResolver.ApplyBlacklistOverride(config, repoProviderConfig);

        await providerManager.CreateCoreProvidersAsync(
            issueProviderConfig, repoProviderConfig, agentProviderConfig, linkedCt);
        var issueProvider = providerManager.ActiveIssueProvider!;

        if (!string.IsNullOrEmpty(brainProviderId))
            await providerManager.CreateBrainProviderAsync(brainProviderId, linkedCt);

        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(
            ProviderSettingKeys.Model, "auto");

        var run = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: issueIdentifier,
            issueTitle: string.Empty,
            issueProviderConfigId: issueProviderId,
            repoProviderConfigId: repoProviderId,
            initiatedBy: "test",
            agentProviderConfigId: agentProviderId,
            brainProviderConfigId: providerManager.ActiveBrainProvider != null ? brainProviderId : null);
        run.RepositoryName = providerManager.ActiveRepoProvider!.RepositoryFullName;
        run.ModelName = configuredModel;
        _lifecycle.ActiveRun = run;

        var pipelineConfigId = await providerManager.CreatePipelineProviderAsync(pipelineProviderId, linkedCt);
        if (pipelineConfigId is not null)
            run.PipelineProviderConfigId = pipelineConfigId;

        try
        {
            await providerManager.ValidateProvidersAsync(repoProviderConfig, agentProviderConfig, linkedCt);
            _lifecycle.NotifyChange();

            try
            {
                var labelsOk = await issueProvider.InitializeAsync(linkedCt);
                if (!labelsOk)
                    _logger.Warning("Pipeline {RunId} issue provider label creation partially failed", run.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Issue provider initialization failed: {ex.Message}", ex);
            }

            await ExecuteStepsAsync(run, issueProvider, providerManager, config, linkedCt);
        }
        catch (Exception ex) when (ex is not InvalidOperationException || ActiveRun != null)
        {
            if (ActiveRun != null && ActiveRun.CurrentStep != PipelineStep.Failed
                && ActiveRun.CurrentStep != PipelineStep.Cancelled)
            {
                run.FailureReason = ex.Message;
                run.CompletedAt = DateTime.UtcNow;
                run.CompletedAtOffset = DateTimeOffset.UtcNow;
                _lifecycle.EmitOutputLine($"âŒ Pipeline failed: {ex.Message}");
                _lifecycle.TransitionTo(run, PipelineStep.Failed);
                await _lifecycle.AddRunToHistoryAsync(run);
            }
            throw;
        }

        return run;
    }

    /// <summary>Cancels the active run.</summary>
    public async Task CancelPipelineAsync()
    {
        // Match production: swap label to agent:cancelled BEFORE lifecycle cancellation
        var run = ActiveRun;
        if (run != null && _activeProviderManager?.ActiveIssueProvider != null)
        {
            try
            {
                await AgentLabelOperations.SwapAsync(
                    (lbl, c) => _activeProviderManager.ActiveIssueProvider!.RemoveLabelAsync(run.IssueIdentifier, lbl, c),
                    (lbl, c) => _activeProviderManager.ActiveIssueProvider!.AddLabelAsync(run.IssueIdentifier, lbl, c),
                    AgentLabels.Cancelled, CancellationToken.None);
            }
            catch { /* match production: swallow label failures */ }
        }
        await _lifecycle.CancelPipelineAsync();
    }

    private async Task ExecuteStepsAsync(
        PipelineRun run,
        IIssueProvider issueProvider,
        PipelineProviderManager providerManager,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        IAgentIssueOperations issueOps = new IssueProviderIssueOperations(issueProvider, _logger);
        PipelineStepContext? ctx = null;

        var callbacks = new TestCallbacks(_lifecycle, run, providerManager, _prOrchestrator, _brainSync, _historyService, () => ctx);
        ctx = PipelineStepContext.ForOrchestrator(
            run: run,
            config: config,
            repoProvider: providerManager.ActiveRepoProvider!,
            agentProvider: providerManager.ActiveAgentProvider!,
            brainProvider: providerManager.ActiveBrainProvider,
            pipelineProvider: providerManager.ActivePipelineProvider,
            cts: _lifecycle.CancellationTokenSource,
            configStore: _configStore,
            callbacks: callbacks,
            issueOps: issueOps,
            agentExecution: _agentExecution,
            qualityGates: _qualityGates,
            brainSync: _brainSync,
            prOrchestrator: _prOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator,
            issueProvider: issueProvider);

        var steps = BuildStepPipeline();

        var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync(steps, ctx, ct);

        switch (outcome)
        {
            case PipelineExecutionOutcome.CancelledOutcome when run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed):
                run.CompletedAt = DateTime.UtcNow;
                run.CompletedAtOffset = DateTimeOffset.UtcNow;
                run.FinalLabel = AgentLabels.Cancelled;
                await callbacks.SwapAgentLabel(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
                _lifecycle.EmitOutputLine("🚫 Pipeline cancelled");
                _lifecycle.TransitionTo(run, PipelineStep.Cancelled);
                await _lifecycle.AddRunToHistoryAsync(run);
                break;

            case PipelineExecutionOutcome.CancelledOutcome:
                // Already cancelled/failed — no additional transitions needed
                break;

            case PipelineExecutionOutcome.FailedOutcome { Exception: var ex }:
                // Preserve existing behavior: generic exceptions propagate to RunAsync's outer catch
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                break;

            case PipelineExecutionOutcome.CompletedOutcome:
                // Steps completed normally — no action needed at this level
                break;
        }
    }

    private IReadOnlyList<IPipelineStep> BuildStepPipeline()
    {
        var steps = new List<IPipelineStep>
        {
            new FetchIssueStep(_issueParser, new IssueImageExtractor()),
            new CloneRepositoryStep(),
            new RunEnvironmentSetupStep(),
            new SyncBrainPreRunStep(),
        };
        steps.AddRange(PipelineStepFactory.CreateCoreImplementationSteps());
        return steps;
    }

    public void Dispose()
    {
        _lifecycle.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test-only IPipelineCallbacks implementation that delegates lifecycle operations
    /// to <see cref="PipelineRunLifecycleService"/> and provider operations to the
    /// mocked providers via <see cref="PipelineProviderManager"/>.
    /// </summary>
    private sealed class TestCallbacks(
        PipelineRunLifecycleService lifecycle,
        PipelineRun run,
        PipelineProviderManager providerManager,
        PullRequestOrchestrator prOrchestrator,
        IBrainSyncService brainSync,
        IPipelineRunHistoryService historyService,
        Func<PipelineStepContext?> ctxAccessor) : IPipelineCallbacks
    {
        public void TransitionTo(PipelineStep step) => lifecycle.TransitionTo(run, step);
        public void EmitOutputLine(string line) => lifecycle.EmitOutputLine(line);
        public void NotifyChange() => lifecycle.NotifyChange();
        public Task AddRunToHistoryAsync(PipelineRun r) => lifecycle.AddRunToHistoryAsync(r);

        public Task UpdateFileChangeStats(PipelineRun r)
            => prOrchestrator.UpdateFileChangeStatsAsync(r, providerManager.ActiveRepoProvider!);

        public async Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
        {
            try
            {
                await AgentLabelOperations.SwapAsync(
                    (lbl, c) => providerManager.ActiveIssueProvider!.RemoveLabelAsync(issueIdentifier, lbl, c),
                    (lbl, c) => providerManager.ActiveIssueProvider!.AddLabelAsync(issueIdentifier, lbl, c),
                    label, ct);
            }
            catch { /* match production: swallow label failures */ }
        }

        public async Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
        {
            try
            {
                await AgentLabelOperations.RemoveAllAsync(
                    (lbl, c) => providerManager.ActiveIssueProvider!.RemoveLabelAsync(issueIdentifier, lbl, c),
                    ct);
            }
            catch { /* match production: swallow label failures */ }
        }

        public async Task CreatePullRequest(PipelineRun r, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            lifecycle.TransitionTo(r, PipelineStep.CreatingPullRequest);
            try
            {
                // Set PR info from linked PR before calling the orchestrator (rework mode)
                if (r.LinkedPullRequest != null)
                {
                    r.PullRequestUrl = r.LinkedPullRequest.Url;
                    r.PullRequestNumber = r.LinkedPullRequest.Number.ToString();
                }

                var prUrl = await prOrchestrator.CreatePullRequestAsync(
                    r, report, isDraft, providerManager.ActiveRepoProvider!, ctxAccessor()?.Issue,
                    ctxAccessor()?.IssueComments, ctxAccessor()?.Config ?? new PipelineConfiguration(), ct,
                    line => lifecycle.EmitOutputLine(line),
                    isRework: r.LinkedPullRequest != null,
                    issueReference: providerManager.ActiveIssueProvider?.FormatIssueReference(r.IssueIdentifier));

                if (prUrl == null)
                {
                    r.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                    r.CompletedAt = DateTime.UtcNow;
                    r.CompletedAtOffset = DateTimeOffset.UtcNow;
                    r.FinalLabel = AgentLabels.Error;
                    await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Error, ct);
                    lifecycle.EmitOutputLine($"âŒ Pipeline failed: {r.FailureReason}");
                    lifecycle.TransitionTo(r, PipelineStep.Failed);
                    await lifecycle.AddRunToHistoryAsync(r);
                    return;
                }

                r.PullRequestUrl = prUrl;
                await PostPullRequestCompletion(r, isDraft, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.FailureReason = $"PR creation failed: {ex.Message}";
                r.CompletedAt = DateTime.UtcNow;
                r.CompletedAtOffset = DateTimeOffset.UtcNow;
                r.FinalLabel = AgentLabels.Error;
                await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Error, ct);
                lifecycle.EmitOutputLine($"âŒ Pipeline failed: {r.FailureReason}");
                lifecycle.TransitionTo(r, PipelineStep.Failed);
                await lifecycle.AddRunToHistoryAsync(r);
            }
        }

        public Task CreateDraftPrIfNotExists(PipelineRun r, CancellationToken ct)
            => Task.CompletedTask;

        public async Task FinalizePullRequest(PipelineRun r, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            lifecycle.TransitionTo(r, PipelineStep.CreatingPullRequest);
            try
            {
                // If no draft PR was created, fall back to the original CreatePullRequest flow
                if (string.IsNullOrEmpty(r.PullRequestNumber))
                {
                    await CreatePullRequest(r, report, isDraft, ct);
                    return;
                }

                var prUrl = await prOrchestrator.FinalizePullRequestAsync(
                    r, report, isDraft, providerManager.ActiveRepoProvider!, ctxAccessor()?.Issue,
                    ctxAccessor()?.IssueComments, ctxAccessor()?.Config ?? new PipelineConfiguration(), ct,
                    line => lifecycle.EmitOutputLine(line),
                    issueReference: providerManager.ActiveIssueProvider?.FormatIssueReference(r.IssueIdentifier));

                if (prUrl == null && r.PullRequestUrl == null)
                {
                    r.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                    r.CompletedAt = DateTime.UtcNow;
                    r.CompletedAtOffset = DateTimeOffset.UtcNow;
                    r.FinalLabel = AgentLabels.Error;
                    await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Error, ct);
                    lifecycle.EmitOutputLine($"âŒ Pipeline failed: {r.FailureReason}");
                    lifecycle.TransitionTo(r, PipelineStep.Failed);
                    await lifecycle.AddRunToHistoryAsync(r);
                    return;
                }

                if (prUrl != null)
                    r.PullRequestUrl = prUrl;

                await PostPullRequestCompletion(r, isDraft, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.FailureReason = $"PR finalization failed: {ex.Message}";
                r.CompletedAt = DateTime.UtcNow;
                r.CompletedAtOffset = DateTimeOffset.UtcNow;
                r.FinalLabel = AgentLabels.Error;
                await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Error, ct);
                lifecycle.EmitOutputLine($"âŒ Pipeline failed: {r.FailureReason}");
                lifecycle.TransitionTo(r, PipelineStep.Failed);
                await lifecycle.AddRunToHistoryAsync(r);
            }
        }

        private async Task PostPullRequestCompletion(PipelineRun r, bool isDraft, CancellationToken ct)
        {
            var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
            if (isDraft)
            {
                r.FailureReason ??= "Quality gates failed after max retries; draft PR created.";
                r.IsDraftPr = true;
                r.FinalLabel = AgentLabels.Error;
                await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Error, ct);
            }
            else
            {
                r.FinalLabel = AgentLabels.Done;
                await SwapAgentLabel(r.IssueIdentifier, AgentLabels.Done, ct);
            }

            // Brain reflection + post-run sync (if brain provider configured and not read-only)
            var config = ctxAccessor()?.Config;
            if (!isDraft && providerManager.ActiveBrainProvider != null && config?.BrainReadOnly != true)
            {
                lifecycle.TransitionTo(r, PipelineStep.ReflectingOnRun);
                try
                {
                    var agentProvider = providerManager.ActiveAgentProvider;
                    if (agentProvider != null)
                    {
                        var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                            r, r.IssueTitle, r.RepositoryName?.Split('/').LastOrDefault());
                        await agentProvider.ExecuteAsync(
                            new AgentRequest { Prompt = reflectionPrompt, WorkspacePath = r.WorkspacePath ?? "", UseResume = true },
                            ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { /* non-fatal â€” match production behavior */ }

                lifecycle.TransitionTo(r, PipelineStep.SyncingBrainRepoPostRun);
                try
                {
                    await brainSync.SyncPostRunAsync(r, providerManager.ActiveBrainProvider, ct,
                        line => lifecycle.EmitOutputLine(line), config?.BrainPushMaxRetries ?? 3);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.BrainUpdatesPushed = false;
                }
            }

            r.CompletedAt ??= DateTime.UtcNow;
            r.CompletedAtOffset ??= DateTimeOffset.UtcNow;

            lifecycle.TransitionTo(r, finalStep);
            await lifecycle.AddRunToHistoryAsync(r);

            var duration = r.CompletedAt!.Value - r.StartedAt;
            if (finalStep == PipelineStep.Completed)
            {
                lifecycle.EmitOutputLine($"✅ Pipeline completed in {(int)duration.TotalMinutes}m {duration.Seconds}s");
                // Workspace cleanup on success (match production)
                historyService.TryDeleteWorkspace(r.WorkspacePath, r.RunId, config?.WorkspaceBaseDirectory ?? "");
            }
            else
                lifecycle.EmitOutputLine($"âŒ Pipeline failed: {r.FailureReason}");
        }

        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
        {
            run.BrainContextLoaded = contextLoaded;
            run.BrainKnowledgeFileCount = knowledgeFileCount;
            lifecycle.NotifyChange();
            return Task.CompletedTask;
        }
    }

    /// <summary>No-op brain update service for tests that don't need brain functionality.</summary>
    private sealed class NullBrainUpdateService : IBrainUpdateService
    {
        public Task<BrainSyncResult> SyncBrainPreRunAsync(string workspacePath, IRepositoryProvider brainProvider, PipelineConfiguration config, CancellationToken ct)
            => Task.FromResult(new BrainSyncResult());
        public Task PullBrainBeforeWriteAsync(string workspacePath, IRepositoryProvider brainProvider, CancellationToken ct)
            => Task.CompletedTask;
        public Task<bool> PushBrainUpdatesAsync(string workspacePath, IRepositoryProvider brainProvider, string issueIdentifier, PipelineConfiguration config, CancellationToken ct)
            => Task.FromResult(false);
        public Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public BrainValidationResult Validate(string brainPath, string runId, IReadOnlyList<string> changedFiles)
            => new() { SessionLogCreated = true, OperationLogUpdated = true, EntryFormatValid = true };
        public Task AppendFallbackLogEntryAsync(string brainPath, string runId, IReadOnlyList<string> modifiedFiles, CancellationToken ct)
            => Task.CompletedTask;
        public Task<BrainSyncResult> CommitAndPushAsync(string brainPath, string runId, string issueIdentifier, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3)
            => Task.FromResult(new BrainSyncResult());
    }
}
