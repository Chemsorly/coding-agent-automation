using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for HubConsolidationOperations.
/// Covers: constructor guards, CompleteModelFetchRequest delegation, HandleConsolidationCompleteAsync paths.
/// </summary>
public sealed class HubConsolidationOperationsTests
{
    private static ModelFetchService MakeModelFetchService()
    {
        var registry = new AgentRegistryService(new Mock<ILogger>().Object);
        var comm = new Mock<IAgentCommunication>();
        return new ModelFetchService(registry, comm.Object, new Mock<ILogger>().Object);
    }

    private readonly ModelFetchService _modelFetch;
    private readonly Mock<IConsolidationService> _consolidation = new();
    private readonly ConsolidationBadgeService _badge = new();
    private readonly Mock<IChangeNotifier> _notifier = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly HubConsolidationOperations _sut;

    public HubConsolidationOperationsTests()
    {
        _modelFetch = MakeModelFetchService();
        _sut = new HubConsolidationOperations(
            _modelFetch,
            _consolidation.Object,
            _badge,
            _notifier.Object,
            _logger.Object);
    }

    private static AgentEntry MakeAgent(string id = "a1") =>
        new()
        {
            AgentId = new AgentId(id),
            ConnectionId = $"conn-{id}",
            Hostname = "host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-1"
        };

    private static void SetupUpdateRun(Mock<IConsolidationService> mock) =>
        mock.Setup(c => c.UpdateRunAsync(
            It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>(), It.IsAny<long>())).Returns(Task.CompletedTask);

    private static ConsolidationJobResult MakeResult(bool success = true) =>
        new()
        {
            JobId = "job-1",
            Success = success,
            Summary = success ? "All done" : null,
            ErrorMessage = success ? null : "Failed"
        };

    // ── Constructor guards (null consolidation/badge/notifier only — ModelFetchService is sealed) ─

    [Fact]
    public void Constructor_NullConsolidation_Throws()
    {
        var act = () => new HubConsolidationOperations(
            _modelFetch, null!, _badge, _notifier.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullBadge_Throws()
    {
        var act = () => new HubConsolidationOperations(
            _modelFetch, _consolidation.Object, null!, _notifier.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── CompleteModelFetchRequest ─────────────────────────────────────────

    [Fact]
    public void CompleteModelFetchRequest_NullResponse_Throws()
    {
        var act = () => _sut.CompleteModelFetchRequest(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── HandleConsolidationCompleteAsync ─────────────────────────────────

    [Fact]
    public async Task HandleConsolidationCompleteAsync_NullResult_Throws()
    {
        var act = () => _sut.HandleConsolidationCompleteAsync(null!, null);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_ClearsAgentActiveJobId()
    {
        SetupUpdateRun(_consolidation);

        var agent = MakeAgent();
        await _sut.HandleConsolidationCompleteAsync(MakeResult(), agent);

        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_NullAgent_DoesNotThrow()
    {
        SetupUpdateRun(_consolidation);

        var act = () => _sut.HandleConsolidationCompleteAsync(MakeResult(), null);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_NotifiesChange()
    {
        SetupUpdateRun(_consolidation);

        await _sut.HandleConsolidationCompleteAsync(MakeResult(), null);

        _notifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_Success_CallsUpdateRunWithSucceeded()
    {
        _consolidation.Setup(c => c.UpdateRunAsync(
            new RunId("job-1"), ConsolidationRunStatus.Succeeded, "All done",
            It.IsAny<CancellationToken>(), It.IsAny<long>())).Returns(Task.CompletedTask);

        await _sut.HandleConsolidationCompleteAsync(MakeResult(success: true), null);

        _consolidation.Verify(c => c.UpdateRunAsync(
            new RunId("job-1"), ConsolidationRunStatus.Succeeded, "All done",
            It.IsAny<CancellationToken>(), It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_Failure_CallsUpdateRunWithFailed()
    {
        _consolidation.Setup(c => c.UpdateRunAsync(
            new RunId("job-1"), ConsolidationRunStatus.Failed, "Failed",
            It.IsAny<CancellationToken>(), It.IsAny<long>())).Returns(Task.CompletedTask);

        await _sut.HandleConsolidationCompleteAsync(MakeResult(success: false), null);

        _consolidation.Verify(c => c.UpdateRunAsync(
            new RunId("job-1"), ConsolidationRunStatus.Failed, "Failed",
            It.IsAny<CancellationToken>(), It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_WithHarnessSuggestions_SavesAndIncrementsBadge()
    {
        SetupUpdateRun(_consolidation);
        _consolidation.Setup(c => c.SaveHarnessSuggestionsAsync(
            It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "job-1",
            Success = true,
            HarnessSuggestions = new HarnessSuggestions
            {
                BasedOnRunCount = 1,
                GeneratedAtUtc = DateTime.UtcNow,
                SuccessRate = 0.8m,
                Suggestions =
                [
                    new HarnessSuggestion { Frequency = 1, Rationale = "R", Text = "T" },
                    new HarnessSuggestion { Frequency = 1, Rationale = "R2", Text = "T2" }
                ]
            }
        };

        await _sut.HandleConsolidationCompleteAsync(result, null);

        _consolidation.Verify(c => c.SaveHarnessSuggestionsAsync(
            It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()), Times.Once);
        _badge.BadgeCount.Should().Be(2); // 2 suggestions
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_WithCreatedIssues_IncrementsBadgeCount()
    {
        SetupUpdateRun(_consolidation);

        var result = new ConsolidationJobResult
        {
            JobId = "job-1",
            Success = true,
            CreatedIssues =
            [
                new CreatedIssueInfo { Identifier = "GH-10", Title = "T1", Url = "" },
                new CreatedIssueInfo { Identifier = "GH-11", Title = "T2", Url = "" },
                new CreatedIssueInfo { Identifier = "GH-12", Title = "T3", Url = "" }
            ]
        };

        await _sut.HandleConsolidationCompleteAsync(result, null);

        _badge.BadgeCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_ReturnsDebugInfo()
    {
        SetupUpdateRun(_consolidation);

        var result = await _sut.HandleConsolidationCompleteAsync(MakeResult(), null);

        result.Should().Contain("agentFound=False");
    }

    [Fact]
    public async Task HandleConsolidationCompleteAsync_UpdateRunThrows_DoesNotPropagate()
    {
        _consolidation.Setup(c => c.UpdateRunAsync(
            It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var act = () => _sut.HandleConsolidationCompleteAsync(MakeResult(), null);
        await act.Should().NotThrowAsync();
    }
}
