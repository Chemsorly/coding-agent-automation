using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="AnalysisStalenessDetector"/>.
/// Tests all three staleness signals, short-circuit behavior, and max refresh cap.
/// </summary>
public class AnalysisStalenessDetectorTests
{
    private readonly Mock<IWorkItemQueryService> _mockQuery = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AnalysisStalenessDetector _detector;

    private static readonly DateTime AnalysisTime = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string IssueId = "owner/repo#42";
    private const string ProviderId = "provider-1";

    public AnalysisStalenessDetectorTests()
    {
        _detector = new AnalysisStalenessDetector(_mockQuery.Object, _mockLogger.Object);
        // Default: no successes, no errors
        _mockQuery.Setup(q => q.GetLastSuccessfulCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
    }

    private static IssueComment CreateAnalysisComment(string? bodyHash = null, DateTime? createdAt = null)
    {
        var hash = bodyHash ?? AnalysisBodyHash.Compute("original body");
        var time = createdAt ?? AnalysisTime;
        return new IssueComment
        {
            Id = "ac-1",
            Author = "bot",
            Body = $"## 🤖 Agent Analysis\n\nContent\n<!-- agent:analysis-body-hash:{hash} -->",
            CreatedAt = time
        };
    }

    private static IssueComment CreateLegacyAnalysisComment(DateTime? createdAt = null)
    {
        return new IssueComment
        {
            Id = "ac-legacy",
            Author = "bot",
            Body = "## 🤖 Agent Analysis\n\nLegacy content without hash",
            CreatedAt = createdAt ?? AnalysisTime
        };
    }

    // ── Signal 1 (agent_error) ────────────────────────────────────────────

    [Fact]
    public async Task Signal1_AgentErrorAfterAnalysis_ForceRefresh()
    {
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(IssueId, ProviderId,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 30, null, CancellationToken.None);

        result.ForceRefresh.Should().BeTrue();
        result.Signal.Should().Be("agent_error");
    }

    [Fact]
    public async Task Signal1_TimeoutAfterAnalysis_NoForceRefresh()
    {
        // Timeout does NOT trigger HasAgentErrorSinceAsync (it only returns true for AgentError)
        // TODO: This test only verifies the detector's reaction to a mock returning false — it does not
        // validate that Timeout is actually excluded by the DB query in WorkItemTransitionService.
        // Same weakness applies to InfrastructureFailure and TokenRefreshFailure tests below.
        // Consider adding integration tests against WorkItemTransitionService.HasAgentErrorSinceAsync
        // with actual WorkItems of each FailureReason type.
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Signal1_InfrastructureFailureAfterAnalysis_NoForceRefresh()
    {
        // InfrastructureFailure does NOT trigger HasAgentErrorSinceAsync (it only returns true for AgentError)
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Signal1_TokenRefreshFailureAfterAnalysis_NoForceRefresh()
    {
        // TokenRefreshFailure does NOT trigger HasAgentErrorSinceAsync (it only returns true for AgentError)
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Signal1_AgentErrorBeforeAnalysis_NoForceRefresh()
    {
        // Setup: error exists but HasAgentErrorSinceAsync correctly checks timestamp
        // TODO: This test is weak — it mocks HasAgentErrorSinceAsync to return false, which is
        // indistinguishable from "no errors at all." The temporal filtering (CompletedAt > since)
        // lives in WorkItemTransitionService which has no corresponding integration test with
        // pre-analysis AgentError work items. Consider adding a DB integration test.
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(IssueId, ProviderId,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // DB query filters by CompletedAt > since

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    // ── Signal 2 (body_changed) ───────────────────────────────────────────

    [Fact]
    public async Task Signal2_HashMismatch_ForceRefresh()
    {
        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("original body"));

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "modified body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeTrue();
        result.Signal.Should().Be("body_changed");
    }

    [Fact]
    public async Task Signal2_HashMatch_NoForceRefresh()
    {
        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("same body"));

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "same body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Signal2_NoHashMarker_Legacy_NoForceRefresh()
    {
        var analysisComment = CreateLegacyAnalysisComment();

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "different body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        // No hash marker → signal 2 skipped, no force refresh (only signal 2 was applicable with threshold=0)
        result.ForceRefresh.Should().BeFalse();
    }

    // ── Signal 3 (commit_threshold) ───────────────────────────────────────
    // TODO: Missing boundary condition test: count == threshold (e.g., 30 == 30) should trigger
    // forceRefresh (implementation uses >=). Without this test, an off-by-one changing >= to >
    // would not be caught.

    [Fact]
    public async Task Signal3_CommitCountAboveThreshold_ForceRefresh()
    {
        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        Func<DateTimeOffset, CancellationToken, Task<int>> getCommits = (_, _) => Task.FromResult(35);

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 30, getCommits, CancellationToken.None);

        result.ForceRefresh.Should().BeTrue();
        result.Signal.Should().Be("commit_threshold");
    }

    [Fact]
    public async Task Signal3_CommitCountBelowThreshold_NoForceRefresh()
    {
        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        Func<DateTimeOffset, CancellationToken, Task<int>> getCommits = (_, _) => Task.FromResult(10);

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 30, getCommits, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task Signal3_ThresholdZero_SignalSkipped()
    {
        var callCount = 0;
        Func<DateTimeOffset, CancellationToken, Task<int>> getCommits = (_, _) =>
        {
            callCount++;
            return Task.FromResult(100);
        };

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, getCommits, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
        callCount.Should().Be(0, "commit count should not be fetched when threshold is 0");
    }

    // ── Short-circuit ─────────────────────────────────────────────────────

    [Fact]
    public async Task ShortCircuit_BodyChangedFires_AgentErrorNotChecked()
    {
        // TODO: This test does not verify that the commit-count delegate is also skipped when
        // body_changed fires. Pass a non-null getCommitCount delegate with a call counter and
        // assert callCount == 0 (like ShortCircuit_AgentErrorFires_CommitCountNotChecked does).
        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("original"));

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "changed body",
            IssueId, ProviderId, 30, null, CancellationToken.None);

        result.ForceRefresh.Should().BeTrue();
        result.Signal.Should().Be("body_changed");
        // HasAgentErrorSinceAsync should not have been called since body_changed fired first
        _mockQuery.Verify(q => q.HasAgentErrorSinceAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShortCircuit_AgentErrorFires_CommitCountNotChecked()
    {
        _mockQuery.Setup(q => q.HasAgentErrorSinceAsync(IssueId, ProviderId,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var callCount = 0;
        Func<DateTimeOffset, CancellationToken, Task<int>> getCommits = (_, _) =>
        {
            callCount++;
            return Task.FromResult(100);
        };

        var analysisComment = CreateAnalysisComment(AnalysisBodyHash.Compute("body"));
        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 30, getCommits, CancellationToken.None);

        result.ForceRefresh.Should().BeTrue();
        result.Signal.Should().Be("agent_error");
        callCount.Should().Be(0, "commit count should not be fetched after agent_error fires");
    }

    // ── Max refresh cap ───────────────────────────────────────────────────

    [Fact]
    public async Task MaxRefreshCap_ThreeRefreshesWithoutSuccess_StalenessSuppressed()
    {
        // 3 analysis comments with hash markers (simulates 3 refreshes)
        var comments = new[]
        {
            CreateAnalysisComment(AnalysisBodyHash.Compute("v1"), new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v2"), new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v3"), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        // No successes → all 3 count
        var result = await _detector.EvaluateAsync(
            comments[2], comments, "different body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
        result.RefreshCount.Should().Be(3);
    }

    [Fact]
    public async Task MaxRefreshCap_SuccessBetweenRefreshes_ResetsCounter()
    {
        // 3 analysis comments with hash markers
        var comments = new[]
        {
            CreateAnalysisComment(AnalysisBodyHash.Compute("v1"), new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v2"), new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v3"), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        // Success at 11:30 — only comment at 12:00 counts (after success)
        _mockQuery.Setup(q => q.GetLastSuccessfulCompletionAsync(IssueId, ProviderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DateTimeOffset(2026, 7, 1, 11, 30, 0, TimeSpan.Zero));

        var result = await _detector.EvaluateAsync(
            comments[2], comments, "different body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        // Only 1 refresh since success → cap not hit → signals evaluated
        result.RefreshCount.Should().Be(1);
        result.ForceRefresh.Should().BeTrue(); // body_changed fires
    }

    [Fact]
    public async Task MaxRefreshCap_SuccessWithNonZeroOffset_ComparesCorrectly()
    {
        // 3 analysis comments at UTC times 10:00, 11:00, 12:00
        var comments = new[]
        {
            CreateAnalysisComment(AnalysisBodyHash.Compute("v1"), new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v2"), new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc)),
            CreateAnalysisComment(AnalysisBodyHash.Compute("v3"), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
        };

        // Success at 17:00 +05:30 = 11:30 UTC — only the 12:00 comment is after the success
        // With the old .DateTime code, this would return 17:00 (Kind=Unspecified), causing
        // all comments to appear "before" success (0 counted). With .UtcDateTime it correctly
        // returns 11:30 UTC, so only the 12:00 comment counts (RefreshCount == 1).
        _mockQuery.Setup(q => q.GetLastSuccessfulCompletionAsync(IssueId, ProviderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DateTimeOffset(2026, 7, 1, 17, 0, 0, TimeSpan.FromHours(5.5)));

        var result = await _detector.EvaluateAsync(
            comments[2], comments, "different body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.RefreshCount.Should().Be(1);
        result.ForceRefresh.Should().BeTrue(); // body_changed fires since cap not hit
    }

    // ── Newest comment selection ──────────────────────────────────────────

    [Fact]
    public async Task NewestAnalysisComment_IsUsedForStalenessEvaluation()
    {
        var oldComment = CreateAnalysisComment(AnalysisBodyHash.Compute("old body"), new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        var newComment = CreateAnalysisComment(AnalysisBodyHash.Compute("current body"), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        // If we pass the NEWEST comment, body matches → no refresh
        var result = await _detector.EvaluateAsync(
            newComment, new[] { oldComment, newComment }, "current body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    // ── Project override ──────────────────────────────────────────────────

    // ── DateTimeKind safety ──────────────────────────────────────────────

    // TODO: This test is environment-dependent — on CI with TZ=UTC the old broken code also passes.
    // Consider using TimeZoneInfo to force a non-UTC zone, or verify the DateTimeOffset value
    // passed to HasAgentErrorSinceAsync via mock capture to provide true regression protection.
    [Fact]
    public async Task EvaluateAsync_LocalDateTimeKind_DoesNotThrow()
    {
        // CreatedAt with Local kind should not throw ArgumentException
        var localTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
        var analysisComment = CreateAnalysisComment(
            AnalysisBodyHash.Compute("body"), createdAt: localTime);

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    // TODO: This test is tautological — new DateTimeOffset(unspecifiedDateTime, TimeSpan.Zero)
    // never throws ArgumentException because .NET permits any offset when Kind is Unspecified.
    // This validates a scenario that was never broken. Consider replacing with a value-based
    // assertion (e.g., verify the DateTimeOffset passed to downstream mocks).
    [Fact]
    public async Task EvaluateAsync_UnspecifiedDateTimeKind_DoesNotThrow()
    {
        // CreatedAt with Unspecified kind should not throw ArgumentException
        var unspecifiedTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var analysisComment = CreateAnalysisComment(
            AnalysisBodyHash.Compute("body"), createdAt: unspecifiedTime);

        var result = await _detector.EvaluateAsync(
            analysisComment, new[] { analysisComment }, "body",
            IssueId, ProviderId, 0, null, CancellationToken.None);

        result.ForceRefresh.Should().BeFalse();
    }

    // ── Project override ──────────────────────────────────────────────────

    [Fact]
    public void ProjectOverride_AnalysisCommitThreshold_AppliedCorrectly()
    {
        var config = new PipelineConfiguration { AnalysisCommitThreshold = 30 };
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            AnalysisCommitThreshold = 50
        };

        var overridden = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);
        overridden.AnalysisCommitThreshold.Should().Be(50);
    }

    [Fact]
    public void ProjectOverride_NullThreshold_InheritsGlobal()
    {
        var config = new PipelineConfiguration { AnalysisCommitThreshold = 30 };
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "Test",
            AnalysisCommitThreshold = null
        };

        var overridden = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);
        overridden.AnalysisCommitThreshold.Should().Be(30);
    }
}
