namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for health/readiness endpoints.
/// </summary>
public interface IPipelineApiHealthClient
{
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
    Task<bool> IsReadyAsync(CancellationToken ct = default);
}
