using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Test subclass of <see cref="AgentRegistryService"/> that exposes state reset
/// for test isolation without polluting the production codebase.
/// </summary>
public sealed class ResettableAgentRegistryService : AgentRegistryService
{
    public ResettableAgentRegistryService(ILogger logger) : base(logger) { }

    public void Reset() => _agents.Clear();
}

/// <summary>
/// Test subclass of <see cref="JobDispatcherService"/> that exposes state reset
/// for test isolation without polluting the production codebase.
/// </summary>
public sealed class ResettableJobDispatcherService : JobDispatcherService
{
    public ResettableJobDispatcherService(AgentRegistryService registry, ILogger logger)
        : base(registry, logger) { }

    public void Reset()
    {
        lock (_queueLock)
        {
            while (_jobQueue.TryDequeue(out _)) { }
        }
        _processingIssues.Clear();
    }
}

/// <summary>
/// Test subclass of <see cref="OrchestratorRunService"/> that exposes state reset
/// for test isolation without polluting the production codebase.
/// </summary>
public sealed class ResettableOrchestratorRunService : OrchestratorRunService
{
    public ResettableOrchestratorRunService(ILogger logger, int defaultBufferCapacity = PipelineConstants.DefaultOutputBufferCapacity)
        : base(logger, defaultBufferCapacity) { }

    public void Reset()
    {
        _activeRuns.Clear();
        _outputBuffers.Clear();
    }
}

/// <summary>
/// Test subclass of <see cref="PipelineOrchestrationService"/> that exposes state reset
/// for test isolation without polluting the production codebase.
/// </summary>
public sealed class ResettablePipelineOrchestrationService : PipelineOrchestrationService
{
    private readonly PipelineRunLifecycleService? _lifecycleForReset;

    public ResettablePipelineOrchestrationService(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null,
        PipelineRunLifecycleService? lifecycle = null)
        : base(configStore, providerFactory, issueParser, agentExecution,
               qualityGates, logger, brainUpdateService, historyService, runService, lifecycle)
    {
        _lifecycleForReset = lifecycle;
    }

    public void Reset()
    {
        // Lifecycle state reset (CTS, ActiveRun, events) is now on the lifecycle service
        if (_lifecycleForReset != null)
        {
            _lifecycleForReset.CancellationTokenSource?.Cancel();
            _lifecycleForReset.CancellationTokenSource?.Dispose();
            _lifecycleForReset.ActiveRun = null;
        }

        // Provider field resets remain on orchestration
        _activeAgentProvider = null;
        _activeRepoProvider = null;
        _activeBrainProvider = null;
        _activeIssueProvider = null;
        _activePipelineProvider = null;
        _activeConfig = null;
        _activeIssue = null;
        _activeParsedIssue = null;
        _activeIssueComments = null;
    }
}
