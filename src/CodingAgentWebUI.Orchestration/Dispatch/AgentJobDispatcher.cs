using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Implements <see cref="IJobDispatcher"/> by coordinating between
/// <see cref="JobDispatcherService"/>, <see cref="AgentRegistryService"/>,
/// <see cref="PipelineOrchestrationService"/>, and the <c>AgentHub</c>
/// to dispatch pipeline jobs to remote agents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Circular Dependency Risk:</b> This class depends on <see cref="PipelineOrchestrationService"/>
/// (concrete) to create dispatched runs. Meanwhile, <c>PipelineLoopService</c> (in the Pipeline project)
/// depends on <see cref="IJobDispatcher"/> (this class's interface) to dispatch issues discovered
/// during polling. This creates a potential circular dependency chain:
/// </para>
/// <para>
/// <c>AgentJobDispatcher</c> → <c>PipelineOrchestrationService</c> → (via <c>PipelineLoopService</c>) → <c>IJobDispatcher</c>
/// </para>
/// <para>
/// The cycle is currently mitigated by interface segregation: <c>PipelineLoopService</c> depends on
/// the <c>IJobDispatcher</c> interface (not the concrete <c>AgentJobDispatcher</c>), and accepts it
/// as an optional nullable parameter. The DI container resolves this without circular instantiation
/// because <c>PipelineOrchestrationService</c> does not directly depend on <c>IJobDispatcher</c> —
/// only <c>PipelineLoopService</c> does, and it is a separate service registration.
/// </para>
/// <para>
/// <b>Do not:</b> Add a direct dependency from <c>PipelineOrchestrationService</c> to
/// <c>IJobDispatcher</c> or <c>AgentJobDispatcher</c>, as this would create an unresolvable
/// circular DI registration.
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
        bool ForceRefreshAnalysis);
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Pipeline.Services.PipelineOrchestrationService _orchestration;
    private readonly ITokenVendingService _tokenVending;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelSwapper _labelSwapper;
    private readonly DispatchResolutionService _resolution;
    private readonly IAgentCommunication _agentComm;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly ILogger _logger;

    public AgentJobDispatcher(
        JobDispatcherService dispatcher,
        AgentRegistryService registry,
        OrchestratorRunService runService,
        Pipeline.Services.PipelineOrchestrationService orchestration,
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelSwapper labelSwapper,
        DispatchResolutionService resolution,
        IAgentCommunication agentComm,
        IShutdownSignal shutdownSignal,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(shutdownSignal);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _runService = runService;
        _orchestration = orchestration;
        _tokenVending = tokenVending;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper;
        _resolution = resolution;
        _agentComm = agentComm;
        _shutdownSignal = shutdownSignal;
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
