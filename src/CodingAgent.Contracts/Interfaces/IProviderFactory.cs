using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

public interface IProviderFactory
{
    IIssueProvider CreateIssueProvider(ProviderConfig config);
    IRepositoryProvider CreateRepositoryProvider(ProviderConfig config);
    IAgentProvider CreateAgentProvider(ProviderConfig config);
    Task<IPipelineProvider> CreatePipelineProviderAsync(ProviderConfig config, CancellationToken ct);
}
