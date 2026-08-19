using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Characterization tests for <see cref="DispatchService"/> label swap delegation (#1868).
/// Verifies that after a successful K8s dispatch, DispatchService delegates to
/// <see cref="ILabelSwapService"/> — and that it correctly skips the swap when
/// <see cref="ILabelSwapService"/> is null or guard conditions are not met.
/// </summary>
[Trait("Feature", "1868-label-swap-extraction")]
public class DispatchServiceLabelSwapTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly LocalDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient = new();
    private readonly Mock<ILabelSwapService> _mockLabelSwapper = new();

    public DispatchServiceLabelSwapTests()
    {
        var dbName = $"DispatchLabelSwap-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new LocalDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private DispatchService CreateService(ILabelSwapService? labelSwapper = null)
    {
        var options = new DispatchServiceOptions
        {
            PollIntervalSeconds = 10, RateLimitPerSecond = 100, Namespace = "default",
            OrchestratorUrl = "http://orchestrator:8080", AgentApiKeySecretName = "agent-api-key",
            KiroPvcPool = ["pvc-1", "pvc-2"]
        };
        var lifecycle = new DispatchLifecycleService(_mockKubeClient.Object, _transitionService, options);
        var templates = new[]
        {
            new JobTemplate { Labels = "dotnet,opencode", Image = "ghcr.io/opencode:latest", ProviderType = "opencode", MaxConcurrent = 10 }
        };
        var templateProvider = JobTemplateStore.LoadFromJson(
            System.Text.Json.JsonSerializer.Serialize(templates));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:PollIntervalSeconds"] = "10",
                ["WorkDistribution:RateLimitPerSecond"] = "100",
                ["WorkDistribution:Namespace"] = "default",
                ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
                ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key"
            })
            .Build();

        return new DispatchService(
            new DispatchServiceCoreDependencies(
                _dbFactory,
                CreateAlwaysLeaderElection(),
                lifecycle,
                LabelSwapper: labelSwapper,
                StateBuilder: new DispatchStateBuilder(
                    _dbFactory, lifecycle, templateProvider,
                    new DispatchTemplateResolver(null, templateProvider),
                    options)),
            config,
            templateProvider);
    }

    private async Task<Guid> InsertPendingItemAsync(
        string issueIdentifier = "org/repo#42",
        string issueProviderConfigId = "issue-1")
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = WorkItemStatus.Pending,
            AgentSelector = "dotnet,opencode",
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task InvokePollAndDispatchAsync(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        // Sets internal fields via reflection to simulate leader state. If _isLeader or _leaderCts
        // are renamed, the null-conditional SetValue silently no-ops, causing PollAndDispatchAsync to
        // exit early (not leader) and producing a misleading assertion failure.
        // TODO: The null-conditional ?. on SetValue silently no-ops if the private field is renamed or
        // removed. If that happens, PollAndDispatchAsync exits as a non-leader and the mock label swapper
        // is never triggered — but the test still passes (Times.Once succeeds on a mock that was never
        // called because Verify only checks calls made, not whether the path was actually reached).
        // Replace the null-conditional with an explicit null-check + throw so reflection failures surface
        // as a test infrastructure error at run time rather than a silent false-green:
        //   if (isLeaderField is null) throw new InvalidOperationException("LeaderElectionService._isLeader field not found — update reflection binding");
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);
        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField?.SetValue(les, new System.Threading.CancellationTokenSource());
        return les;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchService_OnSuccess_DelegatesLabelSwapToLabelSwapService()
    {
        // Verifies acceptance criterion: DispatchService delegates label swap to ILabelSwapService.
        var workItemId = await InsertPendingItemAsync("org/repo#42", "issue-1");

        _mockLabelSwapper
            .Setup(s => s.SwapLabelWithRetryAsync(
                workItemId,
                It.Is<ProviderConfigId>(p => p.Value == "issue-1"),
                It.Is<IssueIdentifier>(i => i.Value == "org/repo#42"),
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(labelSwapper: _mockLabelSwapper.Object);
        await InvokePollAndDispatchAsync(service);

        _mockLabelSwapper.Verify(
            s => s.SwapLabelWithRetryAsync(
                workItemId,
                It.Is<ProviderConfigId>(p => p.Value == "issue-1"),
                It.Is<IssueIdentifier>(i => i.Value == "org/repo#42"),
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify dispatch itself succeeded
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task DispatchService_NullLabelSwapper_SkipsLabelSwap()
    {
        // Verifies guard: when ILabelSwapService is null (label service not configured),
        // DispatchService still dispatches successfully but skips label swap.
        var workItemId = await InsertPendingItemAsync();

        var service = CreateService(labelSwapper: null);
        await InvokePollAndDispatchAsync(service);

        _mockLabelSwapper.Verify(
            s => s.SwapLabelWithRetryAsync(It.IsAny<Guid>(), It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "dispatch should still succeed when label swap service is not configured");
    }

    [Fact]
    public async Task DispatchService_EmptyIssueIdentifier_SkipsLabelSwap()
    {
        // Verifies guard: when IssueIdentifier is empty, label swap is skipped.
        var workItemId = await InsertPendingItemAsync(issueIdentifier: "");

        var service = CreateService(labelSwapper: _mockLabelSwapper.Object);
        await InvokePollAndDispatchAsync(service);

        _mockLabelSwapper.Verify(
            s => s.SwapLabelWithRetryAsync(It.IsAny<Guid>(), It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "label swap must be skipped when IssueIdentifier is empty");
    }

    // ── Local test infrastructure ──────────────────────────────────────────

    private sealed class LocalDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public LocalDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new PipelineDbContext(_options));
    }
}
