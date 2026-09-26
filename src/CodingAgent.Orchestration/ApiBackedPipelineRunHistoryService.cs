using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Polly;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.Client.Stores;

/// <summary>
/// <see cref="IPipelineRunHistoryService"/> implementation for the orchestrator process that routes
/// all persistence and reads through the Pipeline API instead of accessing Postgres directly.
/// This removes the last Postgres dependency from the orchestrator host (T8 item 2).
/// </summary>
public sealed class ApiBackedPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly IPipelineApiRunHistoryClient _client;
    private readonly ILogger _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public ApiBackedPipelineRunHistoryService(IPipelineApiRunHistoryClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _retryPipeline = ResiliencePipelineFactory.CreateHttpPipeline(logger);
    }

    /// <inheritdoc />
    public async Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Defense-in-depth: skip consolidation runs (same guard as PostgresPipelineRunHistoryService).
        if (run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
        {
            _logger.Debug("ApiBackedPipelineRunHistoryService: skipping consolidation run {RunId}", run.RunId);
            return;
        }

        PipelineStep? finalStepOverride = null;
        if (!run.CurrentStep.IsTerminal())
        {
            _logger.Warning(
                "ApiBackedPipelineRunHistoryService: run {RunId} has non-terminal step={Step}, forcing to Failed",
                run.RunId, run.CurrentStep);
            finalStepOverride = PipelineStep.Failed;
        }

        var summary = run.ToSummary(finalStepOverride);
        await AddRunSummaryAsync(summary, ct);
    }

    /// <inheritdoc />
    public async Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // Defense-in-depth: reject consolidation summaries before forwarding to the API.
        // Mirrors the guard in AddRunToHistoryAsync(PipelineRun). Uses the InitiatedBy prefix
        // because PipelineRunSummary has no IssueProviderConfigId property.
        if (summary.InitiatedBy?.StartsWith(ConsolidationConstants.InitiatedByPrefix, StringComparison.Ordinal) == true)
        {
            _logger.Debug("ApiBackedPipelineRunHistoryService: skipping consolidation summary {RunId}", summary.RunId);
            return;
        }

        try
        {
            // Retry transient HTTP failures (5xx, timeout, network) before giving up.
            // TODO: CreateHttpPipeline's ShouldHandle predicate retries ALL HttpRequestException without
            // filtering by HTTP status code, so 4xx responses are also retried up to 3 times. The issue
            // requirement states "Permanent failures (4xx, deserialization) must not be retried." The note
            // about idempotency-key safety is also inaccurate — PipelineRunEndpoints does not read the
            // X-Idempotency-Key header; idempotency is enforced via an upsert at the database layer.
            // To fully satisfy the requirement, ResiliencePipelineFactory.CreateHttpPipeline (or a new
            // variant) should filter HttpRequestException by StatusCode, retrying only when StatusCode is
            // null (network/timeout) or >= 500. See issue #3038 and DotNetSpecialist/Correctness review.
            await _retryPipeline.ExecuteAsync(
                async token => await _client.AddRunToHistoryAsync(summary, token),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal — same contract as PostgresPipelineRunHistoryService.AddRunToHistoryAsync.
            // Reached only after the retry budget is exhausted (for HttpRequestException) or
            // immediately for exception types not in the retry predicate (e.g. InvalidOperationException).
            _logger.Warning(ex, "ApiBackedPipelineRunHistoryService: failed to persist run {RunId} via API", summary.RunId);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
    {
        var result = await _client.GetRunHistoryAsync(page: 1, pageSize: 1000, ct: ct);
        return result.Items;
    }

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
        => await _client.GetRunHistoryAsync(page: page, pageSize: pageSize, ct: ct);

    /// <inheritdoc />
    public async Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default)
        => await _client.GetRunHistoryAsync(page: page, pageSize: pageSize, feedbackOnly: feedbackOnly, ct: ct);

    /// <inheritdoc />
    public async Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
        => await _client.GetRunAsync(runId, ct);

    /// <inheritdoc />
    public void TryDeleteWorkspace(WorkspacePath? workspacePath, string runId, string workspaceBaseDirectory)
    {
        // The orchestrator has no local workspace — no-op.
        // The API host (CodingAgent.Api) owns workspace cleanup in K8s mode.
    }

    /// <inheritdoc />
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null)
    {
        // No-op — same reasoning as TryDeleteWorkspace.
    }
}
