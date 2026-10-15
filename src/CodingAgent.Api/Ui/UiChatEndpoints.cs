using CodingAgent.Api.Chat;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.5b: the Agent Chat page (D5). Launching a chat needs operator on the chosen project, or
/// global operator without one, because the chat runs with that project's MCP servers and secrets. A
/// chat can then only be used by the user who launched it, or by an admin: anyone else gets 404, as for
/// a chat that does not exist. Only chats launched here can be used; pipeline agents cannot be prompted.
/// A refusal carries its reason as a JSON string: 409 for a request that cannot be served, 502 when the
/// pod cannot be started.
/// </summary>
internal static class UiChatEndpoints
{
    public static RouteGroupBuilder MapUiChatEndpoints(this RouteGroupBuilder group)
    {
        var chat = group.MapGroup("/chat").RequireAuthorization(UiPolicies.AnyOperator);
        chat.MapGet("/options", GetOptions);
        chat.MapPost("/{agentId}/prompt", SendPrompt);
        chat.MapPost("/{agentId}/terminate", Terminate);
        chat.MapPost("/{agentId}/keepalive", Keepalive);
        group.MapPost("/chat", Start).RequireAuthorization(UiPolicies.AnyOperator);
        return group;
    }

    /// <summary>GET /api/ui/chat/options — the launchable profiles and the pod connect timeout.</summary>
    internal static async Task<IResult> GetOptions(ChatService chat, CancellationToken ct) =>
        TypedResults.Ok(await chat.GetOptionsAsync(ct));

    /// <summary>POST /api/ui/chat — launches a chat pod and waits until it connects (up to the connect timeout).</summary>
    internal static async Task<IResult> Start(
        UiChatStartRequest request, IUserAccess access, IProjectStore projects, ChatService chat, CancellationToken ct)
    {
        access.DemandOperate(request.ProjectId);
        PipelineProject? project = null;
        if (!string.IsNullOrEmpty(request.ProjectId))
        {
            project = await projects.GetProjectByIdAsync(request.ProjectId, ct);
            if (project is null)
                return TypedResults.NotFound();
        }

        return await RefusalAsJsonAsync(async () =>
            TypedResults.Ok(await chat.StartAsync(request.ProfileId, project, access.IdentitySource, access.Subject!, ct)));
    }

    /// <summary>POST /api/ui/chat/{agentId}/prompt — sends a prompt; the reply streams to the session's hub group.</summary>
    internal static async Task<IResult> SendPrompt(
        string agentId, UiChatPromptRequest request, IUserAccess access, ChatSessionStore sessions, ChatService chat, CancellationToken ct)
    {
        var session = await FindOwnedAsync(agentId, access, sessions);
        if (session is null)
            return TypedResults.NotFound();
        return await RefusalAsJsonAsync(async () =>
        {
            await chat.SendPromptAsync(session, request, ct);
            return TypedResults.NoContent();
        });
    }

    /// <summary>POST /api/ui/chat/{agentId}/terminate — ends the chat and stops its pod.</summary>
    internal static async Task<IResult> Terminate(
        string agentId, IUserAccess access, ChatSessionStore sessions, ChatService chat, CancellationToken ct)
    {
        var session = await FindOwnedAsync(agentId, access, sessions);
        if (session is null)
            return TypedResults.NotFound();
        await chat.TerminateAsync(session, ct);
        return TypedResults.NoContent();
    }

    /// <summary>POST /api/ui/chat/{agentId}/keepalive — keeps the pod of an open chat window from idling out.</summary>
    internal static async Task<IResult> Keepalive(string agentId, IUserAccess access, ChatSessionStore sessions, ChatService chat)
    {
        var session = await FindOwnedAsync(agentId, access, sessions);
        if (session is null)
            return TypedResults.NotFound();
        chat.Keepalive(session);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The session, when the caller launched it or is an admin, and may still operate on its project;
    /// otherwise null (404), so another user's chat looks like none at all.
    /// </summary>
    internal static async Task<ChatSessionRecord?> FindOwnedAsync(string agentId, IUserAccess access, ChatSessionStore sessions)
    {
        var session = await sessions.FindAsync(agentId);
        if (session is null)
            return null;
        var isOwner = string.Equals(session.OwnerSubject, access.Subject, StringComparison.Ordinal)
            && string.Equals(session.OwnerIdentitySource, access.IdentitySource, StringComparison.Ordinal);
        if (!isOwner && access.Grant.GlobalRole != AccessRole.Admin)
            return null;
        return access.CanOperate(session.ProjectId) ? session : null;
    }

    private static async Task<IResult> RefusalAsJsonAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (ChatRequestException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
