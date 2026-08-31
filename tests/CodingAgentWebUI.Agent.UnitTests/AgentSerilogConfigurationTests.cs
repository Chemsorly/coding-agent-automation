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
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First(l => l.StartsWith("{"));
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        root.TryGetProperty("@mt", out _).Should().BeTrue("CLEF format must include @mt (message template)");
        root.TryGetProperty("@t", out var timestampProp).Should().BeTrue("CLEF format must include @t (timestamp)");
        timestampProp.GetString().Should().NotBeNullOrEmpty("@t must contain a non-empty ISO 8601 timestamp");
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

        // Assert — AgentId must be a plain string value, not a nested object.
        // Uses agentId.Value rather than the AgentId struct so CompactJsonFormatter
        // emits a flat string instead of {"Value":"..."}.
        var output = writer.ToString();
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First(l => l.StartsWith("{"));
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

        // Assert — old plain-text template started with '[HH:mm:ss Level]'.
        // Filter to JSON lines only: other active loggers in the test process may
        // emit plain-text lines to stdout that are unrelated to the logger under test.
        var output = writer.ToString();
        var jsonLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("{"))
            .ToList();
        jsonLines.Should().NotBeEmpty("the agent logger must emit at least one CLEF JSON line");
        jsonLines.Should().AllSatisfy(line =>
        {
            line.Should().NotStartWith("[", "JSON output must not start with the old plain-text bracket prefix");
            line.Should().StartWith("{", "CLEF JSON output must start with a JSON object opening brace");
        });
    }
}
