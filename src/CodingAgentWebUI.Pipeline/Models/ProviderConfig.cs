namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for a provider instance (issue, repository, or agent).
/// WARNING: Settings dictionary stores sensitive values (tokens, credentials) as plain text.
/// NOTE: Before production use, encrypt sensitive Settings values using ASP.NET Data Protection
/// or a secrets manager. See post-poc-improvements.md for details.
/// </summary>
public sealed class ProviderConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required ProviderKind Kind { get; init; }
    public required string ProviderType { get; init; }  // Matches enum value name: "GitHub", "KiroCli"
    public required string DisplayName { get; init; }
    public Dictionary<string, string> Settings { get; init; } = new();

    /// <summary>
    /// Role of this repository provider. Only meaningful when Kind == Repository.
    /// Defaults to Work for backward compatibility with existing configurations.
    /// </summary>
    public RepositoryRole RepositoryRole { get; init; } = RepositoryRole.Work;

    /// <summary>
    /// Explicit required agent labels for this provider config. When set, takes precedence
    /// over the <c>requiredAgentLabels</c> entry in the <see cref="Settings"/> dictionary.
    /// Null means fall back to Settings dictionary lookup.
    /// </summary>
    public IReadOnlyList<string>? RequiredLabels { get; init; }

    /// <summary>
    /// Path prefixes excluded from agent commits for this repository.
    /// Only meaningful when Kind == Repository. Null means fall back to
    /// <see cref="PipelineConfiguration.BlacklistedPaths"/> global default.
    /// </summary>
    public IReadOnlyList<string>? BlacklistedPaths { get; init; }

    /// <summary>
    /// Blacklist enforcement mode for this repository.
    /// Only meaningful when Kind == Repository. Null means fall back to
    /// <see cref="PipelineConfiguration.BlacklistMode"/> global default.
    /// </summary>
    public BlacklistMode? BlacklistMode { get; init; }
}
