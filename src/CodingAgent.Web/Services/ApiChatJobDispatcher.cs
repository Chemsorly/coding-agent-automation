using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// <see cref="IChatJobDispatcher"/> implementation for the Blazor monolith.
///
/// <para>
/// The real <see cref="CodingAgent.AgentGateway.ChatJobDispatcher"/> lives in the Pipeline API
/// process alongside <c>AgentHub</c> and the registry it polls. This class is a thin HTTP
/// bridge: it calls <c>POST /api/chat/dispatch</c> and <c>POST /api/chat/{agentId}/terminate</c>,
/// then re-maps API status codes back to the domain exception types that
/// <c>AgentChat.razor.ClassifyLaunchError</c> expects.
/// </para>
/// </summary>
internal sealed class ApiChatJobDispatcher(IPipelineApiChatClient chatClient) : IChatJobDispatcher
{
    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
    {
        try
        {
            return await chatClient.DispatchChatPodAsync(agentSelector, model, effort, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            // 503 — no credential PVC available
            // TODO [WARNING]: The API's error detail (carried by ChatDispatchFailedException.Message when ex is a
            // ChatDispatchFailedException) is discarded here. NoPvcAvailableException takes no message, so the UI
            // always shows the fixed string "No agent credentials available." rather than the API's reason. This
            // is an asymmetry with the 504/500/400 paths that do propagate the API's detail. If NoPvcAvailableException
            // is ever extended to carry a message, pass (ex as ChatDispatchFailedException)?.Message here.
            _ = ex;
            throw new NoPvcAvailableException();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
        {
            // 504 — pod did not connect within the API's timeout; propagate the exact seconds when known
            throw new ChatPodTimeoutException((ex as ChatDispatchFailedException)?.TimeoutSeconds ?? 0);
        }
        // All other exceptions (including ChatDispatchFailedException with non-504 status codes)
        // propagate uncaught so AgentChat.razor's ClassifyLaunchError can render the API's detail.
    }

    public async Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        await chatClient.TerminateChatSessionAsync(agentId, cancellationToken);
    }

    public void SendClientKeepalive(string agentId)
    {
        // TODO [WARNING]: IChatJobDispatcher.SendClientKeepalive still accepts a raw string because
        // the interface was not migrated as part of issue #2996. The string is converted to AgentId
        // via the implicit operator (AgentId(string)) before reaching IPipelineApiChatClient.SendKeepaliveAsync,
        // which means a null/empty agentId throws ArgumentException from inside the implicit operator
        // rather than at an explicit API boundary guard. Migrate IChatJobDispatcher.SendClientKeepalive
        // to AgentId agentId to close the last type-unsafe entry point in the chat-dispatch path.
        // See review findings: Correctness WARNING @ ApiChatJobDispatcher.cs:57, DotNetSpecialist WARNING @ ChatJobDispatcher.cs:443.

        // Fire-and-forget — keepalive failures are non-fatal; the pod will eventually be
        // idle-killed by the server if heartbeats stop arriving. Log silently on failure.
        _ = chatClient.SendKeepaliveAsync(agentId, CancellationToken.None)
            .ContinueWith(
                t => Serilog.Log.Warning(t.Exception, "ApiChatJobDispatcher: keepalive POST failed for {AgentId}", agentId),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }
}
