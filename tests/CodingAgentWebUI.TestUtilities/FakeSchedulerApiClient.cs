using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Test double for <see cref="ISchedulerApiClient"/> used by the E2E WebUI test factory.
///
/// Delegates loop control operations to a locally-hosted <see cref="PipelineLoopService"/> singleton
/// (bypassing HTTP). <see cref="GetLoopStatusAsync"/> builds a <see cref="LoopStatusDto"/> from the
/// local singleton's current state.
///
/// <see cref="StartLoopAsync"/> also persists <see cref="PipelineConfiguration.ClosedLoopAutoStart"/>
/// via <see cref="IPipelineApiConfigClient"/> (when provided) to match the real Scheduler endpoint's
/// behaviour — E2E tests that assert on the persisted flag will get the correct state.
///
/// This preserves the "loop must stay hosted in E2E" invariant — see
/// <see cref="E2EWebApplicationFactory"/> comment for why <see cref="PipelineLoopService"/>
/// must remain hosted rather than unhosted.
///
/// Matches the <see cref="FakeKubernetesJobClient"/> pattern used since Spec 041.
/// </summary>
public sealed class FakeSchedulerApiClient : ISchedulerApiClient
{
    private readonly PipelineLoopService _loopService;
    private readonly IPipelineApiConfigClient? _configClient;

    public FakeSchedulerApiClient(PipelineLoopService loopService, IPipelineApiConfigClient? configClient = null)
    {
        ArgumentNullException.ThrowIfNull(loopService);
        _loopService = loopService;
        _configClient = configClient;
    }

    public async Task<LoopStartResultDto> StartLoopAsync(CancellationToken ct = default)
    {
        var started = await _loopService.StartLoopAsync();
        if (started && _configClient is not null)
        {
            // Mirror the real Scheduler endpoint: persist ClosedLoopAutoStart=true so
            // E2E tests that assert on the config value see the correct state.
            await _configClient.UpdatePipelineConfigAsync(
                c => c with { ClosedLoopAutoStart = true }, ct);
        }
        var error = started ? null
            : _loopService.ValidationErrors.Count > 0 ? "Loop failed to start due to validation errors."
            : _loopService.IsLoopActive ? "Loop is already active."
            : "A manual run is in progress.";
        return new LoopStartResultDto(started, error);
    }

    public async Task StopLoopAsync(CancellationToken ct = default)
    {
        _loopService.StopLoop();
        if (_configClient is not null)
        {
            await _configClient.UpdatePipelineConfigAsync(
                c => c with { ClosedLoopAutoStart = false }, ct);
        }
    }

    public Task ResumeLoopAsync(CancellationToken ct = default)
    {
        _loopService.ResumeLoop();
        return Task.CompletedTask;
    }

    public Task<LoopStatusDto> GetLoopStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new LoopStatusDto(
            _loopService.IsLoopActive,
            _loopService.StatusMessage,
            _loopService.CurrentIssueIdentifier,
            _loopService.ProcessedCount,
            _loopService.FailedCount,
            _loopService.QueueCount,
            _loopService.IsCircuitBroken,
            _loopService.LastPollError,
            _loopService.CurrentCycleTemplateIndex,
            _loopService.CurrentCycleTemplateCount,
            _loopService.ValidationErrors,
            _loopService.TemplateStatuses));

    // These are not called by E2E loop-control tests — stubs only.
    public Task<RetentionSweepResultDto> TriggerRetentionSweepAsync(CancellationToken ct = default)
        => Task.FromResult(new RetentionSweepResultDto(0, 0, 0, 0, 0));

    public Task<WorkItemCountDto[]> GetWorkItemCountsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<WorkItemCountDto>());
}
