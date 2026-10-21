namespace CodingAgent.Contracts.Ui;

/// <summary>
/// What the Agent Chat page offers (<c>GET /api/ui/chat/options</c>): the profiles a chat pod can be
/// launched for, and how long a launch may wait for the pod to connect.
/// </summary>
public sealed record UiChatOptions(IReadOnlyList<UiChatProfile> Profiles, int ConnectTimeoutSeconds);

/// <summary>
/// An enabled agent profile whose job template exists and has a tool (spec 053 req 6.1, 6.5): its tool, the models a
/// chat may use (<c>auto</c>, the models routes name for it, the tool's built-in list), the efforts the tool accepts,
/// and the preselection from the global default route. <paramref name="MatchLabels"/> is empty; it goes once the page
/// no longer reads it.
/// </summary>
public sealed record UiChatProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> MatchLabels,
    string Tool = "",
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? Efforts = null,
    string DefaultModel = "auto",
    string DefaultEffort = "auto");

/// <summary>
/// Body of <c>POST /api/ui/chat</c>. Without a project the chat runs with no project context. <paramref name="Model"/> must
/// be one the options offer for the profile and <paramref name="Effort"/> one its tool accepts (spec 053 req 6.3); when
/// absent, the preselection is used.
/// </summary>
public sealed record UiChatStartRequest(string ProfileId, string? ProjectId, string? Model = null, string? Effort = null);

/// <summary>A launched chat: the agent to send prompts to, and the model it runs (<c>auto</c> when the profile sets none).</summary>
public sealed record UiChatSession(string AgentId, string Model);

/// <summary>
/// Body of <c>POST /api/ui/chat/{agentId}/prompt</c>. <paramref name="SessionId"/> names the reply stream the
/// page subscribes to; the API adds the profile's and project's MCP servers, secrets and steering.
/// </summary>
public sealed record UiChatPromptRequest(string SessionId, string Text);
