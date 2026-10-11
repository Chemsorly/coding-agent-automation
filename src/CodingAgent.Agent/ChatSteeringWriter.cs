using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Agent;

/// <summary>
/// Writes project-level steering content for a chat before the first prompt.
/// Branches on agent provider type: Kiro CLI gets a .kiro/steering/ file in the chat workspace;
/// Claude Code gets a user-level rule in ~/.claude/rules/ and OpenCode an instruction file in
/// ~/.opencode/, both outside the workspace.
///
/// The chat path only carries <c>ProjectSteeringContent</c> (no repo steering) — unlike
/// <see cref="WriteSteeringStep"/> which handles both project and repo sections.
/// </summary>
internal static class ChatSteeringWriter
{
    /// <summary>
    /// Writes <paramref name="projectSteeringContent"/> where <paramref name="providerType"/> reads it.
    /// Claude Code gets a user-level rule in <paramref name="claudeRulesDirectory"/>
    /// (default <c>~/.claude/rules</c>, see <see cref="ClaudeSteeringFiles"/>); OpenCode an instruction
    /// file in <paramref name="openCodeDirectory"/> (default <c>~/.opencode</c>, see <see cref="OpenCodeSteeringFiles"/>).
    /// </summary>
    public static void Write(
        string projectSteeringContent, string chatWorkspace, AgentProviderType providerType,
        string? claudeRulesDirectory = null, string? openCodeDirectory = null)
    {
        switch (providerType)
        {
            case AgentProviderType.OpenCode:
                OpenCodeSteeringFiles.Write(
                    openCodeDirectory ?? OpenCodeSteeringFiles.DefaultDirectory, projectSteeringContent, repoContent: null);
                break;
            case AgentProviderType.ClaudeCode:
                ClaudeSteeringFiles.Write(
                    claudeRulesDirectory ?? ClaudeSteeringFiles.DefaultRulesDirectory, projectSteeringContent, repoContent: null);
                break;
            default:
                WriteKiro(projectSteeringContent, chatWorkspace);
                break;
        }
    }

    private static void WriteKiro(string content, string chatWorkspace)
    {
        var steeringDir = Path.Combine(chatWorkspace, ".kiro", "steering");
        Directory.CreateDirectory(steeringDir);
        var path = Path.Combine(chatWorkspace, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.WriteAllText(path, FormatKiroFile(content));
    }

    private static string FormatKiroFile(string content) =>
        $"""
        ---
        inclusion: always
        ---

        <!-- Written by automation pipeline. Do not edit manually. -->

        {content}
        """;
}
