using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Test factory for pipeline services with sensible defaults.
/// Reduces boilerplate across test files that need a minimal run creator or no-op collaborators.
/// </summary>
public static class TestOrchestrationFactory
{
    /// <summary>
    /// Creates a <see cref="DispatchRunCreationService"/> with no-op/null defaults.
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
        public Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default)
        {
            _runs.Add(summary);
            return Task.CompletedTask;
        }
    }
}
