using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent;

/// <summary>
/// Utility for writing MCP (Model Context Protocol) server configuration files.
/// Consolidates the shared logic previously duplicated in <see cref="AgentWorkerService"/>
/// and <see cref="LocalPipelineExecutor"/>.
/// Supports stdio (command-based), HTTP (URL-based), and SSE (Server-Sent Events URL-based) server types.
/// </summary>
public static class McpConfigWriter
{
    /// <summary>
    /// Writes MCP server configuration to the specified file path.
    /// Creates the parent directory if it does not exist.
    /// </summary>
    /// <param name="fullPath">The full file path where the MCP config JSON will be written.</param>
    /// <param name="servers">The list of MCP server configurations to write.</param>
    public static void WriteConfig(string fullPath, IReadOnlyList<McpServerConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(servers);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var serversDict = new Dictionary<string, object>();
        foreach (var server in servers)
        {
            if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                if (server.Headers.Count > 0)
                {
                    serversDict[server.Name] = new
                    {
                        type = server.Type,
                        url = server.Url,
                        headers = server.Headers,
                        disabled = server.Disabled,
                        autoApprove = server.AutoApprove
                    };
                }
                else
                {
                    serversDict[server.Name] = new
                    {
                        type = server.Type,
                        url = server.Url,
                        disabled = server.Disabled,
                        autoApprove = server.AutoApprove
                    };
                }
            }
            else
            {
                serversDict[server.Name] = new
                {
                    command = server.Command,
                    args = server.Args,
                    env = server.Env,
                    disabled = server.Disabled,
                    autoApprove = server.AutoApprove
                };
            }
        }

        var mcpConfig = new { mcpServers = serversDict };
        var json = JsonSerializer.Serialize(mcpConfig, PipelineJsonOptions.Default);

        File.WriteAllText(fullPath, json);
    }

    /// <summary>
    /// Writes <paramref name="servers"/> in the format <paramref name="providerType"/> reads.
    /// </summary>
    /// <param name="fullPath">The full file path where the MCP config JSON will be written.</param>
    /// <param name="servers">The list of MCP server configurations to write.</param>
    /// <param name="providerType">The agent provider that will read the file.</param>
    public static void WriteConfig(string fullPath, IReadOnlyList<McpServerConfig> servers, AgentProviderType providerType)
    {
        if (providerType == AgentProviderType.ClaudeCode)
            WriteClaudeCodeConfig(fullPath, servers);
        else if (providerType == AgentProviderType.OpenCode)
            WriteOpenCodeConfig(fullPath, servers);
        else
            WriteConfig(fullPath, servers);
    }

    /// <summary>
    /// Writes an OpenCode config file (<c>~/.opencode/opencode.json</c>) with an <c>mcp</c> section, the
    /// only place OpenCode reads MCP servers from: local servers as
    /// <c>{"type":"local","command":[command, ...args],"environment":{...}}</c>, remote ones as
    /// <c>{"type":"remote","url":"...","headers":{...}}</c>, disabled ones with <c>"enabled": false</c>.
    /// </summary>
    public static void WriteOpenCodeConfig(string fullPath, IReadOnlyList<McpServerConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(servers);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var mcp = new Dictionary<string, Dictionary<string, object>>();
        foreach (var server in servers)
        {
            var entry = new Dictionary<string, object>();
            if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                entry["type"] = "remote";
                entry["url"] = server.Url ?? string.Empty;
                if (server.Headers.Count > 0)
                    entry["headers"] = server.Headers;
            }
            else
            {
                entry["type"] = "local";
                entry["command"] = new[] { server.Command ?? string.Empty }.Concat(server.Args).ToArray();
                if (server.Env.Count > 0)
                    entry["environment"] = server.Env;
            }
            entry["enabled"] = !server.Disabled;
            mcp[server.Name] = entry;
        }

        var config = new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["mcp"] = mcp
        };
        File.WriteAllText(fullPath, JsonSerializer.Serialize(config, PipelineJsonOptions.Default));
    }

    /// <summary>
    /// Writes an <c>--mcp-config</c> file for the Claude Code CLI. The CLI validates each entry and
    /// skips one with unknown fields, and has no <c>disabled</c> flag, so disabled servers are left
    /// out and <c>disabled</c> / <c>autoApprove</c> are not written (the CLI runs with all tools allowed).
    /// </summary>
    public static void WriteClaudeCodeConfig(string fullPath, IReadOnlyList<McpServerConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(servers);

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var serversDict = new Dictionary<string, object>();
        foreach (var server in servers.Where(s => !s.Disabled))
        {
            if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                // An entry with a url but no type is read as a stdio server, so type is always written.
                serversDict[server.Name] = server.Headers.Count > 0
                    ? new { type = server.Type.ToLowerInvariant(), url = server.Url, headers = server.Headers }
                    : new { type = server.Type.ToLowerInvariant(), url = server.Url };
            }
            else
            {
                serversDict[server.Name] = new
                {
                    type = "stdio",
                    command = server.Command,
                    args = server.Args,
                    env = server.Env
                };
            }
        }

        var json = JsonSerializer.Serialize(new { mcpServers = serversDict }, PipelineJsonOptions.Default);
        File.WriteAllText(fullPath, json);
    }
}
