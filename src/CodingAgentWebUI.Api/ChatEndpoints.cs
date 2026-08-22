using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for chat pod lifecycle.
///
/// <para>
/// <see cref="ChatJobDispatcher"/> lives in the API process alongside <c>AgentHub</c> and the
/// <c>AgentRegistryService</c> it polls. The Blazor monolith (and any other operator-tier process)
/// calls these endpoints rather than dispatching pods directly.
/// </para>
///
/// <para>
/// Both endpoints are guarded by <see cref="ApiAuthPolicies.Operator"/>: only the master API key
/// may start or stop chat sessions. An agent pod holding a derived per-pod key has no business
/// doing either.
/// </para>
/// </summary>
public static class ChatEndpoints
{
    /// <summary>Request body for <c>POST /api/chat/dispatch</c>.</summary>
    public sealed record DispatchChatPodRequest(
        string AgentSelector,
        string? Model,
        string? Effort);

    /// <summary>Response body for <c>POST /api/chat/dispatch</c>.</summary>
    public sealed record DispatchChatPodResponse(string AgentId);

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapPost("/dispatch", DispatchChatPod);
        group.MapPost("/{agentId}/terminate", TerminateChatSession);
    }

    // ── POST /api/chat/dispatch ────────────────────────────────────────────

    /// <summary>
    /// POST /api/chat/dispatch
    ///
    /// Submits a Kubernetes Job for a chat pod, waits until the pod connects to the hub,
    /// and returns the <c>agentId</c> of the now-connected agent.
    ///
    /// <para>
    /// Blocks until the pod registers (up to <c>ChatPodConnectTimeoutSeconds</c>).
    /// Returns 409 when a chat job is already active for the given selector.
    /// Returns 503 when no credential PVC is available.
    /// Returns 504 when the pod does not connect within the timeout.
    /// </para>
    /// </summary>
    internal static async Task<Results<Ok<DispatchChatPodResponse>, Conflict<string>, StatusCodeHttpResult>> DispatchChatPod(
        DispatchChatPodRequest request,
        IChatJobDispatcher dispatcher,
        CancellationToken ct)
    {
        try
        {
            var agentId = await dispatcher.DispatchChatPodAsync(
                request.AgentSelector, request.Model, request.Effort, ct);

            return TypedResults.Ok(new DispatchChatPodResponse(agentId));
        }
        catch (ChatAlreadyActiveException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
        catch (NoPvcAvailableException)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (ChatPodTimeoutException)
        {
            return TypedResults.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }

    // ── POST /api/chat/{agentId}/terminate ─────────────────────────────────

    /// <summary>
    /// POST /api/chat/{agentId}/terminate
    ///
    /// Sends a CancelChat hub message to the agent pod and waits for the watcher to confirm
    /// the job reached a terminal state, falling back to force-deletion on timeout.
    /// Always 200 (idempotent — terminating an unknown agentId is a no-op).
    /// </summary>
    internal static async Task<Ok> TerminateChatSession(
        string agentId,
        IChatJobDispatcher dispatcher,
        CancellationToken ct)
    {
        await dispatcher.TerminateChatSessionAsync(new AgentId(agentId), ct);
        return TypedResults.Ok();
    }
}
