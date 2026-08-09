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
}
