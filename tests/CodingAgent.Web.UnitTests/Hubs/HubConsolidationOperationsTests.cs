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
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();

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
        _mockRunService.Object,
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
            _mockRunService.Object,
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
            _mockRunService.Object,
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
            _mockRunService.Object,
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
            _mockRunService.Object,
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
            _mockRunService.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("lifecycleManager");
    }

    [Fact]
    public void Constructor_NullRunService_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            null!,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("runService");
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
            _mockRunService.Object,
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── CompleteModelFetchRequestAsync ────────────────────────────────────

    [Fact]
    public async Task CompleteModelFetchRequestAsync_NullResponse_Throws()
    {
        var sut = CreateSut();
        var act = async () => await sut.CompleteModelFetchRequestAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CompleteModelFetchRequestAsync_UnknownRequestId_DoesNotThrow()
    {
        // No pending request with this ID — ModelFetchService logs a warning and moves on
        var response = new FetchModelsResponse { RequestId = "unknown-req-id", Models = [] };
        var sut = CreateSut();

        var act = async () => await sut.CompleteModelFetchRequestAsync(response);
        await act.Should().NotThrowAsync("unknown request IDs are handled gracefully with a warning log");
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
    /// </summary>
    [Fact]
    public async Task HandleConsolidationComplete_DoesNotCallUpdateRunAsync()
    {
        // Use a strict mock so any unexpected call would throw
        var strictConsolidation = new Mock<IConsolidationService>(MockBehavior.Strict);
        strictConsolidation.Setup(c => c.SaveHarnessSuggestionsAsync(
            It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = new HubConsolidationOperations(
            CreateModelFetchService(),
            strictConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            _mockRunService.Object,
            _mockLogger.Object);

        var result = new ConsolidationJobResult { JobId = "crun-success", Success = true, Summary = "Brain updated" };

        // The strict mock will throw if UpdateRunAsync is called (method removed in #3030)
        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("UpdateRunAsync must not be called — removed in #3030");
    }

    // TODO [WARNING]: HandleConsolidationComplete_Success_CallsUpdateRunWithSucceeded,
    // HandleConsolidationComplete_Failure_CallsUpdateRunWithFailedAndErrorMessage, and
    // HandleConsolidationComplete_UpdateRunThrows_DoesNotPropagate are now effectively duplicate
    // no-throw smoke tests — all three only assert NotThrowAsync() with no distinction between
    // success and failure paths. Consider collapsing into one smoke test or replacing with assertions
    // on observable behavior (SaveHarnessSuggestionsAsync called on success, badge incremented,
    // NotifyChange fired). (issue #3030)

    [Fact]
    public async Task HandleConsolidationComplete_Success_CallsUpdateRunWithSucceeded()
    {
        // Issue #3028/#3030: UpdateRunAsync is no longer called from HandleConsolidationCompleteAsync.
        // This test now verifies that the method completes without error.
        var result = new ConsolidationJobResult
        {
            JobId = "crun-success",
            Success = true,
            Summary = "Brain updated"
        };
        var sut = CreateSut();

        // Should complete without error — UpdateRunAsync no longer exists on the interface
        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleConsolidationComplete_Failure_CallsUpdateRunWithFailedAndErrorMessage()
    {
        // Issue #3028/#3030: UpdateRunAsync is no longer called from HandleConsolidationCompleteAsync.
        var result = new ConsolidationJobResult
        {
            JobId = "crun-fail",
            Success = false,
            ErrorMessage = "agent crashed"
        };
        var sut = CreateSut();

        // Should complete without error — UpdateRunAsync no longer exists on the interface
        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleConsolidationComplete_UpdateRunThrows_DoesNotPropagate()
    {
        // Issue #3028/#3030: UpdateRunAsync is no longer called; this test verifies no throw regardless.
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("method must not throw regardless of UpdateRunAsync state");
    }

    // ── HandleConsolidationCompleteAsync — token usage sum ───────────────
    // Issue #3028: UpdateRunAsync is no longer called, so token usage is no longer
    // passed to the ConsolidationRuns store. These tests now verify that the method
    // still completes without throwing when token usage fields are set.

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

    // ── ConsolidationResultSummary propagation — fix for CRITICAL review findings ──
    // Verifies that ConsolidationResultSummary is set on the in-memory PipelineRun BEFORE
    // RunLifecycleManager.RemoveRun claims it, so AddRunToHistoryAsync serialises the
    // summary into the persisted PipelineRunSummary. (review-findings-correctness.md CRITICAL)

    // TODO [WARNING]: This test uses a mock IOrchestratorRunService that returns the same mutable
    // inMemoryRun reference on every GetRun call. The capturedSummary callback closes over that
    // same object, so the test passes vacuously even without _runService.ReplaceRun(inMemoryRun) —
    // it does not exercise Redis copy-semantics. The regression guard against the actual bug is
    // HandleConsolidationComplete_RedisRunService_SummaryIsOnTheRunThatCompletionRemoves below.
    // (review-findings-testqualityreviewer.md WARNING finding #1)
    [Fact]
    public async Task HandleConsolidationComplete_Success_SetsSummaryOnInMemoryRunBeforeLifecycleCall()
    {
        // Arrange: create an in-memory run that GetRun will return
        var jobId = "crun-summary-success";
        var inMemoryRun = new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = new IssueIdentifier("consol:brain-e2e"),
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e"
        };
        _mockRunService.Setup(s => s.GetRun(new RunId(jobId))).Returns(inMemoryRun);

        // Capture the run's ConsolidationResultSummary at the moment CompleteRunAsync is called
        string? capturedSummary = null;
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(new RunId(jobId), WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .Callback(() => capturedSummary = inMemoryRun.ConsolidationResultSummary)
            .ReturnsAsync((PipelineRun?)null);

        var result = new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            Summary = "Consolidated 3 files"
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        capturedSummary.Should().Be("Consolidated 3 files",
            "ConsolidationResultSummary must be set on the in-memory run BEFORE CompleteRunAsync removes it, " +
            "so that AddRunToHistoryAsync serialises it into the persisted PipelineRunSummary");
        inMemoryRun.ConsolidationResultSummary.Should().Be("Consolidated 3 files");
    }

    // TODO [WARNING]: Same vacuous-pass issue as the success sibling above — uses a mock run service
    // returning the same mutable reference, so this test passes even without ReplaceRun. The true
    // regression guard is HandleConsolidationComplete_RedisRunService_SummaryIsOnTheRunThatCompletionRemoves.
    // (review-findings-testqualityreviewer.md WARNING finding #1)
    [Fact]
    public async Task HandleConsolidationComplete_Failure_SetsErrorMessageAsSummaryOnInMemoryRunBeforeLifecycleCall()
    {
        // Arrange: create an in-memory run that GetRun will return
        var jobId = "crun-summary-fail";
        var inMemoryRun = new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = new IssueIdentifier("consol:brain-e2e"),
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e"
        };
        _mockRunService.Setup(s => s.GetRun(new RunId(jobId))).Returns(inMemoryRun);

        // Capture the run's ConsolidationResultSummary at the moment FailRunAsync is called
        string? capturedSummary = null;
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(new RunId(jobId), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .Callback(() => capturedSummary = inMemoryRun.ConsolidationResultSummary)
            .ReturnsAsync((PipelineRun?)null);

        var result = new ConsolidationJobResult
        {
            JobId = jobId,
            Success = false,
            ErrorMessage = "Brain provider unavailable"
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        capturedSummary.Should().Be("Brain provider unavailable",
            "ConsolidationResultSummary must be set to ErrorMessage on the in-memory run BEFORE FailRunAsync removes it, " +
            "so that the failure message surfaces in the history row Summary column");
        inMemoryRun.ConsolidationResultSummary.Should().Be("Brain provider unavailable");
    }

    [Fact]
    public async Task HandleConsolidationComplete_Success_NullSummary_SetsSummaryToNull()
    {
        // Arrange: GetRun returns an in-memory run; result.Summary is null
        var jobId = "crun-summary-null";
        var inMemoryRun = new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = new IssueIdentifier("consol:brain-e2e"),
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e"
        };
        _mockRunService.Setup(s => s.GetRun(new RunId(jobId))).Returns(inMemoryRun);

        var result = new ConsolidationJobResult { JobId = jobId, Success = true, Summary = null };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        inMemoryRun.ConsolidationResultSummary.Should().BeNull(
            "null Summary must not populate ConsolidationResultSummary — the row will show '—'");
    }

    [Fact]
    public async Task HandleConsolidationComplete_RunNotInMemory_DoesNotThrowAndStillCallsLifecycleManager()
    {
        // Arrange: GetRun returns null (run already removed or never in memory)
        var jobId = "crun-not-in-memory";
        _mockRunService.Setup(s => s.GetRun(new RunId(jobId))).Returns((PipelineRun?)null);

        var result = new ConsolidationJobResult { JobId = jobId, Success = true, Summary = "Done" };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("GetRun returning null must be handled gracefully");

        // Lifecycle manager must still be called even when the run is not in memory
        _mockLifecycleManager.Verify(l => l.CompleteRunAsync(
            new RunId(jobId), WorkItemStatus.Succeeded,
            It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once,
            "CompleteRunAsync must be called even when the in-memory run is not found");
    }

    /// <summary>
    /// Regression test for issue #3562: proves that <see cref="HubConsolidationOperations.HandleConsolidationCompleteAsync"/>
    /// must call <c>_runService.ReplaceRun(inMemoryRun)</c> after setting <c>ConsolidationResultSummary</c>.
    ///
    /// With <see cref="DistributedRunService"/>, <c>GetRun</c> deserialises a fresh copy from the Redis hash
    /// on every call. Mutating that copy without calling <c>ReplaceRun</c> discards the change.
    /// <c>RemoveRun</c> (called inside <c>CompleteRunAsync</c>/<c>FailRunAsync</c>) reads the hash again —
    /// if <c>ReplaceRun</c> was not called first, the returned run has <c>ConsolidationResultSummary = null</c>
    /// and the history row renders "—".
    ///
    /// This test uses a real <see cref="DistributedRunService"/> backed by <see cref="CodingAgent.Web.TestUtilities.FakeRedisStore"/>
    /// so that the Redis copy-semantics are exercised. An in-memory <see cref="OrchestratorRunService"/> would
    /// pass vacuously because <c>GetRun</c> returns the same mutable reference.
    ///
    /// NOTE: <see cref="CodingAgent.Web.TestUtilities.FakeRedisStore"/> completes all async operations
    /// synchronously (via <c>Task.FromResult</c>), so fire-and-forget continuations settle before the next
    /// statement; no <c>await Task.Yield()</c> barrier is needed here because <c>ReplaceRun</c> and
    /// <c>RemoveRun</c> are both synchronous (they call <c>.GetAwaiter().GetResult()</c> internally).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleConsolidationComplete_RedisRunService_SummaryIsOnTheRunThatCompletionRemoves(bool success)
    {
        // Arrange: capture variable must be declared before the mock setup lambdas that assign to it
        PipelineRun? removed = null;

        var runService = new DistributedRunService(
            new CodingAgent.Web.TestUtilities.FakeRedisStore(),
            (_, _, _) => Task.FromResult(false),
            Mock.Of<ILogger>());

        var jobId = Guid.NewGuid().ToString();
        runService.AddRun(new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = new IssueIdentifier("RefactoringDetection:tpl-1"),
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-1"
        });

        // _mockLifecycleManager callbacks simulate what RunLifecycleManager does: call RemoveRun to claim
        // the run from the store. The returned PipelineRun is what AddRunToHistoryAsync would serialize.
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(new RunId(jobId), WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .Callback(() => removed = runService.RemoveRun(new RunId(jobId)))
            .ReturnsAsync((PipelineRun?)null);

        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(new RunId(jobId), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .Callback(() => removed = runService.RemoveRun(new RunId(jobId)))
            .ReturnsAsync((PipelineRun?)null);

        // Construct SUT with the real runService instead of _mockRunService.Object.
        // _mockRunService is not involved — no Setup calls needed for it in this test.
        var sut = new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
            _mockLifecycleManager.Object,
            runService,
            _mockLogger.Object);

        var result = success
            ? new ConsolidationJobResult { JobId = jobId, Success = true, Summary = "Created 3 refactoring issue(s): #1, #2, #3" }
            : new ConsolidationJobResult { JobId = jobId, Success = false, ErrorMessage = "Push failed: rejected" };

        // Act
        await sut.HandleConsolidationCompleteAsync(result, null);

        // Assert: the run claimed by RemoveRun (inside the lifecycle manager callback) must carry
        // the summary that was set before CompleteRunAsync/FailRunAsync was called.
        // Without _runService.ReplaceRun(inMemoryRun), removed.ConsolidationResultSummary is null
        // because RemoveRun reads the Redis hash, which was never updated.
        removed.Should().NotBeNull("RemoveRun must have been called by the lifecycle manager mock callback");
        var expectedSummary = success ? "Created 3 refactoring issue(s): #1, #2, #3" : "Push failed: rejected";
        removed!.ConsolidationResultSummary.Should().Be(expectedSummary,
            "ConsolidationResultSummary must be written back to the store via ReplaceRun BEFORE " +
            "CompleteRunAsync/FailRunAsync calls RemoveRun, otherwise the persisted history row shows '—'");
    }
}
