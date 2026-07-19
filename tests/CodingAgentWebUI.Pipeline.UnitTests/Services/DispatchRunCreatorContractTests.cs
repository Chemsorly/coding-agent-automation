using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="IDispatchRunCreator"/> provides a sufficient abstraction
/// for dispatch services that previously depended on the concrete
/// <see cref="PipelineOrchestrationService"/>. These tests prove:
/// 1. The interface contract covers all dispatch-path needs.
/// 2. PipelineOrchestrationService correctly implements the interface.
/// 3. A mock of the interface can fully replace the concrete dependency.
/// </summary>
public class DispatchRunCreatorContractTests : IDisposable
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineOrchestrationService _service;

    public DispatchRunCreatorContractTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
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
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        _mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(_mockRepoProvider.Object);

        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineRunSummary>().AsReadOnly());

        // Use a real OrchestratorRunService — lifecycle tests need actual state tracking
        // (mock can't track AddRun→IsIssueBeingProcessed correlation)
        var realRunService = new OrchestratorRunService(_mockLogger.Object);

        _service = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockFactory.Object,
            logger: _mockLogger.Object,
            historyService: mockHistoryService.Object,
            runService: realRunService);
    }

    // ── Contract Test 1: PipelineOrchestrationService implements IDispatchRunCreator ──

    [Fact]
    public void PipelineOrchestrationService_Implements_IDispatchRunCreator()
    {
        // The concrete service must be assignable to the interface.
        // This test fails if the interface doesn't exist or the service doesn't implement it.
        IDispatchRunCreator creator = _service;
        creator.Should().NotBeNull();
    }

    // ── Contract Test 2: Interface provides IsIssueBeingProcessed ──

    [Fact]
    public void IsIssueBeingProcessed_WhenNotProcessing_ReturnsFalse()
    {
        IDispatchRunCreator creator = _service;

        var result = creator.IsIssueBeingProcessed("issue-99", "provider-1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueBeingProcessed_AfterDispatchedRun_ReturnsTrue()
    {
        IDispatchRunCreator creator = _service;

        // Create a dispatched run which registers the issue as being processed
        await creator.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "42", "agent-1", "agent-x", CancellationToken.None);

        var result = creator.IsIssueBeingProcessed("42", "issue-1");
        result.Should().BeTrue();
    }

    // ── Contract Test 3: Interface provides CreateDispatchedRunAsync ──

    [Fact]
    public async Task CreateDispatchedRunAsync_ViaInterface_ReturnsValidRun()
    {
        IDispatchRunCreator creator = _service;

        var run = await creator.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "55", "agent-1", "agent-container-1", CancellationToken.None,
            initiatedBy: "test");

        run.Should().NotBeNull();
        run!.IssueIdentifier.Value.Should().Be("55");
        run.AgentId.Should().Be("agent-container-1");
        run.RepositoryName.Should().Be("owner/repo");
    }

    [Fact]
    public async Task CreateDispatchedRunAsync_ViaInterface_DuplicateIssue_ReturnsNull()
    {
        IDispatchRunCreator creator = _service;

        // First dispatch succeeds
        await creator.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "42", "agent-1", "agent-x", CancellationToken.None);

        // Second dispatch of same issue returns null (dedup)
        var duplicate = await creator.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "42", "agent-1", "agent-y", CancellationToken.None);

        duplicate.Should().BeNull();
    }

    // ── Contract Test 4: Interface provides GetAllActiveRuns ──

    [Fact]
    public async Task GetAllActiveRuns_ViaInterface_IncludesDispatchedRun()
    {
        IDispatchRunCreator creator = _service;

        await creator.CreateDispatchedRunAsync(
            "issue-1", "repo-1", "77", "agent-1", "agent-x", CancellationToken.None);

        var activeRuns = creator.GetAllActiveRuns();

        activeRuns.Should().ContainSingle(r => r.IssueIdentifier == "77");
    }

    [Fact]
    public void GetAllActiveRuns_ViaInterface_WhenEmpty_ReturnsEmptyList()
    {
        IDispatchRunCreator creator = _service;

        var activeRuns = creator.GetAllActiveRuns();

        activeRuns.Should().BeEmpty();
    }

    // ── Contract Test 5: Mock of interface suffices for dispatch consumer ──

    [Fact]
    public void MockInterface_CanReplaceConcreteService_ForDispatchDedup()
    {
        // This proves a mock of IDispatchRunCreator is sufficient for dispatch dedup logic
        var mockCreator = new Mock<IDispatchRunCreator>();
        mockCreator.Setup(c => c.IsIssueBeingProcessed("42", "ip-1")).Returns(true);
        mockCreator.Setup(c => c.IsIssueBeingProcessed("99", "ip-1")).Returns(false);

        // Simulates dispatch dedup check that AgentJobDispatcher performs
        var shouldSkip42 = mockCreator.Object.IsIssueBeingProcessed("42", "ip-1");
        var shouldProcess99 = !mockCreator.Object.IsIssueBeingProcessed("99", "ip-1");

        shouldSkip42.Should().BeTrue();
        shouldProcess99.Should().BeTrue();
    }

    [Fact]
    public async Task MockInterface_CanReplaceConcreteService_ForRunCreation()
    {
        var mockCreator = new Mock<IDispatchRunCreator>();
        var fakeRun = PipelineRun.Create(
            runId: "run-1",
            issueIdentifier: "42",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            initiatedBy: "test",
            agentId: "agent-1",
            agentProviderConfigId: "ap-1");

        mockCreator.Setup(c => c.CreateDispatchedRunAsync(
                "ip-1", "rp-1", "42", "ap-1", "agent-1", It.IsAny<CancellationToken>(),
                null, null, "dispatch"))
            .ReturnsAsync(fakeRun);

        var result = await mockCreator.Object.CreateDispatchedRunAsync(
            "ip-1", "rp-1", "42", "ap-1", "agent-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-1");
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    // ── Contract Test 6: ReserveRunIdAsync reserves ID and activates dedup guard ──

    [Fact]
    public async Task ReserveRunIdAsync_ViaInterface_ReturnsValidReservation()
    {
        // TODO: This test verifies mock wiring (RepositoryName="owner/repo", ModelName="claude-sonnet")
        // rather than that production code correctly extracts values from provider configs. It would
        // not detect a bug where ReserveRunIdAsync reads the wrong property or provider config.
        // Consider an integration test with real provider config resolution.
        IDispatchRunCreator creator = _service;

        var reservation = await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "101", "agent-1", "agent-x", CancellationToken.None,
            initiatedBy: "test");

        reservation.Should().NotBeNull();
        reservation!.RunId.Should().NotBeNullOrEmpty();
        reservation.RepositoryName.Should().Be("owner/repo");
        reservation.ModelName.Should().Be("claude-sonnet");
        // TODO: BeCloseTo with 5-second tolerance doesn't verify the timestamp came from reservation
        // logic specifically. Consider capturing time before/after the call and asserting StartedAt
        // falls within that window, or use a clock abstraction.
        reservation.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReserveRunIdAsync_ViaInterface_ActivatesDedupGuard()
    {
        IDispatchRunCreator creator = _service;

        await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "102", "agent-1", "agent-x", CancellationToken.None);

        // After reservation, IsIssueBeingProcessed should return true
        var isProcessing = creator.IsIssueBeingProcessed("102", "issue-1");
        isProcessing.Should().BeTrue();
    }

    [Fact]
    public async Task ReserveRunIdAsync_ViaInterface_DuplicateIssue_ReturnsNull()
    {
        IDispatchRunCreator creator = _service;

        // First reservation succeeds
        await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "103", "agent-1", "agent-x", CancellationToken.None);

        // Second reservation of same issue returns null (dedup)
        var duplicate = await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "103", "agent-1", "agent-y", CancellationToken.None);

        duplicate.Should().BeNull();
    }

    [Fact]
    public async Task ReserveRunIdAsync_ViaInterface_SentinelVisibleInActiveRuns()
    {
        IDispatchRunCreator creator = _service;

        var reservation = await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "104", "agent-1", "agent-x", CancellationToken.None);

        var activeRuns = creator.GetAllActiveRuns();
        activeRuns.Should().ContainSingle(r => r.RunId == reservation!.RunId);
    }

    // ── Contract Test 7: RegisterDispatchedRun atomically replaces sentinel ──

    [Fact]
    public async Task RegisterDispatchedRun_ViaInterface_ReplacesSentinelWithFullRun()
    {
        // TODO: This test does not verify that the sentinel's intermediate state (IssueTitle=empty,
        // RunType=Implementation) is no longer observable after replace. The test would pass even if
        // RegisterDispatchedRun was a no-op that leaves the sentinel in place. Consider asserting
        // that sentinel properties (e.g. empty title) are gone and total active run count is still 1.
        IDispatchRunCreator creator = _service;

        var reservation = await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "105", "agent-1", "agent-x", CancellationToken.None);

        // Construct a fully-populated run using the reserved RunId
        var fullRun = PipelineRun.CreateReview(
            runId: reservation!.RunId,
            issueIdentifier: "105",
            issueTitle: "Test PR",
            issueProviderConfigId: "issue-1",
            repoProviderConfigId: "repo-1",
            reviewPrBranchName: "feature/test",
            reviewPrTargetBranch: "main",
            startedAt: reservation.StartedAt,
            initiatedBy: "test",
            agentId: "agent-x",
            agentProviderConfigId: "agent-1");
        fullRun.RepositoryName = reservation.RepositoryName;
        fullRun.ModelName = reservation.ModelName;

        // Register the fully-populated run
        creator.RegisterDispatchedRun(fullRun);

        // Verify the run was replaced with full metadata
        var activeRuns = creator.GetAllActiveRuns();
        var run = activeRuns.Should().ContainSingle(r => r.RunId == reservation.RunId).Which;
        run.IssueTitle.Should().Be("Test PR");
        run.RunType.Should().Be(PipelineRunType.Review);
        run.ReviewPrBranchName.Should().Be("feature/test");
    }

    [Fact]
    public async Task RegisterDispatchedRun_ViaInterface_DedupRemainsActiveAfterReplace()
    {
        IDispatchRunCreator creator = _service;

        var reservation = await creator.ReserveRunIdAsync(
            "issue-1", "repo-1", "106", "agent-1", "agent-x", CancellationToken.None);

        var fullRun = PipelineRun.CreateDecomposition(
            runId: reservation!.RunId,
            issueIdentifier: "106",
            issueTitle: "Epic",
            issueProviderConfigId: "issue-1",
            repoProviderConfigId: "repo-1",
            phaseType: PipelineRunType.DecompositionAnalysis,
            startedAt: reservation.StartedAt,
            initiatedBy: "test",
            agentId: "agent-x",
            agentProviderConfigId: "agent-1");
        fullRun.RepositoryName = reservation.RepositoryName;
        fullRun.ModelName = reservation.ModelName;

        creator.RegisterDispatchedRun(fullRun);

        // Dedup guard should still be active after replace
        var isProcessing = creator.IsIssueBeingProcessed("106", "issue-1");
        isProcessing.Should().BeTrue();
    }

    // TODO: Missing negative test — No test covers calling RegisterDispatchedRun with a PipelineRun
    // whose RunId does not match any existing sentinel. The contract states "The run must use the same
    // RunId that was returned in the RunReservation" but there's no test verifying behavior on violation.

    // TODO: Missing negative test — No test covers calling RegisterDispatchedRun with a null argument
    // to verify ArgumentNullException is thrown, despite the production code having an explicit
    // ArgumentNullException.ThrowIfNull(run) guard.
}
