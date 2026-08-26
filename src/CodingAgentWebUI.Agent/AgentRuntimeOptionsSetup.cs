using CodingAgentWebUI.Pipeline;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Configures <see cref="AgentRuntimeOptions"/> from the raw environment variables that the
/// agent container spec injects. Called at startup via <c>services.AddOptions&lt;AgentRuntimeOptions&gt;()</c>.
///
/// Uses direct env reads here (bootstrap time, before IConfiguration is available) rather than
/// binding via <c>IConfiguration</c> so that the flat env var names (e.g. <c>AGENT_CHAT_MODE</c>)
/// are preserved without requiring IConfiguration section configuration.
/// </summary>
internal sealed class AgentRuntimeOptionsSetup : IConfigureOptions<AgentRuntimeOptions>
{
    public void Configure(AgentRuntimeOptions options)
    {
        options.IsChatMode = string.Equals(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatMode), "true",
            StringComparison.OrdinalIgnoreCase);

        options.ChatSessionId =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatSessionId) ?? "";

        options.AgentLabels =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentLabels) ?? "";

        options.ChatModel =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatModel);

        options.ChatEffort =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvChatEffort);

        options.AgentProviderType =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "";

        options.OpenCodeBaseUrl =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeBaseUrl);

        options.OpenCodeServerPassword =
            Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword);

        var kiroPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        if (!string.IsNullOrEmpty(kiroPath))
            options.KiroCliPath = kiroPath;
    }
}
