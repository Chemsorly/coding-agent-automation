using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Resolved startup configuration for the agent worker.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
/// <remarks>
/// Agent pods run in one of two execution modes:
/// <list type="bullet">
///   <item><term>Work-item mode</term><description>
///     Started by <c>DispatchService</c> with <c>--mode=workitem</c> and <c>--work-item-id</c>.
///     Runs <see cref="WorkItemAgentService"/> to execute a single pipeline work item, then exits.
///   </description></item>
///   <item><term>Chat mode</term><description>
///     Started by <c>ChatJobDispatcher</c> with <c>--mode=chat</c>. Runs
///     <c>AgentWorkerService</c> and stays connected to the orchestrator hub to serve
///     interactive chat sessions and consolidation jobs.
///   </description></item>
/// </list>
/// <para>
/// <c>--mode</c> is required. Absent or unrecognised values throw <see cref="InvalidOperationException"/>.
/// </para>
/// <see cref="IsWorkItemMode"/> discriminates between these two modes.
/// </remarks>
internal sealed record AgentStartupConfig
{
    public required string AgentApiKey { get; init; }
    public required string OrchestratorUrl { get; init; }
    public required AgentId AgentId { get; init; }
    public required string? WorkItemId { get; init; }

    /// <summary>
    /// <see langword="true"/> when the pod was started with <c>--work-item-id</c> (work-item mode);
    /// <see langword="false"/> when started without it (chat mode).
    /// </summary>
    public required bool IsWorkItemMode { get; init; }

    internal static async Task<AgentStartupConfig> ResolveAsync(string[] args)
    {
        var workItemId = args
            .FirstOrDefault(a => a.StartsWith(AgentDefaults.CliWorkItemIdPrefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(AgentDefaults.CliWorkItemIdPrefix.Length);

        // Parse --mode flag. Valid values: "workitem" | "chat". Absent or unknown → InvalidOperationException.
        var modeArg = args
            .FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];

        bool isWorkItemMode;
        if (modeArg != null && string.Equals(modeArg, "workitem", StringComparison.OrdinalIgnoreCase))
        {
            if (workItemId is null)
                throw new InvalidOperationException(
                    "--mode=workitem was set but --work-item-id is absent. Both flags must be present for work-item mode.");
            isWorkItemMode = true;
        }
        else if (modeArg != null && string.Equals(modeArg, "chat", StringComparison.OrdinalIgnoreCase))
        {
            isWorkItemMode = false;
        }
        else if (modeArg is null)
        {
            throw new InvalidOperationException(
                "--mode is required. Valid values: workitem | chat.");
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown --mode value '{modeArg}'. Valid values: workitem | chat.");
        }

        // Read API key: prefer AGENT_API_KEY_FILE (K8s Secret mount), fall back to AGENT_API_KEY env var.
        string agentApiKey;
        var apiKeyFilePath = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKeyFile);
        if (!string.IsNullOrEmpty(apiKeyFilePath))
        {
            agentApiKey = (await File.ReadAllTextAsync(apiKeyFilePath)).Trim();
        }
        else
        {
            agentApiKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey)
                ?? throw new InvalidOperationException(
                    $"Neither {AgentDefaults.EnvAgentApiKeyFile} nor {AgentDefaults.EnvAgentApiKey} is set.");
        }

        var orchestratorUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl)
            ?? throw new InvalidOperationException("ORCHESTRATOR_URL environment variable is required");
        var agentId = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentId)
            ?? Environment.MachineName;

        return new AgentStartupConfig
        {
            AgentApiKey = agentApiKey,
            OrchestratorUrl = orchestratorUrl,
            AgentId = agentId,
            WorkItemId = workItemId,
            IsWorkItemMode = isWorkItemMode
        };
    }
}
