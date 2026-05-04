using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Property-based tests for McpConfigWriter.
/// Feature: 018-encapsulation-improvements, Property 4: MCP config writer determinism
/// </summary>
public class McpConfigWriterPropertyTests
{
    /// <summary>
    /// Feature: 018-encapsulation-improvements, Property 4: MCP config writer determinism
    /// For any list of McpServerConfig instances (mixing stdio and HTTP types),
    /// McpConfigWriter.WriteConfig produces valid JSON where every server entry contains
    /// either (command + args) for stdio servers or (url) for HTTP servers.
    /// **Validates: Requirements 34.1, 34.2, 34.5**
    /// </summary>
    [Property(Arbitrary = [typeof(McpServerConfigArbitrary)])]
    public void WriteConfig_Produces_Valid_Json_With_Correct_Structure(List<McpServerConfig> servers)
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempFile = Path.Combine(tempDir, "mcp.json");

        try
        {
            // Act
            McpConfigWriter.WriteConfig(tempFile, servers);

            // Assert — output is valid JSON
            var json = File.ReadAllText(tempFile);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Assert — root has mcpServers property
            Assert.True(root.TryGetProperty("mcpServers", out var mcpServers));
            Assert.Equal(JsonValueKind.Object, mcpServers.ValueKind);

            // Assert — correct number of server entries
            var serverCount = 0;
            foreach (var _ in mcpServers.EnumerateObject())
                serverCount++;
            var expectedCount = servers.Select(s => s.Name).Distinct().Count();
            Assert.Equal(expectedCount, serverCount);

            // Assert — each server has correct structure based on type
            // When duplicate names exist, the last entry wins (dictionary behavior)
            var lastByName = new Dictionary<string, McpServerConfig>();
            foreach (var server in servers)
                lastByName[server.Name] = server;

            foreach (var (name, server) in lastByName)
            {
                Assert.True(mcpServers.TryGetProperty(name, out var entry),
                    $"Server '{name}' missing from output");

                if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase))
                {
                    // HTTP servers must have url property
                    Assert.True(entry.TryGetProperty("url", out _),
                        $"HTTP server '{name}' missing 'url' property");
                }
                else
                {
                    // Stdio servers must have command and args properties
                    Assert.True(entry.TryGetProperty("command", out _),
                        $"Stdio server '{name}' missing 'command' property");
                    Assert.True(entry.TryGetProperty("args", out _),
                        $"Stdio server '{name}' missing 'args' property");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Feature: 018-encapsulation-improvements, Property 4: MCP config writer determinism
    /// For any list of McpServerConfig instances, calling WriteConfig twice with the same
    /// input produces identical output (deterministic).
    /// **Validates: Requirements 34.1, 34.2, 34.5**
    /// </summary>
    [Property(Arbitrary = [typeof(McpServerConfigArbitrary)])]
    public void WriteConfig_Is_Deterministic_Same_Input_Same_Output(List<McpServerConfig> servers)
    {
        // Arrange
        var tempDir1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempDir2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempFile1 = Path.Combine(tempDir1, "mcp.json");
        var tempFile2 = Path.Combine(tempDir2, "mcp.json");

        try
        {
            // Act — write twice with same input
            McpConfigWriter.WriteConfig(tempFile1, servers);
            McpConfigWriter.WriteConfig(tempFile2, servers);

            // Assert — outputs are identical
            var json1 = File.ReadAllText(tempFile1);
            var json2 = File.ReadAllText(tempFile2);
            Assert.Equal(json1, json2);
        }
        finally
        {
            if (Directory.Exists(tempDir1))
                Directory.Delete(tempDir1, recursive: true);
            if (Directory.Exists(tempDir2))
                Directory.Delete(tempDir2, recursive: true);
        }
    }
}

/// <summary>
/// FsCheck arbitrary that generates random McpServerConfig instances mixing stdio and HTTP types.
/// </summary>
public static class McpServerConfigArbitrary
{
    private static readonly string[] ServerNames =
    [
        "context7", "web-search", "sequential-thinking", "github-actions",
        "redis-mcp", "postgres-mcp", "docker-mcp", "filesystem-mcp",
        "memory-mcp", "brave-search"
    ];

    private static readonly string[] Commands =
    [
        "uvx", "npx", "node", "python", "dotnet", "docker"
    ];

    private static readonly string[] ArgValues =
    [
        "context7-mcp", "--stdio", "server.js", "-m", "run", "--port", "8080"
    ];

    private static readonly string[] Urls =
    [
        "https://mcp.context7.com/mcp",
        "http://localhost:3000/mcp",
        "https://api.example.com/mcp/v1",
        "http://mcp-server:8080/sse"
    ];

    public static Arbitrary<McpServerConfig> McpServerConfigs()
    {
        var boolGen = Gen.Elements(true, false);

        var stdioGen =
            from name in Gen.Elements(ServerNames)
            from command in Gen.Elements(Commands)
            from argCount in Gen.Choose(0, 3)
            from args in Gen.ArrayOf(Gen.Elements(ArgValues), argCount)
            from disabled in boolGen
            select new McpServerConfig
            {
                Name = name,
                Type = "stdio",
                Command = command,
                Args = args.ToList(),
                Disabled = disabled
            };

        var httpGen =
            from name in Gen.Elements(ServerNames)
            from url in Gen.Elements(Urls)
            from disabled in boolGen
            select new McpServerConfig
            {
                Name = name,
                Type = "http",
                Url = url,
                Disabled = disabled
            };

        var serverGen = Gen.OneOf(stdioGen, httpGen);
        return serverGen.ToArbitrary();
    }

    public static Arbitrary<List<McpServerConfig>> McpServerConfigLists()
    {
        var listGen =
            from count in Gen.Choose(0, 8)
            from servers in Gen.ArrayOf(McpServerConfigs().Generator, count)
            select servers.ToList();

        return listGen.ToArbitrary();
    }
}
