using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="LogSanitizer.SanitizeForLog"/>.
/// Verifies that the shared log-injection prevention utility escapes newline characters
/// correctly and handles edge cases (null, empty, no newlines).
/// </summary>
public sealed class LogSanitizerTests
{
    [Fact]
    public void SanitizeForLog_NullInput_ReturnsEmptyString()
    {
        LogSanitizer.SanitizeForLog(null).Should().Be("");
    }

    [Fact]
    public void SanitizeForLog_EmptyString_ReturnsEmpty()
    {
        LogSanitizer.SanitizeForLog("").Should().Be("");
    }

    [Fact]
    public void SanitizeForLog_NoNewlines_ReturnsSameValue()
    {
        LogSanitizer.SanitizeForLog("kiro,dotnet").Should().Be("kiro,dotnet");
    }

    [Fact]
    public void SanitizeForLog_WithLineFeed_EscapesIt()
    {
        LogSanitizer.SanitizeForLog("line1\nline2").Should().Be("line1\\nline2");
    }

    [Fact]
    public void SanitizeForLog_WithCarriageReturn_EscapesIt()
    {
        LogSanitizer.SanitizeForLog("line1\rline2").Should().Be("line1\\rline2");
    }

    [Fact]
    public void SanitizeForLog_WithCRLF_EscapesBoth()
    {
        LogSanitizer.SanitizeForLog("line1\r\nline2").Should().Be("line1\\r\\nline2");
    }

    [Fact]
    public void SanitizeForLog_AttackVector_NeutralizesInjection()
    {
        // Simulate a log injection attempt: selector contains newline + fake log entry
        var malicious = "kiro\r\n[CRITICAL] injected-log-line selector=admin";
        var sanitized = LogSanitizer.SanitizeForLog(malicious);
        sanitized.Should().Contain("\\r");
        sanitized.Should().Contain("\\n");
        sanitized.Should().NotContain("\r");
        sanitized.Should().NotContain("\n");
    }
}
