using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Agent-specific step that writes MCP server configuration to the workspace.
/// </summary>
internal sealed class WriteMcpConfigStep : IPipelineStep
{
    private readonly JobAssignmentMessage _job;
    // TODO: [WARNING] _connection field is unused — remove it. Holding a reference to HubConnection (IAsyncDisposable) unnecessarily.

    public WriteMcpConfigStep(JobAssignmentMessage job, HubConnection connection)
    {
        _job = job;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (_job.McpServers.Count == 0)
            return Task.FromResult(StepResult.Continue);

        try
        {
            var agentConfig = _job.ProviderConfigs.FirstOrDefault(c => c.Id == _job.AgentProviderConfigId);
            var mcpConfigPath = agentConfig?.Settings.GetValueOrDefault("mcpConfigPath", ".kiro/settings/mcp.json")
                ?? ".kiro/settings/mcp.json";
            LocalPipelineExecutor.WriteMcpConfigToWorkspace(context.Run.WorkspacePath!, _job.McpServers, mcpConfigPath);
            context.EmitOutputLine($"🔌 Wrote MCP config with {_job.McpServers.Count} server(s) to {mcpConfigPath}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to write MCP config to workspace, continuing without it");
        }

        return Task.FromResult(StepResult.Continue);
    }
}
