using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for the completion payload feedback flow:
/// agent worker builds JobCompletionPayload with Feedback → orchestrator receives and persists.
/// **Validates: Requirements 9.2, 9.3, 4.5**
/// </summary>
public class CompletionPayloadFeedbackTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static RunFeedback CreateSampleFeedback() => new()
    {
        Outcome = FeedbackOutcome.Success,
        CollectedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
        Harness = new HarnessFeedback
        {
            Category = "missing file context",
            StuckReason = null,
            MissingContext = ["tsconfig.json was needed"],
            MissingCapabilities = [],
            PromptIssues = [],
            Suggestions = ["Include tsconfig.json in initial context"]
        },
        Issue = new IssueFeedback
        {
            Category = "contradictory acceptance criteria",
            Description = "AC #2 conflicts with AC #4",
            AffectedFiles = ["src/main.ts"],
            HumanActionNeeded = "Clarify which acceptance criterion takes priority"
        }
    };

    /// <summary>
    /// When run.Feedback is non-null, the completion payload construction pattern
    /// (as used by BuildCompletionPayload) includes it in the payload.
    /// **Validates: Requirement 9.2**
    /// </summary>
    [Fact]
    public void CompletionPayload_WithNonNullFeedback_IncludesFeedbackInPayload()
    {
        // Arrange — simulate what BuildCompletionPayload does
        var feedback = CreateSampleFeedback();

        // Act — construct payload the same way BuildCompletionPayload does
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = feedback
        };

        // Assert
        payload.Feedback.Should().NotBeNull();
        payload.Feedback.Should().BeSameAs(feedback);
        payload.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
        payload.Feedback.Harness.Category.Should().Be("missing file context");
        payload.Feedback.Issue.Should().NotBeNull();
        payload.Feedback.Issue!.Description.Should().Be("AC #2 conflicts with AC #4");
    }

    /// <summary>
    /// When run.Feedback is null, the completion payload construction pattern
    /// produces a payload with Feedback = null (no crash, no exception).
    /// **Validates: Requirement 9.2**
    /// </summary>
    [Fact]
    public void CompletionPayload_WithNullFeedback_ProducesPayloadWithNullFeedback()
    {
        // Arrange — simulate a run that has no feedback (e.g., feedback collection failed silently)
        RunFeedback? feedback = null;

        // Act
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = feedback
        };

        // Assert
        payload.Feedback.Should().BeNull();
        payload.FinalStep.Should().Be(PipelineStep.Completed);
    }

    /// <summary>
    /// When the orchestrator receives a payload with null Feedback,
    /// assigning it to run.Feedback remains null without crashing.
    /// Simulates the orchestrator's ReportJobCompleted handler.
    /// **Validates: Requirement 9.3**
    /// </summary>
    [Fact]
    public void Orchestrator_ReceivesPayloadWithNullFeedback_RunFeedbackRemainsNull()
    {
        // Arrange — payload with null feedback (from an older agent or failed collection)
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = null
        };

        // Act — simulate what the orchestrator does: run.Feedback = payload.Feedback
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1"
        };
        run.Feedback = payload.Feedback;

        // Assert — no crash, feedback stays null
        run.Feedback.Should().BeNull();
    }

    /// <summary>
    /// When the orchestrator receives a payload with non-null Feedback,
    /// assigning it to run.Feedback correctly propagates the feedback data.
    /// Simulates the orchestrator's ReportJobCompleted handler.
    /// **Validates: Requirement 9.3**
    /// </summary>
    [Fact]
    public void Orchestrator_ReceivesPayloadWithFeedback_RunFeedbackIsSet()
    {
        // Arrange
        var feedback = CreateSampleFeedback();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = feedback
        };

        // Act — simulate orchestrator assignment
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1"
        };
        run.Feedback = payload.Feedback;

        // Assert
        run.Feedback.Should().NotBeNull();
        run.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
        run.Feedback.Harness.MissingContext.Should().ContainSingle()
            .Which.Should().Be("tsconfig.json was needed");
        run.Feedback.Issue.Should().NotBeNull();
    }

    /// <summary>
    /// Deserializing a pre-feature run summary JSON (without a Feedback field)
    /// produces Feedback = null without throwing an error.
    /// This ensures backward compatibility with existing persisted run data.
    /// **Validates: Requirement 4.5**
    /// </summary>
    [Fact]
    public void PreFeatureRunSummary_DeserializesWithoutFeedbackField_ProducesNullFeedback()
    {
        // Arrange — JSON that predates the feedback feature (no Feedback property)
        var preFeatureJson = """
        {
            "RunId": "abc123",
            "IssueIdentifier": "org/repo#42",
            "IssueTitle": "Add login page",
            "FinalStep": "Completed",
            "StartedAt": "2025-06-15T10:00:00Z",
            "CompletedAt": "2025-06-15T10:30:00Z",
            "RetryCount": 1,
            "PullRequestUrl": "https://github.com/org/repo/pull/42",
            "BrainUpdatesPushed": true,
            "IsRework": false
        }
        """;

        // Act
        var summary = JsonSerializer.Deserialize<PipelineRunSummary>(preFeatureJson, JsonOptions);

        // Assert — no exception thrown, Feedback is null
        summary.Should().NotBeNull();
        summary!.Feedback.Should().BeNull();
        summary.RunId.Should().Be("abc123");
        summary.IssueTitle.Should().Be("Add login page");
        summary.RetryCount.Should().Be(1);
    }

    /// <summary>
    /// Deserializing a run summary JSON that includes a Feedback field
    /// correctly populates the Feedback property.
    /// **Validates: Requirement 4.5**
    /// </summary>
    [Fact]
    public void RunSummaryWithFeedback_Deserializes_PreservesFeedbackData()
    {
        // Arrange — JSON with feedback data included
        var jsonWithFeedback = """
        {
            "RunId": "def456",
            "IssueIdentifier": "org/repo#99",
            "IssueTitle": "Fix bug",
            "FinalStep": "Completed",
            "StartedAt": "2026-07-01T12:00:00Z",
            "CompletedAt": "2026-07-01T12:15:00Z",
            "RetryCount": 0,
            "Feedback": {
                "Outcome": "Success",
                "CollectedAtUtc": "2026-07-01T12:15:00Z",
                "Harness": {
                    "Category": "missing file context",
                    "StuckReason": null,
                    "MissingContext": ["tsconfig.json"],
                    "MissingCapabilities": [],
                    "PromptIssues": [],
                    "Suggestions": []
                },
                "Issue": null
            }
        }
        """;

        // Act
        var summary = JsonSerializer.Deserialize<PipelineRunSummary>(jsonWithFeedback, JsonOptions);

        // Assert
        summary.Should().NotBeNull();
        summary!.Feedback.Should().NotBeNull();
        summary.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
        summary.Feedback.Harness.Category.Should().Be("missing file context");
        summary.Feedback.Harness.MissingContext.Should().ContainSingle()
            .Which.Should().Be("tsconfig.json");
        summary.Feedback.Issue.Should().BeNull();
    }
}
