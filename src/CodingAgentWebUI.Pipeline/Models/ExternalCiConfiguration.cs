namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for external CI integration.
/// </summary>
public sealed record ExternalCiConfiguration
{
    public bool ExternalCiEnabled { get; init; } = false;
    public TimeSpan ExternalCiTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExternalCiPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}
