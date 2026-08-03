using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the shared parameters of <see cref="Services.AgentPhaseExecutor.ExecuteAgentAndRecordAsync"/>
/// and <see cref="Services.AgentPhaseExecutor.ExecuteAgentRawAsync"/> to satisfy S107.
/// </summary>
internal sealed record AgentExecutionRequest
{
    public required IAgentProvider AgentProvider { get; init; }
    public required string Prompt { get; init; }
    public required PipelineRun Run { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required string Description { get; init; }
    public required Serilog.ILogger Logger { get; init; }
    public Action? OnChange { get; init; }
    public Action<string>? OnOutputLine { get; init; }
    public string? Phase { get; init; }
}
