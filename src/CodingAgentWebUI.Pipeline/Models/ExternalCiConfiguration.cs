namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for external CI integration (e.g., GitHub Actions).
/// </summary>
public sealed record ExternalCiConfiguration
{
    public bool Enabled { get; init; } = false;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
}
