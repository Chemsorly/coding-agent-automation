using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="McpFailureClassifier.Classify"/>.
/// Covers the acceptance criterion inputs and edge cases.
/// </summary>
public class McpFailureClassifierTests
{
    // ── Auth ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Error: 401 Unauthorized")]
    [InlineData("HTTP 403 Forbidden")]
    [InlineData("token expired")]
    [InlineData("token has expired after 1 hour")]
    [InlineData("Unauthorized access denied")]
    [InlineData("invalid_token provided")]
    [InlineData("authentication failed: bad credentials")]
    [InlineData("authentication required")]
    [InlineData("Forbidden: insufficient permissions")]
    public void Classify_AuthInput_ReturnsAuth(string errorText)
    {
        McpFailureClassifier.Classify(errorText).Should().Be(McpFailureCategory.Auth);
    }

    // ── Connection ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("MCP error -32001: Request timed out")]
    [InlineData("MCP error -32000: Connection error")]
    [InlineData("connect ECONNREFUSED 10.0.0.1:443")]
    [InlineData("ECONNRESET by remote peer")]
    [InlineData("ENOTFOUND sonarqube.internal")]
    [InlineData("EAI_AGAIN: name resolution failed")]
    [InlineData("ETIMEDOUT after 30000ms")]
    [InlineData("socket hang up")]
    [InlineData("Connection closed unexpectedly")]
    [InlineData("Connection refused")]
    [InlineData("connection reset by peer")]
    [InlineData("not connected to server")]
    [InlineData("upstream returned 503 Service Unavailable")]
    [InlineData("502 Bad Gateway")]
    [InlineData("504 Gateway Timeout")]
    [InlineData("bad gateway encountered")]
    [InlineData("service unavailable, please retry")]
    [InlineData("gateway timeout after 60s")]
    [InlineData("Request timed out")]
    [InlineData("operation timeout after 5000ms")]
    public void Classify_ConnectionInput_ReturnsConnection(string errorText)
    {
        McpFailureClassifier.Classify(errorText).Should().Be(McpFailureCategory.Connection);
    }

    // ── Other ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("404 Not Found: dashboard abc")]
    [InlineData("MCP error -32602: Invalid params")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown tool: get_dashboard_list")]
    [InlineData("JSON parse error at line 1")]
    public void Classify_OtherInput_ReturnsOther(string? errorText)
    {
        McpFailureClassifier.Classify(errorText).Should().Be(McpFailureCategory.Other);
    }

    // ── First-match-wins: Auth before Connection ────────────────────────

    [Fact]
    public void Classify_AuthAndConnectionBothMatch_ReturnsAuth()
    {
        // An input that contains both auth and connection patterns — Auth wins (checked first)
        var errorText = "401 connection refused";

        McpFailureClassifier.Classify(errorText).Should().Be(McpFailureCategory.Auth,
            "Auth regex is checked before Connection regex — first match wins");
    }

    // ── Whitespace ──────────────────────────────────────────────────────

    [Fact]
    public void Classify_WhitespaceOnly_ReturnsOther()
    {
        McpFailureClassifier.Classify("\t\n  \r\n").Should().Be(McpFailureCategory.Other);
    }

    // TODO: [WARNING] The RegexMatchTimeoutException code path (the catch block that returns
    // McpFailureCategory.Other) is not exercised by any test. The source-generated regexes
    // (net10.0) use efficient DFA-based matching and rarely timeout in practice, making it
    // difficult to trigger artificially without injecting a delay or using a crafted ReDoS
    // input against a future regex change. If McpFailureClassifier ever switches to a
    // dynamically-constructed regex or adds a more complex pattern, a timeout test should be
    // added — e.g. by extracting the regex objects to an overloadable factory method that
    // tests can swap for a mock that throws RegexMatchTimeoutException.
}
