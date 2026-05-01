using System.Text.Encodings.Web;
using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for AgentApiKeyAuthHandler — validates token extraction from
/// query parameters and Authorization headers, constant-time comparison,
/// and edge cases.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentApiKeyAuthHandlerTests
{
    private readonly Mock<ILogger> _mockLogger;

    public AgentApiKeyAuthHandlerTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    // ── ResolveApiKey ───────────────────────────────────────────────────

    [Fact]
    public void ResolveApiKey_WithEnvironmentVariable_ReturnsConfiguredKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", "my-secret-key");

            var key = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            key.Should().Be("my-secret-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ResolveApiKey_WithoutEnvironmentVariable_GeneratesRandomKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);

            var key1 = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
            var key2 = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            key1.Should().NotBeNullOrWhiteSpace();
            key2.Should().NotBeNullOrWhiteSpace();
            // Generated keys should be different (random)
            key1.Should().NotBe(key2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ResolveApiKey_GeneratedKey_IsBase64Of32Bytes()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);

            var key = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            // Base64 of 32 bytes = 44 characters
            key.Length.Should().Be(44);
            // Should be valid base64
            var bytes = Convert.FromBase64String(key);
            bytes.Length.Should().Be(32);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ResolveApiKey_EmptyEnvironmentVariable_GeneratesRandomKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", "");

            var key = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            // Empty string is treated as "not set"
            key.Should().NotBeNullOrWhiteSpace();
            key.Length.Should().Be(44); // Base64 of 32 bytes
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ResolveApiKey_WhitespaceEnvironmentVariable_GeneratesRandomKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", "   ");

            var key = AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            key.Should().NotBeNullOrWhiteSpace();
            key.Length.Should().Be(44);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    // ── HandleAuthenticateAsync (via integration-style test) ────────────

    [Fact]
    public async Task HandleAuthenticate_NoTokenProvided_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync("expected-key", queryToken: null, authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_ValidQueryToken_ReturnsSuccess()
    {
        var handler = await CreateHandlerAsync("my-api-key", queryToken: "my-api-key", authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_ValidBearerToken_ReturnsSuccess()
    {
        var handler = await CreateHandlerAsync("my-api-key", queryToken: null, authHeader: "Bearer my-api-key");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_InvalidToken_ReturnsFail()
    {
        var handler = await CreateHandlerAsync("correct-key", queryToken: "wrong-key", authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task HandleAuthenticate_EmptyApiKeyConfig_ReturnsFail()
    {
        var handler = await CreateHandlerAsync("", queryToken: "some-token", authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task HandleAuthenticate_QueryTokenTakesPrecedence_OverHeader()
    {
        // When both query token and header are present, query token is used
        var handler = await CreateHandlerAsync("query-key", queryToken: "query-key", authHeader: "Bearer wrong-key");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_BearerPrefix_CaseInsensitive()
    {
        var handler = await CreateHandlerAsync("my-key", queryToken: null, authHeader: "bearer my-key");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_NonBearerScheme_ReturnsNoResult()
    {
        var handler = await CreateHandlerAsync("my-key", queryToken: null, authHeader: "Basic dXNlcjpwYXNz");

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    // ── AgentApiKeyAuthOptions ──────────────────────────────────────────

    [Fact]
    public void AgentApiKeyAuthOptions_DefaultApiKey_IsEmpty()
    {
        var options = new AgentApiKeyAuthOptions();

        options.ApiKey.Should().Be(string.Empty);
    }

    [Fact]
    public void AgentApiKeyAuthOptions_CanSetApiKey()
    {
        var options = new AgentApiKeyAuthOptions { ApiKey = "test-key" };

        options.ApiKey.Should().Be("test-key");
    }

    // ── AgentApiKeyDefaults ─────────────────────────────────────────────

    [Fact]
    public void AgentApiKeyDefaults_Scheme_IsAgentApiKey()
    {
        AgentApiKeyDefaults.AuthenticationScheme.Should().Be("AgentApiKey");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<AgentApiKeyAuthHandler> CreateHandlerAsync(
        string configuredApiKey,
        string? queryToken,
        string? authHeader)
    {
        var options = new AgentApiKeyAuthOptions { ApiKey = configuredApiKey };
        var optionsMonitor = new Mock<IOptionsMonitor<AgentApiKeyAuthOptions>>();
        optionsMonitor.Setup(o => o.Get(It.IsAny<string>())).Returns(options);
        optionsMonitor.Setup(o => o.CurrentValue).Returns(options);

        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<Microsoft.Extensions.Logging.ILogger>());

        var handler = new AgentApiKeyAuthHandler(
            optionsMonitor.Object,
            loggerFactory.Object,
            UrlEncoder.Default,
            _mockLogger.Object);

        // Create a fake HttpContext
        var context = new DefaultHttpContext();
        if (queryToken != null)
        {
            context.Request.QueryString = new QueryString($"?access_token={queryToken}");
        }
        if (authHeader != null)
        {
            context.Request.Headers.Authorization = authHeader;
        }

        var scheme = new AuthenticationScheme(
            AgentApiKeyDefaults.AuthenticationScheme,
            null,
            typeof(AgentApiKeyAuthHandler));

        await handler.InitializeAsync(scheme, context);

        return handler;
    }
}
