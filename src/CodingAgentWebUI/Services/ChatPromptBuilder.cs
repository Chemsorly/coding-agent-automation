using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Orchestration.Dispatch;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Assembles a <see cref="ChatPromptMessage"/> from prompt parameters.
/// Pure logic — no I/O, injectable and unit-testable.
/// </summary>
public interface IChatPromptBuilder
{
    /// <summary>
    /// Builds the full <see cref="ChatPromptMessage"/> for a chat prompt request.
    /// </summary>
    ChatPromptMessage Build(ChatPromptParameters parameters);
}

/// <summary>Input parameters for <see cref="IChatPromptBuilder.Build"/>.</summary>
public sealed record ChatPromptParameters(
    string SessionId,
    string Prompt,
    bool IsFirstPrompt,
    string ChatWindowId,
    AgentProfile? ResolvedProfile,
    PipelineProject? SelectedProject,
    string? ResolvedMcpConfigPath);

/// <summary>
/// Default implementation of <see cref="IChatPromptBuilder"/>.
/// Merges MCP servers, gates first-prompt-only fields, and assembles the message.
/// </summary>
public sealed class ChatPromptBuilder : IChatPromptBuilder
{
    public ChatPromptMessage Build(ChatPromptParameters p)
    {
        // Merge MCP servers: project-level overrides profile-level.
        // Null project → passthrough profile servers unchanged.
        var mergedMcpServers = p.SelectedProject is not null
            ? DispatchOrchestrationService.MergeMcpServers(
                p.ResolvedProfile?.McpServers ?? [],
                p.SelectedProject.McpServers)
            : (p.ResolvedProfile?.McpServers ?? []);

        return new ChatPromptMessage
        {
            SessionId = p.SessionId,
            Prompt = p.Prompt,
            UseResume = !p.IsFirstPrompt,
            McpServers = mergedMcpServers,
            McpConfigPath = p.ResolvedMcpConfigPath ?? "/home/ubuntu/.kiro/settings/mcp.json",
            ChatWindowId = p.ChatWindowId,
            // Secrets, steering, and project identity are only sent on first prompt
            // to limit sensitive wire exposure on subsequent messages.
            ProjectSecrets = p.IsFirstPrompt ? p.SelectedProject?.Secrets : null,
            ProjectSteeringContent = p.IsFirstPrompt ? p.SelectedProject?.SteeringContent : null,
            ProjectId = p.IsFirstPrompt ? p.SelectedProject?.Id : null,
            ProjectName = p.IsFirstPrompt ? p.SelectedProject?.Name : null,
        };
    }
}
