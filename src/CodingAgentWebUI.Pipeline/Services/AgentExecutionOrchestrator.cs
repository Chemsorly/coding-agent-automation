using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles agent execution phases: analysis, code generation (with stall monitoring),
/// and code review iterations. Extracted from PipelineOrchestrationService.
/// Split into partial classes by phase for maintainability.
/// </summary>
internal partial class AgentExecutionOrchestrator : IAgentPhaseExecutor
{
    /// <summary>Minimum length in bytes for analysis.md to be considered valid.</summary>
    internal const int MinAnalysisLength = PipelineConstants.MinAnalysisLength;

    private static readonly JsonSerializerOptions s_camelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Serilog.ILogger _logger;

    public AgentExecutionOrchestrator(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
