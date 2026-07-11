using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Autorest;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="KubernetesJobCleanup"/>.
/// Validates DB lookup + K8s delete + 404 handling + generic exception handling.
/// </summary>
public sealed class KubernetesJobCleanupTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockJobClient;
    private readonly Mock<ILogger> _mockLogger;
    private readonly KubernetesJobCleanup _sut;

    private const string K8sNamespace = "coding-agent";

    public KubernetesJobCleanupTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"KubernetesJobCleanupTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _mockJobClient = new Mock<IKubernetesJobClient>();
        _mockLogger = new Mock<ILogger>();

        _sut = new KubernetesJobCleanup(_dbFactory, _mockJobClient.Object, K8sNamespace, _mockLogger.Object);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_ValidRunIdWithK8sJobName_DeletesJob()
    {
        // Arrange
        var runId = Guid.NewGuid();
        const string jobName = "caa-test-job";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#100",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = jobName
            });
            await db.SaveChangesAsync();
        }

        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        _mockJobClient.Verify(c => c.DeleteJobAsync(jobName, K8sNamespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_NoK8sJobName_DoesNotCallDelete()
    {
        // Arrange: WorkItem without K8sJobName
        var runId = Guid.NewGuid();

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#101",
                IssueProviderConfigId = "ip-2",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = null
            });
            await db.SaveChangesAsync();
        }

        // Act
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_InvalidRunId_NoOp()
    {
        // Act — "not-a-guid" is not parseable
        await _sut.TryDeleteJobForRunAsync("not-a-guid", CancellationToken.None);

        // Assert: no DB or K8s calls
        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_RunIdNotInDb_NoOp()
    {
        // Arrange: no WorkItem seeded for this ID
        var runId = Guid.NewGuid();

        // Act
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        // Assert
        _mockJobClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_DeleteReturns404_GracefullyHandled()
    {
        // Arrange
        var runId = Guid.NewGuid();
        const string jobName = "caa-already-gone";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#102",
                IssueProviderConfigId = "ip-3",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = jobName
            });
            await db.SaveChangesAsync();
        }

        var response404 = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException { Response = new HttpResponseMessageWrapper(response404, "") });

        // Act — should not throw
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        // Assert: no Warning log (only Debug for 404)
        // TODO: This Verify assertion may be tautological — Serilog dispatches Warning(Exception, string, T)
        // to a generic overload that Moq cannot intercept via the (Exception, string, object[]) signature.
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task TryDeleteJobForRunAsync_DeleteThrowsOtherException_GracefullyHandled()
    {
        // Arrange
        var runId = Guid.NewGuid();
        const string jobName = "caa-error-job";

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runId,
                IssueIdentifier = "owner/repo#103",
                IssueProviderConfigId = "ip-4",
                Status = WorkItemStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation,
                K8sJobName = jobName
            });
            await db.SaveChangesAsync();
        }

        _mockJobClient
            .Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("K8s API timeout"));

        // Act — should not throw
        await _sut.TryDeleteJobForRunAsync(runId.ToString(), CancellationToken.None);

        // Assert: Warning log emitted for non-404 exception (generic overload)
        // TODO: This assertion only verifies DeleteJobAsync was called but does not verify the warning was
        // logged or that the exception was swallowed. Consider asserting the method completes without throwing.
        _mockJobClient.Verify(c => c.DeleteJobAsync(jobName, K8sNamespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: Add test for OperationCanceledException propagation. The implementation has
    // `catch (Exception ex) when (ex is not OperationCanceledException)` which allows cancellation
    // to propagate. If this filter were accidentally removed (swallowing cancellation), no test
    // would detect the regression. Swallowed cancellation can cause shutdown hangs and resource leaks.

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }
}
