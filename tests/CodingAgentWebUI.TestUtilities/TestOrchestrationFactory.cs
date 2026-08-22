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
    /// <para>
    /// When <see cref="CreateMinimalOptions.OrchestrationService"/> is set, that instance is returned
    /// directly without constructing the concrete class — allowing tests to inject a
    /// <c>Mock&lt;IPipelineOrchestrationService&gt;</c> without the 6-parameter concrete constructor.
    /// </para>
    /// </summary>
    public static IPipelineOrchestrationService CreateMinimal(CreateMinimalOptions? options = null)
    {
        var o = options ?? new CreateMinimalOptions();

        // Short-circuit: if the caller supplied a pre-built IPipelineOrchestrationService (e.g. a mock),
        // return it directly so tests only need the interface, not the 6-parameter concrete class.
        if (o.OrchestrationService is not null)
            return o.OrchestrationService;

        var logger = o.Logger ?? Serilog.Log.Logger;
        var historyService = o.HistoryService ?? new NullHistoryService();

        var store = o.ConfigStore ?? throw new ArgumentNullException(nameof(options), "IConfigurationStore is required — use a Mock<IConfigurationStore>().Object");
        // TODO: Passing the same store object as both IPipelineConfigStore and IProviderConfigStore prevents tests
        // from verifying that calls are routed to the correct sub-interface. Consider accepting separate parameters.
        return new PipelineOrchestrationService(
            store,
            o.ProviderFactory ?? throw new ArgumentNullException(nameof(options), "IProviderFactory is required — use a Mock<IProviderFactory>().Object"),
            o.CancellationFacade ?? new PipelineCancellationFacade(null, null),
            o.Lifecycle ?? new PipelineRunLifecycleService(historyService, o.RunService, logger),
            o.LabelService ?? NoOpLabelService.Instance,
            logger);
    }

    /// <summary>
    /// Overload that accepts individual named parameters for backward compatibility with existing tests.
    /// Prefer the <see cref="CreateMinimalOptions"/> overload for new test code.
    /// </summary>
    public static PipelineOrchestrationService CreateMinimal(
        IConfigurationStore? configStore = null,
        IProviderFactory? providerFactory = null,
        IPipelineCancellationFacade? cancellationFacade = null,
        PipelineRunLifecycleService? lifecycle = null,
        ILabelService? labelService = null,
        Serilog.ILogger? logger = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null)
    {
        logger ??= Serilog.Log.Logger;
        historyService ??= new NullHistoryService();
        var store = configStore ?? throw new ArgumentNullException(nameof(configStore), "IConfigurationStore is required — use a Mock<IConfigurationStore>().Object");
        return new PipelineOrchestrationService(
            store,
            providerFactory ?? throw new ArgumentNullException(nameof(providerFactory), "IProviderFactory is required — use a Mock<IProviderFactory>().Object"),
            cancellationFacade ?? new PipelineCancellationFacade(null, null),
            lifecycle ?? new PipelineRunLifecycleService(historyService, runService, logger),
            labelService ?? NoOpLabelService.Instance,
            logger);
    }

    /// <summary>
    /// Returns an <see cref="IPipelineOrchestrationService"/> for tests that only need to verify
    /// cancel interactions and do not require the full concrete <see cref="PipelineOrchestrationService"/>
    /// with its 6-parameter constructor.
    /// <para>
    /// Pass a <c>Mock&lt;IPipelineOrchestrationService&gt;().Object</c> to inject a verifiable mock,
    /// or omit the argument to receive a lightweight no-op implementation.
    /// </para>
    /// </summary>
    /// <param name="orchestrationService">
    /// Optional pre-built implementation. When <c>null</c>, a no-op is returned.
    /// </param>
    public static IPipelineOrchestrationService CreateMinimalInterface(
        IPipelineOrchestrationService? orchestrationService = null)
        => orchestrationService ?? new NoOpOrchestrationService();

    private sealed class NoOpOrchestrationService : IPipelineOrchestrationService
    {
        public Task CancelPipelineAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Creates a <see cref="DispatchRunCreationService"/> with the same lifecycle as a companion
    /// <see cref="PipelineOrchestrationService"/>. Use when a test needs both an orchestration service
    /// and a separate <see cref="IDispatchRunCreator"/>.
    /// </summary>
    public static DispatchRunCreationService CreateMinimalRunCreator(
        IConfigurationStore? configStore = null,
        IProviderFactory? providerFactory = null,
        PipelineRunLifecycleService? lifecycle = null,
        Serilog.ILogger? logger = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null)
    {
        logger ??= Serilog.Log.Logger;
        historyService ??= new NullHistoryService();
        var store = configStore ?? throw new ArgumentNullException(nameof(configStore), "IConfigurationStore is required — use a Mock<IConfigurationStore>().Object");

        return new DispatchRunCreationService(
            lifecycle ?? new PipelineRunLifecycleService(historyService, runService, logger),
            store,
            providerFactory ?? throw new ArgumentNullException(nameof(providerFactory), "IProviderFactory is required — use a Mock<IProviderFactory>().Object"),
            logger);
    }

    /// <summary>No-op label service for tests that don't exercise label operations.</summary>
    public sealed class NoOpLabelService : ILabelService
    {
        public static readonly NoOpLabelService Instance = new();
        public Task SwapLabelAsync(ProviderConfigId providerConfigId, IssueIdentifier identifier, string newLabel, LabelTargetKind targetKind, CancellationToken ct) => Task.CompletedTask;
        public Task SwapLabelAsync(ProviderConfigId providerConfigId, IssueIdentifier identifier, string newLabel, LabelTargetKind targetKind, string? expectedCurrentLabel, CancellationToken ct) => Task.CompletedTask;
        public Task SwapLabelStrictAsync(ProviderConfigId providerConfigId, IssueIdentifier identifier, string newLabel, LabelTargetKind targetKind, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> EnsureAgentLabelsAsync(ProviderConfigId providerConfigId, LabelTargetKind targetKind, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>No-op run history service for tests.</summary>
    public sealed class NullHistoryService : IPipelineRunHistoryService
    {
        private readonly List<PipelineRunSummary> _runs = new();
        public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PipelineRunSummary>>(_runs.AsReadOnly());
        public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var items = _runs.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
            var hasMore = items.Count > pageSize;
            if (hasMore)
                items = items.Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<PipelineRunSummary>
            {
                Items = items.AsReadOnly(),
                Page = page,
                PageSize = pageSize,
                HasMore = hasMore
            });
        }
        public Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
        {
            var runIdStr = runId.ToString();
            return Task.FromResult(_runs.FirstOrDefault(s => s.RunId == runIdStr));
        }
        public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default)
        {
            var source = feedbackOnly ? _runs.Where(s => s.Feedback is not null).ToList() : _runs;
            var items = source.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
            var hasMore = items.Count > pageSize;
            if (hasMore)
                items = items.Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<PipelineRunSummary>
            {
                Items = items.AsReadOnly(),
                Page = page,
                PageSize = pageSize,
                HasMore = hasMore
            });
        }
        public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
        {
            _runs.Add(run.ToSummary());
            return Task.CompletedTask;
        }
        public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory) { }
        public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null) { }
    }
}
