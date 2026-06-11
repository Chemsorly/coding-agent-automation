using System.Text.Json;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Reads and deserializes .agent/acceptance-criteria.json from a workspace.
/// Returns null on missing file, invalid JSON, or any error — never throws.
/// </summary>
internal static class AcceptanceCriteriaParser
{
    public static async Task<AcceptanceCriteriaReport?> ParseAsync(string workspacePath, ILogger logger, CancellationToken ct)
    {
        var filePath = Path.Combine(workspacePath, AgentWorkspacePaths.AcceptanceCriteriaFilePath);
        if (!File.Exists(filePath))
        {
            logger.Debug("Acceptance criteria file not found at {FilePath}", filePath);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var report = JsonSerializer.Deserialize<AcceptanceCriteriaReport>(json, PipelineJsonOptions.Lenient);
            if (report is null || report.Criteria is null)
                return null;

            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Failed to parse acceptance criteria file at {FilePath}", filePath);
            return null;
        }
    }
}
