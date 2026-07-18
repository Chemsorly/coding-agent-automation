using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Aggregate that bundles shared dispatch-path dependencies used by both
/// <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>.
/// Reduces constructor parameter count by grouping services that always travel together:
/// provider config building, profile resolution, token vending, and label operations.
/// <para>
/// Registered as a singleton in DI. Consumers access individual services via properties.
/// </para>
/// </summary>
public sealed class DispatchInfrastructure
{
    public ITokenVendingService TokenVending { get; }
    public IProviderFactory ProviderFactory { get; }
    public ILabelService LabelService { get; }
    public DispatchResolutionService Resolution { get; }

    public DispatchInfrastructure(
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelService labelService,
        DispatchResolutionService resolution)
    {
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(resolution);

        TokenVending = tokenVending;
        ProviderFactory = providerFactory;
        LabelService = labelService;
        Resolution = resolution;
    }
}
