using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Api.Client.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ApiBackedPipelineRunHistoryService"/>.
/// </summary>
public sealed class ApiBackedPipelineRunHistoryServiceTests
{
    private readonly Mock<IPipelineApiRunHistoryClient> _client = new();
    private readonly Mock<ILogger> _logger = new();

    private ApiBackedPipelineRunHistoryService CreateSut() =>
        new(_client.Object, _logger.Object);

    // ── Constructor guards ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new ApiBackedPipelineRunHistoryService(null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("client");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new ApiBackedPipelineRunHistoryService(_client.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── AddRunToHistoryAsync — consolidation skip ─────────────────────────

    [Fact]
    public async Task AddRunToHistoryAsync_ConsolidationRun_SkipsClientCall()
    {
        var run = MakeRun(providerConfigId: ConsolidationConstants.ProviderConfigId);

        var sut = CreateSut();
        await sut.AddRunToHistoryAsync(run);

        _client.Verify(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddRunToHistoryAsync_NullRun_Throws()
    {
        var sut = CreateSut();
        var act = async () => await sut.AddRunToHistoryAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── AddRunToHistoryAsync — non-terminal step forced to Failed ─────────

    [Fact]
    public async Task AddRunToHistoryAsync_NonTerminalStep_ForcesFailedInSummary()
    {
        PipelineRunSummary? captured = null;
        _client
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var run = MakeRun(step: PipelineStep.GeneratingCode); // non-terminal
        var sut = CreateSut();

        await sut.AddRunToHistoryAsync(run);

        captured.Should().NotBeNull();
        captured!.FinalStep.Should().Be(PipelineStep.Failed,
            "non-terminal step must be forced to Failed before persistence");
    }

    [Fact]
    public async Task AddRunToHistoryAsync_TerminalStep_PassesSummaryAsIs()
    {
        PipelineRunSummary? captured = null;
        _client
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var run = MakeRun(step: PipelineStep.Completed); // terminal
        var sut = CreateSut();

        await sut.AddRunToHistoryAsync(run);

        captured!.FinalStep.Should().Be(PipelineStep.Completed,
            "terminal step must be preserved without override");
    }

    [Theory]
    [InlineData(PipelineStep.Completed)]
    [InlineData(PipelineStep.Failed)]
    [InlineData(PipelineStep.Cancelled)]
    public async Task AddRunToHistoryAsync_AllTerminalSteps_PassedThrough(PipelineStep step)
    {
        PipelineRunSummary? captured = null;
        _client
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var run = MakeRun(step: step);
        var sut = CreateSut();
        await sut.AddRunToHistoryAsync(run);

        captured!.FinalStep.Should().Be(step);
    }

    // ── AddRunSummaryAsync — exception is swallowed (non-fatal) ──────────

    [Fact]
    public async Task AddRunSummaryAsync_ClientThrows_DoesNotPropagate()
    {
        _client
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API unavailable"));

        var sut = CreateSut();
        var summary = MakeSummary();

        var act = async () => await sut.AddRunSummaryAsync(summary);
        await act.Should().NotThrowAsync("non-fatal persistence failure must be swallowed and logged");
    }

    [Fact]
    public async Task AddRunSummaryAsync_NullSummary_Throws()
    {
        var sut = CreateSut();
        var act = async () => await sut.AddRunSummaryAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AddRunSummaryAsync_CancellationThrows_Propagates()
    {
        _client
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut();
        var act = async () => await sut.AddRunSummaryAsync(MakeSummary());

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate, not be swallowed");
    }

    // ── GetRunHistoryAsync (unpaged) ──────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_Unpaged_ReturnsItems()
    {
        var items = new List<PipelineRunSummary> { MakeSummary(), MakeSummary() };
        _client
            .Setup(c => c.GetRunHistoryAsync(1, 1000, false, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = items, Page = 1, PageSize = 1000, HasMore = false });

        var sut = CreateSut();
        var result = await sut.GetRunHistoryAsync();

        result.Should().HaveCount(2);
    }

    // ── GetRunHistoryAsync (paged) ────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_Paged_DelegatesPageAndSize()
    {
        var expected = new PagedResult<PipelineRunSummary>
        {
            Items = [MakeSummary()],
            Page = 2,
            PageSize = 10,
            HasMore = true
        };

        _client
            .Setup(c => c.GetRunHistoryAsync(2, 10, false, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.GetRunHistoryAsync(page: 2, pageSize: 10);

        result.Page.Should().Be(2);
        result.PageSize.Should().Be(10);
        result.HasMore.Should().BeTrue();
    }

    // ── GetRunHistoryAsync (feedbackOnly) ─────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_PassesFlagToClient()
    {
        var expected = new PagedResult<PipelineRunSummary>
        {
            Items = [],
            Page = 1,
            PageSize = 20,
            HasMore = false
        };

        _client
            .Setup(c => c.GetRunHistoryAsync(1, 20, true, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = CreateSut();
        var result = await sut.GetRunHistoryAsync(page: 1, pageSize: 20, feedbackOnly: true);

        _client.Verify(c => c.GetRunHistoryAsync(1, 20, true, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Items.Should().BeEmpty();
    }

    // ── GetRunAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunAsync_Found_ReturnsSummary()
    {
        var id = Guid.NewGuid();
        var summary = MakeSummary(id.ToString());

        _client.Setup(c => c.GetRunAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var sut = CreateSut();
        var result = await sut.GetRunAsync(id);

        result.Should().NotBeNull();
        result!.RunId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetRunAsync_NotFound_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _client.Setup(c => c.GetRunAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);

        var sut = CreateSut();
        var result = await sut.GetRunAsync(id);

        result.Should().BeNull();
    }

    // ── Workspace methods are no-ops ──────────────────────────────────────

    [Fact]
    public void TryDeleteWorkspace_DoesNotThrow()
    {
        var sut = CreateSut();
        var act = () => sut.TryDeleteWorkspace("/some/path", "run-1", "/base");
        act.Should().NotThrow("orchestrator has no local workspace — must be a no-op");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_DoesNotThrow()
    {
        var sut = CreateSut();
        var act = () => sut.CleanupExpiredWorkspaces(new PipelineConfiguration());
        act.Should().NotThrow("orchestrator has no local workspace — must be a no-op");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PipelineRun MakeRun(
        string? providerConfigId = null,
        PipelineStep step = PipelineStep.Completed) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = providerConfigId ?? "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        CurrentStep = step
    };

    private static PipelineRunSummary MakeSummary(string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5)
    };
}
