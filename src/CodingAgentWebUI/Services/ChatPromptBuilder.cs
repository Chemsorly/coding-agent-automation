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
    public ChatPromptMessage Build(ChatPromptParameters parameters)
    {
        // Merge MCP servers: project-level overrides profile-level.
        // Null project → passthrough profile servers unchanged.
        var mergedMcpServers = parameters.SelectedProject is not null
            ? DispatchOrchestrationService.MergeMcpServers(
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
        };
    }
}
