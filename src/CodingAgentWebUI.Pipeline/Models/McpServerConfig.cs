using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for a single MCP (Model Context Protocol) server that can be
/// attached to an agent profile and written to the workspace at runtime.
/// Supports two transport types: stdio (command-based) and HTTP (URL-based).
/// </summary>
[MessagePackObject]
public sealed record McpServerConfig
{
    /// <summary>Unique name identifying this MCP server (e.g., "context7", "web-search").</summary>
    [Key(0)] public required string Name { get; init; }

    /// <summary>
    /// Transport type: "stdio" (default, command-based) or "http" (URL-based).
    /// When "stdio": Command + Args are used to start the server process.
    /// When "http": Url is used to connect to a remote MCP server.
    /// </summary>
    [Key(1)] public string Type { get; init; } = "stdio";

    /// <summary>Executable command for stdio servers (e.g., "uvx", "npx"). Null for HTTP servers.</summary>
    [Key(2)] public string? Command { get; init; }

    /// <summary>Arguments for the command. Only used for stdio servers.</summary>
    [Key(3)] public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>URL for HTTP-based MCP servers (e.g., "https://mcp.context7.com/mcp"). Null for stdio servers.</summary>
    [Key(4)] public string? Url { get; init; }

    /// <summary>Environment variables passed to stdio server processes.</summary>
    [Key(5)] public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether this server is disabled (present in config but not started).</summary>
    [Key(6)] public bool Disabled { get; init; } = false;

    /// <summary>Tool names to auto-approve (redundant when --trust-all-tools is used, kept for schema compat).</summary>
    [Key(7)] public IReadOnlyList<string> AutoApprove { get; init; } = [];
}
