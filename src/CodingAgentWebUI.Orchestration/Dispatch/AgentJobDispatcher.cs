using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Coordinates between <see cref="JobDispatcherService"/>, <see cref="AgentRegistryService"/>,
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
    /// <summary>
    /// Holds the pre-fetched issue context needed to build a <see cref="JobAssignmentMessage"/>.
    /// </summary>
    private sealed record IssueContext(
        IssueDetail IssueDetail,
        ParsedIssue ParsedIssue,
        IReadOnlyList<IssueComment> IssueComments,
        string? ExistingAnalysis,
        bool ForceRefreshAnalysis,
        string? StalenessSignal = null,
        int RefreshCount = 0);
    private readonly JobDispatcherService _dispatcher;
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IDispatchRunCreator _orchestration;
    private readonly DispatchInfrastructure _infra;
    private readonly IAgentCommunication _agentComm;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly ILogger _logger;

    public AgentJobDispatcher(
        JobDispatcherService dispatcher,
        IAgentRegistryService registry,
        IOrchestratorRunService runService,
        IDispatchRunCreator orchestration,
        DispatchInfrastructure infra,
        IAgentCommunication agentComm,
        IShutdownSignal shutdownSignal,
        ILogger logger,
        IRunLifecycleManager? lifecycleManager = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(infra);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(shutdownSignal);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _runService = runService;
        _orchestration = orchestration;
        _infra = infra;
        _agentComm = agentComm;
        _shutdownSignal = shutdownSignal;
        _lifecycleManager = lifecycleManager;
        _logger = logger;
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
