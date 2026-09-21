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
