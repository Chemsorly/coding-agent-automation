using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

public class ClaudeCodeMcpConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-mcp-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteConfig_ClaudeCode_WritesOnlyFieldsTheCliAccepts()
    {
        var path = Path.Combine(_tempDir, "nested", "mcp.json");
        McpServerConfig[] servers =
        [
            new()
            {
                Name = "fs", Command = "npx", Args = ["-y", "server-fs"],
                Env = new Dictionary<string, string> { ["ROOT"] = "/w" }, AutoApprove = ["read"]
            },
            new()
            {
                Name = "remote", Type = "HTTP", Url = "https://mcp.example",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" }
            },
            new() { Name = "events", Type = "sse", Url = "https://mcp.example/sse" },
            new() { Name = "disabled", Command = "nope", Disabled = true }
        ];

        McpConfigWriter.WriteConfig(path, servers, AgentProviderType.ClaudeCode);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var mcpServers = doc.RootElement.GetProperty("mcpServers");
        mcpServers.EnumerateObject().Select(p => p.Name).Should().Equal("fs", "remote", "events");

        var fs = mcpServers.GetProperty("fs");
        fs.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "command", "args", "env"]);
        fs.GetProperty("type").GetString().Should().Be("stdio");
        fs.GetProperty("env").GetProperty("ROOT").GetString().Should().Be("/w");

        var remote = mcpServers.GetProperty("remote");
        remote.GetProperty("type").GetString().Should().Be("http");
        remote.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer x");

        mcpServers.GetProperty("events").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "url"]);
    }

    [Fact]
    public void WriteConfig_KiroCli_KeepsTheExistingFormat()
    {
        var path = Path.Combine(_tempDir, "mcp.json");

        McpConfigWriter.WriteConfig(path, [new McpServerConfig { Name = "off", Command = "x", Disabled = true }], AgentProviderType.KiroCli);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("mcpServers").GetProperty("off").GetProperty("disabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void WriteConfig_OpenCode_WritesItsConfigFileMcpSection()
    {
        // OpenCode 1.18 reads MCP servers only from an opencode.json "mcp" section, in its own schema.
        var path = Path.Combine(_tempDir, "opencode.json");

        McpConfigWriter.WriteConfig(path,
        [
            new McpServerConfig { Name = "local", Command = "uvx", Args = ["mcp-server", "--flag"], Env = new Dictionary<string, string> { ["TOKEN"] = "t" } },
            new McpServerConfig { Name = "remote", Type = "http", Url = "https://mcp.example.com/mcp", Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" } },
            new McpServerConfig { Name = "off", Command = "x", Disabled = true }
        ], AgentProviderType.OpenCode);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var mcp = doc.RootElement.GetProperty("mcp");
        var local = mcp.GetProperty("local");
        local.GetProperty("type").GetString().Should().Be("local");
        local.GetProperty("command").EnumerateArray().Select(e => e.GetString()).Should().Equal("uvx", "mcp-server", "--flag");
        local.GetProperty("environment").GetProperty("TOKEN").GetString().Should().Be("t");
        local.GetProperty("enabled").GetBoolean().Should().BeTrue();
        var remote = mcp.GetProperty("remote");
        remote.GetProperty("type").GetString().Should().Be("remote");
        remote.GetProperty("url").GetString().Should().Be("https://mcp.example.com/mcp");
        remote.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer x");
        mcp.GetProperty("off").GetProperty("enabled").GetBoolean().Should().BeFalse();
        // No one answers the question tool: a rejected question must not end the turn as if done.
        doc.RootElement.GetProperty("experimental").GetProperty("continue_loop_on_deny").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RemoveStaleConfig_OpenCode_DropsTheEarlierJobsServers_AndKeepsTheSteering()
    {
        var path = Path.Combine(_tempDir, "opencode.json");
        Directory.CreateDirectory(_tempDir);
        // JSONC, as OpenCode accepts it: a comment and a trailing comma.
        File.WriteAllText(path, """
            {
              // written by an earlier job
              "instructions": ["/home/u/.opencode/pipeline-*.md"],
              "mcp": { "projectA": { "type": "remote", "url": "https://a", "headers": { "Authorization": "Bearer a" } } },
            }
            """);

        McpConfigWriter.RemoveStaleConfig(path, AgentProviderType.OpenCode);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.TryGetProperty("mcp", out _).Should().BeFalse();
        doc.RootElement.GetProperty("instructions").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("/home/u/.opencode/pipeline-*.md");
    }

    [Theory]
    [InlineData(AgentProviderType.ClaudeCode, false)]
    [InlineData(AgentProviderType.KiroCli, true)]
    public void RemoveStaleConfig_DeletesOnlyThePipelinesOwnFile(AgentProviderType providerType, bool kept)
    {
        var path = Path.Combine(_tempDir, "mcp.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, """{"mcpServers":{}}""");

        McpConfigWriter.RemoveStaleConfig(path, providerType);

        File.Exists(path).Should().Be(kept);
    }
}
