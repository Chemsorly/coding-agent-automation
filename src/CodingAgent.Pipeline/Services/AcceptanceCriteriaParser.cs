using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Persistence;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Reads and deserializes .agent/acceptance-criteria.json from a workspace.
/// Returns null on missing file, invalid JSON, or any error — never throws.
/// </summary>
internal static class AcceptanceCriteriaParser
{
    public static async Task<AcceptanceCriteriaReport?> ParseAsync(string workspacePath, ILogger logger, CancellationToken ct)
    {
        var filePath = Path.Combine(workspacePath, AgentWorkspacePaths.AcceptanceCriteriaFilePath);

        var report = await JsonFileReader.TryReadJsonFileAsync<AcceptanceCriteriaReport>(
            filePath, PipelineJsonOptions.Lenient, logger, ct);

        if (report is null || report.Criteria is null)
            return null;

        return report;
    }
}
