using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Test factory for constructing <see cref="PipelineOrchestrationService"/> with sensible defaults.
/// Reduces boilerplate across test files that need a minimal orchestration service instance.
/// </summary>
public static class TestOrchestrationFactory
{
    /// <summary>
    /// Creates a <see cref="PipelineOrchestrationService"/> with no-op/null defaults for all facade parameters.
    /// Tests should provide specific implementations only for the dependencies they exercise.
    /// </summary>
    public static PipelineOrchestrationService CreateMinimal(
        IConfigurationStore? configStore = null,
        IProviderFactory? providerFactory = null,
        IssueDescriptionParser? issueParser = null,
        IPipelineExecutionFacade? executionFacade = null,
        IPipelineCompletionFacade? completionFacade = null,
        IPipelineCancellationFacade? cancellationFacade = null,
        PipelineRunLifecycleService? lifecycle = null,
        ILabelSwapper? labelSwapper = null,
        Serilog.ILogger? logger = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null)
    {
        logger ??= Serilog.Log.Logger;
        historyService ??= new NullHistoryService();

        var store = configStore ?? throw new ArgumentNullException(nameof(configStore), "IConfigurationStore is required — use a Mock<IConfigurationStore>().Object");

        // TODO: When `lifecycle` is provided externally but `historyService` is not, the completion facade
        // receives a default NullHistoryService while the lifecycle holds a different history service instance.
        // This could cause confusing test failures if a test passes a custom lifecycle and then calls GetRunHistory().
        return new PipelineOrchestrationService(
            store,
            store,
            store,
            store,
            providerFactory ?? throw new ArgumentNullException(nameof(providerFactory), "IProviderFactory is required — use a Mock<IProviderFactory>().Object"),
            issueParser ?? new IssueDescriptionParser(),
            executionFacade ?? CreateDefaultExecutionFacade(logger),
            completionFacade ?? CreateDefaultCompletionFacade(logger, historyService),
            cancellationFacade ?? new PipelineCancellationFacade(null, null),
            lifecycle ?? new PipelineRunLifecycleService(historyService, runService, logger),
            labelSwapper ?? NoOpLabelSwapper.Instance,
            logger);
    }

    // TODO: CreateDefaultExecutionFacade uses real AgentPhaseExecutor/QualityGateExecutor — tests using
    // CreateMinimal() without explicit facade mocks exercise real internal wiring rather than isolated behavior.
    // If a test relied on verifying interactions via Mock.Verify(), the switch to CreateMinimal silently drops that.
    /// <summary>Creates a default execution facade with no-op implementations.</summary>
    public static PipelineExecutionFacade CreateDefaultExecutionFacade(Serilog.ILogger logger) =>
        new(
            new AgentPhaseExecutor(logger),
            new QualityGateExecutor(
                new NullQualityGateValidator(),
                new PullRequestOrchestrator(logger),
                new CiLogWriter(logger),
                new FeedbackService(logger),
                logger),
            new NullQualityGateValidator(),
            new BrainSyncService(new NullBrainUpdateService(), logger));

    /// <summary>Creates a default completion facade with no-op implementations.</summary>
    public static PipelineCompletionFacade CreateDefaultCompletionFacade(
        Serilog.ILogger logger, IPipelineRunHistoryService? historyService = null) =>
        new(
            new PullRequestOrchestrator(logger),
            new PullRequestFinalizationService(logger),
            new FeedbackService(logger),
            historyService ?? new NullHistoryService());

    /// <summary>No-op label swapper for tests that don't exercise label operations.</summary>
    public sealed class NoOpLabelSwapper : ILabelSwapper
    {
        public static readonly NoOpLabelSwapper Instance = new();
        public Task SwapLabelAsync(string providerConfigId, string identifier, string newLabel, LabelTargetKind targetKind, CancellationToken ct) => Task.CompletedTask;
        public Task SwapLabelAsync(string providerConfigId, string identifier, string newLabel, LabelTargetKind targetKind, string? expectedCurrentLabel, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> EnsureAgentLabelsAsync(string providerConfigId, LabelTargetKind targetKind, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>No-op quality gate validator for tests.</summary>
    public sealed class NullQualityGateValidator : IQualityGateValidator
    {
        public Task<QualityGateReport> ValidateAsync(
            string workspacePath,
            IReadOnlyList<QualityGateConfiguration> qualityGateConfigs,
            CancellationToken ct,
            string? baseBranch = null) =>
            Task.FromResult(new QualityGateReport
            {
                Compilation = new GateResult { Passed = true, GateName = "compilation" },
                Tests = new GateResult { Passed = true, GateName = "tests" }
            });
    }

    /// <summary>No-op run history service for tests.</summary>
    public sealed class NullHistoryService : IPipelineRunHistoryService
    {
        private readonly List<PipelineRunSummary> _runs = new();
        public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _runs.AsReadOnly();
        public IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10) => [];
        public void AddRunToHistory(PipelineRun run) => _runs.Add(run.ToSummary());
        public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }
        public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
    }

    /// <summary>No-op brain update service for tests.</summary>
    public sealed class NullBrainUpdateService : IBrainUpdateService
    {
        public Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public BrainValidationResult Validate(string brainPath, string runId, IReadOnlyList<string> changedFiles) =>
            new() { SessionLogCreated = true, OperationLogUpdated = true, EntryFormatValid = true };
        public Task AppendFallbackLogEntryAsync(string brainPath, string runId, IReadOnlyList<string> modifiedFiles, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<BrainSyncResult> CommitAndPushAsync(string brainPath, string runId, string issueIdentifier, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3) =>
            Task.FromResult(new BrainSyncResult());
    }
}
