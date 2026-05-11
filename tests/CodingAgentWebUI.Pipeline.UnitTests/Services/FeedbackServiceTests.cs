using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="FeedbackService"/> edge cases.
/// Validates: Requirements 2.8, 3.8, 3.10, 8.1, 8.2
/// </summary>
public class FeedbackServiceTests
{
    private static readonly DateTime TestTimestamp = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FeedbackService _sut;

    public FeedbackServiceTests()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        _sut = new FeedbackService(logger);
    }

    #region Empty response text returns fallback

    [Fact]
    public void ParseFeedbackFromResponse_EmptyString_Success_ReturnsFallbackWithNullStuckReason()
    {
        // Validates: Requirement 2.8
        var result = _sut.ParseFeedbackFromResponse(string.Empty, FeedbackOutcome.Success, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Success);
        result.CollectedAtUtc.Should().Be(TestTimestamp);
        result.Harness.StuckReason.Should().BeNull();
        result.Issue.Should().BeNull();
    }

    [Fact]
    public void ParseFeedbackFromResponse_EmptyString_Failure_ReturnsFallbackWithStuckReason()
    {
        // Validates: Requirement 3.8
        var result = _sut.ParseFeedbackFromResponse(string.Empty, FeedbackOutcome.Failure, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.CollectedAtUtc.Should().Be(TestTimestamp);
        result.Harness.StuckReason.Should().Be("Agent did not produce structured feedback");
        result.Issue.Should().BeNull();
    }

    #endregion

    #region Response with no JSON block returns fallback

    [Fact]
    public void ParseFeedbackFromResponse_ProseWithNoJson_Success_ReturnsFallback()
    {
        // Validates: Requirement 2.8
        const string response = "The run completed successfully. Everything went well and no issues were found.";

        var result = _sut.ParseFeedbackFromResponse(response, FeedbackOutcome.Success, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Success);
        result.Harness.StuckReason.Should().BeNull();
        result.Issue.Should().BeNull();
    }

    [Fact]
    public void ParseFeedbackFromResponse_ProseWithNoJson_Failure_ReturnsFallbackWithStuckReason()
    {
        // Validates: Requirement 3.8
        const string response = "I was unable to complete the task. The tests kept failing due to a missing dependency.";

        var result = _sut.ParseFeedbackFromResponse(response, FeedbackOutcome.Failure, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.Harness.StuckReason.Should().Be("Agent did not produce structured feedback");
        result.Issue.Should().BeNull();
    }

    #endregion

    #region Malformed JSON returns partial parse result

    [Fact]
    public void ParseFeedbackFromResponse_MalformedJson_PreservesCategory()
    {
        // Validates: Requirement 8.2
        // missingContext is "not an array" instead of an array — partial parse should still extract category
        const string response = """
            ```json
            {"harness": {"category": "test failure", "missingContext": "not an array"}}
            ```
            """;

        var result = _sut.ParseFeedbackFromResponse(response, FeedbackOutcome.Failure, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.Harness.Category.Should().Be("test failure");
    }

    #endregion

    #region CreateFallbackFeedback sets all fields correctly

    [Fact]
    public void CreateFallbackFeedback_Failure_TimedOut_SetsAllFields()
    {
        // Validates: Requirement 3.10
        var result = _sut.CreateFallbackFeedback(
            FeedbackOutcome.Failure,
            "Feedback collection timed out",
            TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.CollectedAtUtc.Should().Be(TestTimestamp);
        result.Harness.StuckReason.Should().Be("Feedback collection timed out");
        result.Harness.Category.Should().BeNull();
        result.Harness.MissingContext.Should().BeEmpty();
        result.Harness.MissingCapabilities.Should().BeEmpty();
        result.Harness.PromptIssues.Should().BeEmpty();
        result.Harness.Suggestions.Should().BeEmpty();
        result.Issue.Should().BeNull();
    }

    [Fact]
    public void CreateFallbackFeedback_Failure_ExceptionMessage_SetsAllFields()
    {
        // Validates: Requirement 8.1
        var result = _sut.CreateFallbackFeedback(
            FeedbackOutcome.Failure,
            "Feedback collection failed: timeout",
            TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.CollectedAtUtc.Should().Be(TestTimestamp);
        result.Harness.StuckReason.Should().Be("Feedback collection failed: timeout");
        result.Harness.Category.Should().BeNull();
        result.Harness.MissingContext.Should().BeEmpty();
        result.Harness.MissingCapabilities.Should().BeEmpty();
        result.Harness.PromptIssues.Should().BeEmpty();
        result.Harness.Suggestions.Should().BeEmpty();
        result.Issue.Should().BeNull();
    }

    #endregion

    #region Valid JSON in markdown fence extracts correctly

    [Fact]
    public void ParseFeedbackFromResponse_ValidJsonInMarkdownFence_ExtractsCorrectly()
    {
        // Validates: Requirement 2.7, 3.7
        const string response = """
            Here is my feedback:

            ```json
            {
              "harness": {
                "category": "missing file context",
                "stuckReason": "Could not find the configuration file",
                "missingContext": ["appsettings.json", "launchSettings.json"],
                "missingCapabilities": ["file search"],
                "promptIssues": [],
                "suggestions": ["Include config files in initial context"]
              },
              "issue": {
                "category": "incomplete requirements",
                "description": "The acceptance criteria do not specify error handling behavior",
                "affectedFiles": ["src/Service.cs"],
                "humanActionNeeded": "Add error handling acceptance criteria"
              }
            }
            ```

            That's my assessment.
            """;

        var result = _sut.ParseFeedbackFromResponse(response, FeedbackOutcome.Failure, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.Harness.Category.Should().Be("missing file context");
        result.Harness.StuckReason.Should().Be("Could not find the configuration file");
        result.Harness.MissingContext.Should().BeEquivalentTo(["appsettings.json", "launchSettings.json"]);
        result.Harness.MissingCapabilities.Should().BeEquivalentTo(["file search"]);
        result.Harness.PromptIssues.Should().BeEmpty();
        result.Harness.Suggestions.Should().BeEquivalentTo(["Include config files in initial context"]);
        result.Issue.Should().NotBeNull();
        result.Issue!.Category.Should().Be("incomplete requirements");
        result.Issue.Description.Should().Be("The acceptance criteria do not specify error handling behavior");
        result.Issue.AffectedFiles.Should().BeEquivalentTo(["src/Service.cs"]);
        result.Issue.HumanActionNeeded.Should().Be("Add error handling acceptance criteria");
    }

    #endregion

    #region Valid JSON as bare object extracts correctly

    [Fact]
    public void ParseFeedbackFromResponse_BareJsonObject_ExtractsCorrectly()
    {
        // Validates: Requirement 2.7, 3.7
        const string response = """
            {"harness": {"category": "mcp tool timeout", "stuckReason": "MCP server was unresponsive", "missingContext": [], "missingCapabilities": [], "promptIssues": ["Timeout too short"], "suggestions": ["Increase MCP timeout to 30s"]}}
            """;

        var result = _sut.ParseFeedbackFromResponse(response, FeedbackOutcome.Failure, TestTimestamp);

        result.Outcome.Should().Be(FeedbackOutcome.Failure);
        result.Harness.Category.Should().Be("mcp tool timeout");
        result.Harness.StuckReason.Should().Be("MCP server was unresponsive");
        result.Harness.MissingContext.Should().BeEmpty();
        result.Harness.MissingCapabilities.Should().BeEmpty();
        result.Harness.PromptIssues.Should().BeEquivalentTo(["Timeout too short"]);
        result.Harness.Suggestions.Should().BeEquivalentTo(["Increase MCP timeout to 30s"]);
    }

    #endregion
}
