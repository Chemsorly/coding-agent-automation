using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for DispatchService handling of consolidation WorkItems (TaskType=Consolidation).
/// Validates: Issue #1086 — K8s mode DispatchService creates K8s Jobs for consolidation items.
/// </summary>
[Trait("Feature", "K8sConsolidationDispatch")]
public class DispatchServiceConsolidationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly Mock<ITokenVendingService> _mockTokenVending;
    private readonly Mock<IConsolidationRunStore> _mockRunStore;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<IProviderConfigStore> _mockProviderConfigStore;
    private readonly Mock<IAgentProfileStore> _mockAgentProfileStore;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IPipelineConfigStore> _mockPipelineConfigStore;
    private readonly LeaderElectionService _leaderElection;

    private const string TestTemplateId = "template-001";
    private const string TestRepoProviderId = "repo-provider-001";
    private const string TestAgentProviderId = "agent-provider-001";

    public DispatchServiceConsolidationTests()
    {
        var dbName = $"DispatchConsolidation-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _mockTokenVending = new Mock<ITokenVendingService>();
        _mockRunStore = new Mock<IConsolidationRunStore>();
        _mockConsolidationService = new Mock<IConsolidationService>();
        _mockProviderConfigStore = new Mock<IProviderConfigStore>();
        _mockAgentProfileStore = new Mock<IAgentProfileStore>();
        _mockProjectStore = new Mock<IProjectStore>();
        _mockPipelineConfigStore = new Mock<IPipelineConfigStore>();
        _leaderElection = CreateAlwaysLeaderElection();

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Happy Path ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_CreatesK8sJobAndTransitionsToDispatched()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Queued
            });

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Dispatched
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        item.DispatchedAt.Should().NotBeNull();
        item.K8sJobName.Should().StartWith("caa-");

        // Assert: K8s Job was created
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), "default", It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: ConsolidationRun transitioned to Running (via IConsolidationService for cache invalidation)
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(runId, ConsolidationRunStatus.Running, null, It.IsAny<CancellationToken>(), 0),
            Times.Once);

        // Assert: Payload was updated with ProviderConfigs
        var updatedPayload = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload!, PipelineJsonOptions.Default);
        updatedPayload!.ProviderConfigs.Should().NotBeNull();
        // TODO: Assertion too weak — test setup provides exactly 2 configs (agent + repo). Should assert
        // .Be(2) and verify the specific config IDs to catch regressions in provider resolution logic.
        updatedPayload.ProviderConfigs!.Count.Should().BeGreaterThan(0);
        updatedPayload.RepoProviderConfigId.Should().Be(TestRepoProviderId);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_DoesNotAttemptLabelSwap()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationWorkItem(workItemId, workItemId.ToString(), "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockLabelService = new Mock<ILabelService>();
        var service = CreateService(labelService: mockLabelService.Object);

        await InvokePollAndDispatch(service);

        // Label swapper should never be called for consolidation items
        mockLabelService.Verify(
            s => s.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Token Vending ───────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_VendsTokensAtDispatchTime()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationWorkItem(workItemId, workItemId.ToString(), "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Token vending should have been called
        // TODO: Verification uses It.IsAny<IReadOnlyList<ProviderConfig>>() — doesn't validate that the
        // correct resolved configs were passed. A bug in BuildConsolidationProviderConfigsAsync that returns
        // wrong/empty configs would not be caught. Verify specific config contents.
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), TestRepoProviderId, It.IsAny<CancellationToken>(), false),
            Times.Once);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_TokenVendingFailure_FailsWorkItem()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationWorkItem(workItemId, workItemId.ToString(), "kiro,dotnet");

        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("Token vending failed"));

        var service = CreateService();

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("Provider config resolution failed");
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    // ── ConsolidationRunStatus Transitions ──────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_RunNotFound_DispatchStillSucceeds()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationWorkItem(workItemId, workItemId.ToString(), "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRunStore
            .Setup(s => s.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Dispatch still succeeds
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);

        // SaveRunAsync never called (no run to transition)
        _mockRunStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_RunNotQueued_DoesNotTransition()
    {
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Run is already Running (not Queued)
        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Dispatch succeeds but SaveRunAsync NOT called (status guard)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);

        _mockRunStore.Verify(s => s.SaveRunAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cancellation Race Guard ─────────────────────────────────────────

    // TODO: Add boundary test for empty/null IssueIdentifier — should bypass the guard and proceed with normal dispatch.
    // TODO: Add test for null _consolidationRunStore path — guard is skipped entirely when store is not registered.

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_RunCancelled_CancelsWorkItemEarly()
    {
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Cancelled
            });

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Cancelled with CompletedAt set
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();

        // Assert: K8s Job was NOT created (early return before any K8s work)
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: No cascade to ConsolidationService (run is already terminal)
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_RunFailed_CancelsWorkItemEarly()
    {
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Failed
            });

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Cancelled with CompletedAt set
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();

        // Assert: K8s Job was NOT created (early return before any K8s work)
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: No cascade to ConsolidationService (run is already terminal)
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.Never);
    }

    // ── Error Handling ──────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_TransitionsViaConsolidationService_NotDirectStore()
    {
        // Regression test: TransitionConsolidationRunToRunningAsync must use
        // IConsolidationService.UpdateRunAsync (which invalidates GetRunHistoryAsync cache)
        // instead of IConsolidationRunStore.SaveRunAsync (which leaves cache stale).
        // Without this, the Active Runs section shows "(0)" even when an agent is busy
        // with a consolidation job.
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Queued
            });

        var service = CreateService();

        await InvokePollAndDispatch(service);

        // Assert: status transition goes through IConsolidationService.UpdateRunAsync
        // (which invalidates the in-memory cache), NOT directly through IConsolidationRunStore.SaveRunAsync
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(runId, ConsolidationRunStatus.Running, null, It.IsAny<CancellationToken>(), 0),
            Times.Once,
            "TransitionConsolidationRunToRunningAsync must use IConsolidationService.UpdateRunAsync to invalidate the history cache");

        // IConsolidationRunStore.SaveRunAsync should NOT be called directly for the transition
        _mockRunStore.Verify(
            s => s.SaveRunAsync(It.Is<ConsolidationRun>(r => r.Status == ConsolidationRunStatus.Running), It.IsAny<CancellationToken>()),
            Times.Never,
            "Direct store write bypasses cache invalidation — must use ConsolidationService instead");
    }

    // TODO: Missing test for K8s 409 Conflict handling — production code handles HttpOperationException
    // with StatusCode.Conflict by treating it as success (idempotent dispatch). No test covers this path.
    // If this handler were removed, consolidation dispatches that race against themselves would fail.

    // TODO: Missing test for RefactoringDetection provider config resolution —
    // BuildConsolidationProviderConfigsAsync has special logic to include an issue provider config when
    // consolidationRunType == ConsolidationRunType.RefactoringDetection. All dispatch tests use
    // BrainConsolidation, so this conditional branch is entirely untested.

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_K8sJobCreationFails_FailsWorkItem()
    {
        var workItemId = Guid.NewGuid();
        await InsertConsolidationWorkItem(workItemId, workItemId.ToString(), "kiro,dotnet");

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s API unavailable"));

        var service = CreateService();

        await InvokePollAndDispatch(service);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("K8s Job creation failed");
        // TODO: Add assertion that item.ClaimedPvcName is null after K8s failure in consolidation path.
        // Currently this test does not claim a PVC (no PVC setup), so the SaveChangesAsync fix at line ~697
        // is not regression-protected. Add a variant with PVC claiming to validate the fix.

        // Verify cascade fires for K8s creation failure path too (not just template resolution)
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(
                workItemId.ToString(),
                ConsolidationRunStatus.Failed,
                It.Is<string>(summary => summary.Contains("K8s Job creation failed")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_InvalidPayload_FailsWorkItem()
    {
        var workItemId = Guid.NewGuid();

        // Insert with empty/invalid payload
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = workItemId.ToString(),
                IssueProviderConfigId = "consolidation",
                Status = WorkItemStatus.Pending,
                AgentSelector = "kiro,dotnet",
                TaskType = WorkItemTaskType.Consolidation,
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 1800,
                Payload = "not-valid-json"
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();

        await InvokePollAndDispatch(service);

        await using var dbCheck = await _dbFactory.CreateDbContextAsync();
        var item = await dbCheck.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("payload");

        // Verify cascade fires for invalid payload path (IssueIdentifier = workItemId.ToString())
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(
                workItemId.ToString(),
                ConsolidationRunStatus.Failed,
                It.Is<string>(summary => summary.Contains("payload")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Label Resolution: AgentSelector must match template key ────────

    /// <summary>
    /// Regression test: When ConsolidationDispatcher produces an AgentSelector that is a SUBSET
    /// of the template's label set (e.g., "dotnet,dotnet10" vs template "kiro,dotnet,dotnet10"),
    /// DispatchService must still resolve the template correctly.
    ///
    /// Root cause: ConsolidationDispatcher uses raw requiredLabels as AgentSelector instead of
    /// resolving the profile's MatchLabels (which IS the template key). Normal pipeline dispatch
    /// uses profile.MatchLabels and works fine.
    ///
    /// This test simulates the production scenario:
    /// - Template: labels="kiro,dotnet,dotnet10" (3-label key)
    /// - Work item AgentSelector: "dotnet,dotnet10" (2-label subset, missing "kiro")
    /// - Expected: dispatch succeeds (after fix resolves profile → full label set)
    /// - Bug behavior: "No job template for selector: dotnet,dotnet10"
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_SubsetSelector_ResolvesTemplateViaProfile()
    {
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();

        // Insert with subset selector — what ConsolidationDispatcher actually produces
        // when DefaultRequiredAgentLabels = "dotnet,dotnet10" (missing "kiro")
        await InsertConsolidationWorkItem(workItemId, runId, "dotnet,dotnet10");

        // Profile has the full label set that matches the template key
        _mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-1",
                    DisplayName = "Kiro Dotnet10",
                    Enabled = true,
                    MatchLabels = ["dotnet", "dotnet10", "kiro"],
                    AgentProviderConfigId = TestAgentProviderId,
                    Priority = 1
                }
            });

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Queued
            });

        // Template keyed by full 3-label set (realistic production config)
        var service = CreateServiceWithThreeLabelTemplate();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: With the bug, this FAILS — Resolve("dotnet,dotnet10") finds no template keyed as "dotnet,dotnet10,kiro"
        // With the fix, DispatchService resolves the profile from AgentSelector labels and uses profile.MatchLabels
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "work item should be dispatched — template resolution must succeed even when AgentSelector is a subset of template labels");
        item.ErrorMessage.Should().BeNull();
        item.K8sJobName.Should().StartWith("caa-");
    }

    // ── FailWorkItem cascading to ConsolidationRun ──────────────────────

    /// <summary>
    /// When a consolidation WorkItem fails (e.g., no template match, K8s Job creation failure),
    /// the associated ConsolidationRun must also transition to Failed. Without this, the run
    /// stays in Queued/Running status permanently and the UI shows a stuck entry.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_FailsWorkItem_CascadesToConsolidationRunFailed()
    {
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();

        // Insert with a selector that won't match ANY template (no profile either)
        await InsertConsolidationWorkItem(workItemId, runId, "nonexistent-label");

        // No profiles match this selector
        _mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        // ConsolidationRun exists in Queued state
        var consolidationRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Queued
        };
        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(consolidationRun);

        var service = CreateServiceWithThreeLabelTemplate();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem should be Failed (no template match)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("No job template for selector");

        // Assert: ConsolidationRun should ALSO be transitioned to Failed via IConsolidationService
        _mockConsolidationService.Verify(
            s => s.UpdateRunAsync(
                runId,
                ConsolidationRunStatus.Failed,
                It.Is<string>(summary => summary.Contains("No job template for selector")),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "ConsolidationRun must be transitioned to Failed via IConsolidationService.UpdateRunAsync");
    }

    // ── Integration: Full Lifecycle ─────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_ConsolidationInsert_Dispatch_Complete()
    {
        // Step 1: Insert via KubernetesWorkDistributor
        var distributor = new KubernetesWorkDistributor(
            _dbFactory, _transitionService,
            NullLogger<KubernetesWorkDistributor>.Instance);

        var runId = Guid.NewGuid().ToString();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 0,
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationTemplateId = TestTemplateId,
            ConsolidationWorkspacePath = "/tmp/consolidation/test",
            RunId = runId
        };

        var result = await distributor.DistributeAsync(request, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.Queued.Should().BeTrue();

        // Step 2: Dispatch via DispatchService
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRunStore
            .Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = runId,
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Queued
            });

        var service = CreateService();
        await InvokePollAndDispatch(service);

        // Verify dispatched
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FirstOrDefaultAsync(w => w.IssueIdentifier == runId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
            item.K8sJobName.Should().StartWith("caa-");

            // Verify payload was enriched
            var payload = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload!, PipelineJsonOptions.Default);
            payload!.ProviderConfigs.Should().NotBeNull();
            payload.RepoProviderConfigId.Should().Be(TestRepoProviderId);
            payload.PipelineConfiguration.Should().NotBeNull();
        }

        // Step 3: Simulate agent completion
        var workItemId = Guid.Parse(result.WorkItemId!);
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Running, _ => { }, CancellationToken.None);
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Succeeded,
            w => w.CompletedAt = DateTimeOffset.UtcNow, CancellationToken.None);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
        }
    }

    // ── Race condition: WorkItem no longer Pending after Job creation (#1488) ──

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_TransitionedAfterJobCreation_PvcReleasedAndJobDeleted()
    {
        // Arrange: Consolidation item with 1 PVC, plus a second consolidation item to prove PVC release
        var racedId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var racedRunId = racedId.ToString();
        var secondRunId = secondId.ToString();
        await InsertConsolidationWorkItem(racedId, racedRunId, "kiro,dotnet");
        await InsertConsolidationWorkItem(secondId, secondRunId, "kiro,dotnet");

        // Ensure second item is newer (ordered by CreatedAt)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var first = await db.WorkItems.FindAsync(racedId);
            first!.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var second = await db.WorkItems.FindAsync(secondId);
            second!.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var createCallCount = 0;
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // TODO: async lambda in Moq .Callback creates an async void delegate (fire-and-forget).
            // Works reliably only because the InMemory/SQLite provider completes synchronously.
            // If the test DB is ever changed to a truly async provider, replace with .Returns(async () => { ... })
            // or TaskCompletionSource-based synchronization to make ordering guarantees explicit.
            .Callback<V1Job, string, CancellationToken>(async (job, ns, ct) =>
            {
                createCallCount++;
                if (createCallCount == 1)
                {
                    // Simulate race: another process transitions the first consolidation item
                    await using var raceDb = await _dbFactory.CreateDbContextAsync();
                    var item = await raceDb.WorkItems.FindAsync(racedId);
                    item!.Status = WorkItemStatus.Dispatched;
                    item.DispatchedAt = DateTimeOffset.UtcNow;
                    await raceDb.SaveChangesAsync();
                }
            })
            .Returns(Task.CompletedTask);

        _mockKubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use only 1 PVC to prove the release
        var service = CreateServiceWithPvcPool(new[] { "pvc-consolidation-race" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: DeleteJobAsync was called for the raced consolidation item's orphaned Job
        var expectedJobName = $"caa-{racedId.ToString("N")[..8]}";
        _mockKubeClient.Verify(
            k => k.DeleteJobAsync(expectedJobName, "default", It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: Second consolidation item was dispatched (PVC was released back to pool)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var secondItem = await db.WorkItems.FindAsync(secondId);
            secondItem!.Status.Should().Be(WorkItemStatus.Dispatched,
                "Second consolidation item should dispatch because PVC was released after race condition on first");
        }
    }

    // ── Project Secrets ─────────────────────────────────────────────────

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_WithProjectSecrets_CreatesK8sSecret()
    {
        // Arrange: consolidation work item with template that belongs to a project
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        var projectId = Guid.NewGuid();

        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        // Seed ProjectEntity with secrets in Settings JSON
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectId,
                Name = "Test Project",
                Enabled = true,
                TemplateIds = [TestTemplateId],
                Settings = JsonSerializer.Serialize(new { Secrets = new Dictionary<string, string> { ["MY_SECRET"] = "secret-value", ["API_KEY"] = "key-123" } })
            });
            await db.SaveChangesAsync();
        }

        // Mock _projectStore.LoadProjectsAsync to return a project owning the template
        _mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = projectId.ToString(), Name = "Test Project", Enabled = true, TemplateIds = [TestTemplateId] }
            });

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockKubeClient
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockKubeClient
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "job-uid-123" } });

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Dispatched
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
        }

        // Assert: K8s Secret was created with the project secrets
        _mockKubeClient.Verify(
            k => k.CreateSecretAsync(
                It.Is<V1Secret>(s =>
                    s.StringData != null &&
                    s.StringData.ContainsKey("MY_SECRET") &&
                    s.StringData["MY_SECRET"] == "secret-value" &&
                    s.StringData.ContainsKey("API_KEY") &&
                    s.StringData["API_KEY"] == "key-123"),
                "default",
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: Job spec includes the project-secrets volume mount
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(
                It.Is<V1Job>(j =>
                    j.Spec.Template.Spec.Volumes.Any(v => v.Name == "project-secrets") &&
                    j.Spec.Template.Spec.Containers[0].VolumeMounts.Any(vm => vm.Name == "project-secrets")),
                "default",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_WithProjectId_NoSecrets_SkipsSecretCreation()
    {
        // Arrange: consolidation work item with a project that has no secrets
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        var projectId = Guid.NewGuid();

        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        // Seed ProjectEntity WITHOUT secrets (empty Settings)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectId,
                Name = "No-Secrets Project",
                Enabled = true,
                TemplateIds = [TestTemplateId],
                Settings = JsonSerializer.Serialize(new { MaxRetries = 3 })
            });
            await db.SaveChangesAsync();
        }

        // Mock _projectStore.LoadProjectsAsync to return the project
        _mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = projectId.ToString(), Name = "No-Secrets Project", Enabled = true, TemplateIds = [TestTemplateId] }
            });

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem transitioned to Dispatched
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched);
        }

        // Assert: CreateSecretAsync was NOT called
        _mockKubeClient.Verify(
            k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAndDispatch_ConsolidationItem_CreateSecretFails_JobStillDispatched()
    {
        // Arrange: consolidation work item with project secrets, but secret creation fails
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString();
        var projectId = Guid.NewGuid();

        await InsertConsolidationWorkItem(workItemId, runId, "kiro,dotnet");

        // Seed ProjectEntity with secrets
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectId,
                Name = "Secrets Project",
                Enabled = true,
                TemplateIds = [TestTemplateId],
                Settings = JsonSerializer.Serialize(new { Secrets = new Dictionary<string, string> { ["TOKEN"] = "abc" } })
            });
            await db.SaveChangesAsync();
        }

        _mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = projectId.ToString(), Name = "Secrets Project", Enabled = true, TemplateIds = [TestTemplateId] }
            });

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockKubeClient
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "job-uid-456" } });
        _mockKubeClient
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated secret creation failure"));

        var service = CreateService();

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem still transitions to Dispatched (secret failure is non-fatal)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Dispatched,
                "Secret creation failure should be non-fatal — job still dispatches");
        }

        // Assert: Job was still created
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), "default", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        // Agent profile store: return a profile matching "kiro,dotnet"
        _mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-1",
                    DisplayName = "Kiro Dotnet",
                    Enabled = true,
                    MatchLabels = ["dotnet", "kiro"],
                    AgentProviderConfigId = TestAgentProviderId,
                    Priority = 1
                }
            });

        // Provider config store
        var agentConfig = new ProviderConfig
        {
            Id = TestAgentProviderId,
            DisplayName = "Agent",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli"
        };
        var repoConfig = new ProviderConfig
        {
            Id = TestRepoProviderId,
            DisplayName = "Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub"
        };

        _mockProviderConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig });
        _mockProviderConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        // Project store: return a template
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new()
                {
                    Id = TestTemplateId,
                    Name = "Test Template",
                    IssueProviderId = "issue-provider-001",
                    RepoProviderId = TestRepoProviderId
                }
            });

        // Pipeline config store
        _mockPipelineConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Token vending: pass-through (return same configs)
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _) =>
                Task.FromResult(configs));

        // Consolidation run store: default no-op
        _mockRunStore
            .Setup(s => s.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);
    }

    private DispatchService CreateService(ILabelService? labelService = null)
    {
        return CreateServiceWithPvcPool(new[] { "pvc-test-1", "pvc-test-2" }, labelService);
    }

    private DispatchService CreateServiceWithPvcPool(string[] pvcPool, ILabelService? labelService = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key"
        };
        for (var i = 0; i < pvcPool.Length; i++)
            configData[$"WorkDistribution:CredentialPools:Kiro:{i}"] = pvcPool[i];

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var templateProvider = BuildTemplateProvider();

        return new DispatchService(
            _dbFactory, _leaderElection, _mockKubeClient.Object, _transitionService, config, templateProvider,
            labelService,
            _mockTokenVending.Object,
            _mockRunStore.Object,
            _mockConsolidationService.Object,
            _mockProviderConfigStore.Object,
            _mockAgentProfileStore.Object,
            _mockProjectStore.Object,
            _mockPipelineConfigStore.Object,
            new ConsolidationJobPreparationService(
                _mockProviderConfigStore.Object,
                _mockProjectStore.Object,
                _mockTokenVending.Object,
                Serilog.Log.Logger,
                _mockAgentProfileStore.Object));
    }

    private static JobTemplateProvider BuildTemplateProvider()
    {
        var templates = new List<JobTemplate>
        {
            new() { Labels = "dotnet,kiro", Image = "ghcr.io/agent:latest", ProviderType = "kiro" }
        };
        var json = JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    /// <summary>
    /// Builds a template provider with a realistic 3-label template (kiro,dotnet,dotnet10).
    /// Used to reproduce the subset selector bug where AgentSelector = "dotnet,dotnet10"
    /// fails to match template key "dotnet,dotnet10,kiro".
    /// </summary>
    private static JobTemplateProvider BuildThreeLabelTemplateProvider()
    {
        var templates = new List<JobTemplate>
        {
            new() { Labels = "kiro,dotnet,dotnet10", Image = "ghcr.io/agent:kiro-dotnet10", ProviderType = "kiro" }
        };
        var json = JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    private DispatchService CreateServiceWithThreeLabelTemplate()
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
            ["WorkDistribution:CredentialPools:Kiro:0"] = "pvc-test-1",
            ["WorkDistribution:CredentialPools:Kiro:1"] = "pvc-test-2"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var templateProvider = BuildThreeLabelTemplateProvider();

        return new DispatchService(
            _dbFactory, _leaderElection, _mockKubeClient.Object, _transitionService, config, templateProvider,
            null,
            _mockTokenVending.Object,
            _mockRunStore.Object,
            _mockConsolidationService.Object,
            _mockProviderConfigStore.Object,
            _mockAgentProfileStore.Object,
            _mockProjectStore.Object,
            _mockPipelineConfigStore.Object,
            new ConsolidationJobPreparationService(
                _mockProviderConfigStore.Object,
                _mockProjectStore.Object,
                _mockTokenVending.Object,
                Serilog.Log.Logger,
                _mockAgentProfileStore.Object));
    }

    private async Task InsertConsolidationWorkItem(Guid id, string runId, string selector)
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = selector,
            TimeoutSeconds = 0,
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationTemplateId = TestTemplateId,
            ConsolidationWorkspacePath = "/tmp/consolidation/test",
            RunId = runId
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = WorkItemStatus.Pending,
            AgentSelector = selector,
            TaskType = WorkItemTaskType.Consolidation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 1800,
            Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    private async Task InvokePollAndDispatch(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField?.SetValue(les, new CancellationTokenSource());

        return les;
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
