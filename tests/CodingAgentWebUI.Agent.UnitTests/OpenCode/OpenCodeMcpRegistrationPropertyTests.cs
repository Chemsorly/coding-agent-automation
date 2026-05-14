using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for MCP server registration (Property 10).
/// Verifies that RegisterMcpServersAsync correctly:
/// (a) filters out disabled servers,
/// (b) produces { command, args, env } config for stdio-type servers,
/// (c) produces { url } config for http-type servers,
/// (d) excludes keys in ExcludedEnvKeys from the env object,
/// (e) continues registering remaining servers if any individual registration fails.
///
/// **Validates: Requirements 9.1, 9.3, 9.4, 9.5, 9.6**
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "10")]
public class OpenCodeMcpRegistrationPropertyTests
{
    private static readonly string[] ExcludedEnvKeys =
    [
        "OPENCODE_SERVER_PASSWORD",
        "ANTHROPIC_API_KEY",
        "OPENAI_API_KEY",
        "OPENROUTER_API_KEY"
    ];

    /// <summary>
    /// Property 10a: Disabled servers are filtered out — no POST /mcp is sent for them.
    /// For any list of McpServerConfig entries, only entries where Disabled == false
    /// result in outbound POST /mcp requests.
    /// **Validates: Requirements 9.1**
    /// </summary>
    [Property(Arbitrary = [typeof(McpRegistrationArbitrary)], MaxTest = 100)]
    public void DisabledServers_AreFilteredOut_NoPOSTSent(List<McpServerConfig> servers)
    {
        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        // Enqueue OK responses for all enabled servers
        var enabledCount = servers.Count(s => !s.Disabled);
        for (var i = 0; i < enabledCount; i++)
            handler.EnqueueOk();

        // Act
        provider.RegisterMcpServersAsync(servers, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — number of POST requests equals number of enabled servers
        var postRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/mcp")
            .ToList();

        postRequests.Should().HaveCount(enabledCount);
    }

    /// <summary>
    /// Property 10b: Stdio-type servers produce config with command, args, and env fields.
    /// For any enabled stdio server, the POST body contains a config object with
    /// "command", "args", and "env" properties.
    /// **Validates: Requirements 9.3**
    /// </summary>
    [Property(Arbitrary = [typeof(McpRegistrationArbitrary)], MaxTest = 100)]
    public void StdioServers_ProduceCorrectConfigShape(McpServerConfig server)
    {
        // Only test stdio servers that are enabled
        if (server.Disabled || string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
            return;

        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);
        handler.EnqueueOk();

        // Act
        provider.RegisterMcpServersAsync([server], CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        var postRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/mcp")
            .ToList();
        postRequests.Should().HaveCount(1);

        var body = postRequests[0].Body;
        body.Should().NotBeNull();

        var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;

        // Verify name
        root.GetProperty("name").GetString().Should().Be(server.Name);

        // Verify config has command, args, env
        var config = root.GetProperty("config");
        config.TryGetProperty("command", out _).Should().BeTrue("stdio config must have 'command'");
        config.TryGetProperty("args", out _).Should().BeTrue("stdio config must have 'args'");
        config.TryGetProperty("env", out _).Should().BeTrue("stdio config must have 'env'");

        // Verify command value
        config.GetProperty("command").GetString().Should().Be(server.Command ?? string.Empty);

        // Verify args array
        var argsArray = config.GetProperty("args");
        argsArray.GetArrayLength().Should().Be(server.Args.Count);
    }

    /// <summary>
    /// Property 10c: HTTP-type servers produce config with url field only.
    /// For any enabled http/sse server, the POST body contains a config object with "url" property.
    /// **Validates: Requirements 9.4**
    /// </summary>
    [Property(Arbitrary = [typeof(McpRegistrationArbitrary)], MaxTest = 100)]
    public void HttpServers_ProduceCorrectConfigShape(McpServerConfig server)
    {
        // Only test http/sse servers that are enabled
        if (server.Disabled ||
            (!string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase)))
            return;

        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);
        handler.EnqueueOk();

        // Act
        provider.RegisterMcpServersAsync([server], CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        var postRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/mcp")
            .ToList();
        postRequests.Should().HaveCount(1);

        var body = postRequests[0].Body;
        body.Should().NotBeNull();

        var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;

        // Verify name
        root.GetProperty("name").GetString().Should().Be(server.Name);

        // Verify config has url
        var config = root.GetProperty("config");
        config.TryGetProperty("url", out _).Should().BeTrue("http config must have 'url'");
        config.GetProperty("url").GetString().Should().Be(server.Url ?? string.Empty);
    }

    /// <summary>
    /// Property 10d: ExcludedEnvKeys are removed from the env object for stdio servers.
    /// For any enabled stdio server with env vars that include excluded keys,
    /// those keys must NOT appear in the outbound POST body's config.env.
    /// **Validates: Requirements 9.5**
    /// </summary>
    [Property(Arbitrary = [typeof(McpRegistrationArbitrary)], MaxTest = 100)]
    public void ExcludedEnvKeys_AreRemovedFromStdioConfig(McpServerConfig server)
    {
        // Only test enabled stdio servers that have env vars
        if (server.Disabled || string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(server.Type, "sse", StringComparison.OrdinalIgnoreCase))
            return;

        if (server.Env.Count == 0)
            return;

        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);
        handler.EnqueueOk();

        // Act
        provider.RegisterMcpServersAsync([server], CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        var postRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/mcp")
            .ToList();
        postRequests.Should().HaveCount(1);

        var body = postRequests[0].Body;
        body.Should().NotBeNull();

        var doc = JsonDocument.Parse(body!);
        var config = doc.RootElement.GetProperty("config");
        var envObj = config.GetProperty("env");

        // Verify excluded keys are NOT present (case-insensitive check)
        foreach (var excludedKey in ExcludedEnvKeys)
        {
            foreach (var prop in envObj.EnumerateObject())
            {
                prop.Name.Should().NotBeEquivalentTo(excludedKey,
                    $"excluded key '{excludedKey}' must not appear in env object");
            }
        }

        // Verify non-excluded keys ARE present
        var expectedKeys = server.Env
            .Where(kvp => !ExcludedEnvKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var kvp in expectedKeys)
        {
            envObj.TryGetProperty(kvp.Key, out var val).Should().BeTrue(
                $"non-excluded key '{kvp.Key}' should be present in env");
            val.GetString().Should().Be(kvp.Value);
        }
    }

    /// <summary>
    /// Property 10e: Registration continues for remaining servers if one fails.
    /// For any list of enabled servers where one server's POST returns an error,
    /// the remaining servers still get registered.
    /// **Validates: Requirements 9.6**
    /// </summary>
    [Property(Arbitrary = [typeof(McpRegistrationArbitrary)], MaxTest = 100)]
    public void RegistrationContinues_WhenOneServerFails(List<McpServerConfig> servers)
    {
        // Filter to only enabled servers for this test
        var enabledServers = servers.Where(s => !s.Disabled).ToList();
        if (enabledServers.Count < 2)
            return; // Need at least 2 enabled servers to test failure resilience

        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        // First server fails, rest succeed
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "{\"error\":\"server error\"}");
        for (var i = 1; i < enabledServers.Count; i++)
            handler.EnqueueOk();

        // Act — should not throw despite first server failing
        provider.RegisterMcpServersAsync(servers, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — all enabled servers attempted (one POST per enabled server)
        var postRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/mcp")
            .ToList();

        postRequests.Should().HaveCount(enabledServers.Count,
            "all enabled servers should be attempted even if one fails");
    }
}

/// <summary>
/// FsCheck arbitrary that generates random McpServerConfig instances for MCP registration tests.
/// Generates configs with varying Disabled flags, types (stdio/http/sse), env vars including
/// excluded keys to verify filtering behavior.
/// </summary>
public static class McpRegistrationArbitrary
{
    private static readonly string[] ServerNames =
    [
        "context7", "web-search", "sequential-thinking", "github-actions",
        "redis-mcp", "postgres-mcp", "docker-mcp", "filesystem-mcp",
        "memory-mcp", "brave-search", "custom-tool", "data-pipeline"
    ];

    private static readonly string[] Commands =
    [
        "uvx", "npx", "node", "python", "dotnet", "docker", "cargo"
    ];

    private static readonly string[] ArgValues =
    [
        "context7-mcp", "--stdio", "server.js", "-m", "run", "--port", "8080", "--verbose"
    ];

    private static readonly string[] Urls =
    [
        "https://mcp.context7.com/mcp",
        "http://localhost:3000/mcp",
        "https://api.example.com/mcp/v1",
        "http://mcp-server:8080/sse"
    ];

    /// <summary>
    /// All possible env var keys — mix of safe and excluded keys.
    /// Includes case variations to test case-insensitive filtering.
    /// </summary>
    private static readonly string[] AllEnvKeys =
    [
        "NODE_ENV", "PATH", "HOME", "CUSTOM_TOKEN", "MCP_DEBUG", "LOG_LEVEL",
        "OPENCODE_SERVER_PASSWORD", "opencode_server_password", "Opencode_Server_Password",
        "ANTHROPIC_API_KEY", "anthropic_api_key", "Anthropic_Api_Key",
        "OPENAI_API_KEY", "openai_api_key", "Openai_Api_Key",
        "OPENROUTER_API_KEY", "openrouter_api_key", "Openrouter_Api_Key"
    ];

    private static readonly string[] EnvValues =
    [
        "production", "development", "/usr/local/bin", "sk-abc123", "true", "debug"
    ];

    public static Arbitrary<McpServerConfig> McpServerConfigs()
    {
        var envKeyGen = Gen.Elements(AllEnvKeys);

        var stdioGen =
            from name in Gen.Elements(ServerNames)
            from command in Gen.Elements(Commands)
            from argCount in Gen.Choose(0, 3)
            from args in Gen.ArrayOf(Gen.Elements(ArgValues), argCount)
            from disabled in Gen.Elements(true, false)
            from envCount in Gen.Choose(0, 4)
            from envKeys in Gen.ArrayOf(envKeyGen, envCount)
            from envVals in Gen.ArrayOf(Gen.Elements(EnvValues), envCount)
            let env = envKeys.Zip(envVals).DistinctBy(p => p.First).ToDictionary(p => p.First, p => p.Second)
            select new McpServerConfig
            {
                Name = name,
                Type = "stdio",
                Command = command,
                Args = args.ToList(),
                Disabled = disabled,
                Env = env
            };

        var httpGen =
            from name in Gen.Elements(ServerNames)
            from url in Gen.Elements(Urls)
            from disabled in Gen.Elements(true, false)
            from type in Gen.Elements("http", "sse")
            select new McpServerConfig
            {
                Name = name,
                Type = type,
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
