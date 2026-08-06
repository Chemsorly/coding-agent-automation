using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Writes project-level steering content to a chat workspace before the first prompt.
/// Branches on agent provider type: Kiro CLI gets a .kiro/steering/ file, OpenCode gets AGENTS.md.
///
/// The chat path only carries <c>ProjectSteeringContent</c> (no repo steering), so this helper
/// builds a single-source block — unlike <see cref="WriteSteeringStep"/> which handles both
/// project and repo sections.
/// </summary>
internal static class ChatSteeringWriter
{
    private const string BeginMarker = "<!-- BEGIN PIPELINE STEERING (auto-generated, do not commit) -->";
    private const string EndMarker = "<!-- END PIPELINE STEERING -->";

    private static readonly Regex SteeringBlockRegex = new(
        // TODO: Multiline + Singleline combination means .* matches newlines non-greedily from the
        // first BEGIN marker to the first END marker. If a user's AGENTS.md contains the string
        // "<!-- END PIPELINE STEERING -->" (e.g. from copied docs), the replace will consume only
        // up to the false marker, leaving the tail of the real block unstripped and breaking
        // idempotency. Consider using a more resilient extraction that counts markers or scans
        // for the canonical END marker at the end of the STEERING block only.
        @"^" + Regex.Escape(BeginMarker) + @"\r?\n.*?" + Regex.Escape(EndMarker) + @"\r?\n?",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(5));

    /// <summary>
    /// Writes <paramref name="projectSteeringContent"/> to <paramref name="chatWorkspace"/>.
    /// For Kiro CLI agents: writes <c>.kiro/steering/pipeline-project.md</c>.
    /// For OpenCode agents: prepends a pipeline marker block to <c>AGENTS.md</c>.
    /// </summary>
    public static void Write(string projectSteeringContent, string chatWorkspace, bool isOpenCodeProvider)
    {
        if (isOpenCodeProvider)
            WriteOpenCode(projectSteeringContent, chatWorkspace);
        else
            WriteKiro(projectSteeringContent, chatWorkspace);
    }

    private static void WriteKiro(string content, string chatWorkspace)
    {
        var steeringDir = Path.Combine(chatWorkspace, ".kiro", "steering");
        Directory.CreateDirectory(steeringDir);
        var path = Path.Combine(chatWorkspace, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.WriteAllText(path, FormatKiroFile(content));
    }

    private static void WriteOpenCode(string content, string chatWorkspace)
    {
        var agentsPath = Path.Combine(chatWorkspace, AgentWorkspacePaths.OpenCodeAgentsFilePath);

        // Read existing content and strip any previous pipeline block
        var existingContent = File.Exists(agentsPath) ? File.ReadAllText(agentsPath) : string.Empty;
        existingContent = SteeringBlockRegex.Replace(existingContent, string.Empty);

        // Build single-source pipeline block (project only — no repo steering in chat path)
        var block = BuildChatBlock(content);

        // Prepend pipeline block, preserve existing content below
        var combined = string.IsNullOrEmpty(existingContent)
            ? block
            : block + "\n" + existingContent;

        File.WriteAllText(agentsPath, combined);
    }

    /// <summary>
    /// Builds the AGENTS.md pipeline block. Only emits <c># Project Instructions</c>
    /// because the chat path has no repo steering content.
    /// </summary>
    internal static string BuildChatBlock(string projectContent)
    {
        return string.Join("\n",
            BeginMarker,
            "# Project Instructions",
            string.Empty,
            projectContent,
            EndMarker) + "\n";
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
