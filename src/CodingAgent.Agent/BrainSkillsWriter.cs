using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Agent;

/// <summary>
/// Writes (or removes) the brain skills steering file for each supported agent provider type.
/// Spec 054, req 5.4.
/// </summary>
internal static class BrainSkillsWriter
{
    /// <summary>
    /// Deletes any stale brain skills file then writes the new text.
    /// </summary>
    /// <param name="providerType">The agent provider determining where the file lives.</param>
    /// <param name="workspacePath">Workspace root (Kiro CLI only).</param>
    /// <param name="text">The skills text to write. Null or empty removes the file only.</param>
    /// <param name="claudeRulesDirectory">Override for Claude Code's rules directory (T8: use temp in tests).</param>
    /// <param name="openCodeDirectory">Override for OpenCode's directory (T8: use temp in tests).</param>
    /// <returns>True when a file was written.</returns>
    internal static bool Write(
        AgentProviderType providerType,
        string workspacePath,
        string? text,
        string? claudeRulesDirectory = null,
        string? openCodeDirectory = null)
    {
        return providerType switch
        {
            AgentProviderType.KiroCli => WriteKiro(workspacePath, text),
            AgentProviderType.ClaudeCode => ClaudeSteeringFiles.WriteBrain(
                claudeRulesDirectory ?? ClaudeSteeringFiles.DefaultRulesDirectory, text),
            _ => OpenCodeSteeringFiles.WriteBrain(
                openCodeDirectory ?? OpenCodeSteeringFiles.DefaultDirectory, text),
        };
    }

    private static bool WriteKiro(string workspacePath, string? text)
    {
        var steeringDir = Path.Combine(workspacePath, ".kiro", "steering");
        var filePath = Path.Combine(workspacePath, AgentWorkspacePaths.KiroSteeringBrainFilePath);

        if (File.Exists(filePath))
            File.Delete(filePath);

        if (string.IsNullOrEmpty(text))
            return false;

        Directory.CreateDirectory(steeringDir);
        File.WriteAllText(filePath, WriteSteeringStep.FormatKiroFile(text));
        return true;
    }
}
