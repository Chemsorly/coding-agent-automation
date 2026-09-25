using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Agent.OpenCode;

public sealed partial class OpenCodeAgentProvider
{
    /// <summary>
    /// Environment variable keys that MUST NOT be passed to MCP server child processes.
    /// </summary>
    private static readonly HashSet<string> ExcludedEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentDefaults.EnvOpenCodeServerPassword,
        AgentDefaults.EnvAnthropicApiKey,
        AgentDefaults.EnvOpenAiApiKey,
        AgentDefaults.EnvOpenRouterApiKey
    };

    internal async Task RegisterMcpServersAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken ct)
    {
        var enabledServers = servers.Where(s => !s.Disabled).ToList();

        foreach (var server in enabledServers)
        {
            try
            {
                object config;

                if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: Headers passed to the OpenCode API are not filtered through an equivalent of
                    // ExcludedEnvKeys. The stdio path explicitly strips sensitive keys (Anthropic, OpenAI,
                    // OpenRouter API keys, OpenCode server password) before forwarding env vars. The HTTP
                    // header path has no such guard — a user who sets e.g. Authorization=Bearer <ANTHROPIC_API_KEY>
                    // will have that value forwarded verbatim to the external HTTP MCP server. Consider applying
                    // a header-key filter analogous to ExcludedEnvKeys to prevent accidental credential leakage.
                    config = new McpHttpConfig
                    {
                        Url = server.Url ?? string.Empty,
                        Headers = server.Headers.Count > 0 ? server.Headers : null
                    };
                }
                else
                {
                    // stdio (default)
                    // TODO: [WARNING] filteredEnv strips LLM API-key variables (ExcludedEnvKeys) but does NOT
                    // strip OTEL_*, TRACEPARENT, or TRACESTATE. These keys are forwarded verbatim as the `env`
                    // field of the OpenCode MCP stdio config, so any OTEL variables present in the agent pod
                    // environment (including the OTLP write token in OTEL_EXPORTER_OTLP_HEADERS) reach every
                    // stdio MCP server child process launched by the OpenCode daemon. This is a parallel
                    // credential-exposure and data-pollution path to the one fixed in issue #2968. Fix by also
                    // filtering keys that match the OTEL_ prefix or equal TRACEPARENT/TRACESTATE, or by calling
                    // ChildProcessEnvironment-equivalent logic on filteredEnv before building McpStdioConfig.
                    var filteredEnv = server.Env
                        .Where(kvp => !ExcludedEnvKeys.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    config = new McpStdioConfig
                    {
                        Command = server.Command ?? string.Empty,
                        Args = server.Args,
                        Env = filteredEnv
                    };
                }

                var request = new RegisterMcpRequest
                {
                    Name = server.Name,
                    Config = config
                };

                using var client = _httpClientFactory.CreateClient(AgentDefaults.OpenCodeHttpClientName);
                var response = await client.PostAsJsonAsync("/mcp", request, OpenCodeJson.JsonOptions, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Failed to register MCP server {ServerName}", server.Name);
            }
        }
    }
}
