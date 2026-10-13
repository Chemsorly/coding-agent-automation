namespace CodingAgent.Contracts.Ui;

/// <summary>
/// What the Agent Chat page offers (<c>GET /api/ui/chat/options</c>): the profiles a chat pod can be
/// launched for, and how long a launch may wait for the pod to connect.
/// </summary>
public sealed record UiChatOptions(IReadOnlyList<UiChatProfile> Profiles, int ConnectTimeoutSeconds);

/// <summary>An enabled agent profile whose labels name a job template.</summary>
public sealed record UiChatProfile(string Id, string DisplayName, IReadOnlyList<string> MatchLabels);

/// <summary>Body of <c>POST /api/ui/chat</c>. Without a project the chat runs with no project context.</summary>
public sealed record UiChatStartRequest(string ProfileId, string? ProjectId);

/// <summary>A launched chat: the agent to send prompts to, and the model it runs (<c>auto</c> when the profile sets none).</summary>
public sealed record UiChatSession(string AgentId, string Model);

/// <summary>
/// Body of <c>POST /api/ui/chat/{agentId}/prompt</c>. <paramref name="SessionId"/> names the reply stream the
/// page subscribes to; the API adds the profile's and project's MCP servers, secrets and steering.
/// </summary>
public sealed record UiChatPromptRequest(string SessionId, string Text);
