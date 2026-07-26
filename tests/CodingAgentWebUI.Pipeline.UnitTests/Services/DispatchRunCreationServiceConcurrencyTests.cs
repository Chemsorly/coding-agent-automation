using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="DispatchRunCreationService"/> atomic reservation mechanism
/// correctly prevents duplicate <see cref="PipelineRun"/> registration when multiple
/// callers attempt to dispatch the same issue concurrently.
/// </summary>
public class DispatchRunCreationServiceConcurrencyTests : IAsyncDisposable
{
    private readonly DispatchRunCreationService _service;

    public DispatchRunCreationServiceConcurrencyTests()
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockLogger = new Mock<Serilog.ILogger>();

        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test Repo" }
            });
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test Agent",
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet" } }
            });
        mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepoProvider.Object);

        var realRunService = new OrchestratorRunService(mockLogger.Object);
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>().AsReadOnly());

        var lifecycle = new PipelineRunLifecycleService(mockHistoryService.Object, realRunService, mockLogger.Object);

        _service = new DispatchRunCreationService(
            lifecycle,
            mockConfigStore.Object,
            mockFactory.Object,
            mockLogger.Object);
    }

    [Fact]
    public async Task ConcurrentDispatchSameIssue_ExactlyOneRunCreated()
    {
        // Arrange: multiple concurrent callers dispatching the same issue
        const int concurrentCallers = 10;
        var barrier = new Barrier(concurrentCallers);

        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _service.CreateDispatchedRunAsync(
                "issue-1", "repo-1", "42", "agent-1", "agent-x", CancellationToken.None);
        })).ToArray();

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert: exactly one caller succeeds, all others get null
        var successfulRuns = results.Where(r => r is not null).ToList();
        successfulRuns.Should().ContainSingle("only one concurrent caller should win the atomic reservation");
        successfulRuns[0]!.IssueIdentifier.Value.Should().Be("42");

        var nullResults = results.Count(r => r is null);
        nullResults.Should().Be(concurrentCallers - 1);
    }

    [Fact]
    public async Task ConcurrentDispatchSameIssue_WinnerRunIsRegistered()
    {
        // Arrange
        const int concurrentCallers = 5;
        var barrier = new Barrier(concurrentCallers);

        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _service.CreateDispatchedRunAsync(
                "issue-1", "repo-1", "99", "agent-1", "agent-x", CancellationToken.None);
        })).ToArray();

        // Act
        await Task.WhenAll(tasks);

        // Assert: the winning run is visible in active runs
        var activeRuns = _service.GetAllActiveRuns();
        activeRuns.Should().ContainSingle(r => r.IssueIdentifier == "99");
    }

    [Fact]
    public async Task ConcurrentDispatchDifferentIssues_AllSucceed()
    {
        // Arrange: concurrent callers dispatching different issues should all succeed
        const int concurrentCallers = 5;
        var barrier = new Barrier(concurrentCallers);

        var tasks = Enumerable.Range(0, concurrentCallers).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _service.CreateDispatchedRunAsync(
                "issue-1", "repo-1", $"issue-{i}", "agent-1", "agent-x", CancellationToken.None);
        })).ToArray();

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert: all callers succeed since they target different issues
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        var activeRuns = _service.GetAllActiveRuns();
        activeRuns.Should().HaveCount(concurrentCallers);
    }

    [Fact]
    public async Task ConcurrentReserveRunIdSameIssue_ExactlyOneReservation()
    {
        // Arrange: multiple concurrent callers reserving the same issue
        const int concurrentCallers = 10;
        var barrier = new Barrier(concurrentCallers);

        var tasks = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _service.ReserveRunIdAsync(
                "issue-1", "repo-1", "77", "agent-1", "agent-x", CancellationToken.None);
        })).ToArray();

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert: exactly one caller wins the reservation
        var successfulReservations = results.Where(r => r is not null).ToList();
        successfulReservations.Should().ContainSingle("only one concurrent caller should win the atomic reservation");
        successfulReservations[0]!.RunId.Should().NotBeNullOrEmpty();
        successfulReservations[0]!.RepositoryName.Should().Be("owner/repo");

        var nullResults = results.Count(r => r is null);
        nullResults.Should().Be(concurrentCallers - 1);
    }

    [Fact]
    public async Task SequentialDispatchAfterCompletion_ReservationReleasedCorrectly()
    {
        // Arrange: first dispatch succeeds and completes (reservation released by finally block)
        var firstRun = await _service.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "50", "agent-1", "agent-x", CancellationToken.None);
        firstRun.Should().NotBeNull();

        // Act: second dispatch for same issue — should fail due to lifecycle dedup
        // (the issue is registered as being processed), NOT due to stale reservation
        var secondRun = await _service.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "50", "agent-1", "agent-y", CancellationToken.None);

        // Assert: second call returns null because the issue is being processed (lifecycle guard),
        // not because the reservation leaked
        secondRun.Should().BeNull();
        _service.IsIssueBeingProcessed("50", "issue-1").Should().BeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
    }
}
