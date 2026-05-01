using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

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
    public IPipelineProvider CreatePipelineProvider(ProviderConfig config) => PipelineProvider;
}
