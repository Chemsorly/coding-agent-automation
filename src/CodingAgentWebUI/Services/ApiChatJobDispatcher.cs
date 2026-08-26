using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// <see cref="IChatJobDispatcher"/> implementation for the Blazor monolith.
///
/// <para>
/// The real <see cref="CodingAgentWebUI.Hub.ChatJobDispatcher"/> lives in the Pipeline API
/// process alongside <c>AgentHub</c> and the registry it polls. This class is a thin HTTP
/// bridge: it calls <c>POST /api/chat/dispatch</c> and <c>POST /api/chat/{agentId}/terminate</c>,
/// then re-maps API status codes back to the domain exception types that
/// <c>AgentChat.razor.ClassifyLaunchError</c> expects.
/// </para>
/// </summary>
internal sealed class ApiChatJobDispatcher(IPipelineApiChatClient chatClient) : IChatJobDispatcher
{
    // The connect timeout shown in the "did not connect within Xs" error message is owned by the
    // API. We don't know the exact value here, so report -1 to signal "unknown" — the UI falls
    // back to ex.Message which already contains the API's description.
    private const int UnknownTimeoutSeconds = -1;

    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
    {
        try
        {
            return await chatClient.DispatchChatPodAsync(agentSelector, model, effort, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 — a chat session is already active for this agent
            _ = ex;
            throw new ChatAlreadyActiveException("unknown");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            // 503 — no credential PVC available
            _ = ex;
            throw new NoPvcAvailableException();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
        {
            // 504 — pod did not connect within the API's timeout
            _ = ex;
            throw new ChatPodTimeoutException(UnknownTimeoutSeconds);
        }
    }

    public async Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        await chatClient.TerminateChatSessionAsync(agentId.Value, cancellationToken);
    }
}
