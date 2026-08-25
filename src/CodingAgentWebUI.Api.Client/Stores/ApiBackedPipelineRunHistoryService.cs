using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Api.Client.Stores;

/// <summary>
/// <see cref="IPipelineRunHistoryService"/> implementation for the orchestrator process that routes
/// all persistence and reads through the Pipeline API instead of accessing Postgres directly.
/// This removes the last Postgres dependency from the orchestrator host (T8 item 2).
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP client wrapper — requires integration tests, not unit tests.")]
public sealed class ApiBackedPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly IPipelineApiRunHistoryClient _client;
    private readonly ILogger _logger;

    public ApiBackedPipelineRunHistoryService(IPipelineApiRunHistoryClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
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
        try
        {
            await _client.AddRunToHistoryAsync(summary, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal — same contract as PostgresPipelineRunHistoryService.AddRunToHistoryAsync
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
    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory)
    {
        // The orchestrator has no local workspace — no-op.
        // The API host (CodingAgentWebUI.Api) owns workspace cleanup in K8s mode.
    }

    /// <inheritdoc />
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null)
    {
        // No-op — same reasoning as TryDeleteWorkspace.
    }
}
