using MessagePack;

namespace CodingAgent.Pipeline.Models;

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
    /// Not stored on the connection. Filled from the repository's pipeline job template when a job is prepared.
    /// Null means fall back to <see cref="PipelineConfiguration.BlacklistedPaths"/> global default.
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
    public required string ProviderType { get; init; }  // See ProviderTypes for valid values

    /// <summary>
    /// Role of this repository provider. Only meaningful when Kind == Repository.
    /// Defaults to Work for backward compatibility with existing configurations.
    /// </summary>
    [Key(5)]
    public RepositoryRole RepositoryRole { get; init; } = RepositoryRole.Work;

    /// <summary>
    /// Not stored on the connection. Filled from the repository's pipeline job template (its stack labels) when a job is
    /// prepared.
    /// </summary>
    [Key(6)]
    public IReadOnlyList<string>? RequiredLabels { get; init; }

    /// <summary>
    /// Not stored on the connection. Filled from the repository's pipeline job template when a job is prepared.
    /// Keys are variable names, values are plaintext.
    /// </summary>
    [Key(7)]
    public Dictionary<string, string>? Secrets { get; init; }

    [Key(8)]
    public Dictionary<string, string> Settings { get; init; } = new();

    /// <summary>
    /// Not stored on the connection. Filled from the repository's pipeline job template when a job is prepared.
    /// Commands can reference Secrets via standard shell variable expansion ($KEY).
    /// </summary>
    [Key(9)]
    public IReadOnlyList<SetupStep>? SetupSteps { get; init; }

    /// <summary>
    /// Not stored on the connection. Filled from the repository's pipeline job template when a job is prepared.
    /// </summary>
    [Key(10)]
    public string? SteeringContent { get; init; }
}
