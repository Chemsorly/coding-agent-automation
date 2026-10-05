using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using DispatchStateBuilder = CodingAgent.Api.Dispatch.DispatchStateBuilder;
using DispatchLifecycleService = CodingAgent.Api.Dispatch.DispatchLifecycleService;
using DispatchTemplateResolver = CodingAgent.Api.Dispatch.DispatchTemplateResolver;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchStateBuilder"/>.
/// Validates: state building, concurrency gating, rate limiting, template resolution, PVC gating.
/// </summary>
[Trait("Feature", "DispatchStateBuilder")]
public class DispatchStateBuilderTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly string _dbName;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly LeaderElectionService _leaderElection;

    public DispatchStateBuilderTests()
    {
        _dbName = $"DispatchStateBuilder-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _leaderElection = CreateAlwaysLeaderElection();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task BuildStateAsync_FiltersByTaskType()
    {
        // Arrange: mix of consolidation and regular items
        await InsertWorkItem(Guid.NewGuid(), "regular", WorkItemStatus.Pending, WorkItemTaskType.Implementation);
        await InsertWorkItem(Guid.NewGuid(), "consolidation", WorkItemStatus.Pending, WorkItemTaskType.Consolidation);

        var builder = CreateBuilder();

        // Act: filter for non-consolidation
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        // Assert
        state.Should().NotBeNull();
        state!.PendingItems.Should().HaveCount(1);
        state.PendingItems[0].AgentSelector.Should().Be("regular");
    }

    [Fact]
    public async Task BuildStateAsync_ReturnsNull_WhenNoPendingItems()
    {
        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task BuildStateAsync_BuildsConcurrencyMap()
    {
        // Arrange: pending + active items
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Dispatched);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Running);
        await InsertWorkItem(Guid.NewGuid(), "kiro,python", WorkItemStatus.Running);

        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.ConcurrencyBySelector["kiro,dotnet"].Should().Be(2);
        state.ConcurrencyBySelector["kiro,python"].Should().Be(1);
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_RateLimitHit_StopsIteration()
    {
        // Arrange: 3 pending items, rate limiter allows only 1
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,kiro"] = "img:latest" });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        // Rate limiter with 1 token, no replenishment
        using var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            TokensPerPeriod = 1,
            AutoReplenishment = false
        });

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().HaveCount(1, "rate limiter should stop iteration after 1 item");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_ConcurrencyLimitReached_SkipsItem()
    {
        // Arrange: 1 pending + 2 active, max concurrent = 2
        var selector = "kiro,dotnet";
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Dispatched);
        await InsertWorkItem(Guid.NewGuid(), selector, WorkItemStatus.Running);

        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 2 });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty("concurrency limit of 2 already reached with 2 active items");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_NoTemplate_CallsOnNoTemplateCallback()
    {
        await InsertWorkItem(Guid.NewGuid(), "unknown-selector", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new()); // No templates

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        PendingWorkItemProjection? failedItem = null;
        string? failedMessage = null;

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (item, message, _) =>
            {
                failedItem = item;
                failedMessage = message;
                return Task.CompletedTask;
            },
            CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty();
        failedItem.Should().NotBeNull();
        failedItem!.AgentSelector.Should().Be("unknown-selector");
        failedMessage.Should().Contain("No job template for selector");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_NoPvc_SkipsKiroItem()
    {
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        // Builder with empty PVC pool
        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            pvcPool: []); // No PVCs available

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty("no PVC available for kiro agent");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_Cancellation_StopsIteration()
    {
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,kiro"] = "img:latest" });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        using var cts = new CancellationTokenSource();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, cts.Token))
        {
            candidates.Add(candidate);
            cts.Cancel(); // Cancel after first candidate
        }

        candidates.Should().HaveCount(1, "should stop after cancellation");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_FallbackTemplateResolved_YieldsCandidate()
    {
        // Arrange: item with selector "kiro" — no direct template for "kiro",
        // but profile resolves it to "dotnet,kiro" which has a template.
        await InsertWorkItem(Guid.NewGuid(), "kiro", WorkItemStatus.Pending);

        var profile = new AgentProfile
        {
            DisplayName = "Kiro+Dotnet Profile",
            AgentProviderConfigId = "agent-kiro",
            MatchLabels = ["dotnet", "kiro"],
            Enabled = true
        };
        var mockProfileStore = new Mock<IAgentProfileStore>();
        mockProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile });

        // Template is keyed to the resolved "dotnet,kiro" selector
        var builder = CreateBuilder(
            imageMapping: new() { ["dotnet,kiro"] = "img:latest" },
            profileStore: mockProfileStore.Object);

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().HaveCount(1, "fallback profile resolution should yield a candidate");
        candidates[0].EffectiveSelector.Should().Be("dotnet,kiro");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_LeadershipLostMidIteration_StopsImmediately()
    {
        // Arrange: 2 pending items
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,kiro"] = "img:latest" });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        // Build a LeaderElectionService that loses leadership after the first candidate
        var leaderElection = CreateAlwaysLeaderElection();
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
            // Simulate leadership loss after the first candidate
            isLeaderField!.SetValue(leaderElection, false);
        }

        candidates.Should().HaveCount(1, "leadership loss should stop iteration after the first candidate");
    }

    // TODO: Missing test for the exception path on the FIRST query (pendingItems ToListAsync throws).
    // The try/catch in BuildStateAsync wraps the entire body after `var db = ...`, so a failure on
    // the pendingItems query should also dispose the DbContext. There is no test covering this path:
    // BuildStateAsync_DisposesDbContext_WhenExceptionOccursAfterPendingItemsFetch explicitly seeds
    // the DB so pendingItems succeeds. Add a sibling test that injects an exception on the FIRST
    // WorkItems access (counter threshold = 1) to verify disposal on the early-query failure path.
    // See TestQualityReviewer WARNING (Issue #1910).

    /// <summary>
    /// Verifies the acceptance criterion from Issue #1910: when an exception occurs
    /// after the pendingItems query (i.e., after the early-return null path is bypassed),
    /// <see cref="DispatchStateBuilder.BuildStateAsync"/> must dispose the DbContext before
    /// propagating the exception — so the caller never receives and leaks an undisposed context.
    ///
    /// Uses a spy DbContext subclass that throws <see cref="OperationCanceledException"/> on the
    /// second access to <c>Set&lt;WorkItemEntity&gt;()</c> (the activeCounts query). The EF Core
    /// InMemory provider does not respect cancellation tokens in <c>ToListAsync</c>
    /// (dotnet/efcore#13368), so the exception is injected at the <c>Set&lt;T&gt;()</c> override
    /// level, which IS dispatched virtually through the runtime type.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_DisposesDbContext_WhenExceptionOccursAfterPendingItemsFetch()
    {
        // Arrange: insert one pending item so the pendingItems.Count == 0 early-return is NOT taken.
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        // Build spy options backed by the same in-memory database so the spy context can see the item.
        var spyOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        DisposeTrackingContext? spyContext = null;
        var spyFactory = new DelegatingDbContextFactory(() =>
        {
            spyContext = new DisposeTrackingContext(spyOptions);
            return spyContext;
        });

        var templateProvider = BuildTemplateProvider(new() { ["dotnet,kiro"] = "img:latest" });
        var templateResolver = new DispatchTemplateResolver(null, templateProvider);
        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            new Infrastructure.Persistence.Services.WorkItemTransitionService(
                _dbFactory, new Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Persistence.Services.WorkItemTransitionService>()),
            new DispatchServiceOptions { KiroPvcPool = ["pvc-1"] });
        var options = new DispatchServiceOptions { KiroPvcPool = ["pvc-1"] };

        var builder = new DispatchStateBuilder(spyFactory, lifecycle, templateProvider, templateResolver, options);

        // Act: OperationCanceledException thrown on 2nd WorkItems access (activeCounts query).
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildStateAsync(
                w => w.TaskType != WorkItemTaskType.Consolidation,
                recordTelemetry: false,
                CancellationToken.None));

        // Assert: DbContext was disposed despite the exception.
        spyContext.Should().NotBeNull("factory must have been called");
        // TODO: This assertion does not verify that the propagated exception is the same one injected
        // by the spy. If BuildStateAsync accidentally wraps or replaces the exception, Assert.ThrowsAsync
        // above still passes while the propagation contract is broken. Consider capturing the thrown
        // exception and asserting ex.Message contains the sentinel string.
        // See TestQualityReviewer WARNING.
        spyContext!.WasDisposed.Should().BeTrue(
            "BuildStateAsync must dispose DbContext on all exception paths after the early-return guard");
    }

    [Fact]
    public async Task GetEligibleCandidatesAsync_ResolvedSelectorAtConcurrencyLimit_SkipsItem()
    {
        // Exercises DispatchStateBuilder.TryResolveCandidateAsync lines 313-321:
        // when a work item's AgentSelector doesn't directly match a template, but profile-based
        // resolution finds a fallback template whose effective selector IS at its concurrency limit,
        // the item should be skipped with skip:true (not dispatched).
        //
        // Setup:
        //   - Item selector "kiro" (partial — no direct template match)
        //   - AgentProfile with MatchLabels ["dotnet","kiro"] covers the "kiro" selector
        //   - Template "dotnet,kiro" has MaxConcurrent=1
        //   - 1 active item already using "dotnet,kiro" → concurrency limit reached
        var partialSelector = "kiro";
        var fullSelector = "dotnet,kiro";

        // 1 pending item with partial selector
        await InsertWorkItem(Guid.NewGuid(), partialSelector, WorkItemStatus.Pending);
        // 1 active item with the full resolved selector — fills the concurrency slot
        await InsertWorkItem(Guid.NewGuid(), fullSelector, WorkItemStatus.Running);

        var profileStore = new Mock<IAgentProfileStore>();
        profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new AgentProfile
                {
                    Id = "kiro-dotnet",
                    DisplayName = "Kiro + DotNet",
                    AgentProviderConfigId = "test-provider",
                    MatchLabels = new List<string> { "dotnet", "kiro" }
                }
            });

        var builder = CreateBuilder(
            imageMapping: new() { [fullSelector] = "img:latest" },
            maxConcurrent: new() { [fullSelector] = 1 },
            profileStore: profileStore.Object);

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();

        var candidates = new List<DispatchCandidate>();
        await foreach (var candidate in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "Test",
            (_, _, _) => Task.CompletedTask, CancellationToken.None))
        {
            candidates.Add(candidate);
        }

        candidates.Should().BeEmpty(
            "the resolved selector 'dotnet,kiro' is at its concurrency limit (1/1), so the partial-selector item should be skipped");
    }

    // ── PriorityWeight ordering (within-tier) ────────────────────────────

    /// <summary>
    /// Within a tier, PriorityWeight DESC is the secondary sort key — the higher-weight item
    /// (PriorityWeight=100, manual) appears before the lower-weight item (PriorityWeight=0, auto)
    /// even when it was created later.
    ///
    /// Note: Both items are Implementation (same tier), so this test covers the within-tier ordering
    /// only. For the tier-primary sort invariant (Review &gt; Decomp &gt; Impl &gt; Consolidation), see
    /// DispatchStateBuilderBranchTests in CodingAgent.Orchestration.UnitTests.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_WithinTier_OrdersByPriorityWeightDescThenCreatedAtAsc()
    {
        // Seed: low-weight item created first, high-weight item created later
        var lowId = Guid.NewGuid();
        var highId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        await InsertWorkItem(lowId, "kiro,dotnet", WorkItemStatus.Pending,
            priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));
        await InsertWorkItem(highId, "kiro,dotnet", WorkItemStatus.Pending,
            priorityWeight: 100, createdAt: baseTime.AddMinutes(-5));

        var builder = CreateBuilder(
            imageMapping: new Dictionary<string, string> { ["kiro,dotnet"] = "kiro-image" },
            maxConcurrent: new Dictionary<string, int> { ["kiro,dotnet"] = 10 });

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.PendingItems.Should().HaveCountGreaterThanOrEqualTo(2);

        var fixtureItems = state.PendingItems
            .Where(i => i.Id == lowId || i.Id == highId)
            .ToList();

        fixtureItems.Should().HaveCount(2);
        fixtureItems[0].Id.Should().Be(highId,
            "high-weight item (PriorityWeight=100) must appear before low-weight item (PriorityWeight=0) in BuildStateAsync output");
        fixtureItems[1].Id.Should().Be(lowId,
            "low-weight item must appear after high-weight item in BuildStateAsync output");
    }

    // ── IsAtConcurrencyLimit — static helper ─────────────────────────────

    [Fact]
    public void IsAtConcurrencyLimit_MaxConcurrentZero_AlwaysReturnsFalse()
    {
        // maxConcurrent == 0 means "no limit configured" — always allow
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchStateBuilder.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: 0)
            .Should().BeFalse("zero maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NegativeMax_AlwaysReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchStateBuilder.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: -1)
            .Should().BeFalse("negative maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_BelowLimit_ReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 1 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeFalse("current(1) < max(2) — not at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AtLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 2 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(2) == max(2) — at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AboveLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 5 };
        DispatchStateBuilder.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(5) > max(2) — above limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_SelectorNotInMap_ReturnsFalse()
    {
        // Selector not in the dictionary → GetValueOrDefault returns 0
        var concurrency = new Dictionary<string, int>();
        DispatchStateBuilder.IsAtConcurrencyLimit("new-selector", concurrency, maxConcurrent: 1)
            .Should().BeFalse("no active items for selector → current=0 < max=1");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NullSelector_FallsBackToEmpty_ReturnsFalse()
    {
        // Null agentSelector → "" key, nothing in map → 0 active
        var concurrency = new Dictionary<string, int>();
        DispatchStateBuilder.IsAtConcurrencyLimit(null, concurrency, maxConcurrent: 1)
            .Should().BeFalse("null selector uses empty key, not in map → 0 active");
    }

    // ── IsKiroAgentWithoutPvc — static helper ─────────────────────────────

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgent_NoPvcs_ReturnsTrue()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: new List<string>())
            .Should().BeTrue("kiro agent with no PVCs must be blocked");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_KiroAgent_HasPvc_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: true, availablePvcs: new List<string> { "pvc-1" })
            .Should().BeFalse("kiro agent with available PVC may proceed");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_NonKiroAgent_NoPvcs_ReturnsFalse()
    {
        // Non-kiro agents don't need PVCs
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: false, availablePvcs: new List<string>())
            .Should().BeFalse("non-kiro agents never require a PVC");
    }

    [Fact]
    public void IsKiroAgentWithoutPvc_NonKiroAgent_HasPvc_ReturnsFalse()
    {
        DispatchStateBuilder.IsKiroAgentWithoutPvc(isKiroAgent: false, availablePvcs: new List<string> { "pvc-1" })
            .Should().BeFalse("non-kiro agent with PVC — still false");
    }

    // ── BuildStateAsync — telemetry path ──────────────────────────────────

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_True_WithItems_DoesNotThrow()
    {
        // The telemetry-recording path calls WorkDistributionTelemetry.RecordLastPollEpoch()
        // and UpdateCredentialPoolMetrics(). Verify the call succeeds without throwing.
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(pvcPool: ["pvc-1"]);

        // Should not throw even when telemetry path is exercised
        var act = () => builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            CancellationToken.None);

        await act.Should().NotThrowAsync("telemetry recording must be non-faulting");
    }

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_True_NoPendingItems_ReturnsNull()
    {
        // Telemetry path with no items: calls DispatcherPollCount.Add(1) then returns null
        var builder = CreateBuilder();

        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            CancellationToken.None);

        state.Should().BeNull("no pending items → null regardless of telemetry flag");
    }

    [Fact]
    public async Task BuildStateAsync_RecordTelemetry_False_WithItems_ReturnsState()
    {
        // Verify the non-telemetry path still builds state correctly
        await InsertWorkItem(Guid.NewGuid(), "kiro,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.PendingItems.Should().HaveCount(1);
    }

    // ── GetEligibleCandidatesAsync — concurrency with PVC available ───────

    [Fact]
    public async Task GetEligibleCandidatesAsync_MultipleItems_AllEligible_YieldsAll()
    {
        await InsertWorkItem(Guid.NewGuid(), "opencode,dotnet", WorkItemStatus.Pending);
        await InsertWorkItem(Guid.NewGuid(), "opencode,dotnet", WorkItemStatus.Pending);

        var builder = CreateBuilder(imageMapping: new() { ["dotnet,opencode"] = "img:latest" });
        var state = await builder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            CancellationToken.None);

        using var rateLimiter = CreateUnlimitedRateLimiter();
        var candidates = new List<DispatchCandidate>();

        await foreach (var c in builder.GetEligibleCandidatesAsync(
            state!, _leaderElection, rateLimiter, "TestCaller",
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None))
        {
            candidates.Add(c);
        }

        candidates.Should().HaveCount(2, "two eligible items with no concurrency limit");
    }

    // ── Tier ordering (RunType tier = primary sort key) ───────────────────

    // TODO: These tests use the EF Core InMemory provider, which evaluates the OrderBy ternary
    // as a CLR expression (LINQ-to-Objects) — it never exercises the SQL CASE WHEN translation
    // used against PostgreSQL in production. If Npgsql failed to translate the conditional (it
    // does not — this is supported), these tests would still pass, giving false confidence.
    // Consider adding one tier-ordering assertion to the Testcontainers-backed Npgsql integration
    // suite (tests/CodingAgent.Infrastructure.IntegrationTests) to guard the actual SQL translation.

    // TODO: All tier-ordering tests below pass `w => true` as the taskTypeFilter, exercising the
    // "future CAS-locked poller" code path only. The acceptance criteria also require that tier
    // ordering works when the legacy filter (`w => w.TaskType != Consolidation`) is active.
    // Add a test that passes the legacy filter with a mixed-type seed (including a Consolidation
    // item that should be hidden) to verify the filter-before-sort contract and guard against a
    // future refactor accidentally swapping the filter and sort in BuildStateAsync.

    /// <summary>
    /// AC 1: Mixed task types are ordered by tier then PriorityWeight then CreatedAt.
    /// Review (tier 0) &gt; Decomposition (tier 1) &gt; Implementation (tier 2) &gt; Consolidation (tier 3).
    /// All items have equal PriorityWeight and different CreatedAt to make tier the sole differentiator.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_MixedTaskTypes_OrdersByTierThenPriorityThenCreatedAt()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var reviewId = Guid.NewGuid();
        var decompId = Guid.NewGuid();
        var implId = Guid.NewGuid();
        var consId = Guid.NewGuid();

        // Seed newest → oldest so that CreatedAt alone would produce the wrong order.
        await InsertWorkItemFull(reviewId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime.AddMinutes(3));
        await InsertWorkItemFull(decompId, WorkItemTaskType.Decomposition, priorityWeight: 0, createdAt: baseTime.AddMinutes(2));
        await InsertWorkItemFull(implId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(1));
        await InsertWorkItemFull(consId, WorkItemTaskType.Consolidation, priorityWeight: 0, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,   // include all task types — Pending-status filter is applied inside BuildStateAsync
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var ids = state!.PendingItems.Select(i => i.Id).ToList();
        ids.Should().ContainInConsecutiveOrder(new[] { reviewId, decompId, implId, consId },
            "tier ordering must place Review→Decomp→Impl→Consolidation regardless of CreatedAt");
    }

    /// <summary>
    /// AC 2: Within a tier, PriorityWeight DESC is the secondary sort key.
    /// A manual (weight=100) Implementation item dispatches before an automatic (weight=0) one.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_WithinTier_HigherPriorityWeightFirst()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var highId = Guid.NewGuid();
        var lowId = Guid.NewGuid();

        // lowId is older — without PriorityWeight ordering it would come first (FIFO).
        await InsertWorkItemFull(lowId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));
        await InsertWorkItemFull(highId, WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(highId,
            "higher PriorityWeight (100) must come before lower weight (0) within the same tier");
        items[1].Id.Should().Be(lowId);
    }

    /// <summary>
    /// AC 3: Within a tier, CreatedAt ASC (FIFO) is the tiebreaker when PriorityWeight is equal.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_WithinTier_OlderItemFirst_WhenSamePriority()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();

        await InsertWorkItemFull(newerId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime);
        await InsertWorkItemFull(olderId, WorkItemTaskType.Implementation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-10));

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(olderId,
            "older item (earlier CreatedAt) must come first when PriorityWeight and tier are equal");
        items[1].Id.Should().Be(newerId);
    }

    /// <summary>
    /// AC 4: Consolidation (tier 3) is last even if it was created first (oldest CreatedAt).
    /// A Review item created later still dispatches before consolidation.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_ConsolidationIsLast_EvenIfCreatedFirst()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var consId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();

        await InsertWorkItemFull(consId, WorkItemTaskType.Consolidation, priorityWeight: 0, createdAt: baseTime.AddMinutes(-100));
        await InsertWorkItemFull(reviewId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime);

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(reviewId,
            "Review (tier 0) must come before Consolidation (tier 3) regardless of CreatedAt");
        items[1].Id.Should().Be(consId,
            "Consolidation must be last even though it was created first");
    }

    /// <summary>
    /// AC 6: A manual (PriorityWeight=100) Implementation item must NOT cross the tier boundary
    /// and jump ahead of a Review item (PriorityWeight=0). Tier is the primary key.
    /// </summary>
    [Fact]
    public async Task BuildStateAsync_ManualItemWithinTier_DoesNotCrossOtherTier()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var implManualId = Guid.NewGuid();
        var reviewAutoId = Guid.NewGuid();

        await InsertWorkItemFull(implManualId, WorkItemTaskType.Implementation, priorityWeight: 100, createdAt: baseTime);
        await InsertWorkItemFull(reviewAutoId, WorkItemTaskType.Review, priorityWeight: 0, createdAt: baseTime.AddMinutes(10));

        var builder = CreateBuilder();
        var state = await builder.BuildStateAsync(
            w => true,
            recordTelemetry: false,
            CancellationToken.None);

        state.Should().NotBeNull();
        var items = state!.PendingItems;
        items[0].Id.Should().Be(reviewAutoId,
            "Review (tier 0) must come before a manual Implementation (tier 2) — tier beats PriorityWeight");
        items[1].Id.Should().Be(implManualId,
            "manual Implementation item must be second, not first");
    }

    // TODO: Missing test — legacy flat loop filter contract: add a test that calls BuildStateAsync
    // with the legacy filter (`w => w.TaskType != WorkItemTaskType.Consolidation`) when a Consolidation
    // Pending item exists in the DB, and asserts that the item does NOT appear in PendingItems.
    // This guards the filter-before-sort contract in BuildStateAsync and prevents the filter from
    // being accidentally removed in a future refactor. The existing
    // PollAndDispatch_ConsolidationItem_IsNotDispatchedByThisService covers the end-to-end poller
    // behaviour, but not the BuildStateAsync filter boundary directly.

    // ── Helpers ─────────────────────────────────────────────────────────

    private DispatchStateBuilder CreateBuilder(
        Dictionary<string, string>? imageMapping = null,
        Dictionary<string, int>? maxConcurrent = null,
        string[]? pvcPool = null,
        IAgentProfileStore? profileStore = null)
    {
        imageMapping ??= new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" };
        maxConcurrent ??= new();
        pvcPool ??= ["pvc-1", "pvc-2"];

        var templateProvider = BuildTemplateProvider(imageMapping, maxConcurrent);
        var templateResolver = new DispatchTemplateResolver(profileStore, templateProvider);
        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            new Infrastructure.Persistence.Services.WorkItemTransitionService(
                _dbFactory, new Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Persistence.Services.WorkItemTransitionService>()),
            new DispatchServiceOptions { KiroPvcPool = pvcPool.ToList() });
        var options = new DispatchServiceOptions { KiroPvcPool = pvcPool.ToList() };

        return new DispatchStateBuilder(_dbFactory, lifecycle, templateProvider, templateResolver, options);
    }

    private static JobTemplateStore BuildTemplateProvider(
        Dictionary<string, string> imageMapping,
        Dictionary<string, int>? maxConcurrentPods = null)
    {
        var normalizedMaxConcurrent = maxConcurrentPods?.ToDictionary(
            kv => JobTemplateStore.NormalizeLabels(kv.Key), kv => kv.Value);

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = kv.Key.Contains("kiro") ? "kiro" : "opencode",
            MaxConcurrent = normalizedMaxConcurrent?.GetValueOrDefault(
                JobTemplateStore.NormalizeLabels(kv.Key), 0) ?? 0
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateStore.LoadFromJson(json);
    }

    private static TokenBucketRateLimiter CreateUnlimitedRateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = 1000,
        AutoReplenishment = true
    });

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField!.SetValue(les, true);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        leaderCtsField!.SetValue(les, new CancellationTokenSource());
        return les;
    }

    private async Task InsertWorkItem(Guid id, string agentSelector, WorkItemStatus status,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        int priorityWeight = 0,
        DateTimeOffset? createdAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"owner/repo#{id.ToString("N")[..4]}",
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = agentSelector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}",
            TaskType = taskType,
            PriorityWeight = priorityWeight
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Extended helper supporting PriorityWeight and CreatedAt for tier-ordering tests.
    /// Inserts a Pending item on the default "kiro,dotnet" selector.
    /// </summary>
    private async Task InsertWorkItemFull(
        Guid id,
        WorkItemTaskType taskType,
        int priorityWeight = 0,
        DateTimeOffset? createdAt = null,
        string agentSelector = "kiro,dotnet")
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"owner/repo#{id.ToString("N")[..4]}",
            IssueProviderConfigId = "provider-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = agentSelector,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}",
            TaskType = taskType,
            PriorityWeight = priorityWeight
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    // ── Spy infrastructure for DbContext disposal test (#1910) ───────────

    /// <summary>
    /// A thin <see cref="IDbContextFactory{PipelineDbContext}"/> that delegates context creation
    /// to a caller-supplied factory function, allowing the test to capture the returned instance
    /// for disposal tracking.
    /// </summary>
    private sealed class DelegatingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly Func<PipelineDbContext> _factory;
        public DelegatingDbContextFactory(Func<PipelineDbContext> factory) => _factory = factory;
        public PipelineDbContext CreateDbContext() => _factory();
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// A <see cref="PipelineDbContext"/> subclass that:
    /// <list type="bullet">
    ///   <item>Tracks whether <see cref="DisposeAsync"/> has been called.</item>
    ///   <item>
    ///     Throws <see cref="OperationCanceledException"/> on the second call to
    ///     <c>Set&lt;WorkItemEntity&gt;()</c>, which corresponds to the <c>activeCounts</c>
    ///     query inside <see cref="DispatchStateBuilder.BuildStateAsync"/>. The first access
    ///     (the <c>pendingItems</c> query) succeeds so the early-return guard is bypassed.
    ///   </item>
    /// </list>
    /// The EF Core InMemory provider does not respect <see cref="CancellationToken"/> in
    /// <c>ToListAsync</c> (dotnet/efcore#13368), so the exception is injected at the
    /// <c>Set&lt;T&gt;()</c> override level.
    /// </summary>
    private sealed class DisposeTrackingContext : PipelineDbContext
    {
        private int _workItemSetCallCount;
        public bool WasDisposed { get; private set; }

        public DisposeTrackingContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }

        // TODO: This counter-based injection is coupled to the internal call order of BuildStateAsync.
        // If the implementation reorders queries or inserts an additional WorkItems access before
        // activeCounts, the counter will fire at the wrong point (too early or never), silently
        // breaking the test's ability to verify the post-guard exception path. Consider replacing
        // the counter with a factory-level or flag-based mechanism that throws unconditionally on
        // any access after pendingItems has been successfully fetched. See TestQualityReviewer WARNING.
        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(WorkItemEntity))
            {
                if (++_workItemSetCallCount >= 2)
                    throw new OperationCanceledException(
                        "Simulated OperationCanceledException on second WorkItems access (activeCounts query)");
            }
            return base.Set<TEntity>();
        }

        // TODO: Synchronous Dispose() is not overridden here, so WasDisposed remains false if EF Core
        // or a caller uses the synchronous disposal path instead of DisposeAsync(). This could produce
        // a false-positive "leak detected" result. Override Dispose(bool) and set WasDisposed=true there
        // as well to cover both paths. See TestQualityReviewer WARNING.
        public override async ValueTask DisposeAsync()
        {
            if (WasDisposed) return;
            WasDisposed = true;
            await base.DisposeAsync();
        }
    }
}
