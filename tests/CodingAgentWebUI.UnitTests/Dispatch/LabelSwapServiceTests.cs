using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
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
/// Unit tests for <see cref="LabelSwapService"/> in isolation.
/// Verifies retry behavior, reconciliation flagging, cancellation handling, and
/// run-type-based provider/kind selection logic (#1871).
/// </summary>
public sealed class LabelSwapServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ILabelService> _mockLabelService = new();

    public LabelSwapServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"LabelSwapServiceTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SwapLabelWithRetry_Success_CallsSwapLabelStrictOnce()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: exactly one call, no reconciliation flag set
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
        var item = await GetWorkItem(workItemId);
        item!.NeedsLabelReconciliation.Should().BeFalse("successful swap must not flag for reconciliation");
    }

    [Fact]
    public async Task SwapLabelWithRetry_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        var callCount = 0;
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1) return Task.FromException(new HttpRequestException("rate limited"));
                return Task.CompletedTask;
            });

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: retried and succeeded — no reconciliation flag
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        var item = await GetWorkItem(workItemId);
        item!.NeedsLabelReconciliation.Should().BeFalse("eventual success must not flag for reconciliation");
    }

    [Fact]
    public async Task SwapLabelWithRetry_AllAttemptsExhausted_FlagsForReconciliation()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: called 3 times (1 initial + 2 retries), reconciliation flagged
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        var item = await GetWorkItem(workItemId);
        item!.NeedsLabelReconciliation.Should().BeTrue("exhausted retries must flag for reconciliation");
    }

    [Fact]
    public async Task SwapLabelWithRetry_OperationCanceled_DoesNotRetry_DoesNotFlag()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService();

        // Act: OCE should propagate out of SwapLabelWithRetryAsync
        var act = async () => await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: propagates OCE, no retry, no reconciliation flag
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.InProgress, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var item = await GetWorkItem(workItemId);
        item!.NeedsLabelReconciliation.Should().BeFalse("OCE must not trigger reconciliation flag");
        // TODO: This test uses CancellationToken.None as the outer token, so ct.IsCancellationRequested
        // is always false in the finally block — reconciliation is correctly skipped. However, it does
        // not cover the scenario where SwapLabelStrictAsync throws OCE *because* the caller's token
        // was cancelled (ct.IsCancellationRequested == true). In that case the finally block WOULD
        // flag for reconciliation (intended: shutdown during label swap). Add a complementary test:
        //   use a cancelled CancellationToken and verify NeedsLabelReconciliation becomes true.
        // The shutdown path is partially covered by SwapLabelWithRetry_ShutdownDuringFirstFailure
        // but only via Task.Delay cancellation, not via SwapLabelStrictAsync itself. (#1871)
    }

    [Fact]
    public async Task SwapLabelWithRetry_ShutdownDuringFirstFailure_FlagsForReconciliation()
    {
        // When ct is cancelled after the first failure (simulating shutdown during backoff),
        // the finally block should flag for reconciliation.
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        using var cts = new CancellationTokenSource();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel()) // cancel during the first attempt
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService();

        // Act: the retry delay (Task.Delay) will throw OCE when cts is cancelled
        try { await service.SwapLabelWithRetryAsync(workItemId, request, cts.Token); }
        catch (OperationCanceledException) { /* expected — propagates from Task.Delay */ }

        // Assert: reconciliation flagged (finally block fires because ct.IsCancellationRequested)
        var item = await GetWorkItem(workItemId);
        item!.NeedsLabelReconciliation.Should().BeTrue("shutdown during retry must flag for reconciliation");
    }

    [Fact]
    public async Task SwapLabelWithRetry_ReviewRunType_UsesRepoPrKind()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Review);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: repo provider + PullRequest kind used for Review run type
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SwapLabelWithRetry_ImplementationRunType_UsesIssueProviderIssueKind()
    {
        // Arrange
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Implementation);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: issue provider + Issue kind used for non-Review run types
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SwapLabelWithRetry_DecompositionRunType_UsesIssueProviderIssueKind()
    {
        // Ensures non-Review run types (Decomposition, DecompositionAnalysis) use Issue kind.
        // Addresses the TODO in existing drain tests about missing Decomposition coverage.
        var workItemId = await InsertWorkItem();
        var request = BuildRequest(PipelineRunType.Decomposition);
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await service.SwapLabelWithRetryAsync(workItemId, request, CancellationToken.None);

        // Assert: Decomposition uses Issue kind, not PullRequest
        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync("issue-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private LabelSwapService CreateService() =>
        new(_dbFactory, _mockLabelService.Object, NullLogger<LabelSwapService>.Instance);

    private static JobDistributionRequest BuildRequest(PipelineRunType runType) =>
        new()
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            RunId = Guid.NewGuid().ToString(),
            TimeoutSeconds = 3600,
            RunType = runType
        };

    private async Task<Guid> InsertWorkItem()
    {
        var id = Guid.NewGuid();
        var request = BuildRequest(PipelineRunType.Implementation);
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            Status = WorkItemStatus.Dispatched,
            Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default),
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<WorkItemEntity?> GetWorkItem(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkItems.FindAsync(id);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new PipelineDbContext(_options));
    }
}
