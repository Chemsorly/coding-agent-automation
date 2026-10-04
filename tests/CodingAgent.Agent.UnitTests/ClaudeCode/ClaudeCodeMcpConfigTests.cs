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

    [Theory]
    [InlineData(AgentProviderType.KiroCli)]
    [InlineData(AgentProviderType.OpenCode)]
    public void WriteConfig_OtherProviders_KeepTheExistingFormat(AgentProviderType providerType)
    {
        var path = Path.Combine(_tempDir, "mcp.json");

        McpConfigWriter.WriteConfig(path, [new McpServerConfig { Name = "off", Command = "x", Disabled = true }], providerType);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("mcpServers").GetProperty("off").GetProperty("disabled").GetBoolean().Should().BeTrue();
    }
}
