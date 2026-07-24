using System.Text.Json;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Formats gate comments (not-ready or wont-do) from assessment JSON.
/// Delegates to <see cref="AgentPhaseExecutor.BuildWontDoComment"/> and
/// <see cref="AgentPhaseExecutor.BuildNotReadyComment"/> for structured formatting.
/// </summary>
internal sealed class GateCommentFormatter : IGateCommentFormatter
{
    private readonly ILogger _logger;

    public GateCommentFormatter(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string FormatGateComment(string? assessmentJson, bool isWontDo)
    {
        if (string.IsNullOrWhiteSpace(assessmentJson))
            return isWontDo ? "## 🚫 Analysis Gate: Won't Do" : "## ⚠️ Analysis Gate: Needs Refinement";

        try
        {
            var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(assessmentJson);
            if (assessment is not null)
            {
                return isWontDo
                    ? AgentPhaseExecutor.BuildWontDoComment(assessment)
                    : AgentPhaseExecutor.BuildNotReadyComment(assessment);
            }
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Failed to deserialize assessment JSON for gate comment");
        }

        // Fallback: wrap raw JSON in a code block
        return isWontDo
            ? $"## 🚫 Analysis Gate: Won't Do\n\n```json\n{assessmentJson}\n```"
            : $"## ⚠️ Analysis Gate: Needs Refinement\n\n```json\n{assessmentJson}\n```";
    }
}
