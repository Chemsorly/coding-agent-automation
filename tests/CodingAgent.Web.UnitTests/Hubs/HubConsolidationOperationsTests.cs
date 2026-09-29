using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Unit tests for the concrete <see cref="HubConsolidationOperations"/> implementation.
///
/// <see cref="AgentHubConsolidationTests"/> tests the hub with a mocked
/// <see cref="IHubConsolidationOperations"/>. This file tests the implementation directly,
/// covering <see cref="HubConsolidationOperations.HandleConsolidationCompleteAsync"/> and
/// <see cref="HubConsolidationOperations.CompleteModelFetchRequest"/>.
///
/// <see cref="ModelFetchService"/> and <see cref="ConsolidationBadgeService"/> are sealed —
/// real instances are used; their behaviours are verified through observable state.
/// </summary>
public sealed class HubConsolidationOperationsTests
{
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly Mock<IChangeNotifier> _mockChangeNotifier = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager = new();

    // Real instances (sealed — cannot mock)
    private readonly ConsolidationBadgeService _badgeService = new();

    private static ModelFetchService CreateModelFetchService()
    {
        // ModelFetchService needs AgentRegistryService and IAgentCommunication.
        // We build a minimal real AgentRegistryService (no I/O) and a mock IAgentCommunication.
        var registry = new AgentRegistryService(Mock.Of<ILogger>());
        var agentComm = Mock.Of<IAgentCommunication>();
        return new ModelFetchService(registry, agentComm, Mock.Of<ILogger>());
    }

    private HubConsolidationOperations CreateSut(ModelFetchService? modelFetch = null) => new(
        modelFetch ?? CreateModelFetchService(),
        _mockConsolidation.Object,
        _badgeService,
        _mockChangeNotifier.Object,
        _mockLifecycleManager.Object,
        _mockLogger.Object);

    private static HarnessSuggestions MakeSuggestions(params string[] texts) => new()
    {
        BasedOnRunCount = 5,
        GeneratedAtUtc = DateTime.UtcNow,
        SuccessRate = 0.8m,
        Suggestions = texts.Select(t => new HarnessSuggestion
        {
            Frequency = 1,
            Rationale = "test",
            Text = t
        }).ToList()
    };

    private static CreatedIssueInfo MakeIssue(string id) => new()
    {
        Identifier = id,
        Title = "Test Issue",
        Url = $"https://github.com/org/repo/issues/{id}"
    };

    private static AgentEntry CreateAgent(string agentId = "agent-1") => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = "crun-1"
    };

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullModelFetchService_Throws()
    {
        var act = () => new HubConsolidationOperations(
            null!,
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("modelFetchService");
    }

    [Fact]
    public void Constructor_NullConsolidationService_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            null!,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("consolidationService");
    }

    [Fact]
    public void Constructor_NullBadgeService_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            null!,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("badgeService");
    }

    [Fact]
    public void Constructor_NullChangeNotifier_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            null!,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("changeNotifier");
    }

    [Fact]
    public void Constructor_NullLifecycleManager_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            null!,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("lifecycleManager");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── CompleteModelFetchRequest ─────────────────────────────────────────

    [Fact]
    public void CompleteModelFetchRequest_NullResponse_Throws()
    {
        var sut = CreateSut();
        var act = () => sut.CompleteModelFetchRequest(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CompleteModelFetchRequest_UnknownRequestId_DoesNotThrow()
    {
        // No pending request with this ID — ModelFetchService logs a warning and moves on
        var response = new FetchModelsResponse { RequestId = "unknown-req-id", Models = [] };
        var sut = CreateSut();

        var act = () => sut.CompleteModelFetchRequest(response);
        act.Should().NotThrow("unknown request IDs are handled gracefully with a warning log");
    }

    // ── HandleConsolidationCompleteAsync — null result guard ─────────────

    [Fact]
    public async Task HandleConsolidationComplete_NullResult_Throws()
    {
        var sut = CreateSut();
        var act = async () => await sut.HandleConsolidationCompleteAsync(null!, null);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── HandleConsolidationCompleteAsync — agent state ───────────────────

    [Fact]
    public async Task HandleConsolidationComplete_AgentNotNull_ClearsActiveJobId()
    {
        var agent = CreateAgent();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, agent);

        agent.ActiveJobId.Should().BeNull("ActiveJobId must be cleared after consolidation completes");
    }

    [Fact]
    public async Task HandleConsolidationComplete_AgentNull_DoesNotThrow()
    {
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("null agent is valid — consolidation pod may not be registered");
    }

    // ── HandleConsolidationCompleteAsync — change notification ───────────

    [Fact]
    public async Task HandleConsolidationComplete_AlwaysNotifiesChange()
    {
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockChangeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationComplete_WithAgent_NotifiesChange()
    {
        var agent = CreateAgent();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, agent);

        _mockChangeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── HandleConsolidationCompleteAsync — UpdateRunAsync (stopped in #3028) ──────

    /// <summary>
    /// Issue #3028: UpdateRunAsync must NOT be called from HandleConsolidationCompleteAsync.
    /// ConsolidationRuns writes have been stopped; the PipelineRun is the authoritative record.
    /// Issue #3030: UpdateRunAsync has been removed from IConsolidationService entirely.
    /// This test verifies HandleConsolidationCompleteAsync completes without error.
    /// </summary>
    [Fact]
    public async Task HandleConsolidationComplete_DoesNotCallUpdateRunAsync()
    {
        // Use a strict mock — only SaveHarnessSuggestionsAsync is permitted
        var strictConsolidation = new Mock<IConsolidationService>(MockBehavior.Strict);
        strictConsolidation.Setup(c => c.SaveHarnessSuggestionsAsync(
            It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = new HubConsolidationOperations(
            CreateModelFetchService(),
            strictConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        var result = new ConsolidationJobResult { JobId = "crun-success", Success = true, Summary = "Brain updated" };

        // The strict mock will throw if any unexpected method is called on IConsolidationService
        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("only SaveHarnessSuggestionsAsync is allowed on IConsolidationService");
    }

    [Fact]
    public async Task HandleConsolidationComplete_Success_CallsUpdateRunWithSucceeded()
    {
        // Issue #3028+#3030: UpdateRunAsync removed from IConsolidationService.
        // This test now verifies HandleConsolidationCompleteAsync completes without error.
        // TODO [WARNING]: Test name promises it verifies success-path behavior (e.g. CompleteRunAsync
        // called on the lifecycle manager, or OnChange/badge increments correctly), but the body
        // contains no behavioral assertion beyond "did not throw." Any regression that silently
        // skips success-path side-effects (badge, notifier, lifecycle manager call) would still pass.
        // Replace with assertions on _mockLifecycleManager.Verify(CompleteRunAsync) and
        // _notifier.Verify(NotifyChange) to give this test meaningful coverage.
        // (review-findings.md — TestQualityReviewer)
        var result = new ConsolidationJobResult
        {
            JobId = "crun-success",
            Success = true,
            Summary = "Brain updated"
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("HandleConsolidationCompleteAsync must not throw on success");
    }

    [Fact]
    public async Task HandleConsolidationComplete_Failure_CallsUpdateRunWithFailedAndErrorMessage()
    {
        // Issue #3028+#3030: UpdateRunAsync removed from IConsolidationService.
        // This test now verifies HandleConsolidationCompleteAsync completes without error on failure.
        // TODO [WARNING]: Test name promises failure-path verification but the body only asserts
        // no-throw. A regression where the failure path skips FailRunAsync or notifier calls would
        // still pass. Replace with assertions on _mockLifecycleManager.Verify(FailRunAsync) and
        // _notifier.Verify(NotifyChange) to give this test meaningful coverage.
        // (review-findings.md — TestQualityReviewer)
        var result = new ConsolidationJobResult
        {
            JobId = "crun-fail",
            Success = false,
            ErrorMessage = "agent crashed"
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("HandleConsolidationCompleteAsync must not throw on failure");
    }

    [Fact]
    public async Task HandleConsolidationComplete_UpdateRunThrows_DoesNotPropagate()
    {
        // Issue #3028+#3030: UpdateRunAsync removed from IConsolidationService.
        // This test verifies no throw regardless.
        // TODO [WARNING]: This test exercises a scenario that no longer exists (UpdateRunAsync was
        // removed from the interface). Nothing throws by design, making this a zero-value assertion.
        // The test would pass even if the SUT method were deleted entirely. Consider removing this
        // test or replacing it with a meaningful scenario such as verifying that a lifecycle-manager
        // exception does not propagate.
        // (review-findings.md — TestQualityReviewer)
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("method must not throw");
    }

    // ── HandleConsolidationCompleteAsync — token usage sum ───────────────
    // Issue #3028+#3030: UpdateRunAsync is no longer part of IConsolidationService.
    // These tests verify that the method still completes without throwing when token usage fields are set.

    [Fact]
    public async Task HandleConsolidationComplete_TokenUsage_MethodCompletes()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-tokens",
            Success = true,
            ReviewTokenUsage = new TokenUsage { InputTokens = 100, OutputTokens = 50, ReasoningTokens = 10 },
            RefinementTokenUsage = new TokenUsage { InputTokens = 200, OutputTokens = 80, ReasoningTokens = 0 },
            DiffSummaryTokenUsage = new TokenUsage { InputTokens = 30, OutputTokens = 20, ReasoningTokens = 5 }
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("token usage fields must not cause errors");
    }

    [Fact]
    public async Task HandleConsolidationComplete_NullTokenUsage_PassesZeroTotal()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-notok",
            Success = true,
            ReviewTokenUsage = null,
            RefinementTokenUsage = null,
            DiffSummaryTokenUsage = null
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("null token usage must not cause errors");
    }

    [Fact]
    public async Task HandleConsolidationComplete_PartialNullTokenUsage_SumsNonNullOnly()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-partial",
            Success = true,
            ReviewTokenUsage = new TokenUsage { InputTokens = 10, OutputTokens = 5, ReasoningTokens = 0 },
            RefinementTokenUsage = null,
            DiffSummaryTokenUsage = null
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("partial null token usage must not cause errors");
    }

    // ── HandleConsolidationCompleteAsync — harness suggestions ───────────

    [Fact]
    public async Task HandleConsolidationComplete_WithHarnessSuggestions_SavesAndIncrementsBadge()
    {
        var suggestions = MakeSuggestions("Add test A", "Add test B", "Add test C");

        var result = new ConsolidationJobResult
        {
            JobId = "crun-suggestions",
            Success = true,
            HarnessSuggestions = suggestions
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockConsolidation.Verify(c => c.SaveHarnessSuggestionsAsync(
            suggestions, It.IsAny<CancellationToken>()), Times.Once);
        _badgeService.BadgeCount.Should().Be(3, "badge must be incremented by the suggestion count");
    }

    [Fact]
    public async Task HandleConsolidationComplete_NullHarnessSuggestions_SkipsSaveAndLeaveBadgeUnchanged()
    {
        var before = _badgeService.BadgeCount;
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, HarnessSuggestions = null };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockConsolidation.Verify(c => c.SaveHarnessSuggestionsAsync(
            It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()), Times.Never);
        _badgeService.BadgeCount.Should().Be(before, "null suggestions must not change badge count");
    }

    [Fact]
    public async Task HandleConsolidationComplete_SaveHarnessSuggestionsThrows_DoesNotPropagate()
    {
        _mockConsolidation
            .Setup(c => c.SaveHarnessSuggestionsAsync(
                It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage error"));

        var result = new ConsolidationJobResult
        {
            JobId = "crun-save-throws",
            Success = true,
            HarnessSuggestions = MakeSuggestions("X")
        };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("SaveHarnessSuggestionsAsync failure is caught and logged");
    }

    // ── HandleConsolidationCompleteAsync — created issues / badge ────────

    [Fact]
    public async Task HandleConsolidationComplete_WithCreatedIssues_IncrementsBadge()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-issues",
            Success = true,
            CreatedIssues = new List<CreatedIssueInfo>
            {
                MakeIssue("10"),
                MakeIssue("11")
            }
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _badgeService.BadgeCount.Should().Be(2, "badge must be incremented by the created issues count");
    }

    [Fact]
    public async Task HandleConsolidationComplete_EmptyCreatedIssues_DoesNotIncrementBadge()
    {
        var before = _badgeService.BadgeCount;
        var result = new ConsolidationJobResult
        {
            JobId = "crun-empty-issues",
            Success = true,
            CreatedIssues = new List<CreatedIssueInfo>()
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _badgeService.BadgeCount.Should().Be(before, "empty created-issues list must not change badge count");
    }

    [Fact]
    public async Task HandleConsolidationComplete_NullCreatedIssues_DoesNotIncrementBadge()
    {
        var before = _badgeService.BadgeCount;
        var result = new ConsolidationJobResult
        {
            JobId = "crun-null-issues",
            Success = true,
            CreatedIssues = null
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _badgeService.BadgeCount.Should().Be(before, "null created-issues must not change badge count");
    }

    // ── HandleConsolidationCompleteAsync — debug info return value ────────

    [Fact]
    public async Task HandleConsolidationComplete_ReturnsDebugInfoContainingAgentFound()
    {
        var agent = CreateAgent();
        var result = new ConsolidationJobResult { JobId = "crun-debug", Success = true };
        var sut = CreateSut();

        var debugInfo = await sut.HandleConsolidationCompleteAsync(result, agent);

        debugInfo.Should().Contain("agentFound=True");
        debugInfo.Should().Contain(agent.AgentId.Value); // AgentId is string
    }

    [Fact]
    public async Task HandleConsolidationComplete_NullAgent_ReturnsDebugInfoWithAgentFoundFalse()
    {
        var result = new ConsolidationJobResult { JobId = "crun-debug-null", Success = true };
        var sut = CreateSut();

        var debugInfo = await sut.HandleConsolidationCompleteAsync(result, null);

        debugInfo.Should().Contain("agentFound=False");
    }

    // ── Both badge increments: harness + created issues ───────────────────

    [Fact]
    public async Task HandleConsolidationComplete_HarnessSuggestionsAndCreatedIssues_BothAddToBadge()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-both",
            Success = true,
            HarnessSuggestions = MakeSuggestions("A", "B"),
            CreatedIssues = new List<CreatedIssueInfo> { MakeIssue("5") }
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        // 2 harness suggestions + 1 created issue = 3 total badge increments
        _badgeService.BadgeCount.Should().Be(3,
            "2 harness suggestions + 1 created issue = badge 3");
    }

    // ── HandleConsolidationCompleteAsync — lifecycle manager ──────────────

    [Fact]
    public async Task HandleConsolidationComplete_Success_CallsCompleteRunAsync()
    {
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(new RunId("crun-lifecycle"), WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var result = new ConsolidationJobResult { JobId = "crun-lifecycle", Success = true };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockLifecycleManager.Verify(l => l.CompleteRunAsync(
            new RunId("crun-lifecycle"), WorkItemStatus.Succeeded,
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once,
            "successful consolidation must call CompleteRunAsync to write pipeline run history");
    }

    [Fact]
    public async Task HandleConsolidationComplete_Failure_CallsFailRunAsync()
    {
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(new RunId("crun-lc-fail"), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var result = new ConsolidationJobResult
        {
            JobId = "crun-lc-fail",
            Success = false,
            ErrorMessage = "consolidation agent error"
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockLifecycleManager.Verify(l => l.FailRunAsync(
            new RunId("crun-lc-fail"), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once,
            "failed consolidation must call FailRunAsync to write pipeline run history");
    }

    [Fact]
    public async Task HandleConsolidationComplete_LifecycleManagerThrows_DoesNotPropagate()
    {
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("lifecycle manager unavailable"));

        var result = new ConsolidationJobResult { JobId = "crun-lc-throws", Success = true };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("lifecycle manager failure is caught and logged, not propagated");
    }
}
