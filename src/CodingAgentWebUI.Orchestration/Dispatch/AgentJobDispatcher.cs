using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Coordinates between <see cref="JobDeduplicationGuardService"/>, <see cref="AgentRegistryService"/>,
/// <see cref="IDispatchRunCreator"/>, and the <c>AgentHub</c>
/// to dispatch pipeline jobs to remote agents.
/// </summary>
/// <remarks>
/// <para>
/// This class is <c>internal</c> — consumed only by <see cref="LegacyWorkDistributor"/>
/// (same assembly) and <see cref="JobQueueDrainService"/> (same assembly).
/// It is NOT directly injectable from DI; external code uses <see cref="IWorkDistributor"/>.
/// </para>
/// </remarks>
public sealed partial class AgentJobDispatcher : IJobDispatcher
{
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IDispatchRunCreator _orchestration;
    private readonly DispatchInfrastructure _infra;
    private readonly IAgentCommunication _agentComm;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly ILogger _logger;

    public AgentJobDispatcher(
        AgentJobDispatcherDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Dispatcher);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.Orchestration);
        ArgumentNullException.ThrowIfNull(deps.Infra);
        ArgumentNullException.ThrowIfNull(deps.AgentComm);
        ArgumentNullException.ThrowIfNull(deps.ShutdownSignal);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _dispatcher = deps.Dispatcher;
        _registry = deps.Registry;
        _runService = deps.RunService;
        _orchestration = deps.Orchestration;
        _infra = deps.Infra;
        _agentComm = deps.AgentComm;
        _shutdownSignal = deps.ShutdownSignal;
        _lifecycleManager = deps.LifecycleManager;
        _logger = deps.Logger;
    }

    /// <inheritdoc />
    public bool HasRegisteredAgents => _registry.GetAllAgents().Count > 0;

    /// <inheritdoc />
    public bool IsIssueBeingProcessedOrQueued(string issueIdentifier, string issueProviderConfigId)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        return _dispatcher.IsIssueQueued(issueIdentifier, issueProviderConfigId)
            || _runService.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId);
    }
}
