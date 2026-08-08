using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="PendingWorkItemDrainService"/> label swap behavior (#997).
/// Verifies that the drain service swaps the issue label to agent:in-progress
/// only after successful SignalR delivery to an agent.
/// </summary>
public sealed class PendingWorkItemDrainServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public PendingWorkItemDrainServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DrainTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DrainPendingItems_SuccessfulDispatch_SwapsLabelToInProgress()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#42",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use a completion signal to know when the label swap has been invoked
        var labelSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => labelSwapCalled.TrySetResult());

        var service = CreateService();

        // Act: trigger a single drain cycle and wait for label swap (with timeout)
        service.Signal();
        var task = service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(labelSwapCalled.Task, Task.Delay(10_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await service.StopAsync(cts.Token); } catch (OperationCanceledException) { }

        // Assert: label swap was actually called (not a timeout)
        completed.Should().BeSameAs(labelSwapCalled.Task, "label swap should have been called within timeout");
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DrainPendingItems_SignalRDeliveryFails_DoesNotSwapLabel()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#99",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent available but SignalR delivery fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label was NOT swapped
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.InProgress, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert: WorkItem was reverted to Pending (not stuck in Dispatched)
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.DispatchedAt.Should().BeNull();
        item.AssignedAgentId.Should().BeNull();
    }

    [Fact]
    public async Task DrainPendingItems_NoIdleAgent_DoesNotSwapLabel()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#77",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#77",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: no idle agent
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns((AgentResolveResult?)null);

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label was NOT swapped
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.InProgress, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TODO: Add [Theory] with [InlineData(PipelineRunType.Decomposition)] and [InlineData(PipelineRunType.DecompositionAnalysis)]
    //       to verify non-Review run types still use IssueProviderConfigId + LabelTargetKind.Issue (#1089).
    [Fact]
    public async Task DrainPendingItems_ReviewWorkItem_SwapsLabelWithPullRequestKindAndRepoProvider()
    {
        // Arrange: insert a Pending Review WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Review,
            RunType = PipelineRunType.Review,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Review,
                IssueIdentifier = "org/repo#42",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use a completion signal to know when the label swap has been invoked
        var labelSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => labelSwapCalled.TrySetResult());

        var service = CreateService();

        // Act: trigger a single drain cycle and wait for label swap (with timeout)
        service.Signal();
        var task = service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(labelSwapCalled.Task, Task.Delay(10_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await service.StopAsync(cts.Token); } catch (OperationCanceledException) { }

        // Assert: label swap was called with PullRequest target kind and repo provider (NOT issue provider)
        completed.Should().BeSameAs(labelSwapCalled.Task, "label swap should have been called within timeout");
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()),
            Times.Once);
        // Ensure issue provider was NOT used
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DrainPendingItems_LabelSwapTransientFailure_RetriesAndSucceeds()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#200",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#200",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup: label swap fails on first call, succeeds on second
        var callCount = 0;
        var labelSwapDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#200", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromException(new HttpRequestException("rate limited"));
                labelSwapDone.TrySetResult();
                return Task.CompletedTask;
            });

        var service = CreateService();

        // Act: trigger a single drain cycle and wait for label swap to complete
        service.Signal();
        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(labelSwapDone.Task, Task.Delay(10_000));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await service.StopAsync(cts.Token); } catch (OperationCanceledException) { }

        // Assert: label swap was retried and succeeded on the second attempt
        completed.Should().BeSameAs(labelSwapDone.Task, "label swap should have succeeded on retry within timeout");
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#200", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Assert: NeedsLabelReconciliation is NOT set (swap eventually succeeded)
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.NeedsLabelReconciliation.Should().BeFalse();
        item.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task DrainPendingItems_LabelSwapExhaustsRetries_FlagsForReconciliation()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#201",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#201",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup: label swap always fails
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#201", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: label swap was called exactly 3 times (1 initial + 2 retries)
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#201", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Exactly(3));

        // Assert: NeedsLabelReconciliation flag is set
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.NeedsLabelReconciliation.Should().BeTrue("flag should be set after retry exhaustion");

        // Assert: dispatch itself succeeded (status is Dispatched, not reverted)
        item.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task DrainPendingItems_LabelSwapCancellation_DoesNotRetryOrFlag()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#202",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#202",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup: label swap throws OperationCanceledException (shutdown)
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#202", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService();

        // Act: trigger drain — OCE should propagate up, no retry, no flag
        await InvokeDrainAsync(service);

        // Assert: label swap was only called once (no retry on OCE)
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#202", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: NeedsLabelReconciliation flag is NOT set
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.NeedsLabelReconciliation.Should().BeFalse("OCE should not trigger reconciliation flag");
    }

    [Fact]
    public async Task DrainPendingItems_ShutdownDuringLabelSwapBackoff_FlagsForReconciliation()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#300",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#300",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: idle agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup: label swap fails with transient error AND cancels the CTS to simulate
        // shutdown arriving during the subsequent Task.Delay backoff (#1681)
        using var cts = new CancellationTokenSource();
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#300", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService();

        // Act: invoke DrainPendingItemsAsync directly with a real cancellable token.
        // Cannot use InvokeDrainAsync because it passes CancellationToken.None — the fix's
        // finally block checks ct.IsCancellationRequested which requires a real token.
        var method = typeof(PendingWorkItemDrainService).GetMethod("DrainPendingItemsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DrainPendingItemsAsync not found");
        var task = (Task)method.Invoke(service, [cts.Token])!;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected: OCE propagates from Task.Delay when ct is cancelled
        }

        // Assert: label swap was called exactly 1 time (backoff was interrupted by shutdown)
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#300", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: NeedsLabelReconciliation IS set (the finally block fired)
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.NeedsLabelReconciliation.Should().BeTrue("shutdown during backoff should flag for reconciliation");

        // Assert: dispatch itself succeeded (status is Dispatched, not reverted)
        item.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task DrainPendingItems_SignalRDeliveryFails_RevertsWorkItemToPending()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#50",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#50",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent available but SignalR delivery fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var service = CreateService();

        // Act: trigger a single drain cycle
        await InvokeDrainAsync(service);

        // Assert: WorkItem reverted to Pending with cleared dispatch fields
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must revert to Pending on SignalR delivery failure so it's eligible for re-drain");
        item.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on revert");
        item.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on revert");
        item.RetryCount.Should().Be(1, "RetryCount must be incremented on each failed delivery attempt");
    }

    [Fact]
    public async Task DrainPendingItems_RepeatedSignalRFailures_IncrementsRetryCount()
    {
        // Arrange: insert a Pending WorkItem with RetryCount = 0
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#60",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#60",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                RetryCount = 0
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent always available, SignalR delivery always fails
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Act: first drain cycle
        var service1 = CreateService();
        await InvokeDrainAsync(service1);

        // Assert: RetryCount is 1 after first failure
        await using (var db1 = await _dbFactory.CreateDbContextAsync())
        {
            var item1 = await db1.WorkItems.FindAsync(workItemId);
            item1!.RetryCount.Should().Be(1);
            item1.Status.Should().Be(WorkItemStatus.Pending);
        }

        // Act: second drain cycle (new service instance — same DB and mocks)
        var service2 = CreateService();
        await InvokeDrainAsync(service2);

        // Assert: RetryCount is 2 after second failure
        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var item2 = await db2.WorkItems.FindAsync(workItemId);
        item2.Should().NotBeNull();
        item2!.RetryCount.Should().Be(2,
            "RetryCount must increment on each failed delivery attempt");
        item2.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must remain Pending after repeated failures (not Failed)");
    }

    [Fact]
    public async Task DrainPendingItems_ShutdownDuringDispatch_RevertsWorkItemToPending()
    {
        // Reproduction: When stoppingToken is cancelled during dispatch (graceful shutdown),
        // the catch block's revert TransitionAsync also used the same cancelled token,
        // causing the revert to throw OperationCanceledException and leaving the work item
        // stuck in Dispatched status. Fix: use CancellationToken.None for the revert call.
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#1259",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#1259",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Setup: agent available, but AssignJobAsync simulates shutdown by cancelling the CTS then throwing
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        using var cts = new CancellationTokenSource();
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns((string _, JobAssignmentMessage _, CancellationToken _) =>
            {
                cts.Cancel(); // Simulate graceful shutdown — token is now cancelled
                throw new OperationCanceledException(cts.Token);
            });

        var cancellingTransitionService = new WorkItemTransitionService(
            new CancellationAwareDbContextFactory(_dbOptions),
            NullLogger<WorkItemTransitionService>.Instance);

        var service = new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                _dbFactory,
                _mockResolver.Object,
                _mockAgentComm.Object,
                cancellingTransitionService,
                _mockPendingWork.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            MakeLabelSwapService(),
            new DispatchRevertHandler(_dbFactory, _mockResolver.Object, _runService, cancellingTransitionService, NullLogger<DispatchRevertHandler>.Instance),
            _mockProjectStore.Object);

        // Act: start with the CTS that will be cancelled inside the mock
        service.Signal();
        await service.StartAsync(cts.Token);
        // Wait for ExecuteAsync to complete (cts is cancelled inside AssignJobAsync mock,
        // which causes the drain to revert and the loop to exit)
        var executeTask = service.ExecuteTask;
        if (executeTask is not null)
        {
            var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(executeTask, "ExecuteAsync should complete after CTS cancellation");
        }
        await service.StopAsync(CancellationToken.None);

        // Assert: WorkItem must be reverted to Pending despite the cancelled stoppingToken
        await using var checkDb = await _dbFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must revert to Pending during graceful shutdown — revert must use CancellationToken.None");
        item.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on revert");
        item.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on revert");
        item.RetryCount.Should().Be(1, "RetryCount must be incremented even during shutdown revert");
    }

    private PendingWorkItemDrainService CreateService()
    {
        return new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                _dbFactory,
                _mockResolver.Object,
                _mockAgentComm.Object,
                _transitionService,
                _mockPendingWork.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            MakeLabelSwapService(),
            MakeDispatchRevertHandler(),
            _mockProjectStore.Object);
    }

    private LabelSwapService MakeLabelSwapService() =>
        new(_dbFactory, _mockLabelService.Object, NullLogger<LabelSwapService>.Instance);

    private DispatchRevertHandler MakeDispatchRevertHandler() =>
        new(_dbFactory, _mockResolver.Object, _runService, _transitionService, NullLogger<DispatchRevertHandler>.Instance);

    /// <summary>
    /// Invokes DrainPendingItemsAsync directly via reflection to avoid BackgroundService
    /// scheduling races. This is deterministic — no Task.Delay needed.
    /// Catches OperationCanceledException to mirror ExecuteAsync's loop behavior.
    /// </summary>
    private static async Task InvokeDrainAsync(PendingWorkItemDrainService service)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("DrainPendingItemsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DrainPendingItemsAsync not found");
        var task = (Task)method.Invoke(service, [CancellationToken.None])!;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Mirrors ExecuteAsync behavior: OCE during drain is swallowed by the loop
        }
    }

    [Fact]
    public async Task DrainPendingItems_TransitionsToDispatched_BeforeSendingViaSIgnalR()
    {
        // Reproduction: DrainService called AssignJobAsync BEFORE TransitionAsync(Dispatched).
        // This caused the agent's JobAccepted → "Pending → Running" transition to fail
        // because the DB row was still Status=Pending when the agent reported acceptance.
        //
        // The fix: TransitionAsync(Dispatched) must run BEFORE AssignJobAsync so the
        // DB state is Dispatched by the time the agent receives and accepts the job.
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#100",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#100",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Track the order of operations
        var operationOrder = new List<string>();

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => operationOrder.Add("AssignJobAsync"));

        var service = CreateService();

        // Act: invoke drain — need to check DB state WHEN AssignJobAsync is called
        WorkItemStatus? statusAtAssignTime = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // At the moment AssignJobAsync is called, check what the DB status is
                await using var checkDb = await _dbFactory.CreateDbContextAsync();
                var item = await checkDb.WorkItems.FindAsync(workItemId);
                statusAtAssignTime = item?.Status;
            });

        await InvokeDrainAsync(service);

        // Assert: the WorkItem must already be Dispatched when AssignJobAsync fires
        statusAtAssignTime.Should().Be(WorkItemStatus.Dispatched,
            "WorkItem must be transitioned to Dispatched BEFORE sending via SignalR, " +
            "otherwise the agent's JobAccepted → Running transition fails with 'Invalid transition: Pending → Running'");
    }

    [Fact]
    public async Task DrainPendingItems_WithProjectId_InjectsProjectSecrets()
    {
        // Reproduction: DrainService dispatched jobs WITHOUT project secrets.
        // SignalRWorkDistributor injected them at delivery time, but the drain path
        // skipped this step entirely. Agents running jobs dispatched via drain had
        // no access to project-level secrets (API keys, tokens).
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#200",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600,
            ProjectId = "project-42"
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#200",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                ProjectId = "project-42"
            });
            await db.SaveChangesAsync();
        }

        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // Setup: project store returns secrets for project-42
        var secrets = new Dictionary<string, string> { ["API_KEY"] = "secret-value-123" };
        _mockProjectStore.Setup(p => p.GetProjectByIdAsync("project-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = "project-42", Name = "Test", Secrets = secrets });

        // Capture the message sent to AssignJobAsync
        JobAssignmentMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => capturedMessage = msg);

        var service = CreateService();
        await InvokeDrainAsync(service);

        // Assert: the message must contain project secrets
        capturedMessage.Should().NotBeNull("AssignJobAsync should have been called");
        capturedMessage!.ProjectSecrets.Should().NotBeNull("project secrets must be injected");
        capturedMessage.ProjectSecrets.Should().ContainKey("API_KEY");
        capturedMessage.ProjectSecrets!["API_KEY"].Should().Be("secret-value-123");
    }

    [Fact]
    public async Task DrainPendingItems_TransitionToDispatchedFails_IncrementsRetryCount()
    {
        // Reproduction: When TransitionAsync(Dispatched) itself throws (e.g., DB constraint violation),
        // the item remains Pending. The catch block's TransitionAsync(Pending) is idempotent (item already
        // at target) and skips the mutate callback — RetryCount was never incremented, causing infinite retries.
        // Fix: detect when dispatchedSuccessfully is false and increment RetryCount directly via DB.
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#1381",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        // Use a separate DB with a first-save-throws interceptor for the transition service.
        // The drain service's _dbFactory uses normal options (for the direct RetryCount++ fallback).
        var interceptor = new FirstSaveThrowsInterceptor();

        // Both factories share the same InMemory database (by name) so seeded data is visible to both.
        // The interceptor factory is used by the transition service (throws on first save),
        // while the normal factory is used for seeding and the drain service's direct DB fallback.
        var sharedDbName = $"DrainTest_TransitionFails_{Guid.NewGuid()}";
        var normalDbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(sharedDbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var interceptorDbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(sharedDbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;

        var normalFactory = new InMemoryDbContextFactory(normalDbOptions);
        var interceptorFactory = new InMemoryDbContextFactory(interceptorDbOptions);

        // Seed the work item using the normal factory (no interceptor interference)
        await using (var db = await normalFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#1381",
                IssueProviderConfigId = "issue-provider-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                RetryCount = 0
            });
            await db.SaveChangesAsync();
        }

        // Now arm the interceptor — next SaveChangesAsync through this interceptor will throw
        interceptor.Armed = true;

        // The transition service uses the interceptor factory (will throw on Dispatched transition save)
        var transitionService = new WorkItemTransitionService(
            interceptorFactory, NullLogger<WorkItemTransitionService>.Instance);

        // Setup: agent available
        _mockResolver.Setup(r => r.ResolveAgent(""))
            .Returns(new AgentResolveResult("conn-1", "agent-1"));

        // The drain service uses normalFactory for its direct DB access (_dbFactory field)
        var service = new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                normalFactory,
                _mockResolver.Object,
                _mockAgentComm.Object,
                transitionService,
                _mockPendingWork.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            new LabelSwapService(normalFactory, _mockLabelService.Object, NullLogger<LabelSwapService>.Instance),
            new DispatchRevertHandler(normalFactory, _mockResolver.Object, _runService, transitionService, NullLogger<DispatchRevertHandler>.Instance),
            _mockProjectStore.Object);

        // Act
        await InvokeDrainAsync(service);

        // Assert: RetryCount must be incremented despite TransitionAsync(Dispatched) failure
        await using var checkDb = await normalFactory.CreateDbContextAsync();
        var item = await checkDb.WorkItems.FindAsync(workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "WorkItem must remain Pending when TransitionAsync(Dispatched) fails");
        item.RetryCount.Should().Be(1,
            "RetryCount must be incremented even when TransitionAsync(Dispatched) fails — " +
            "prevents infinite retry loops for items that consistently fail at dispatch stage");
    }

    // TODO: Add negative test verifying RetryCount is NOT double-incremented when dispatchedSuccessfully
    // is true (exception occurs after Dispatched transition, e.g., SignalR failure). The existing test
    // DrainPendingItems_RetryCountIncrements_AcrossMultipleFailedAttempts covers this indirectly but
    // does not explicitly assert against double-increment if the !dispatchedSuccessfully guard is removed.

    public void Dispose()
    {
        // Cleanup in-memory database
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }

    /// <summary>
    /// Factory that throws <see cref="OperationCanceledException"/> when CreateDbContextAsync
    /// is called with a cancelled token — simulating real DB provider behavior that the
    /// EF Core InMemory provider does not replicate (see dotnet/efcore#13368).
    /// Used to prove that the revert path passes CancellationToken.None rather than the
    /// cancelled stoppingToken.
    /// </summary>
    private sealed class CancellationAwareDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public CancellationAwareDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineDbContext(_options));
        }
    }

    /// <summary>
    /// EF Core interceptor that throws <see cref="DbUpdateException"/> on the first
    /// <c>SaveChangesAsync</c> call after being armed. Subsequent calls succeed normally.
    /// Used to simulate TransitionAsync(Dispatched) failing at the DB level while allowing
    /// subsequent DB operations (the direct RetryCount++ fallback) to succeed.
    /// </summary>
    private sealed class FirstSaveThrowsInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        private bool _thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !_thrown)
            {
                _thrown = true;
                throw new DbUpdateException(
                    "Simulated DB failure during TransitionAsync(Dispatched)",
                    new InvalidOperationException("Simulated constraint violation"));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

/// <summary>
/// Direct-invocation tests for the extracted <c>DispatchPipelineItemAsync</c> private method,
/// satisfying the acceptance criterion: "consolidation and pipeline dispatch paths are separate methods
/// independently callable from tests." Uses reflection consistent with the existing InvokeDrainAsync pattern.
/// </summary>
public sealed class DispatchPipelineItemAsyncTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPendingWorkQuery> _mockPendingWork = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public DispatchPipelineItemAsyncTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DispatchPipelineItemTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockPendingWork.Setup(p => p.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingJob>().AsReadOnly());
    }

    [Fact]
    public async Task DispatchPipelineItem_SuccessfulDispatch_ReturnsTrue_AndRecordsTelemetry()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        var (item, request) = await InsertAndBuildItem(workItemId, "agent-1");

        _mockAgentComm.Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockResolver.Setup(r => r.AssignJob("agent-1", workItemId.ToString()));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchPipelineItemAsync(service, item, request, "agent-1", "conn-1", CancellationToken.None);

        // Assert
        result.Should().BeTrue("successful dispatch must return true");
        _mockAgentComm.Verify(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockResolver.Verify(r => r.AssignJob("agent-1", workItemId.ToString()), Times.Once);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Dispatched);
        stored.AssignedAgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task DispatchPipelineItem_AssignJobThrows_RevertsToPending_IncrementsRetryCount_ReturnsFalse()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        var (item, request) = await InsertAndBuildItem(workItemId, "agent-1");

        _mockAgentComm.Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SignalR delivery failed"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = CreateService();

        // Act
        var result = await InvokeDispatchPipelineItemAsync(service, item, request, "agent-1", "conn-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse("failed dispatch must return false");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.AssignedAgentId.Should().BeNull();
        stored.DispatchedAt.Should().BeNull();
        stored.RetryCount.Should().Be(1, "pipeline dispatch failure must increment RetryCount");
        // TODO: This test exercises the orchestrator-restart recovery path (run is null → AddRun is called)
        // because no run was pre-registered in _runService. After the dispatch failure, the catch block calls
        // _runService.RemoveRun(request.RunId) (because dispatchedSuccessfully == true before AssignJobAsync throws).
        // Add an assertion that _runService.GetRun(request.RunId) == null after the call to verify the leaked
        // in-memory run is cleaned up. Without it, accidentally removing RemoveRun from the catch block would
        // not be detected by this test.
    }

    [Fact]
    public async Task DispatchPipelineItem_TransitionToDispatchedFails_IncrementsRetryCountViaDirect_ReturnsFalse()
    {
        // Arrange: make the Dispatched transition fail, but allow subsequent DB operations to succeed
        var workItemId = Guid.NewGuid();

        var sharedDbName = $"DispatchPipelineTransitionFails_{Guid.NewGuid()}";
        var normalOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(sharedDbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var interceptor = new FirstSaveThrowsInterceptor();
        var interceptorOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(sharedDbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;

        var normalFactory = new InMemoryDbContextFactory(normalOptions);
        var interceptorFactory = new InMemoryDbContextFactory(interceptorOptions);

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        await using (var db = await normalFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                Payload = payload,
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                RetryCount = 0
            });
            await db.SaveChangesAsync();
        }

        interceptor.Armed = true;
        var failingTransition = new WorkItemTransitionService(interceptorFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var service = new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                normalFactory, _mockResolver.Object, _mockAgentComm.Object,
                failingTransition, _mockPendingWork.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            new LabelSwapService(normalFactory, _mockLabelService.Object, NullLogger<LabelSwapService>.Instance),
            new DispatchRevertHandler(normalFactory, _mockResolver.Object, _runService, failingTransition, NullLogger<DispatchRevertHandler>.Instance),
            _mockProjectStore.Object);

        var item = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            RetryCount = 0
        };

        // Act
        var result = await InvokeDispatchPipelineItemAsync(service, item, request, "agent-1", "conn-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse("transition failure must return false");
        await using var checkDb = await normalFactory.CreateDbContextAsync();
        var stored = await checkDb.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        // TODO: Potential double-increment ambiguity. When TransitionAsync(Dispatched) fails, the catch block
        // calls TryRevertToPendingAsync(incrementRetryCount: true) via failingTransition (interceptorFactory),
        // which throws once and then succeeds — so the revert fires and increments RetryCount to 1.
        // The catch block also has a direct-DB increment path (for the !dispatchedSuccessfully case) via
        // normalFactory — which would read RetryCount==1 and write RetryCount==2. If both paths fire,
        // the correct assertion should be 2, not 1. Whether the direct-DB path fires depends on whether
        // TryRevertToPendingAsync was idempotent. Verify the actual execution path and update the assertion
        // to match the expected value precisely. The comment "direct-DB RetryCount increment must fire"
        // may be incorrect if the revert already incremented via TryRevertToPendingAsync.
        stored.RetryCount.Should().Be(1, "direct-DB RetryCount increment must fire when Dispatched transition itself fails");
    }

    private PendingWorkItemDrainService CreateService() =>
        new(new DrainServiceDependencies(
                _dbFactory, _mockResolver.Object, _mockAgentComm.Object,
                _transitionService, _mockPendingWork.Object,
                NullLogger<PendingWorkItemDrainService>.Instance),
            new LabelSwapService(_dbFactory, _mockLabelService.Object, NullLogger<LabelSwapService>.Instance),
            new DispatchRevertHandler(_dbFactory, _mockResolver.Object, _runService, _transitionService, NullLogger<DispatchRevertHandler>.Instance),
            _mockProjectStore.Object);

    private async Task<(WorkItemEntity item, JobDistributionRequest request)> InsertAndBuildItem(Guid workItemId, string agentId)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = workItemId.ToString(),
            TimeoutSeconds = 3600
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            RetryCount = 0
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();
        return (entity, request);
    }

    private static async Task<bool> InvokeDispatchPipelineItemAsync(
        PendingWorkItemDrainService service, WorkItemEntity item, JobDistributionRequest request,
        string agentId, string connectionId, CancellationToken ct)
    {
        var method = typeof(PendingWorkItemDrainService).GetMethod("DispatchPipelineItemAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DispatchPipelineItemAsync not found");
        var task = (Task<bool>)method.Invoke(service, [item, request, (AgentId)agentId, connectionId, ct])!;
        return await task;
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }

    private sealed class FirstSaveThrowsInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        private bool _thrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !_thrown)
            {
                _thrown = true;
                throw new DbUpdateException(
                    "Simulated DB failure during TransitionAsync(Dispatched)",
                    new InvalidOperationException("Simulated constraint violation"));
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
