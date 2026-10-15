using CodingAgent.AgentGateway;
using CodingAgent.Contracts;
using CodingAgent.Contracts.Ui;
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
/// Spec 051 2.5: the Agent Chat page's operations, moved from the web host (D5). The API resolves the
/// selector, model and effort from the profile, launches the pod, records who launched it, and builds
/// every prompt itself from the session record, so the project's secrets never pass through the web host.
/// The caller checks ownership before prompting, keeping alive or ending a session.
/// </summary>
internal sealed class ChatService(
    IAgentProfileStore profiles,
    IProviderConfigStore providerConfigs,
    IProjectStore projects,
    JobTemplateStore templates,
    IChatJobDispatcher dispatcher,
    ChatSessionStore sessions,
    IAgentRegistryService registry,
    IHubContext<AgentHub, IAgentHubClient> hub,
    DispatchServiceOptions options,
    ILogger<ChatService> logger)
{
    private const string DefaultMcpConfigPath = "/home/ubuntu/.kiro/settings/mcp.json";

    /// <summary>
    /// The profiles a chat can be launched for: enabled, with labels that name a job template, the same
    /// profile → template lookup pipeline dispatch uses. A profile without labels has no selector.
    /// </summary>
    public async Task<UiChatOptions> GetOptionsAsync(CancellationToken ct)
    {
        var chatProfiles = (await profiles.LoadAgentProfilesAsync(ct))
            .Where(IsLaunchable)
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new UiChatProfile(p.Id, p.DisplayName, p.MatchLabels))
            .ToList();
        return new UiChatOptions(chatProfiles, options.ChatPodConnectTimeoutSeconds);
    }

    private bool IsLaunchable(AgentProfile profile) =>
        profile.Enabled && profile.MatchLabels.Count > 0
        && templates.Resolve(profile.JobTemplateKey()) is not null;

    /// <summary>
    /// Launches a chat pod for the profile and waits until it connects, then records the session for
    /// <paramref name="ownerSubject"/>. The caller has checked operator on <paramref name="project"/>.
    /// </summary>
    public async Task<UiChatSession> StartAsync(
        string profileId, PipelineProject? project, string? ownerIdentitySource, string ownerSubject, CancellationToken ct)
    {
        var profile = (await profiles.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == profileId && IsLaunchable(p))
            ?? throw new ChatRequestException(StatusCodes.Status409Conflict, "The selected agent profile is no longer available. Reload the page.");
        var agentConfig = await LoadAgentConfigAsync(profile, ct);
        var model = agentConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model);

        string agentId;
        try
        {
            agentId = await dispatcher.DispatchChatPodAsync(
                profile.JobTemplateKey(), model, agentConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Effort), ct);
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
            agentId, ownerIdentitySource, ownerSubject, project?.Id, profile.Id, ChatWindowId: Guid.NewGuid().ToString()));
        logger.LogInformation("Chat pod {AgentId} launched for profile {ProfileId}", agentId, profile.Id);
        return new UiChatSession(agentId, model ?? "auto");
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
        var agentConfig = await LoadAgentConfigAsync(profile, ct);
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

    private async Task<ProviderConfig> LoadAgentConfigAsync(AgentProfile profile, CancellationToken ct) =>
        // A launch with defaults would silently drop the profile's model, effort and auth mode.
        await providerConfigs.GetProviderConfigByIdAsync(profile.AgentProviderConfigId, ProviderKind.Agent, ct)
            ?? throw new ChatRequestException(StatusCodes.Status409Conflict,
                $"Agent profile '{profile.DisplayName}' points to an agent provider config that does not exist. " +
                "Fix it in Settings → Label Routing → Agent Profiles.");

    /// <summary>
    /// Where the agent writes the MCP servers: the provider's setting, otherwise the default of its type.
    /// The type is stored PascalCase ("OpenCode"), hence the lower-casing. Without a type the Kiro path is
    /// used, which leaves an OpenCode or Claude pod without its MCP servers, so that is logged.
    /// </summary>
    private string McpConfigPath(ProviderConfig agentConfig)
    {
        if (agentConfig.Settings.TryGetValue(ProviderSettingKeys.McpConfigPath, out var path) && !string.IsNullOrWhiteSpace(path))
            return path;
        if (string.IsNullOrWhiteSpace(agentConfig.ProviderType))
            logger.LogWarning("Agent provider config {ProviderConfig} has no provider type; using the Kiro MCP config path", agentConfig.Id);
        return agentConfig.ProviderType?.ToLowerInvariant() switch
        {
            "opencode" => "/home/ubuntu/.opencode/opencode.json",
            "claudecode" => AgentDefaults.ClaudeMcpConfigPath,
            _ => DefaultMcpConfigPath,
        };
    }
}
