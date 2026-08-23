using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using System.Reflection;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentHub.SanitizeForLog (private static — accessed via reflection).
/// Verifies log injection prevention: newline characters are replaced with escaped literals.
/// </summary>
public sealed class AgentHubSanitizeTests
{
    private static string SanitizeForLog(string? value)
    {
        var method = typeof(AgentHub).GetMethod("SanitizeForLog",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return (string)method!.Invoke(null, [value])!;
    }

    [Fact]
    public void SanitizeForLog_NullInput_ReturnsEmptyString()
    {
        SanitizeForLog(null).Should().Be("");
    }

    [Fact]
    public void SanitizeForLog_EmptyString_ReturnsEmpty()
    {
        SanitizeForLog("").Should().Be("");
    }

    [Fact]
    public void SanitizeForLog_NoNewlines_ReturnsSameValue()
    {
        SanitizeForLog("agent-abc-123").Should().Be("agent-abc-123");
    }

    [Fact]
    public void SanitizeForLog_WithLineFeed_EscapesIt()
    {
        SanitizeForLog("line1\nline2").Should().Be("line1\\nline2");
    }

    [Fact]
    public void SanitizeForLog_WithCarriageReturn_EscapesIt()
    {
        SanitizeForLog("line1\rline2").Should().Be("line1\\rline2");
    }

    [Fact]
    public void SanitizeForLog_WithCRLF_EscapesBoth()
    {
        SanitizeForLog("line1\r\nline2").Should().Be("line1\\r\\nline2");
    }

    [Fact]
    public void SanitizeForLog_AttackVector_NeutralizesInjection()
    {
        // Simulate log injection attempt: agent ID contains newline + fake log entry
        var malicious = "agent-1\n[ERROR] fake injected log entry";
        var sanitized = SanitizeForLog(malicious);
        sanitized.Should().Contain("\\n");
        sanitized.Should().NotContain("\n");
    }
}
