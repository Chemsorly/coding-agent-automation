using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Characterization tests verifying that the <see cref="DispatchRunCreationService"/> reservation guard
/// (the atomic TryAdd / finally-TryRemove bracket) releases the reservation on all failure paths,
/// allowing a subsequent dispatch attempt to proceed. These tests confirm the invariant that the
/// TryRemove in the finally block runs even when the delegate body fails (RegisterDispatchedRun
/// returns false).
/// </summary>
public class DispatchRunCreationServiceReservationReleaseTests : IAsyncDisposable
{
    // Shared provider config / factory setup returned by helpers below.
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();

    public DispatchRunCreationServiceReservationReleaseTests()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test Repo" }
            });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test Agent",
                    Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet" } }
            });
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                // TODO: This uses sync-over-async (.GetAwaiter().GetResult()) which risks deadlock if
                // LoadProviderConfigsAsync is ever changed to return an incomplete Task (e.g. a
                // TaskCompletionSource-backed stub). Convert to an async lambda using ReturnsAsync or
                // Returns<...>(async (id, kind, ct) => ...) when refactoring this test setup.
                // See review finding: DotNetSpecialist [WARNING] line 41.
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
        _mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>().AsReadOnly());
    }

    /// <summary>
    /// Builds a service where the mock run service's <see cref="IOrchestratorRunService.IsIssueBeingProcessed"/>
    /// returns values from <paramref name="callSequence"/> in order (repeating the last value once exhausted).
    /// This lets individual tests simulate: "first call = false (proceed), second call = true (RegisterDispatchedRun
    /// fails), third call = false (second dispatch attempt proceeds), ..." without coupling the test to internal
    /// call counts.
    /// </summary>
    private DispatchRunCreationService BuildService(Queue<bool> isProcessingSequence)
    {
        var mockRunService = new Mock<IOrchestratorRunService>();

        mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(() => isProcessingSequence.Count > 0 ? isProcessingSequence.Dequeue() : false);

        // AddRun is a void method; default Mock behavior (do-nothing) is correct.
        // ReplaceRun likewise.

        var lifecycle = new PipelineRunLifecycleService(_mockHistoryService.Object, mockRunService.Object, _mockLogger.Object);

        return new DispatchRunCreationService(
            lifecycle,
            _mockConfigStore.Object,
            _mockFactory.Object,
            _mockLogger.Object);
    }

    // ── CreateDispatchedRunAsync failure-path release ────────────────────────────────

    /// <summary>
    /// When <see cref="PipelineRunLifecycleService.RegisterDispatchedRun"/> returns false during
    /// <see cref="DispatchRunCreationService.CreateDispatchedRunAsync"/>, the finally block must
    /// run TryRemove so that a subsequent dispatch of the same issue is not permanently blocked.
    /// </summary>
    [Fact]
    public async Task CreateDispatchedRunAsync_WhenRegisterDispatchedRunReturnsFalse_ReservationIsReleasedSoIssueCanBeDispatchedAgain()
    {
        // Arrange:
        // Call 1 — the guard inside the try block: returns false → we proceed into the body
        // Call 2 — RegisterDispatchedRun's internal IsIssueBeingProcessed: returns true → RegisterDispatchedRun returns false → body returns null
        // Call 3 — second dispatch, the guard inside the try block: returns false → we proceed
        // Call 4 — second dispatch's RegisterDispatchedRun's internal IsIssueBeingProcessed: returns false → RegisterDispatchedRun returns true → registers successfully
        // TODO: This queue is fragile because it depends on knowing how many times
        // PipelineRunLifecycleService.RegisterDispatchedRun internally calls IsIssueBeingProcessed.
        // If that implementation changes (guard removed, extra call added), the queue will deliver
        // values out of phase and the test may silently pass for the wrong reason or fail spuriously.
        // A more robust approach: mock IOrchestratorRunService.AddRun to throw on the first call,
        // directly forcing RegisterDispatchedRun to return false without relying on the internal
        // call count. See review finding: Correctness [WARNING] line 84.
        var isProcessingSequence = new Queue<bool>([false, true, false, false]);
        await using var service = BuildService(isProcessingSequence);

        var request = new DispatchRunRequest
        {
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            IssueIdentifier = "200",
            AgentProviderId = "agent-1",
            AgentId = "agent-x"
        };

        // Act — first dispatch: RegisterDispatchedRun returns false → null result
        var firstResult = await service.CreateDispatchedRunAsync(request, CancellationToken.None);

        // Assert — first call returns null (body failed), but the reservation was released
        firstResult.Should().BeNull(
            "RegisterDispatchedRun returned false so the run was not created");

        // Act — second dispatch: reservation is released so this should NOT be blocked by TryAdd
        var secondRequest = request with { AgentId = "agent-y" };
        var secondResult = await service.CreateDispatchedRunAsync(secondRequest, CancellationToken.None);

        // Assert — second call succeeds, proving TryRemove ran in the finally block
        secondResult.Should().NotBeNull(
            "after the reservation was released by the finally block, a subsequent dispatch must succeed");
    }

    /// <summary>
    /// When <see cref="DispatchRunCreationService.CreateDispatchedRunAsync"/> body throws an
    /// exception (simulated by making the provider factory throw), the finally block must
    /// run TryRemove so that a subsequent dispatch of the same issue is not permanently blocked.
    /// </summary>
    [Fact]
    public async Task CreateDispatchedRunAsync_WhenBodyThrows_ReservationIsReleasedSoIssueCanBeDispatchedAgain()
    {
        // Arrange: make the factory throw on the first call only
        var callCount = 0;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated provider failure");
                return _mockRepoProvider.Object;
            });

        // IsIssueBeingProcessed always false so both dispatches reach the body
        var mockRunService = new Mock<IOrchestratorRunService>();
        mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        var lifecycle = new PipelineRunLifecycleService(
            _mockHistoryService.Object, mockRunService.Object, _mockLogger.Object);

        await using var service = new DispatchRunCreationService(
            lifecycle,
            _mockConfigStore.Object,
            mockFactory.Object,
            _mockLogger.Object);

        var request = new DispatchRunRequest
        {
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            IssueIdentifier = "201",
            AgentProviderId = "agent-1",
            AgentId = "agent-x"
        };

        // Act — first dispatch: provider factory throws
        var act = () => service.CreateDispatchedRunAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("the provider factory was set to throw");

        // Act — second dispatch: reservation must be released so this succeeds
        var secondResult = await service.CreateDispatchedRunAsync(request, CancellationToken.None);

        // Assert — second call succeeds, proving TryRemove ran in the finally block
        secondResult.Should().NotBeNull(
            "after the reservation was released by the finally block, a subsequent dispatch must succeed");
    }

    // ── ReserveRunIdAsync failure-path release ───────────────────────────────────────

    // TODO: Add tests for the IsIssueBeingProcessed guard path inside WithIssueReservationAsync.
    // Currently only the "body fails" path is covered. The finally TryRemove also runs when the
    // IsIssueBeingProcessed guard fires *before* the body is invoked (path c). If TryRemove were
    // accidentally moved inside an else branch of that guard instead of the finally, a guard-triggered
    // rejection would leak the reservation permanently, which none of the current four tests would
    // catch. Add a test that sets IsIssueBeingProcessed to true on the first call and false on the
    // second, then asserts the second call succeeds — for both CreateDispatchedRunAsync and
    // ReserveRunIdAsync. See review finding: TestQualityReviewer [WARNING] line 168.

    /// <summary>
    /// When <see cref="PipelineRunLifecycleService.RegisterDispatchedRun"/> returns false during
    /// <see cref="DispatchRunCreationService.ReserveRunIdAsync"/>, the finally block must
    /// run TryRemove so that a subsequent reservation of the same issue is not permanently blocked.
    /// </summary>
    [Fact]
    public async Task ReserveRunIdAsync_WhenRegisterDispatchedRunReturnsFalse_ReservationIsReleasedSoIssueCanBeReservedAgain()
    {
        // Arrange:
        // Call 1 — the guard inside the try block: returns false → we proceed into the body
        // Call 2 — RegisterDispatchedRun's internal IsIssueBeingProcessed: returns true → RegisterDispatchedRun returns false → body returns null
        // Call 3 — second reservation, the guard inside the try block: returns false → we proceed
        // Call 4 — second reservation's RegisterDispatchedRun's internal IsIssueBeingProcessed: returns false → RegisterDispatchedRun returns true → registers successfully
        // TODO: This queue is fragile because it depends on knowing how many times
        // PipelineRunLifecycleService.RegisterDispatchedRun internally calls IsIssueBeingProcessed.
        // If that implementation changes (guard removed, extra call added), the queue will deliver
        // values out of phase and the test may silently pass for the wrong reason or fail spuriously.
        // A more robust approach: mock IOrchestratorRunService.AddRun to throw on the first call,
        // directly forcing RegisterDispatchedRun to return false without relying on the internal
        // call count. See review finding: Correctness [WARNING] line 84.
        var isProcessingSequence = new Queue<bool>([false, true, false, false]);
        await using var service = BuildService(isProcessingSequence);

        var request = new DispatchRunRequest
        {
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            IssueIdentifier = "202",
            AgentProviderId = "agent-1",
            AgentId = "agent-x"
        };

        // Act — first reservation: RegisterDispatchedRun returns false → null result
        var firstResult = await service.ReserveRunIdAsync(request, CancellationToken.None);

        // Assert — first call returns null (body failed), but the reservation was released
        firstResult.Should().BeNull(
            "RegisterDispatchedRun returned false so the reservation was not created");

        // Act — second reservation: reservation is released so this should NOT be blocked by TryAdd
        var secondRequest = request with { AgentId = "agent-y" };
        var secondResult = await service.ReserveRunIdAsync(secondRequest, CancellationToken.None);

        // Assert — second call succeeds, proving TryRemove ran in the finally block
        secondResult.Should().NotBeNull(
            "after the reservation was released by the finally block, a subsequent reservation must succeed");
        secondResult!.RunId.Should().NotBeNullOrEmpty();
        secondResult.RepositoryName.Should().Be("owner/repo");
    }

    /// <summary>
    /// When <see cref="DispatchRunCreationService.ReserveRunIdAsync"/> body throws an exception,
    /// the finally block must run TryRemove so that a subsequent reservation of the same issue
    /// is not permanently blocked.
    /// </summary>
    [Fact]
    public async Task ReserveRunIdAsync_WhenBodyThrows_ReservationIsReleasedSoIssueCanBeReservedAgain()
    {
        // Arrange: make the factory throw on the first call only
        var callCount = 0;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated provider failure");
                return _mockRepoProvider.Object;
            });

        var mockRunService = new Mock<IOrchestratorRunService>();
        mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        var lifecycle = new PipelineRunLifecycleService(
            _mockHistoryService.Object, mockRunService.Object, _mockLogger.Object);

        await using var service = new DispatchRunCreationService(
            lifecycle,
            _mockConfigStore.Object,
            mockFactory.Object,
            _mockLogger.Object);

        var request = new DispatchRunRequest
        {
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            IssueIdentifier = "203",
            AgentProviderId = "agent-1",
            AgentId = "agent-x"
        };

        // Act — first reservation: provider factory throws
        var act = () => service.ReserveRunIdAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("the provider factory was set to throw");

        // Act — second reservation: reservation must be released so this succeeds
        var secondResult = await service.ReserveRunIdAsync(request, CancellationToken.None);

        // Assert — second call succeeds, proving TryRemove ran in the finally block
        secondResult.Should().NotBeNull(
            "after the reservation was released by the finally block, a subsequent reservation must succeed");
        secondResult!.RunId.Should().NotBeNullOrEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
    }
}
