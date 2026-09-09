using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// Provider factory that returns shared fake instances.
/// Each Create* method returns the same fake (fakes have no-op DisposeAsync).
/// </summary>
public sealed class FakeProviderFactory : IProviderFactory
{
    public InMemoryIssueProvider IssueProvider { get; } = new();
    public InMemoryRepositoryProvider RepositoryProvider { get; } = new();
    public ScriptedAgentProvider AgentProvider { get; } = new();
    public InMemoryPipelineProvider PipelineProvider { get; } = new();

    public void Reset()
    {
        IssueProvider.Reset();
        RepositoryProvider.Reset();
        AgentProvider.Reset();
        PipelineProvider.Reset();
    }

    public IIssueProvider CreateIssueProvider(ProviderConfig config) => IssueProvider;
    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config) => RepositoryProvider;
    public IAgentProvider CreateAgentProvider(ProviderConfig config) => AgentProvider;
    public Task<IPipelineProvider> CreatePipelineProviderAsync(ProviderConfig config, CancellationToken ct) => Task.FromResult<IPipelineProvider>(PipelineProvider);
}
