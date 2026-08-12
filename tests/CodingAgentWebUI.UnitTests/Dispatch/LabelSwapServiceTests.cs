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

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="LabelSwapService"/> (#1868).
/// Verifies retry policy, exponential backoff, reconciliation flagging, OCE propagation,
/// and maxAttempts=1 (K8s mode) vs maxAttempts=3 (SignalR mode) configurations.
/// </summary>
public sealed class LabelSwapServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ILabelService> _mockLabelService = new();

    private static readonly Guid WorkItemId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly ProviderConfigId Provider = (ProviderConfigId)"issue-provider-1";
    private static readonly IssueIdentifier Identifier = (IssueIdentifier)"org/repo#42";
    private static readonly LabelTargetKind Kind = LabelTargetKind.Issue;

    public LabelSwapServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"LabelSwapTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    private LabelSwapService CreateService(int maxAttempts = 3) =>
        new(_mockLabelService.Object, _dbFactory,
            NullLogger<LabelSwapService>.Instance, maxAttempts);

    private async Task InsertWorkItemAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = WorkItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "org/repo#42",
            IssueProviderConfigId = "issue-provider-1",
            Status = WorkItemStatus.Dispatched,
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    private async Task<bool> GetReconciliationFlagAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.WorkItems.FindAsync(WorkItemId);
        return entity?.NeedsLabelReconciliation ?? false;
    }

    // ── maxAttempts=3 tests ─────────────────────────────────────────────────

    [Fact]
    public async Task SwapLabel_FirstAttemptSucceeds_CallsSwapLabelStrictOnce()
    {
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(maxAttempts: 3);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeFalse("happy path must not set reconciliation flag");
    }

    [Fact]
    public async Task SwapLabel_FirstAttemptFails_RetriesAndSucceeds()
    {
        await InsertWorkItemAsync();

        var callCount = 0;
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException(new HttpRequestException("rate limited"))
                    : Task.CompletedTask;
            });

        var service = CreateService(maxAttempts: 3);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        (await GetReconciliationFlagAsync()).Should().BeFalse("successful retry must not set reconciliation flag");
    }

    [Fact]
    public async Task SwapLabel_AllAttemptsExhausted_FlagsForReconciliation()
    {
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService(maxAttempts: 3);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        (await GetReconciliationFlagAsync()).Should().BeTrue("exhausting all retry attempts must set NeedsLabelReconciliation");
    }

    [Fact]
    public async Task SwapLabel_CancellationOnFirstAttempt_PropagatesOce_DoesNotFlag_MaxAttempts3()
    {
        // OCE propagation is unconditional regardless of maxAttempts.
        // Note: CancellationToken.None is passed so ct.IsCancellationRequested is always false,
        // meaning the finally-block reconciliation guard never fires. This correctly reflects
        // the test scenario: OCE thrown by the swap itself (not by backoff) must not set the flag.
        // TODO: These OCE tests (MaxAttempts3 and MaxAttemptsOne variants) do not cover the scenario
        // where the caller's token is cancelled before/during the swap call — i.e., where the OCE
        // thrown by SwapLabelStrictAsync is caused by a live cancelled CancellationToken rather than
        // by the swap implementation throwing unconditionally. In that case ct.IsCancellationRequested
        // is true and the finally block WOULD flag for reconciliation. This "shutdown during swap"
        // path (distinct from "shutdown during backoff delay") has no test coverage. Add a test:
        //   use a pre-cancelled CancellationToken, verify OCE propagates AND NeedsLabelReconciliation=true.
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(maxAttempts: 3);

        var act = async () => await service.SwapLabelWithRetryAsync(
            WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>("OCE must propagate unconditionally");

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeFalse(
            "OCE during the swap attempt (not during backoff) must not set reconciliation flag — ct.IsCancellationRequested is false here");
    }

    [Fact]
    public async Task SwapLabel_ShutdownDuringBackoff_FlagsForReconciliation()
    {
        // Simulates shutdown arriving during Task.Delay backoff between retries.
        // Mechanism: first attempt fails, the CTS is cancelled, Task.Delay throws OCE,
        // the outer finally block fires and flags for reconciliation because
        // ct.IsCancellationRequested == true and labelSwapCompleted == false. (#1681)
        await InsertWorkItemAsync();

        using var cts = new CancellationTokenSource();
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new HttpRequestException("transient failure"));

        var service = CreateService(maxAttempts: 3);

        // TODO: The bare catch below swallows OCE without asserting it was actually thrown.
        // Rewrite using `await act.Should().ThrowAsync<OperationCanceledException>()` to explicitly
        // assert OCE propagation, consistent with the other OCE tests in this class.
        try
        {
            await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: OCE propagates from Task.Delay when ct is cancelled during backoff
        }

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeTrue(
            "shutdown during backoff must flag for reconciliation via the outer finally block");
    }

    // ── maxAttempts=1 (K8s mode) tests ─────────────────────────────────────

    [Fact]
    public async Task SwapLabel_MaxAttemptsOne_Success_NoFlag()
    {
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(maxAttempts: 1);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeFalse("K8s mode happy path must not set reconciliation flag");
    }

    [Fact]
    public async Task SwapLabel_MaxAttemptsOne_FailureOnlyAttempt_FlagsImmediately()
    {
        // K8s mode: single attempt, reconciliation flag on failure (no retry). (#1868)
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var service = CreateService(maxAttempts: 1);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeTrue(
            "K8s mode: single-attempt failure must immediately flag for reconciliation");
    }

    [Fact]
    public async Task SwapLabel_CancellationOnFirstAttempt_PropagatesOce_DoesNotFlag_MaxAttemptsOne()
    {
        // OCE propagation is unconditional regardless of maxAttempts.
        // Note: CancellationToken.None is passed so ct.IsCancellationRequested is always false,
        // meaning the finally-block reconciliation guard never fires. This correctly reflects
        // the test scenario: OCE thrown by the swap itself (not by backoff) must not set the flag.
        await InsertWorkItemAsync();

        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(maxAttempts: 1);

        var act = async () => await service.SwapLabelWithRetryAsync(
            WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>("OCE must propagate unconditionally even with maxAttempts=1");

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
        (await GetReconciliationFlagAsync()).Should().BeFalse(
            "OCE during the swap attempt (not during backoff) must not set reconciliation flag");
    }

    // ── Infrastructure ──────────────────────────────────────────────────────

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new PipelineDbContext(_options));
    }
}
