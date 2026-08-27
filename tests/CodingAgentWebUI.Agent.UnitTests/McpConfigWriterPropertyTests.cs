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
    /// For any list of McpServerConfig instances (mixing stdio, HTTP, and SSE types),
    /// McpConfigWriter.WriteConfig produces valid JSON where every server entry contains
    /// either (command + args) for stdio servers or (url) for HTTP/SSE servers.
    /// **Validates: Requirements 34.1, 34.2, 34.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(McpServerConfigArbitrary)])]
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

                // TODO: This branch relies on the closed-world assumption that the generator only emits
                // "stdio", "http", or "sse". If a future generator change introduces an unrecognised type,
                // the else-branch would silently accept whatever the writer emits rather than failing.
                // Consider adding Assert.Contains(new[]{"stdio","http","sse"}, server.Type) before this
                // branch to make the invariant explicit and catch generator drift early.
                if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
                {
                    // HTTP and SSE servers must have url property
                    Assert.True(entry.TryGetProperty("url", out _),
                        $"HTTP/SSE server '{name}' missing 'url' property");
                    // headers must appear iff non-empty
                    var hasHeaders = entry.TryGetProperty("headers", out _);
                    Assert.Equal(server.Headers.Count > 0, hasHeaders);
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
    [Property(MaxTest = 20, Arbitrary = [typeof(McpServerConfigArbitrary)])]
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

    /// <summary>
    /// An SSE-type server with a URL and headers must produce a JSON entry with
    /// "type": "sse", "url", and "headers" — not "command" or "args".
    /// Verifies acceptance criterion: McpConfigWriter serializes Url and Headers for "sse" server types.
    /// </summary>
    // TODO: Case-insensitive matching is not covered by the tests below. The production condition uses
    // StringComparison.OrdinalIgnoreCase, so "SSE" and "Sse" should also route to the URL branch.
    // Consider adding a test with Type = "SSE" (uppercase) to guard against a future accidental removal
    // of OrdinalIgnoreCase from the condition in McpConfigWriter.WriteConfig.
    [Fact]
    public void WriteConfig_SseType_WithHeaders_ProducesUrlAndHeadersEntry()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "sse-server",
            Type = "sse",
            Url = "http://mcp-server:8080/sse",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer token123"
            }
        };

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempFile = Path.Combine(tempDir, "mcp.json");

        try
        {
            // Act
            McpConfigWriter.WriteConfig(tempFile, [server]);

            // Assert
            var json = File.ReadAllText(tempFile);
            var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("sse-server");

            // Must have type=sse (not "http")
            Assert.Equal("sse", entry.GetProperty("type").GetString());
            // Must have url
            Assert.Equal("http://mcp-server:8080/sse", entry.GetProperty("url").GetString());
            // Must have headers
            Assert.True(entry.TryGetProperty("headers", out var headers),
                "Expected 'headers' property to be present");
            Assert.Equal("Bearer token123", headers.GetProperty("Authorization").GetString());
            // Must NOT have command or args
            Assert.False(entry.TryGetProperty("command", out _),
                "SSE entry must not contain 'command'");
            Assert.False(entry.TryGetProperty("args", out _),
                "SSE entry must not contain 'args'");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// An SSE-type server with no headers must produce a JSON entry with
    /// "type": "sse" and "url" but without a "headers" key — matching the behavior of HTTP servers
    /// with empty headers.
    /// </summary>
    [Fact]
    public void WriteConfig_SseType_NoHeaders_OmitsHeadersProperty()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "sse-server-noheaders",
            Type = "sse",
            Url = "http://mcp-server:8080/sse"
            // Headers defaults to empty dictionary
        };

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempFile = Path.Combine(tempDir, "mcp.json");

        try
        {
            // Act
            McpConfigWriter.WriteConfig(tempFile, [server]);

            // Assert
            var json = File.ReadAllText(tempFile);
            var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("sse-server-noheaders");

            // Must have type=sse
            Assert.Equal("sse", entry.GetProperty("type").GetString());
            // Must have url
            Assert.Equal("http://mcp-server:8080/sse", entry.GetProperty("url").GetString());
            // Must NOT have headers (empty headers are omitted)
            Assert.False(entry.TryGetProperty("headers", out _),
                "Expected 'headers' property to be absent when headers are empty");
            // Must NOT have command or args
            Assert.False(entry.TryGetProperty("command", out _),
                "SSE entry must not contain 'command'");
            Assert.False(entry.TryGetProperty("args", out _),
                "SSE entry must not contain 'args'");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}

/// <summary>
/// FsCheck arbitrary that generates random McpServerConfig instances mixing stdio, HTTP, and SSE types.
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

    private static readonly string[] HeaderKeys =
    [
        "Authorization", "X-Org", "X-Api-Key", "X-Custom-Header"
    ];

    private static readonly string[] HeaderValues =
    [
        "Bearer token123", "myorg", "apikey-abc", "custom-value"
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
            from headerCount in Gen.Choose(0, 2)
            from headerKeys in Gen.ArrayOf(Gen.Elements(HeaderKeys), headerCount)
            from headerVals in Gen.ArrayOf(Gen.Elements(HeaderValues), headerCount)
            let headers = headerKeys.Zip(headerVals).DistinctBy(p => p.First).ToDictionary(p => p.First, p => p.Second)
            select new McpServerConfig
            {
                Name = name,
                Type = "http",
                Url = url,
                Disabled = disabled,
                Headers = headers
            };

        var sseGen =
            from name in Gen.Elements(ServerNames)
            from url in Gen.Elements(Urls)
            from disabled in boolGen
            from headerCount in Gen.Choose(0, 2)
            from headerKeys in Gen.ArrayOf(Gen.Elements(HeaderKeys), headerCount)
            from headerVals in Gen.ArrayOf(Gen.Elements(HeaderValues), headerCount)
            let headers = headerKeys.Zip(headerVals).DistinctBy(p => p.First).ToDictionary(p => p.First, p => p.Second)
            select new McpServerConfig
            {
                Name = name,
                Type = "sse",
                Url = url,
                Disabled = disabled,
                Headers = headers
            };

        var serverGen = Gen.OneOf(stdioGen, httpGen, sseGen);
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
