using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum AgentProviderType { KiroCli }

public interface IAgentProvider
{
    AgentProviderType ProviderType { get; }
    AgentHealthStatus GetHealthStatus();
    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null);
    Task<AgentResult> ExecuteWithResumeAsync(string instruction, string workspacePath, TimeSpan timeout, CancellationToken ct, Action<string>? onOutputLine = null);
}
