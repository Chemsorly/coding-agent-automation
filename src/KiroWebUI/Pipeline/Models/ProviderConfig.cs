namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Configuration for a provider instance (issue, repository, or agent).
/// WARNING: Settings dictionary stores sensitive values (tokens, credentials) as plain text.
/// TODO: Before production use, encrypt sensitive Settings values using ASP.NET Data Protection
/// or a secrets manager. See post-poc-improvements.md for details.
/// </summary>
public sealed class ProviderConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required ProviderKind Kind { get; init; }
    public required string ProviderType { get; init; }  // Matches enum value name: "GitHub", "KiroCli"
    public required string DisplayName { get; init; }
    public Dictionary<string, string> Settings { get; init; } = new();
}
