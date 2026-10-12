using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The agent list of the Fleet and Overview pages (spec 051 2.5a). A global reader gets every agent; a
/// project-scoped user gets the agents running work of their projects. The credential pool needs global read.
/// </summary>
public interface IUiAgentClient
{
    Task<IReadOnlyList<AgentEntryDto>> GetAgentsAsync(CancellationToken ct = default);

    Task<CredentialPoolStatus> GetCredentialPoolAsync(CancellationToken ct = default);
}

public sealed class UiAgentClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiAgentClient
{
    public Task<IReadOnlyList<AgentEntryDto>> GetAgentsAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/agents", UiJsonContext.Default.IReadOnlyListAgentEntryDto, ct);

    public Task<CredentialPoolStatus> GetCredentialPoolAsync(CancellationToken ct = default) =>
        GetAsync("/api/ui/agents/credential-pool", UiJsonContext.Default.CredentialPoolStatus, ct);
}
