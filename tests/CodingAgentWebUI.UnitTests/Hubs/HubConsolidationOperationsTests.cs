using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

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

    // Real instances (sealed — cannot mock)
    private readonly ConsolidationBadgeService _badgeService = new();

    private ModelFetchService CreateModelFetchService()
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
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("changeNotifier");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new HubConsolidationOperations(
            CreateModelFetchService(),
            _mockConsolidation.Object,
            _badgeService,
            _mockChangeNotifier.Object,
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

    // ── HandleConsolidationCompleteAsync — UpdateRunAsync ─────────────────

    [Fact]
    public async Task HandleConsolidationComplete_Success_CallsUpdateRunWithSucceeded()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-success",
            Success = true,
            Summary = "Brain updated"
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockConsolidation.Verify(c => c.UpdateRunAsync(
            new RunId("crun-success"),
            ConsolidationRunStatus.Succeeded,
            "Brain updated",
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationComplete_Failure_CallsUpdateRunWithFailedAndErrorMessage()
    {
        var result = new ConsolidationJobResult
        {
            JobId = "crun-fail",
            Success = false,
            ErrorMessage = "agent crashed"
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        _mockConsolidation.Verify(c => c.UpdateRunAsync(
            new RunId("crun-fail"),
            ConsolidationRunStatus.Failed,
            "agent crashed",
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationComplete_UpdateRunThrows_DoesNotPropagate()
    {
        _mockConsolidation
            .Setup(c => c.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };
        var sut = CreateSut();

        var act = async () => await sut.HandleConsolidationCompleteAsync(result, null);
        await act.Should().NotThrowAsync("UpdateRunAsync failure is caught and logged, not propagated");
    }

    // ── HandleConsolidationCompleteAsync — token usage sum ───────────────

    [Fact]
    public async Task HandleConsolidationComplete_TokenUsage_SummedCorrectly()
    {
        long capturedTokens = -1;
        _mockConsolidation
            .Setup(c => c.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Callback<RunId, ConsolidationRunStatus, string?, CancellationToken, long>(
                (_, _, _, _, tokens) => capturedTokens = tokens)
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "crun-tokens",
            Success = true,
            ReviewTokenUsage     = new TokenUsage { InputTokens = 100, OutputTokens = 50,  ReasoningTokens = 10 }, // 160
            RefinementTokenUsage = new TokenUsage { InputTokens = 200, OutputTokens = 80,  ReasoningTokens = 0  }, // 280
            DiffSummaryTokenUsage= new TokenUsage { InputTokens = 30,  OutputTokens = 20,  ReasoningTokens = 5  }  // 55
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        capturedTokens.Should().Be(495, "100+50+10 + 200+80+0 + 30+20+5 = 495");
    }

    [Fact]
    public async Task HandleConsolidationComplete_NullTokenUsage_PassesZeroTotal()
    {
        long capturedTokens = -1;
        _mockConsolidation
            .Setup(c => c.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Callback<RunId, ConsolidationRunStatus, string?, CancellationToken, long>(
                (_, _, _, _, tokens) => capturedTokens = tokens)
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "crun-notok",
            Success = true,
            ReviewTokenUsage = null, RefinementTokenUsage = null, DiffSummaryTokenUsage = null
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        capturedTokens.Should().Be(0, "all-null token usages must sum to zero");
    }

    [Fact]
    public async Task HandleConsolidationComplete_PartialNullTokenUsage_SumsNonNullOnly()
    {
        long capturedTokens = -1;
        _mockConsolidation
            .Setup(c => c.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Callback<RunId, ConsolidationRunStatus, string?, CancellationToken, long>(
                (_, _, _, _, tokens) => capturedTokens = tokens)
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "crun-partial",
            Success = true,
            ReviewTokenUsage = new TokenUsage { InputTokens = 10, OutputTokens = 5, ReasoningTokens = 0 },
            RefinementTokenUsage = null,
            DiffSummaryTokenUsage = null
        };
        var sut = CreateSut();

        await sut.HandleConsolidationCompleteAsync(result, null);

        capturedTokens.Should().Be(15, "10+5+0 = 15; nulls contribute 0");
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
}
