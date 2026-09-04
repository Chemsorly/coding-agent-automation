using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for a provider instance (issue, repository, or agent).
/// WARNING: Settings dictionary stores sensitive values (tokens, credentials) as plain text.
/// NOTE: Before production use, encrypt sensitive Settings values using ASP.NET Data Protection
/// or a secrets manager. See post-poc-improvements.md for details.
/// </summary>
[MessagePackObject]
public sealed class ProviderConfig
{
    /// <summary>
    /// Path prefixes excluded from agent commits for this repository.
    /// Only meaningful when Kind == Repository. Null means fall back to
    /// <see cref="PipelineConfiguration.BlacklistedPaths"/> global default.
    /// </summary>
    [Key(0)]
    public IReadOnlyList<string>? BlacklistedPaths { get; init; }

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(3)]
    public required ProviderKind Kind { get; init; }

    [Key(4)]
    public required string ProviderType { get; init; }  // Matches enum value name: "GitHub", "KiroCli"

    /// <summary>
    /// Role of this repository provider. Only meaningful when Kind == Repository.
    /// Defaults to Work for backward compatibility with existing configurations.
    /// </summary>
    [Key(5)]
    public RepositoryRole RepositoryRole { get; init; } = RepositoryRole.Work;

    /// <summary>
    /// Explicit required agent labels for this provider config. When set, takes precedence
    /// in label resolution. Null or empty means fall back to
    /// <see cref="Models.PipelineConfiguration.DefaultRequiredAgentLabels"/>, then empty (any agent).
    /// </summary>
    [Key(6)]
    public IReadOnlyList<string>? RequiredLabels { get; init; }

    /// <summary>
    /// Environment secrets injected into setup step processes as environment variables.
    /// Only meaningful when Kind == Repository and RepositoryRole == Work.
    /// Keys are variable names, values are plaintext.
    /// </summary>
    [Key(7)]
    public Dictionary<string, string>? Secrets { get; init; }

    [Key(8)]
    public Dictionary<string, string> Settings { get; init; } = new();

    /// <summary>
    /// Shell commands executed sequentially after clone, before the agent starts.
    /// Only meaningful when Kind == Repository and RepositoryRole == Work.
    /// Commands can reference Secrets via standard shell variable expansion ($KEY).
    /// </summary>
    [Key(9)]
    public IReadOnlyList<SetupStep>? SetupSteps { get; init; }

    /// <summary>
    /// Optional markdown steering content written to the agent workspace before each run.
    /// Only meaningful when Kind == Repository.
    /// </summary>
    [Key(10)]
    public string? SteeringContent { get; init; }
}
