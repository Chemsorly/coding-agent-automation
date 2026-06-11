using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Agent-specific step that writes MCP server configuration to the workspace.
/// </summary>
internal sealed class WriteMcpConfigStep : IPipelineStep
{
    public string StepName => "WriteMcpConfig";

    private readonly JobAssignmentMessage _job;
    private readonly ILogger _logger;

    public WriteMcpConfigStep(JobAssignmentMessage job, ILogger? logger = null)
    {
        _job = job;
        _logger = logger ?? Log.Logger;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (_job.McpServers.Count == 0)
        {
            _logger.Debug("Pipeline {RunId} no MCP servers configured, skipping", context.Run.RunId);
            return Task.FromResult(StepResult.Continue);
        }

        try
        {
            var mcpConfigPath = context.AgentProvider.McpConfigPath;
            McpConfigWriter.WriteConfig(mcpConfigPath, _job.McpServers);
            _logger.Information("Pipeline {RunId} wrote MCP config with {ServerCount} server(s) to {McpConfigPath}",
                context.Run.RunId, _job.McpServers.Count, mcpConfigPath);
            context.Callbacks.EmitOutputLine($"🔌 Wrote MCP config with {_job.McpServers.Count} server(s) to {mcpConfigPath}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to write MCP config to workspace, continuing without it",
                context.Run.RunId);
        }

        return Task.FromResult(StepResult.Continue);
    }
}
