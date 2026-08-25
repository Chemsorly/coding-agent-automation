using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// E2E test double for <see cref="ILoopStatusService"/> that delegates to a locally-hosted
/// <see cref="PipelineLoopService"/> singleton.
///
/// Used in <see cref="E2EWebApplicationFactory"/> so that MainLayout and AgentCoding receive
/// real loop state without a live Scheduler process.
///
/// Implements <see cref="IDisposable"/> to unsubscribe the <see cref="PipelineLoopService.OnChange"/>
/// delegate on disposal, preventing stale lambdas from accumulating across <c>ResetAll()</c>
/// cycles in the same test assembly.
/// </summary>
internal sealed class FakeLoopStatusService : ILoopStatusService, IDisposable
{
    private readonly PipelineLoopService _loopService;
    private readonly Action _onChangeBridge;

    public FakeLoopStatusService(PipelineLoopService loopService)
    {
        ArgumentNullException.ThrowIfNull(loopService);
        _loopService = loopService;
        // Store the delegate so we can unsubscribe in Dispose()
        _onChangeBridge = () => OnChange?.Invoke();
        _loopService.OnChange += _onChangeBridge;
    }

    public void Dispose()
    {
        _loopService.OnChange -= _onChangeBridge;
    }

    public event Action? OnChange;

    public bool IsLoopActive => _loopService.IsLoopActive;
    public string StatusMessage => _loopService.StatusMessage;
    public string? CurrentIssueIdentifier => _loopService.CurrentIssueIdentifier;
    public int ProcessedCount => _loopService.ProcessedCount;
    public int FailedCount => _loopService.FailedCount;
    public int QueueCount => _loopService.QueueCount;
    public bool IsCircuitBroken => _loopService.IsCircuitBroken;
    public string? LastPollError => _loopService.LastPollError;
    public int CurrentCycleTemplateIndex => _loopService.CurrentCycleTemplateIndex;
    public int CurrentCycleTemplateCount => _loopService.CurrentCycleTemplateCount;
    public IReadOnlyList<string> ValidationErrors => _loopService.ValidationErrors;
    public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses => _loopService.TemplateStatuses;

    /// <summary>Always false — Scheduler connectivity is not a concern in the E2E harness.</summary>
    public bool IsSchedulerUnreachable => false;
}
