using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 9 constructor parameters of <see cref="Services.PipelineLoopService"/>
/// into a single parameter object to satisfy S107. Registered as singleton in DI.
/// </summary>
public sealed record PipelineLoopServiceDependencies
{
    public required IDispatchRunCreator Orchestration { get; init; }
    public required IProviderFactory ProviderFactory { get; init; }
    public required IPipelineConfigStore PipelineConfigStore { get; init; }
    public required IProviderConfigStore ProviderConfigStore { get; init; }
    public required IProjectStore ProjectStore { get; init; }
    public required Serilog.ILogger Logger { get; init; }
    public required IWorkDistributor? WorkDistributor { get; init; }
    public required IDispatchOrchestrationService? DispatchOrchestration { get; init; }
    public required IDependencyChecker? DependencyChecker { get; init; }
    public required IAutoUpdatePrBranchService? AutoUpdateService { get; init; }
}
