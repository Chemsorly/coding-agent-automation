using CodingAgent.Api.Chat;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// GET /api/ui/chat/options — the launchable profiles with their models and efforts, and the pod connect timeout.
    /// With <paramref name="projectId"/>, the models of that project's repository rows count; without, those of the
    /// projects the caller can read (spec 053 req 6.2).
    /// </summary>
    internal static async Task<IResult> GetOptions(
        [FromQuery] string? projectId, IUserAccess access, IProjectStore projects, ChatService chat, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(projectId))
            access.DemandOperate(projectId);
        return TypedResults.Ok(await chat.GetOptionsAsync(await ChatTemplatesAsync(projectId, access, projects, ct), ct));
    }

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

        var chatTemplates = await ChatTemplatesAsync(request.ProjectId, access, projects, ct);
        return await RefusalAsJsonAsync(async () =>
            TypedResults.Ok(await chat.StartAsync(request, project, chatTemplates, access.IdentitySource, access.Subject!, ct)));
    }

    /// <summary>
    /// The templates whose repository rows a chat's model list includes: the chat project's, or without a project those
    /// of every project the caller can read, so no other project's routes are offered (review-2 n12).
    /// </summary>
    private static async Task<IReadOnlyList<PipelineJobTemplate>> ChatTemplatesAsync(
        string? projectId, IUserAccess access, IProjectStore projects, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(projectId))
            return await projects.LoadTemplatesForProjectAsync(projectId, ct);
        var readable = (await projects.LoadProjectsAsync(ct))
            .Where(p => access.CanRead(p.Id))
            .SelectMany(p => p.TemplateIds)
            .ToHashSet(StringComparer.Ordinal);
        return (await projects.LoadAllTemplatesAsync(ct)).Where(t => readable.Contains(t.Id)).ToList();
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
