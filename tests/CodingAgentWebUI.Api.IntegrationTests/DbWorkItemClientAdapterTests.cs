using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="DbWorkItemClientAdapter"/>.
/// Covers the only real method (<see cref="DbWorkItemClientAdapter.GetK8sJobNameAsync"/>)
/// and the constructor guard. The NotSupportedException stubs are excluded from coverage
/// via [ExcludeFromCodeCoverage] on the class — they are by design never invoked.
/// </summary>
public sealed class DbWorkItemClientAdapterTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public DbWorkItemClientAdapterTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DbAdapterTest-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureDeleted();
    }

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new DbWorkItemClientAdapter(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("dbFactory");
    }

    [Fact]
    public void Constructor_ValidFactory_DoesNotThrow()
    {
        var act = () => new DbWorkItemClientAdapter(_dbFactory);
        act.Should().NotThrow();
    }

    // ── GetK8sJobNameAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetK8sJobNameAsync_ExistingItem_ReturnsJobName()
    {
        var id = Guid.NewGuid();
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.WorkItems.Add(CreateWorkItem(id, k8sJobName: "caa-agent-abc123"));
            await db.SaveChangesAsync();
        }

        var sut = new DbWorkItemClientAdapter(_dbFactory);
        var result = await sut.GetK8sJobNameAsync(id);

        result.Should().Be("caa-agent-abc123");
    }

    [Fact]
    public async Task GetK8sJobNameAsync_NullJobName_ReturnsNull()
    {
        var id = Guid.NewGuid();
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.WorkItems.Add(CreateWorkItem(id, k8sJobName: null));
            await db.SaveChangesAsync();
        }

        var sut = new DbWorkItemClientAdapter(_dbFactory);
        var result = await sut.GetK8sJobNameAsync(id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetK8sJobNameAsync_EmptyJobName_ReturnsNull()
    {
        var id = Guid.NewGuid();
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.WorkItems.Add(CreateWorkItem(id, k8sJobName: ""));
            await db.SaveChangesAsync();
        }

        var sut = new DbWorkItemClientAdapter(_dbFactory);
        var result = await sut.GetK8sJobNameAsync(id);

        result.Should().BeNull("empty string is treated as not set");
    }

    [Fact]
    public async Task GetK8sJobNameAsync_UnknownId_ReturnsNull()
    {
        var sut = new DbWorkItemClientAdapter(_dbFactory);
        var result = await sut.GetK8sJobNameAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static WorkItemEntity CreateWorkItem(Guid id, string? k8sJobName) => new()
    {
        Id = id,
        IssueIdentifier = "org/repo#1",
        IssueProviderConfigId = "ip-1",
        Status = WorkItemStatus.Running,
        AgentSelector = "",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow,
        TimeoutSeconds = 3600,
        Payload = "{}",
        K8sJobName = k8sJobName
    };

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entity.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
