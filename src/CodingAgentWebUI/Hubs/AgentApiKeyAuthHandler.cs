using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CodingAgentWebUI.Agent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Authentication scheme name used for agent API key validation.
/// </summary>
public static class AgentApiKeyDefaults
{
    public const string AuthenticationScheme = "AgentApiKey";
}

/// <summary>
/// Options for the agent API key authentication handler.
/// </summary>
public sealed class AgentApiKeyAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The expected API key. Read from the <c>AGENT_API_KEY</c> environment variable at startup.
    /// If not set, a random key is generated and logged.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// ASP.NET Core authentication handler that validates Bearer tokens in the <c>Authorization</c>
/// header against the configured <c>AGENT_API_KEY</c>. Uses constant-time string comparison
/// via <see cref="CryptographicOperations.FixedTimeEquals"/> to prevent timing attacks.
/// </summary>
public sealed class AgentApiKeyAuthHandler : AuthenticationHandler<AgentApiKeyAuthOptions>
{
    private readonly ILogger _serilogLogger;

    public AgentApiKeyAuthHandler(
        IOptionsMonitor<AgentApiKeyAuthOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        ILogger serilogLogger)
        : base(options, loggerFactory, encoder)
    {
        _serilogLogger = serilogLogger;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // SignalR negotiation sends the token as a query parameter when using WebSockets
        // (browsers can't set Authorization headers on WebSocket upgrade requests).
        // Check query parameter first, then Authorization header.
        string? token = null;

        // Check query parameter (SignalR WebSocket transport)
        if (Request.Query.TryGetValue("access_token", out var queryToken))
        {
            token = queryToken.ToString();
        }

        // Check Authorization header (HTTP-based transports)
        if (string.IsNullOrEmpty(token))
        {
            var authHeader = Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var masterKey = Options.ApiKey;
        if (string.IsNullOrEmpty(masterKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Server API key not configured"));
        }

        // Derive expected key from agentId query parameter (HMAC path),
        // or use raw master key if agentId is absent (legacy fallback).
        var agentId = Request.Query["agentId"].FirstOrDefault();
        string expectedKey;
        if (!string.IsNullOrEmpty(agentId))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterKey));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId));
            expectedKey = Convert.ToHexString(hash).ToLowerInvariant();
        }
        else
        {
            expectedKey = masterKey;
        }

        // Constant-time comparison to prevent timing attacks.
        // Hash both values to a fixed-size digest before comparing. This prevents
        // CryptographicOperations.FixedTimeEquals from leaking length information
        // via its early return when spans differ in length.
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));

        if (!CryptographicOperations.FixedTimeEquals(tokenHash, expectedHash))
        {
            _serilogLogger.Warning("Agent API key authentication failed — invalid key from {RemoteIp}", Request.HttpContext.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Create a claims identity with the authenticated agent ID
        var claimId = !string.IsNullOrEmpty(agentId) ? agentId : "agent";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, claimId) };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Resolves the API key from the <c>AGENT_API_KEY</c> environment variable.
    /// If not set, generates a random 32-byte key and logs it.
    /// </summary>
    public static string ResolveApiKey(ILogger logger)
    {
        var key = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        if (!string.IsNullOrWhiteSpace(key))
        {
            logger.Information("AGENT_API_KEY loaded from environment variable");
            return key;
        }

        // Generate a random key
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var generatedKey = Convert.ToBase64String(randomBytes);
        logger.Warning("AGENT_API_KEY not set — generated random key (length={KeyLength}). Set AGENT_API_KEY env var for production use", generatedKey.Length);
        return generatedKey;
    }
}
