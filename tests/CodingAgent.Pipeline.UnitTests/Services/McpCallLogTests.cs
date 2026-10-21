using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="McpCallLog"/>. Uses a real Serilog <see cref="ILogger"/>
/// backed by <see cref="CaptureSink"/> — not a mock — so structured properties are captured
/// and the message template is verifiable.
/// </summary>
public class McpCallLogTests
{
    private readonly CaptureSink _sink;
    private readonly ILogger _logger;

    public McpCallLogTests()
    {
        _sink = new CaptureSink();
        _logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    // ── Failed — message template and properties ─────────────────────────

    [Fact]
    public void Failed_EmitsOneWarning_WithCorrectMessageTemplate()
    {
        McpCallLog.Failed(_logger, "sonarqube", "get_issues", McpFailureCategory.Other, "some error", null);

        _sink.Events.Should().HaveCount(1);
        var evt = _sink.Events.Single();
        evt.Level.Should().Be(LogEventLevel.Warning);
        evt.MessageTemplate.Text.Should().Be("MCP call failed: {McpServer}/{McpTool} ({McpFailureCategory}): {McpError}");
    }

    [Fact]
    public void Failed_EmitsCorrectStructuredProperties()
    {
        McpCallLog.Failed(_logger, "grafana", "query_datasource", McpFailureCategory.Auth, "401 Unauthorized", null);

        var evt = _sink.Events.Single();
        evt.Properties.Should().ContainKey("McpServer");
        evt.Properties.Should().ContainKey("McpTool");
        evt.Properties.Should().ContainKey("McpFailureCategory");
        evt.Properties.Should().ContainKey("McpError");

        evt.Properties["McpServer"].ToString().Should().Contain("grafana");
        evt.Properties["McpTool"].ToString().Should().Contain("query_datasource");
        evt.Properties["McpFailureCategory"].ToString().Should().Contain("Auth");
        evt.Properties["McpError"].ToString().Should().Contain("401 Unauthorized");
    }

    // ── Failed — null error ──────────────────────────────────────────────

    [Fact]
    public void Failed_NullError_LogsEmptyString()
    {
        McpCallLog.Failed(_logger, "server", "tool", McpFailureCategory.Unknown, null, null);

        var evt = _sink.Events.Single();
        evt.Properties["McpError"].ToString().Should().Be("\"\"",
            "null error must be logged as an empty string");
    }

    // ── Failed — truncation ──────────────────────────────────────────────

    [Fact]
    public void Failed_ErrorLongerThan200Chars_IsTruncatedWithEllipsis()
    {
        var error = new string('x', 201);

        McpCallLog.Failed(_logger, "server", "tool", McpFailureCategory.Connection, error, null);

        var evt = _sink.Events.Single();
        var logged = evt.Properties["McpError"].ToString().Trim('"');
        logged.Should().EndWith("…", "errors longer than 200 chars must end with the ellipsis character");
        // 200 chars of 'x' + 1 ellipsis char = 201 Unicode chars, not 203 ASCII dots
        logged.Length.Should().Be(201, "200 content chars + 1 ellipsis");
    }

    [Fact]
    public void Failed_ErrorExactly200Chars_IsNotTruncated()
    {
        var error = new string('x', 200);

        McpCallLog.Failed(_logger, "server", "tool", McpFailureCategory.Other, error, null);

        var evt = _sink.Events.Single();
        var logged = evt.Properties["McpError"].ToString().Trim('"');
        logged.Should().NotEndWith("…", "errors of exactly 200 chars must not be truncated");
        logged.Length.Should().Be(200);
    }

    // ── Failed — masking before cutting ─────────────────────────────────

    [Fact]
    public void Failed_SecretStraddlingCutBoundary_IsMaskedBeforeCut()
    {
        // 185 benign chars + 42-char secret = total 227 chars (> 200).
        // If code cuts first (buggy): the first 15 chars of the secret ("SUPER-SECRET-TO") would
        // appear in the log; the remaining 27 chars would be cut off. NotContain(secret) would pass
        // on the buggy code because only a 15-char prefix is present, not the full 42-char string.
        // Correct behaviour: mask first (secret → "***"), total becomes 188 chars, no cut needed.
        const string secret = "SUPER-SECRET-TOKEN-THAT-STRADDLES-BOUNDARY";  // 42 chars
        var secretPrefix = secret[..15]; // "SUPER-SECRET-TO" — what leaks in the cut-first bug
        var error = new string('a', 185) + secret;  // 227 chars total
        var secrets = new Dictionary<string, string> { ["key"] = secret };

        McpCallLog.Failed(_logger, "server", "tool", McpFailureCategory.Other, error, secrets);

        var evt = _sink.Events.Single();
        var logged = evt.Properties["McpError"].ToString().Trim('"');

        logged.Should().NotContain(secret, "the full secret must be masked before truncation");
        // Also verify no recognisable prefix leaked (the load-bearing assertion for the cut-first bug)
        logged.Should().NotContain(secretPrefix,
            "even a partial secret prefix must not appear when masking happens before cutting");
        logged.Should().NotEndWith("…", "after masking the text is short enough not to be cut");
    }

    [Fact]
    public void Failed_SecretBeforeCutBoundary_IsMasked()
    {
        // A secret entirely within the first 200 chars must be masked
        const string secret = "mysecrettoken";
        var error = secret + new string('z', 50);
        var secrets = new Dictionary<string, string> { ["key"] = secret };

        McpCallLog.Failed(_logger, "server", "tool", McpFailureCategory.Auth, error, secrets);

        var evt = _sink.Events.Single();
        var logged = evt.Properties["McpError"].ToString().Trim('"');
        logged.Should().NotContain(secret, "secrets in the first 200 chars must be masked");
        logged.Should().Contain("***");
    }

    [Fact]
    public void Failed_NullSecrets_DoesNotThrow()
    {
        var act = () => McpCallLog.Failed(_logger, "s", "t", McpFailureCategory.Other, "error", null);
        act.Should().NotThrow();
    }

    // ── Unavailable ──────────────────────────────────────────────────────

    [Fact]
    public void Unavailable_EmitsOneWarning_WithCorrectMessageTemplate()
    {
        McpCallLog.Unavailable(_logger, "sonarqube", McpServerStatuses.NeedsAuth);

        _sink.Events.Should().HaveCount(1);
        var evt = _sink.Events.Single();
        evt.Level.Should().Be(LogEventLevel.Warning);
        evt.MessageTemplate.Text.Should().Be("MCP server unavailable: {McpServer} ({McpServerStatus})");
    }

    [Fact]
    public void Unavailable_EmitsCorrectStructuredProperties()
    {
        McpCallLog.Unavailable(_logger, "grafana", McpServerStatuses.Failed);

        var evt = _sink.Events.Single();
        evt.Properties.Should().ContainKey("McpServer");
        evt.Properties.Should().ContainKey("McpServerStatus");
        evt.Properties["McpServer"].ToString().Should().Contain("grafana");
        evt.Properties["McpServerStatus"].ToString().Should().Contain(McpServerStatuses.Failed);
    }
}

/// <summary>
/// Simple in-memory Serilog sink for capturing log events in tests.
/// Follows the pattern of <c>LifecycleCaptureSink</c> in PipelineRunLifecycleServiceLoggingTests.
/// </summary>
internal sealed class CaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    public IReadOnlyCollection<LogEvent> Events => _events;
    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
