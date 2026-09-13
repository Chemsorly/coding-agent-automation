using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using DispatchLifecycleService = CodingAgent.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgent.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgent.Api.Dispatch.DispatchTemplateResolver;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for the pre-dispatch eligibility gate in <see cref="WorkItemDispatchService"/>:
/// <c>CheckEligibilityAsync</c>, <c>CheckPrEligibilityAsync</c>, and <c>SettingsMatch</c>.
///
/// Tests insert Pending WorkItems directly into an in-memory DB, then call
/// <c>PollAndDispatchAsync</c> with a configured <see cref="IProviderConfigStore"/> and
/// <see cref="IProviderFactory"/> to exercise the gate. The DB state after the call
/// confirms whether the item was dispatched or cancelled.
/// </summary>
[Trait("Feature", "WorkItemDispatchServiceEligibilityGate")]
public class WorkItemDispatchServiceEligibilityTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly AlwaysLeaderService _leader = new();

    public WorkItemDispatchServiceEligibilityTests()
    {
        var dbName = $"WorkItemEligibility-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Implementation item eligibility ──────────────────────────────────

    /// <summary>
    /// When the issue is open (<c>IsIssueClosedAsync</c> returns false), the item proceeds
    /// to K8s Job creation and is transitioned to Dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenIssueIsOpen_DispatchesImplementationItem()
    {
        var id = Guid.NewGuid();
        var issueId = "101";
        await InsertWorkItem(id, WorkItemTaskType.Implementation, issueIdentifier: issueId);

        var issueProvider = new Mock<IIssueProvider>();
        issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // issue is open

        var (configStore, factory) = SetupIssueProvider("github", "GitHub", issueProvider.Object);
        var handler = CreateHandler(configStore, factory);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "an open issue must proceed to K8s Job creation");
    }

    /// <summary>
    /// When the issue is closed (<c>IsIssueClosedAsync</c> returns true), the item is
    /// cancelled without a RetryCount increment and no K8s Job is created.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenIssueIsClosed_CancelsImplementationItemWithoutRetry()
    {
        var id = Guid.NewGuid();
        var issueId = "202";
        await InsertWorkItem(id, WorkItemTaskType.Implementation, issueIdentifier: issueId);

        var issueProvider = new Mock<IIssueProvider>();
        issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // issue is closed

        var (configStore, factory) = SetupIssueProvider("github", "GitHub", issueProvider.Object);
        var handler = CreateHandler(configStore, factory);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Cancelled,
            "a closed issue must cancel the WorkItem");
        item.RetryCount.Should().Be(0,
            "cancellation via the eligibility gate must not increment RetryCount");
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no K8s Job must be created when the issue is closed");
    }

    /// <summary>
    /// Per-cycle cache deduplication: two WorkItems for the same issue/provider
    /// must result in only one <c>IsIssueClosedAsync</c> call.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenTwoItemsSameIssue_CallsProviderOnlyOnce()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        const string issueId = "303";

        await InsertWorkItem(id1, WorkItemTaskType.Implementation,
            issueIdentifier: issueId, createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await InsertWorkItem(id2, WorkItemTaskType.Implementation,
            issueIdentifier: issueId, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var issueProvider = new Mock<IIssueProvider>();
        issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // both open

        var (configStore, factory) = SetupIssueProvider("github", "GitHub", issueProvider.Object);
        var handler = CreateHandler(configStore, factory);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        // The cache must deduplicate — only one provider call for both items.
        issueProvider.Verify(
            p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the per-cycle eligibility cache must deduplicate calls for the same (provider, issue) pair");
    }

    /// <summary>
    /// When <c>IsIssueClosedAsync</c> throws, the gate fails open: item stays Pending
    /// (shouldContinue = true) and no K8s Job is created this cycle.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenProviderThrows_FailsOpenAndLeavesItemPending()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation, issueIdentifier: "404");

        var issueProvider = new Mock<IIssueProvider>();
        issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var (configStore, factory) = SetupIssueProvider("github", "GitHub", issueProvider.Object);
        var handler = CreateHandler(configStore, factory);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);

        // Fail-open: despite the provider exception, the item should proceed to K8s Job creation.
        // (The eligibility gate catches the exception and returns true = eligible.)
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "a provider exception must fail open — the item proceeds to dispatch");
    }

    /// <summary>
    /// When <c>GetProviderConfigByIdAsync</c> returns null (provider not found), the gate
    /// fails open and the item is dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenProviderConfigNotFound_FailsOpenAndDispatches()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation, issueIdentifier: "505");

        var configStore = new Mock<IProviderConfigStore>();
        configStore
            .Setup(s => s.GetProviderConfigByIdAsync(
                It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null); // provider not found

        var handler = CreateHandler(configStore.Object, new Mock<IProviderFactory>().Object);

        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "missing provider config must fail open — item proceeds to dispatch");
    }

    // ── Review item (PR) eligibility ─────────────────────────────────────

    /// <summary>
    /// When the PR number is in the first page of open PRs, the Review item is dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenPrIsOpen_DispatchesReviewItem()
    {
        var id = Guid.NewGuid();
        const string prNumber = "42";
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: prNumber);

        var (configStore, factory) = SetupReviewProviders(
            issueProviderId: "github",
            owner: "myorg", repo: "myrepo",
            openPrNumbers: [42]);

        var handler = CreateHandler(configStore, factory);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "a PR that is still open must proceed to K8s Job creation");
    }

    /// <summary>
    /// When the PR number is NOT in the first page of open PRs and HasMore=false,
    /// the Review item is cancelled (PR is definitively closed/merged).
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenPrIsClosedAndFirstPageComplete_CancelsReviewItem()
    {
        var id = Guid.NewGuid();
        const string prNumber = "99";
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: prNumber);

        // Open PRs do not contain PR 99; HasMore=false → all PRs fetched → PR 99 is closed.
        var (configStore, factory) = SetupReviewProviders(
            issueProviderId: "github",
            owner: "myorg", repo: "myrepo",
            openPrNumbers: [10, 20], // PR 99 absent
            hasMore: false);

        var handler = CreateHandler(configStore, factory);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Cancelled,
            "a PR absent from complete first-page results must be cancelled");
        item.RetryCount.Should().Be(0,
            "cancellation via the eligibility gate must not increment RetryCount");
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When HasMore=true (>100 open PRs), the gate fails open — the item is dispatched
    /// rather than incorrectly cancelled.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenPrPageHasMore_FailsOpenAndDispatches()
    {
        var id = Guid.NewGuid();
        const string prNumber = "77";
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: prNumber);

        // PR 77 is not on the first page, but HasMore=true means there are more PRs.
        var (configStore, factory) = SetupReviewProviders(
            issueProviderId: "github",
            owner: "myorg", repo: "myrepo",
            openPrNumbers: [1, 2, 3],
            hasMore: true);

        var handler = CreateHandler(configStore, factory);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "HasMore=true means the PR may be on a later page — must fail open to avoid false cancellation");
    }

    /// <summary>
    /// When no matching repo provider config is found for the issue provider's owner/repo,
    /// the gate fails open and the item is dispatched.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenNoMatchingRepoProvider_FailsOpenAndDispatches()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: "55");

        var issueConfig = MakeProviderConfig("github", ProviderKind.Issue, "GitHub",
            owner: "org-a", repo: "repo-a");
        var repoConfig = MakeProviderConfig("repo-github", ProviderKind.Repository, "GitHub",
            owner: "org-b", repo: "repo-b"); // different owner/repo — no match

        var configStore = new Mock<IProviderConfigStore>();
        configStore
            .Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        configStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repoConfig]);

        var handler = CreateHandler(configStore.Object, new Mock<IProviderFactory>().Object);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "no matching repo provider must fail open — item proceeds to dispatch");
    }

    // ── SettingsMatch ─────────────────────────────────────────────────────

    /// <summary>
    /// Two configs with matching Owner and Repo settings (same values, same type) match.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenProviderSettingsMatch_UsesRepoProviderForPrCheck()
    {
        var id = Guid.NewGuid();
        const string prNumber = "33";
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: prNumber);

        // Both issue and repo provider have owner=myorg, repo=myrepo → SettingsMatch = true.
        var (configStore, factory) = SetupReviewProviders(
            issueProviderId: "github",
            owner: "myorg", repo: "myrepo",
            openPrNumbers: [33]);

        var handler = CreateHandler(configStore, factory);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "matching provider settings must resolve the repo provider and check the PR");
    }

    /// <summary>
    /// When settings don't match (different owner/repo), the gate fails open.
    /// Verifies SettingsMatch rejects mismatched configs.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenOwnerMismatch_FailsOpenAndDispatches()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Review, issueIdentifier: "66");

        var issueConfig = MakeProviderConfig("github", ProviderKind.Issue, "GitHub",
            owner: "org-a", repo: "same-repo");
        var repoConfig = MakeProviderConfig("repo-github", ProviderKind.Repository, "GitHub",
            owner: "org-b", repo: "same-repo"); // different owner

        var configStore = new Mock<IProviderConfigStore>();
        configStore
            .Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        configStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repoConfig]);

        var handler = CreateHandler(configStore.Object, new Mock<IProviderFactory>().Object);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "owner mismatch means no matching repo provider is found — must fail open");
    }

    // ── Gate disabled (no providerConfigStore wired) ──────────────────────

    /// <summary>
    /// When no <see cref="IProviderConfigStore"/> is wired (as in tests that don't exercise
    /// the gate), the eligibility check is skipped and the item is dispatched normally.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenGateNotWired_SkipsCheckAndDispatches()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, WorkItemTaskType.Implementation);

        // No provider config store or factory — gate must be skipped entirely.
        var handler = CreateHandler(providerConfigStore: null, providerFactory: null);
        await handler.PollAndDispatchAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "when the eligibility gate is not wired, items must dispatch without any check");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private WorkItemDispatchService CreateHandler(
        IProviderConfigStore? providerConfigStore = null,
        IProviderFactory? providerFactory = null)
    {
        var imageMapping = new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:latest" };
        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = "kiro"
        }).ToList();
        var templateStore = JobTemplateStore.LoadFromJson(JsonSerializer.Serialize(templates));
        var options = new DispatchServiceOptions
        {
            PollIntervalSeconds = 10,
            RateLimitPerSecond = 100,
            Namespace = "default",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            KiroPvcPool = ["pvc-1"]
        };
        var lifecycle = new DispatchLifecycleService(_mockKubeClient.Object, _transitionService, options);
        var stateBuilder = new DispatchStateBuilder(
            _dbFactory, lifecycle, templateStore,
            new DispatchTemplateResolver(null, templateStore),
            options);

        return new WorkItemDispatchService(
            new WorkItemDispatchServiceDependencies(
                _dbFactory, _leader, lifecycle, templateStore,
                Mock.Of<Microsoft.Extensions.Configuration.IConfiguration>(),
                _transitionService,
                stateBuilder,
                ProviderConfigStore: providerConfigStore,
                ProviderFactory: providerFactory),
            options);
    }

    /// <summary>
    /// Builds a config store and factory for an Implementation WorkItem check.
    /// The issue provider config is returned for <paramref name="issueProviderId"/>;
    /// the factory creates the given <paramref name="issueProvider"/> from it.
    /// </summary>
    private static (IProviderConfigStore ConfigStore, IProviderFactory Factory) SetupIssueProvider(
        string issueProviderId,
        string providerType,
        IIssueProvider issueProvider,
        string? owner = "myorg",
        string? repo = "myrepo")
    {
        var config = MakeProviderConfig(issueProviderId, ProviderKind.Issue, providerType, owner, repo);

        var configStore = new Mock<IProviderConfigStore>();
        configStore
            .Setup(s => s.GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(issueProvider);

        return (configStore.Object, factory.Object);
    }

    /// <summary>
    /// Builds a config store and factory for a Review WorkItem check.
    /// The issue provider config and a matching repo provider config are wired so the
    /// <c>SettingsMatch</c> lookup succeeds, and the repo provider's
    /// <c>ListOpenPullRequestsAsync</c> returns <paramref name="openPrNumbers"/>.
    /// </summary>
    private static (IProviderConfigStore ConfigStore, IProviderFactory Factory) SetupReviewProviders(
        string issueProviderId,
        string owner,
        string repo,
        int[] openPrNumbers,
        bool hasMore = false)
    {
        var issueConfig = MakeProviderConfig(issueProviderId, ProviderKind.Issue, "GitHub", owner, repo);
        var repoConfig = MakeProviderConfig("repo-" + issueProviderId, ProviderKind.Repository, "GitHub", owner, repo);

        var configStore = new Mock<IProviderConfigStore>();
        configStore
            .Setup(s => s.GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        configStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repoConfig]);

        var openPrs = openPrNumbers.Select(n => new PullRequestSummary
        {
            Number = n,
            Identifier = n.ToString(),
            Title = $"PR #{n}",
            Description = string.Empty,
            Labels = [],
            BranchName = $"feature/pr-{n}",
            TargetBranch = "main",
            Url = $"https://github.com/{owner}/{repo}/pull/{n}",
            IsDraft = false
        }).ToList();

        var repoProvider = new Mock<IRepositoryProvider>();
        repoProvider
            .Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = openPrs,
                Page = 1,
                PageSize = 100,
                HasMore = hasMore
            });

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(repoProvider.Object);

        return (configStore.Object, factory.Object);
    }

    private static ProviderConfig MakeProviderConfig(
        string id,
        ProviderKind kind,
        string providerType,
        string? owner = null,
        string? repo = null)
    {
        var settings = new Dictionary<string, string>();
        if (owner is not null) settings[ProviderSettingKeys.Owner] = owner;
        if (repo is not null) settings[ProviderSettingKeys.Repo] = repo;

        return new ProviderConfig
        {
            Id = id,
            Kind = kind,
            ProviderType = providerType,
            DisplayName = id,
            Settings = settings
        };
    }

    private async Task InsertWorkItem(
        Guid id,
        WorkItemTaskType taskType,
        string issueIdentifier = "issue-1",
        string issueProviderConfigId = "github",
        string agentSelector = "kiro,dotnet",
        DateTimeOffset? createdAt = null)
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "",
            InitiatedBy = "test",
            TaskType = taskType,
            AgentSelector = agentSelector,
            TimeoutSeconds = 300,
            RunId = id.ToString()
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = WorkItemStatus.Pending,
            AgentSelector = agentSelector,
            TaskType = taskType,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            TimeoutSeconds = 300,
            PriorityWeight = 0,
            Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default)
        });
        await db.SaveChangesAsync();
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
