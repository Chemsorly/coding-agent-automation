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

    [Fact]
    public void ResolveApiKey_GeneratedKey_LogsKeyLength()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);

            AgentApiKeyAuthHandler.ResolveApiKey(_mockLogger.Object);

            _mockLogger.Verify(l => l.Warning(
                It.Is<string>(msg => msg.Contains("{KeyLength}") && msg.Contains("Set AGENT_API_KEY env var for production")),
                It.Is<int>(length => length > 0)));
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

    // ── HMAC derivation tests ──────────────────────────────────────────

    [Fact]
    public async Task HandleAuthenticate_HmacDerivedToken_ReturnsSuccess()
    {
        var masterKey = "my-master-key";
        var agentId = "agent-1";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(masterKey));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        var handler = await CreateHandlerAsync(masterKey, queryToken: derivedToken, authHeader: null, agentId: agentId);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_HmacDerivedToken_SetsNameIdentifierToAgentId()
    {
        var masterKey = "my-master-key";
        var agentId = "agent-1";
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(masterKey));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        var handler = await CreateHandlerAsync(masterKey, queryToken: derivedToken, authHeader: null, agentId: agentId);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        var nameId = result.Principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        nameId.Should().Be(agentId);
    }

    [Fact]
    public async Task HandleAuthenticate_RawTokenWithAgentId_ReturnsFail()
    {
        // Presenting the raw master key when agentId is present should fail
        var masterKey = "my-master-key";
        var agentId = "agent-1";

        var handler = await CreateHandlerAsync(masterKey, queryToken: masterKey, authHeader: null, agentId: agentId);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticate_TokenDerivedFromWrongAgent_ReturnsFail()
    {
        var masterKey = "my-master-key";
        // Derive token for agent-1 but present with agentId=agent-2
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(masterKey));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("agent-1"));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        var handler = await CreateHandlerAsync(masterKey, queryToken: derivedToken, authHeader: null, agentId: "agent-2");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticate_NoAgentId_LegacyFallback_ReturnsSuccess()
    {
        // When agentId is absent, raw key comparison should work (legacy)
        var masterKey = "my-master-key";

        var handler = await CreateHandlerAsync(masterKey, queryToken: masterKey, authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_NoAgentId_SetsNameIdentifierToAgent()
    {
        var masterKey = "my-master-key";

        var handler = await CreateHandlerAsync(masterKey, queryToken: masterKey, authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        var nameId = result.Principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        nameId.Should().Be("agent");
    }

    [Fact]
    public async Task HandleAuthenticate_DifferentAgents_ProduceDifferentKeys()
    {
        var masterKey = "my-master-key";
        using var hmac1 = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(masterKey));
        var hash1 = hmac1.ComputeHash(System.Text.Encoding.UTF8.GetBytes("agent-1"));
        var key1 = Convert.ToHexString(hash1).ToLowerInvariant();

        using var hmac2 = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(masterKey));
        var hash2 = hmac2.ComputeHash(System.Text.Encoding.UTF8.GetBytes("agent-2"));
        var key2 = Convert.ToHexString(hash2).ToLowerInvariant();

        key1.Should().NotBe(key2);
    }

    // ── SHA-256 normalization (handler-level) ───────────────────────────

    [Fact]
    public async Task HandleAuthenticate_TokenShorterThanExpected_RejectsWithoutException()
    {
        // A token shorter than the expected key exercises the SHA-256 normalization path.
        // Without SHA-256 pre-hashing, FixedTimeEquals would early-return on length mismatch
        // (leaking length info via timing). This test verifies the handler handles
        // different-length tokens gracefully through the SHA-256 normalization.
        var masterKey = "a]long-master-key-that-is-definitely-longer-than-short";
        var handler = await CreateHandlerAsync(masterKey, queryToken: "short", authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task HandleAuthenticate_TokenLongerThanExpected_RejectsWithoutException()
    {
        // A token longer than the expected key exercises the SHA-256 normalization path.
        var masterKey = "short-key";
        var longToken = "this-is-a-very-long-token-that-exceeds-the-expected-key-length-significantly";
        var handler = await CreateHandlerAsync(masterKey, queryToken: longToken, authHeader: null);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task HandleAuthenticate_HmacPath_TokenDifferentLengthFromExpected_RejectsWithoutException()
    {
        // HMAC-derived expected keys are 64 hex chars. A short token exercises
        // the SHA-256 normalization on the HMAC path specifically.
        var masterKey = "my-master-key";
        var agentId = "agent-1";
        var shortToken = "abc"; // Much shorter than 64-char hex HMAC output

        var handler = await CreateHandlerAsync(masterKey, queryToken: shortToken, authHeader: null, agentId: agentId);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Task<AgentApiKeyAuthHandler> CreateHandlerAsync(
        string configuredApiKey,
        string? queryToken,
        string? authHeader)
        => CreateHandlerAsync(configuredApiKey, queryToken, authHeader, agentId: null);

    private async Task<AgentApiKeyAuthHandler> CreateHandlerAsync(
        string configuredApiKey,
        string? queryToken,
        string? authHeader,
        string? agentId)
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
        var queryParts = new List<string>();
        if (queryToken != null)
            queryParts.Add($"access_token={Uri.EscapeDataString(queryToken)}");
        if (agentId != null)
            queryParts.Add($"agentId={Uri.EscapeDataString(agentId)}");
        if (queryParts.Count > 0)
            context.Request.QueryString = new QueryString("?" + string.Join("&", queryParts));
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
