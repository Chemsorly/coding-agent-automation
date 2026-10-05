using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// Provider factory that returns shared fake instances.
/// Each Create* method returns the same fake (fakes have no-op DisposeAsync).
///
/// <para>
/// <see cref="CreateIssueProvider"/> returns <see cref="IssueProvider"/> for most config IDs, but
/// returns <see cref="SecondaryIssueProvider"/> when the config ID is
/// <see cref="SecondaryIssueProviderConfigId"/>. Use this to give two different templates distinct
/// issue provider instances so that per-provider test scenarios (e.g. project-ordering tests that
/// need issues only reachable via a specific template) are discriminating.
/// </para>
/// </summary>
public sealed class FakeProviderFactory : IProviderFactory
{
    /// <summary>
    /// The provider config ID that causes <see cref="CreateIssueProvider"/> to return
    /// <see cref="SecondaryIssueProvider"/> instead of <see cref="IssueProvider"/>.
    /// </summary>
    public const string SecondaryIssueProviderConfigId = "issue-e2e-2";

    /// <summary>
    /// The provider configs the E2E hosts seed on top of the shared config-store defaults:
    /// the config for <see cref="SecondaryIssueProvider"/>.
    /// </summary>
    public static IEnumerable<ProviderConfig> ExtraProviderConfigs() =>
    [
        new ProviderConfig { Id = SecondaryIssueProviderConfigId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "E2E Secondary Issue Provider" },
    ];

    public InMemoryIssueProvider IssueProvider { get; } = new();

    /// <summary>
    /// A second issue provider returned when the config ID is <see cref="SecondaryIssueProviderConfigId"/>.
    /// Allows tests to seed distinct issue sets per template without requiring per-template
    /// provider factory instances.
    /// </summary>
    public InMemoryIssueProvider SecondaryIssueProvider { get; } = new();

    public InMemoryRepositoryProvider RepositoryProvider { get; } = new();
    public ScriptedAgentProvider AgentProvider { get; } = new();
    public InMemoryPipelineProvider PipelineProvider { get; } = new();

    public void Reset()
    {
        IssueProvider.Reset();
        SecondaryIssueProvider.Reset();
        RepositoryProvider.Reset();
        AgentProvider.Reset();
        PipelineProvider.Reset();
    }

    public IIssueProvider CreateIssueProvider(ProviderConfig config) =>
        config.Id == SecondaryIssueProviderConfigId ? SecondaryIssueProvider : IssueProvider;

    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config) => RepositoryProvider;
    public IAgentProvider CreateAgentProvider(ProviderConfig config) => AgentProvider;
    public Task<IPipelineProvider> CreatePipelineProviderAsync(ProviderConfig config, CancellationToken ct) => Task.FromResult<IPipelineProvider>(PipelineProvider);
}
