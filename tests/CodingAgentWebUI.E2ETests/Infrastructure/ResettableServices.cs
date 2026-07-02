using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

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
        IPipelineExecutionFacade executionFacade,
        IPipelineCompletionFacade completionFacade,
        IPipelineCancellationFacade cancellationFacade,
        PipelineRunLifecycleService lifecycle,
        ILabelSwapper labelSwapper,
        ILogger logger)
        : base(configStore, providerFactory, issueParser, executionFacade,
               completionFacade, cancellationFacade, lifecycle, labelSwapper, logger)
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
        _providerManager.Reset();
        _activeConfig = null;
        _activeIssue = null;
        _activeParsedIssue = null;
        _activeIssueComments = null;
    }
}
