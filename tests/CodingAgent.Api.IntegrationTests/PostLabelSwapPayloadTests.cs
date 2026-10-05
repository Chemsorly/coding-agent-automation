using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Direct tests for the review branch of <see cref="WorkItemDispatchEndpoints.PostLabelSwap"/>, which
/// reads <c>RepoProviderConfigId</c> from the stored <see cref="JobDistributionRequest"/> payload.
/// <para>
/// That read must go through <see cref="WorkItemPayload.TryDeserialize"/> like the other payload
/// readers (<c>GetPendingWorkItems</c>, <c>GetActiveWorkItems</c>, <c>GetAssignment</c>): a malformed
/// payload falls back to the issue provider config instead of throwing, and a legacy PascalCase payload
/// is parsed with <see cref="PipelineJsonOptions.Lenient"/>. #2787 made that change, and the concurrent
/// dispatch-extraction refactor (#2756) reverted it to a raw <c>JsonSerializer.Deserialize</c> call
/// with <see cref="PipelineJsonOptions.Default"/>.
/// </para>
/// The method is called directly with a mocked <see cref="ILabelSwapService"/> so the provider config
/// id handed to the swap can be asserted.
/// </summary>
public sealed class PostLabelSwapPayloadTests
{
    private const string IssueProviderConfigId = "ip-1";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"PostLabelSwapPayload-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedReviewAsync(
        DbContextOptions<PipelineDbContext> opts, string? payload)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "owner/repo#42",
            IssueProviderConfigId = IssueProviderConfigId,
            Status = WorkItemStatus.Dispatched,
            TaskType = WorkItemTaskType.Review,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var ctx = new TestDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static Task<IResult> PostLabelSwapAsync(
        DbContextOptions<PipelineDbContext> opts, Guid id, ILabelSwapService labelSwapService)
        => WorkItemDispatchEndpoints.PostLabelSwap(
            id,
            new LabelSwapRequest { Label = "agent:in-progress" },
            new TestDbContextFactory(opts),
            labelSwapService,
            CancellationToken.None);

    private static void VerifySwappedWith(Mock<ILabelSwapService> swap, Guid id, string providerConfigId)
        => swap.Verify(s => s.SwapLabelWithRetryAsync(
                id,
                new ProviderConfigId(providerConfigId),
                new IssueIdentifier("owner/repo#42"),
                LabelTargetKind.PullRequest,
                It.IsAny<CancellationToken>()),
            Times.Once);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{not-valid-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task PostLabelSwap_ReviewWithUnreadablePayload_FallsBackToIssueProviderConfigId(string payload)
    {
        var opts = CreateDbOptions();
        var item = await SeedReviewAsync(opts, payload);
        var swap = new Mock<ILabelSwapService>();

        var act = () => PostLabelSwapAsync(opts, item.Id, swap.Object);

        var result = (await act.Should().NotThrowAsync(
            "a payload that cannot be read must not fail the label swap; the other payload readers " +
            "treat it as absent")).Subject;
        result.Should().BeOfType<Ok>();
        VerifySwappedWith(swap, item.Id, IssueProviderConfigId);
    }

    [Fact]
    public async Task PostLabelSwap_ReviewWithPascalCasePayload_UsesRepoProviderConfigId()
    {
        // A legacy payload written before camelCase serialization was enforced. Case-sensitive
        // PipelineJsonOptions.Default matches none of these names, so the required properties look
        // missing and deserialization throws.
        const string payload = """
            {
                "IssueIdentifier": "owner/repo#42",
                "IssueProviderConfigId": "ip-1",
                "RepoProviderConfigId": "repo-pascal",
                "InitiatedBy": "legacy-loop",
                "TaskType": "Review",
                "AgentSelector": "dotnet",
                "TimeoutSeconds": 3600
            }
            """;
        var opts = CreateDbOptions();
        var item = await SeedReviewAsync(opts, payload);
        var swap = new Mock<ILabelSwapService>();

        var result = await PostLabelSwapAsync(opts, item.Id, swap.Object);

        result.Should().BeOfType<Ok>();
        VerifySwappedWith(swap, item.Id, "repo-pascal");
    }

    [Fact]
    public async Task PostLabelSwap_ReviewWithCamelCasePayload_UsesRepoProviderConfigId()
    {
        var payload = JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#42"),
            IssueProviderConfigId = IssueProviderConfigId,
            RepoProviderConfigId = "repo-camel",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        }, PipelineJsonOptions.Default);
        var opts = CreateDbOptions();
        var item = await SeedReviewAsync(opts, payload);
        var swap = new Mock<ILabelSwapService>();

        var result = await PostLabelSwapAsync(opts, item.Id, swap.Object);

        result.Should().BeOfType<Ok>();
        VerifySwappedWith(swap, item.Id, "repo-camel");
    }

    // ── Test infrastructure ───────────────────────────────────────────────────

    private sealed class TestDbContext : PipelineDbContext
    {
        public TestDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Disable RowVersion concurrency token and partial indexes — not supported by InMemory provider
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexes = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var idx in indexes)
                    entityType.RemoveIndex(idx);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
