using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Groups the optional parameters for <see cref="TestOrchestrationFactory.CreateMinimal"/>
/// to reduce method parameter count (S107). All members are optional.
/// </summary>
public sealed record CreateMinimalOptions
{
    public IConfigurationStore? ConfigStore { get; init; }
    public IProviderFactory? ProviderFactory { get; init; }
    public IPipelineCancellationFacade? CancellationFacade { get; init; }
    public PipelineRunLifecycleService? Lifecycle { get; init; }
    public ILabelService? LabelService { get; init; }
    public Serilog.ILogger? Logger { get; init; }
    public IPipelineRunHistoryService? HistoryService { get; init; }
    public IOrchestratorRunService? RunService { get; init; }

    /// <summary>
    /// Optional pre-built <see cref="IPipelineOrchestrationService"/> implementation (e.g. a mock).
    /// When set, <see cref="TestOrchestrationFactory.CreateMinimal(CreateMinimalOptions)"/> returns this
    /// instance directly instead of constructing a <see cref="PipelineOrchestrationService"/> — allowing
    /// tests to inject <c>Mock&lt;IPipelineOrchestrationService&gt;</c> without the 6-parameter constructor.
    /// Not used by the named-parameter <see cref="TestOrchestrationFactory.CreateMinimal(IConfigurationStore,IProviderFactory,IPipelineCancellationFacade,PipelineRunLifecycleService,ILabelService,Serilog.ILogger,IPipelineRunHistoryService,IOrchestratorRunService)"/> overload.
    /// </summary>
    public IPipelineOrchestrationService? OrchestrationService { get; init; }
}
