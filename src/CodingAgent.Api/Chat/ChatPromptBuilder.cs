using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Chat;

/// <summary>Input parameters for <see cref="ChatPromptBuilder.Build"/>.</summary>
internal sealed record ChatPromptParameters(
    string SessionId,
    string Prompt,
    bool IsFirstPrompt,
    string ChatWindowId,
    AgentProfile? ResolvedProfile,
    PipelineProject? SelectedProject,
    string? ResolvedMcpConfigPath,
    string? ResolvedAuthMode = null);

/// <summary>
/// Assembles a <see cref="ChatPromptMessage"/>: merges MCP servers, gates first-prompt-only fields.
/// Pure logic. It ran in the web host until spec 051 2.5; the API now builds every chat prompt, so
/// project secrets never pass through the web host.
/// </summary>
internal static class ChatPromptBuilder
{
    public static ChatPromptMessage Build(ChatPromptParameters parameters)
    {
        // Merge MCP servers: project-level overrides profile-level.
        // Null project → passthrough profile servers unchanged.
        var mergedMcpServers = parameters.SelectedProject is not null
            ? McpServerMerge.Merge(
                parameters.ResolvedProfile?.McpServers ?? [],
                parameters.SelectedProject.McpServers)
            : (parameters.ResolvedProfile?.McpServers ?? []);

        return new ChatPromptMessage
        {
            SessionId = parameters.SessionId,
            Prompt = parameters.Prompt,
            UseResume = !parameters.IsFirstPrompt,
            McpServers = mergedMcpServers,
            McpConfigPath = parameters.ResolvedMcpConfigPath ?? "/home/ubuntu/.kiro/settings/mcp.json",
            ChatWindowId = parameters.ChatWindowId,
            // Secrets, steering, and project identity are only sent on first prompt
            // to limit sensitive wire exposure on subsequent messages.
            ProjectSecrets = parameters.IsFirstPrompt ? parameters.SelectedProject?.Secrets : null,
            ProjectSteeringContent = parameters.IsFirstPrompt ? parameters.SelectedProject?.SteeringContent : null,
            ProjectId = parameters.IsFirstPrompt ? parameters.SelectedProject?.Id : null,
            ProjectName = parameters.IsFirstPrompt ? parameters.SelectedProject?.Name : null,
            // Sent with every prompt: the Claude Code provider is recreated on the first prompt only,
            // but a pod that restarted mid-conversation needs it on a follow-up too.
            AgentAuthMode = parameters.ResolvedAuthMode,
        };
    }
}
