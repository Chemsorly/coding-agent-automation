using CodingAgent.AgentGateway;
using CodingAgent.Contracts;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.Api.Chat;

/// <summary>A chat request the API refuses; <see cref="StatusCode"/> carries the HTTP status, the message the reason to show.</summary>
internal sealed class ChatRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Spec 051 2.5: the Agent Chat page's operations, moved from the web host (D5). The API launches the pod for the
/// chosen profile, model and effort (spec 053 D13), records who launched it, and builds every prompt itself from the
/// session record, so the project's secrets never pass through the web host. The caller checks ownership before
/// prompting, keeping alive or ending a session.
/// </summary>
internal sealed class ChatService(
    IAgentProfileStore profiles,
    AgentRoutingStore routing,
    IProjectStore projects,
    JobTemplateStore templates,
    IChatJobDispatcher dispatcher,
    ChatSessionStore sessions,
    IAgentRegistryService registry,
    IHubContext<AgentHub, IAgentHubClient> hub,
    DispatchServiceOptions options,
    ILogger<ChatService> logger)
{
    /// <summary>
    /// The profiles a chat can be launched for (spec 053 req 6.1, 6.5): enabled, with a job template that exists and
    /// has a tool. Each with the models a chat may use and the preselection.
    /// </summary>
    /// <param name="chatTemplates">The templates whose repository rows count: the chat project's, or the readable ones.</param>
    public async Task<UiChatOptions> GetOptionsAsync(IReadOnlyList<PipelineJobTemplate> chatTemplates, CancellationToken ct)
    {
        var table = await routing.LoadAsync(ct) ?? AgentRouting.Empty;
        var toolsByKey = templates.ToolsByKey();
        var chatProfiles = (await profiles.LoadAgentProfilesAsync(ct))
            .Select(p => (Profile: p, Tool: LaunchableTool(p, toolsByKey)))
            .Where(x => x.Tool is not null)
            .OrderBy(x => x.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var (defaultModel, defaultEffort) = Defaults(x.Profile, x.Tool!, table);
                return new UiChatProfile(
                    x.Profile.Id, x.Profile.DisplayName, x.Tool!.Id,
                    ModelsFor(x.Profile, x.Tool, table, chatTemplates),
                    [AgentToolCatalog.Auto, .. x.Tool.Efforts],
                    defaultModel, defaultEffort);
            })
            .ToList();
        return new UiChatOptions(chatProfiles, options.ChatPodConnectTimeoutSeconds);
    }

    /// <summary>
    /// Spec 053 req 6.2: the models a chat on <paramref name="profile"/> may use: <c>auto</c>, every model a global row or
    /// a repository row of <paramref name="chatTemplates"/> names for it, and the tool's built-in list (Claude Code).
    /// No free text reaches the CLI (D13).
    /// </summary>
    internal static IReadOnlyList<string> ModelsFor(
        AgentProfile profile, AgentTool tool, AgentRouting routing, IReadOnlyList<PipelineJobTemplate> chatTemplates)
    {
        var routes = new[] { routing.Default }
            .Concat(routing.ByTaskType?.Values ?? [])
            .Concat(chatTemplates.SelectMany(t => t.AgentRoutes?.Values ?? []));
        var routeModels = routes
            .Where(r => r is not null && string.Equals(r.ProfileId, profile.Id, StringComparison.Ordinal))
            .Select(r => r!.Model);
        return new[] { AgentToolCatalog.Auto }
            .Concat(routeModels)
            .Concat(tool.BuiltInModels)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Spec 053 req 6.3a: the global default route's model and effort when it names the profile, else <c>auto</c>.
    /// </summary>
    internal static (string Model, string Effort) Defaults(AgentProfile profile, AgentTool tool, AgentRouting routing) =>
        routing.Default is { } route && string.Equals(route.ProfileId, profile.Id, StringComparison.Ordinal)
            ? (route.Model, AgentToolCatalog.AcceptsEffort(tool, route.Effort) ? route.Effort : AgentToolCatalog.Auto)
            : (AgentToolCatalog.Auto, AgentToolCatalog.Auto);

    /// <summary>The profile's tool when a chat can be launched for it: enabled, its job template exists and has a tool.</summary>
    private static AgentTool? LaunchableTool(AgentProfile profile, IReadOnlyDictionary<string, string> toolsByKey) =>
        profile.Enabled && toolsByKey.TryGetValue(profile.JobTemplateKey(), out var providerType)
            ? AgentToolCatalog.Find(providerType)
            : null;

    /// <summary>
    /// Launches a chat pod for the profile, model and effort and waits until it connects, then records the session for
    /// <paramref name="ownerSubject"/>. The caller has checked operator on <paramref name="project"/>. Refuses with 400 a
    /// profile that cannot be launched, a model the options do not offer, and an effort the tool does not accept
    /// (spec 053 req 6.3).
    /// </summary>
    /// <param name="chatTemplates">The templates whose repository rows count for the model list (req 6.2).</param>
    public async Task<UiChatSession> StartAsync(
        UiChatStartRequest request,
        PipelineProject? project,
        IReadOnlyList<PipelineJobTemplate> chatTemplates,
        string? ownerIdentitySource,
        string ownerSubject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var toolsByKey = templates.ToolsByKey();
        var profile = (await profiles.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == request.ProfileId);
        var tool = profile is null ? null : LaunchableTool(profile, toolsByKey);
        if (profile is null || tool is null)
        {
            throw new ChatRequestException(StatusCodes.Status400BadRequest,
                "The selected agent profile is disabled, or its job template does not exist or has no tool. Reload the page.");
        }

        var table = await routing.LoadAsync(ct) ?? AgentRouting.Empty;
        var (defaultModel, defaultEffort) = Defaults(profile, tool, table);
        var model = string.IsNullOrWhiteSpace(request.Model) ? defaultModel : request.Model;
        var effort = string.IsNullOrWhiteSpace(request.Effort) ? defaultEffort : request.Effort;
        if (!ModelsFor(profile, tool, table, chatTemplates).Contains(model, StringComparer.Ordinal))
        {
            throw new ChatRequestException(StatusCodes.Status400BadRequest,
                $"Model '{model}' is not offered for agent profile '{profile.DisplayName}'. Choose one from the list.");
        }
        if (!AgentToolCatalog.AcceptsEffort(tool, effort))
        {
            throw new ChatRequestException(StatusCodes.Status400BadRequest,
                $"{tool.DisplayName} does not accept effort '{effort}'.");
        }

        string agentId;
        try
        {
            agentId = await dispatcher.DispatchChatPodAsync(profile.JobTemplateKey(), model, effort, ct);
        }
        catch (NoPvcAvailableException)
        {
            throw new ChatRequestException(StatusCodes.Status502BadGateway, "No agent credentials available. Try again later.");
        }
        catch (ChatPodTimeoutException ex)
        {
            throw new ChatRequestException(StatusCodes.Status502BadGateway, $"Pod did not connect within {ex.TimeoutSeconds}s. Check cluster logs.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ChatRequestException(StatusCodes.Status502BadGateway, $"Launch failed: {ex.Message}");
        }

        await sessions.SaveAsync(new ChatSessionRecord(
            agentId, ownerIdentitySource, ownerSubject, project?.Id, profile.Id, ChatWindowId: Guid.NewGuid().ToString(),
            Model: model, Effort: effort));
        logger.LogInformation("Chat pod {AgentId} launched for profile {ProfileId} with model {Model} and effort {Effort}",
            agentId, profile.Id, model, effort);
        return new UiChatSession(agentId, model);
    }

    /// <summary>
    /// Builds the prompt from the session's profile and project, as they are now, and delivers it to the
    /// agent. The session ID is stamped on the agent first, so its replies pass the hub's ownership check
    /// on any replica.
    /// </summary>
    public async Task SendPromptAsync(ChatSessionRecord session, UiChatPromptRequest request, CancellationToken ct)
    {
        var profile = (await profiles.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == session.ProfileId)
            ?? throw new ChatRequestException(StatusCodes.Status409Conflict, "The chat's agent profile no longer exists. End the chat and start a new one.");
        var tool = LaunchableTool(profile, templates.ToolsByKey())
            ?? throw new ChatRequestException(StatusCodes.Status409Conflict, "The chat's agent profile can no longer run. End the chat and start a new one.");
        // The chat's own model and effort; sessions stored before spec 053 have none and run with the tool's choice.
        var agentConfig = AgentProviderConfigFactory.Build(
            profile, tool.Id, session.Model ?? AgentToolCatalog.Auto, session.Effort ?? AgentToolCatalog.Auto);
        PipelineProject? project = null;
        if (session.ProjectId is not null)
        {
            project = await projects.GetProjectByIdAsync(session.ProjectId, ct)
                ?? throw new ChatRequestException(StatusCodes.Status409Conflict, "The chat's project no longer exists. End the chat and start a new one.");
        }

        var entry = await registry.GetByAgentIdAsync(session.AgentId, ct)
            ?? throw new ChatRequestException(StatusCodes.Status409Conflict, "The chat pod is gone. End the chat and start a new one.");
        if (entry.Status == AgentStatus.Disconnected)
            throw new ChatRequestException(StatusCodes.Status409Conflict, "The chat pod is disconnected.");

        var message = ChatPromptBuilder.Build(new ChatPromptParameters(
            SessionId: request.SessionId,
            Prompt: request.Text,
            IsFirstPrompt: !session.Prompted,
            ChatWindowId: session.ChatWindowId,
            ResolvedProfile: profile,
            SelectedProject: project,
            ResolvedMcpConfigPath: McpConfigPath(agentConfig),
            ResolvedAuthMode: agentConfig.Settings.GetValueOrDefault(ProviderSettingKeys.AuthMode)));

        await registry.UpdateAgentFieldAsync(entry.AgentId, AgentFieldNames.ActiveChatSessionId, request.SessionId);
        await hub.Clients.Client(entry.ConnectionId).AssignChatPrompt(message);

        if (!session.Prompted)
            await sessions.SaveAsync(session with { Prompted = true });
    }

    /// <summary>Ends the chat: the pod is stopped, and the session forgotten.</summary>
    public async Task TerminateAsync(ChatSessionRecord session, CancellationToken ct)
    {
        await dispatcher.TerminateChatSessionAsync(new AgentId(session.AgentId), ct);
        await sessions.DeleteAsync(session.AgentId);
    }

    /// <summary>Resets the idle clock, so an open chat window keeps its pod.</summary>
    public void Keepalive(ChatSessionRecord session) => dispatcher.SendClientKeepalive(session.AgentId);

    /// <summary>
    /// Where the agent writes the MCP servers: the profile's setting, otherwise the default of its tool
    /// (<see cref="AgentToolCatalog"/>). Without a known type the Kiro path is used, which leaves an OpenCode or
    /// Claude pod without its MCP servers, so that is logged.
    /// </summary>
    private string McpConfigPath(ProviderConfig agentConfig)
    {
        if (agentConfig.Settings.TryGetValue(ProviderSettingKeys.McpConfigPath, out var path) && !string.IsNullOrWhiteSpace(path))
            return path;
        if (string.IsNullOrWhiteSpace(agentConfig.ProviderType))
            logger.LogWarning("Agent provider config {ProviderConfig} has no provider type; using the Kiro MCP config path", agentConfig.Id);
        return (AgentToolCatalog.Find(agentConfig.ProviderType) ?? AgentToolCatalog.Kiro).DefaultMcpConfigPath;
    }
}
