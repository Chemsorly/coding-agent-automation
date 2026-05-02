using CodingAgentWebUI.Pipeline.Interfaces;
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
    public ResettableOrchestratorRunService(ILogger logger, int defaultBufferCapacity = 10_000)
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
    public ResettablePipelineOrchestrationService(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IQualityGateValidator qualityGateValidator,
        CiLogWriter ciLogWriter,
        ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null)
        : base(configStore, configStore, configStore, configStore,
               providerFactory, issueParser, qualityGateValidator,
               ciLogWriter, logger, brainUpdateService, historyService, runService) { }

    public void Reset()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        ActiveRun = null;
        _activeAgentProvider = null;
        _activeRepoProvider = null;
        _activeBrainProvider = null;
        _activeIssueProvider = null;
        _activePipelineProvider = null;
        _activeConfig = null;
        _activeIssue = null;
        _activeParsedIssue = null;
        _activeIssueComments = null;
        ClearEventSubscribers();
    }
}
