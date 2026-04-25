using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public interface IProviderFactory
{
    IIssueProvider CreateIssueProvider(ProviderConfig config);
    IRepositoryProvider CreateRepositoryProvider(ProviderConfig config);
    IAgentProvider CreateAgentProvider(ProviderConfig config);
    IPipelineProvider CreatePipelineProvider(ProviderConfig config);
}
