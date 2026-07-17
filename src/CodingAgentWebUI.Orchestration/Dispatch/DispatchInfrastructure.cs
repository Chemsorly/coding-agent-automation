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
    public ILabelSwapper LabelSwapper { get; }
    public DispatchResolutionService Resolution { get; }
    public ProviderConfigPreparationService ProviderConfigPreparation { get; }

    public DispatchInfrastructure(
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelSwapper labelSwapper,
        DispatchResolutionService resolution,
        ProviderConfigPreparationService providerConfigPreparation)
    {
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(providerConfigPreparation);

        TokenVending = tokenVending;
        ProviderFactory = providerFactory;
        LabelSwapper = labelSwapper;
        Resolution = resolution;
        ProviderConfigPreparation = providerConfigPreparation;
    }
}
