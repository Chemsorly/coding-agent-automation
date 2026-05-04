using System.Text.Json;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Utility for writing MCP (Model Context Protocol) server configuration files.
/// Consolidates the shared logic previously duplicated in <see cref="AgentWorkerService"/>
/// and <see cref="LocalPipelineExecutor"/>.
/// Supports both stdio (command-based) and HTTP (URL-based) server types.
/// </summary>
public static class McpConfigWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
            if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase))
            {
                serversDict[server.Name] = new
                {
                    type = "http",
                    url = server.Url,
                    disabled = server.Disabled,
                    autoApprove = server.AutoApprove
                };
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
        var json = JsonSerializer.Serialize(mcpConfig, SerializerOptions);

        File.WriteAllText(fullPath, json);
    }
}
