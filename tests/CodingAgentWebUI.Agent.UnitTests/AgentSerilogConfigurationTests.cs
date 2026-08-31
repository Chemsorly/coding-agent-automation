using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentSerilogConfiguration.CreateAgentLogger"/>.
///
/// Verifies that the agent logger emits CLEF (Compact Log Event Format) JSON lines
/// to stdout instead of plain-text bracket-prefixed lines. This is required so that
/// Loki's <c>| json</c> pipeline stage can parse agent logs without <c>JSONParserErr</c>.
///
/// <b>Ordering constraint:</b> <c>Console.SetOut</c> MUST be called before
/// <c>AgentSerilogConfiguration.CreateAgentLogger</c> because Serilog's console sink
/// captures a reference to <c>Console.Out</c> at logger creation time.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class AgentSerilogConfigurationTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;

    public void Dispose()
    {
        Console.SetOut(_originalOut);
    }

    // Serilog.Core.Logger (the concrete type returned by LoggerConfiguration.CreateLogger())
    // implements IDisposable, but is returned as Serilog.ILogger. Cast to flush/dispose.
    private static void DisposeLogger(Serilog.ILogger logger)
    {
        if (logger is IDisposable d)
            d.Dispose();
    }

    // ── JSON format ────────────────────────────────────────────────────────

    // TODO: All four tests use Information-level log messages. No test logs at Error level, which
    // is the primary acceptance criterion (`level="error"` query). CLEF-level-to-field serialization
    // for errors includes additional fields (e.g., `@x` for exception data). Add a test that logs
    // `.Error(exception, "message")` and asserts the exception is inlined as JSON (not multi-line).
    // Also add a test that logs at Error level and asserts `@l` == "Error". See review finding for #2205.

    [Fact]
    public void CreateAgentLogger_EmitsJsonFormattedLines()
    {
        // Arrange — redirect stdout BEFORE creating the logger
        using var writer = new StringWriter();
        Console.SetOut(writer);

        var logger = AgentSerilogConfiguration.CreateAgentLogger(new AgentId("test-agent"));

        // Act
        logger.Information("Test structured message {Value}", 42);
        DisposeLogger(logger); // flush

        // Assert — each non-empty line must parse as valid JSON
        var output = writer.ToString();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().NotBeEmpty("at least one log line must be emitted to stdout");

        foreach (var line in lines)
        {
            var action = () => JsonDocument.Parse(line);
            action.Should().NotThrow($"line must be valid JSON for Loki | json parsing, but got: {line}");
        }
    }

    [Fact]
    public void CreateAgentLogger_EmitsMessageTemplateAndTimestampFields()
    {
        // Arrange — redirect stdout BEFORE creating the logger
        using var writer = new StringWriter();
        Console.SetOut(writer);

        var logger = AgentSerilogConfiguration.CreateAgentLogger(new AgentId("test-agent"));

        // Act
        logger.Information("Agent started with property {Key}", "value");
        DisposeLogger(logger); // flush

        // Assert — CLEF format emits @mt (message template) and @t (timestamp)
        var output = writer.ToString();
        // TODO: `.First()` is fragile — if CompactJsonFormatter or the OTLP sink emits a header/empty
        // flush line before the log event, `First()` returns that line and `JsonDocument.Parse` throws
        // a confusing error. Consider iterating non-empty lines (as in `EmitsJsonFormattedLines`) or
        // asserting `lines.Length >= 1` before taking `lines[0]`. See review finding for issue #2205.
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        root.TryGetProperty("@mt", out _).Should().BeTrue("CLEF format must include @mt (message template)");
        root.TryGetProperty("@t", out _).Should().BeTrue("CLEF format must include @t (timestamp)");
        // TODO: `TryGetProperty` only checks presence, not value. `@t` with an empty string or zero
        // would still pass. For the acceptance criterion (SIGTERM events queryable with timestamp),
        // assert that `@t` is non-empty and contains a parseable ISO 8601 value. See review finding.
        // TODO: No test verifies that the `@l` level field is emitted. The primary acceptance criterion
        // is `{service_name="agent"} | json | level="error"` (via `@l` after label-rename). A regression
        // dropping `@l` from non-Information events would not be caught. Add a test that logs at Error
        // level and asserts `@l` == "Error". See review finding for issue #2205.
    }

    // ── AgentId as plain string ────────────────────────────────────────────

    [Fact]
    public void CreateAgentLogger_IncludesAgentIdAsStructuredProperty()
    {
        // Arrange — redirect stdout BEFORE creating the logger
        using var writer = new StringWriter();
        Console.SetOut(writer);

        var logger = AgentSerilogConfiguration.CreateAgentLogger(new AgentId("test-agent-42"));

        // Act
        logger.Information("Probe message");
        DisposeLogger(logger); // flush

        // Assert — AgentId must be a plain string value, not a nested object
        var output = writer.ToString();
        // TODO: `.First()` is fragile — see note in `CreateAgentLogger_EmitsMessageTemplateAndTimestampFields`.
        // TODO: This assertion (`JsonValueKind.String`) currently FAILS because `Enrich.WithProperty("AgentId", agentId)`
        // passes the `AgentId` struct, which CompactJsonFormatter destructures into `{"Value":"..."}`. The test
        // correctly captures the requirement but will fail against the current implementation. Fix the production
        // code to pass `agentId.Value` (see TODO in AgentSerilogConfiguration.cs). See review finding for issue #2205.
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        root.TryGetProperty("AgentId", out var agentIdProp).Should().BeTrue("AgentId must be present as a top-level field");
        agentIdProp.ValueKind.Should().Be(JsonValueKind.String, "AgentId must serialize as a plain string, not a JSON object");
        agentIdProp.GetString().Should().Be("test-agent-42");
    }

    // ── No plain-text bracket prefix ──────────────────────────────────────

    [Fact]
    public void CreateAgentLogger_DoesNotEmitPlainTextBrackets()
    {
        // Arrange — redirect stdout BEFORE creating the logger
        using var writer = new StringWriter();
        Console.SetOut(writer);

        var logger = AgentSerilogConfiguration.CreateAgentLogger(new AgentId("test-agent"));

        // Act
        logger.Information("Some message");
        DisposeLogger(logger); // flush

        // Assert — old plain-text template started with '[HH:mm:ss Level]'
        var output = writer.ToString();
        // TODO: `.First()` is fragile — if any sink emits a non-log preamble line, `First()` points to
        // that line instead of the CLEF JSON event. Use `.FirstOrDefault(l => l.StartsWith("{"))` or
        // assert array length before taking `lines[0]`. See review finding for issue #2205.
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First();
        line.Should().NotStartWith("[", "JSON output must not start with the old plain-text bracket prefix");
        // TODO: `StartsWith("{")` is structurally weak — any JSON-like string (e.g., `{}`) passes.
        // The positive JSON-validity assertion in `CreateAgentLogger_EmitsJsonFormattedLines` provides
        // stronger coverage; this test's negative assertion is redundant with that. See review finding.
        line.Should().StartWith("{", "CLEF JSON output must start with a JSON object opening brace");
    }
}
