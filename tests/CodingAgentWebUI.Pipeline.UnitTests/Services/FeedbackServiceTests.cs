using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
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

    #region LoadPreviousCategories

    // TODO: [WARNING] Add test for exception-handling path — mock GetRunHistoryAsync() to throw and assert empty lists are returned (covers the catch block's graceful degradation).

    [Fact]
    public async Task LoadPreviousCategories_NullHistoryService_ReturnsEmptyLists()
    {
        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(null);

        harness.Should().BeEmpty();
        issue.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPreviousCategories_EmptyHistory_ReturnsEmptyLists()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(mockHistory.Object);

        harness.Should().BeEmpty();
        issue.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadPreviousCategories_ExtractsDistinctCategories()
    {
        var summaries = new[]
        {
            CreateSummaryWithCategories(DateTimeOffset.UtcNow, "build-failure", "feature"),
            CreateSummaryWithCategories(DateTimeOffset.UtcNow.AddMinutes(-1), "build-failure", "bug"),
            CreateSummaryWithCategories(DateTimeOffset.UtcNow.AddMinutes(-2), "test-failure", "feature"),
        };
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(summaries);

        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(mockHistory.Object);

        harness.Should().BeEquivalentTo(["build-failure", "test-failure"]);
        issue.Should().BeEquivalentTo(["feature", "bug"]);
    }

    [Fact]
    public async Task LoadPreviousCategories_SkipsNullFeedbackEntries()
    {
        var summaries = new[]
        {
            CreateSummaryWithCategories(DateTimeOffset.UtcNow, "build-failure", "feature"),
            new PipelineRunSummary
            {
                RunId = "run-2", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
                FinalStep = PipelineStep.Completed, StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
                Feedback = null
            },
        };
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(summaries);

        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(mockHistory.Object);

        harness.Should().BeEquivalentTo(["build-failure"]);
        issue.Should().BeEquivalentTo(["feature"]);
    }

    [Fact]
    public async Task LoadPreviousCategories_OrdersByStartedAtOffsetDescending_TakesNewest()
    {
        // Create more than MaxRecentRunsForCategories summaries
        var summaries = Enumerable.Range(0, FeedbackConstraints.MaxRecentRunsForCategories + 10)
            .Select(i => CreateSummaryWithCategories(
                DateTimeOffset.UtcNow.AddMinutes(-i), $"cat-{i}", $"issue-{i}"))
            .ToList();

        // Shuffle to ensure ordering is applied by the method, not pre-sorted input
        var shuffled = summaries.OrderBy(_ => Guid.NewGuid()).ToArray();

        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(shuffled);

        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(mockHistory.Object);

        // Should only have categories from the top MaxRecentRunsForCategories entries (by StartedAtOffset desc)
        harness.Should().HaveCount(FeedbackConstraints.MaxRecentRunsForCategories);
        harness.Should().NotContain($"cat-{FeedbackConstraints.MaxRecentRunsForCategories + 5}");
    }

    [Fact]
    public async Task LoadPreviousCategories_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var expectedToken = cts.Token;
        CancellationToken receivedToken = default;

        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => receivedToken = ct)
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        await _sut.LoadPreviousCategoriesAsync(mockHistory.Object, expectedToken);

        receivedToken.Should().Be(expectedToken);
    }

    // TODO: This test validates graceful-degradation but is not regression-proof for token propagation.
    // The mock uses It.IsAny<CancellationToken>() and unconditionally throws, so it would pass even if
    // the fix were reverted. To make it regression-proof, use It.Is<CancellationToken>(t => t.IsCancellationRequested)
    // and return successfully for non-cancelled tokens.
    [Fact]
    public async Task LoadPreviousCategories_CancelledToken_ReturnsEmptyLists()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var (harness, issue) = await _sut.LoadPreviousCategoriesAsync(mockHistory.Object, cts.Token);

        harness.Should().BeEmpty();
        issue.Should().BeEmpty();
    }

    private static PipelineRunSummary CreateSummaryWithCategories(
        DateTimeOffset startedAt, string harnessCategory, string issueCategory) => new()
    {
        RunId = $"run-{Guid.NewGuid():N}",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test Issue",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = startedAt,
        Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback { Category = harnessCategory },
            Issue = new IssueFeedback { Category = issueCategory }
        }
    };

    #endregion
}
