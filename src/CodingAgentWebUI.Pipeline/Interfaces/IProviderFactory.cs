using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IProviderFactory
{
    IIssueProvider CreateIssueProvider(ProviderConfig config);
    IRepositoryProvider CreateRepositoryProvider(ProviderConfig config);
    IAgentProvider CreateAgentProvider(ProviderConfig config);
    IPipelineProvider CreatePipelineProvider(ProviderConfig config);
}
