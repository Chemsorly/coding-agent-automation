using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DispatchLifecycleService.CreateJobSecretAsync"/> and
/// <see cref="DispatchLifecycleService.GetJobUidAsync"/> via
/// <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>.
///
/// <para>
/// Issue #2666: <c>GetJobUidAsync</c> silently returned <see langword="null"/> on any exception,
/// and <c>CreateJobSecretAsync</c> used <c>?? ""</c>, producing an invalid Kubernetes
/// <see cref="V1OwnerReference"/> with an empty UID. Kubernetes ignores such references,
/// causing the Secret to leak permanently when the Job is deleted.
/// </para>
///
/// <para>
/// Fix: <c>GetJobUidAsync</c> now retries up to 3 times with bounded backoff and logs a warning
/// on exhaustion. <c>CreateJobSecretAsync</c> omits <c>OwnerReferences</c> entirely when the UID
/// is unavailable, rather than writing an empty string.
/// </para>
/// </summary>
public sealed class DispatchLifecycleServiceSecretTests : IDisposable
{
    private readonly string _dbName = $"secret-test-{Guid.NewGuid():N}";
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    // Test secrets injected via prepareVariant — bypasses LoadProjectSecretsAsync.
    private static readonly Dictionary<string, string> TestSecrets = new()
    {
        ["API_KEY"] = "test-api-key",
        ["DB_PASS"] = "test-db-pass"
    };

    public DispatchLifecycleServiceSecretTests()
    {
        // Zero out retry delays for all tests in this class to prevent real wall-clock waits.
        // Mirrors the pattern established by ResiliencePipelineFactory.TestRetryDelayOverride.
        DispatchLifecycleService.TestRetryDelayOverride = TimeSpan.Zero;

        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new TestDbContextFactory(opts);
    }

    public void Dispose()
    {
        // Reset the override so other test classes are not affected.
        DispatchLifecycleService.TestRetryDelayOverride = null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private DispatchLifecycleService CreateService(IKubernetesJobClient k8sClient)
    {
        var transitionSvc = new WorkItemTransitionService(
            _dbFactory,
            Mock.Of<ILogger<WorkItemTransitionService>>());
        var opts = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            OrchestratorUrl = "http://test",
            AgentApiKeySecretName = "agent-key",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "sa",
            KiroPvcPool = ["pvc-0"]
        };
        return new DispatchLifecycleService(k8sClient, transitionSvc, opts);
    }

    /// <summary>
    /// Seeds a <see cref="WorkItemEntity"/> with <c>Status = Pending</c> (the default
    /// <see cref="DispatchLifecycleContext.ExpectedInitialStatus"/>).
    /// </summary>
    private async Task<WorkItemEntity> SeedPendingWorkItemAsync()
    {
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            Status = WorkItemStatus.Pending,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}"
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private static JobTemplate BuildTemplate() =>
        new JobTemplate
        {
            Labels = "kiro,dotnet",
            Image = "test-image:latest",
            ProviderType = "kiro",
            MaxConcurrent = 5
        };

    private static PendingWorkItemProjection BuildProjection(WorkItemEntity entity) =>
        new PendingWorkItemProjection
        {
            Id = entity.Id,
            AgentSelector = entity.AgentSelector ?? "kiro,dotnet",
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType
        };

    /// <summary>
    /// Runs the dispatch lifecycle with <paramref name="projectSecrets"/> injected via
    /// <c>prepareVariant</c>. Returns the <see cref="V1Secret"/> passed to
    /// <c>CreateSecretAsync</c>, or <see langword="null"/> if <c>CreateSecretAsync</c> was never called.
    /// </summary>
    private async Task<V1Secret?> RunDispatchAndCaptureSecretAsync(
        WorkItemEntity entity,
        IKubernetesJobClient k8sClient,
        Dictionary<string, string>? projectSecrets)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var service = CreateService(k8sClient);
        using var _ = service; // ensure Dispose is called

        var projection = BuildProjection(entity);
        var template = BuildTemplate();
        var ctx = new DispatchLifecycleContext(
            db,
            projection,
            template,
            IsKiroAgent: true,
            AvailablePvcs: ["pvc-0"],
            ConcurrencyBySelector: new Dictionary<string, int>(),
            LogPrefix: "test ");

        await service.ExecuteDispatchLifecycleAsync(
            ctx,
            prepareVariant: _ => Task.FromResult((shouldContinue: true, projectSecrets)),
            onDispatchSuccess: null,
            CancellationToken.None);

        // TODO [WARNING]: This method always returns null — the return type Task<V1Secret?> is
        // misleading. Callers capture the secret via the Moq callback side-effect instead of
        // using the return value. Change the return type to Task and rename (e.g. RunDispatchAsync)
        // to match the actual contract. See: TestQualityReviewer / DotNetSpecialist [WARNING].
        return null; // caller checks the mock capture
    }

    // ── Test 1: retry succeeds on second attempt ────────────────────────────────────────────────

    /// <summary>
    /// When <c>ReadJobAsync</c> throws on the first attempt but succeeds on the second,
    /// <c>GetJobUidAsync</c> retries and the secret is created with the correct <c>OwnerReference.Uid</c>.
    /// </summary>
    [Fact]
    public async Task GetJobUidAsync_RetriesOnException_AndReturnsUidOnSecondAttempt()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var callCount = 0;
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpOperationException("not yet available")
                    {
                        Response = new HttpResponseMessageWrapper(
                            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "")
                    };
                return Task.FromResult(new V1Job
                {
                    Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "retry-uid-value" }
                });
            });

        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAndCaptureSecretAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: ReadJobAsync called exactly twice (one failure, one success)
        // TODO [WARNING]: Times.Exactly(2) is correct for this scenario but could pass for the
        // wrong reason if the mock is called twice for an unrelated reason. A complementary
        // assertion that callCount > 1 before verifying the UID would make the retry intent
        // explicit. See: TestQualityReviewer [WARNING] on Test 1 assertion robustness.
        k8sMock.Verify(
            k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "ReadJobAsync must be called twice: once throwing, once returning the UID");

        // Assert: secret created with the correct OwnerReference UID
        capturedSecret.Should().NotBeNull("CreateSecretAsync must have been called");
        capturedSecret!.Metadata.OwnerReferences.Should().HaveCount(1,
            "exactly one OwnerReference must be set when the UID is successfully obtained");
        capturedSecret.Metadata.OwnerReferences![0].Uid.Should().Be("retry-uid-value",
            "the UID from the successful retry attempt must be used");
    }

    // ── Test 2: retry exhaustion → no OwnerReference, no empty UID ─────────────────────────────

    /// <summary>
    /// Primary AC test (issue #2666): when <c>ReadJobAsync</c> throws on all 3 attempts,
    /// the secret is created <em>without</em> an <c>OwnerReference</c> — never with an empty UID.
    /// </summary>
    [Fact]
    public async Task GetJobUidAsync_ExhaustsRetries_SecretCreatedWithoutOwnerReference_NeverWithEmptyUid()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("Job not found")
            {
                Response = new HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "")
            });

        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAndCaptureSecretAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: all 3 attempts were made
        // TODO [WARNING]: Times.Exactly(3) is a hard-coded constant not tied to the retry
        // configuration. If the retryDelays array in GetJobUidAsync grows (e.g. 3 delays → 4
        // attempts), this test will fail with a confusing mismatch rather than an obvious
        // config-mismatch error. Consider extracting the expected attempt count to a named
        // constant or computing it from a shared source. See: TestQualityReviewer [WARNING].
        k8sMock.Verify(
            k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "ReadJobAsync must be called 3 times — one initial attempt plus two retries");

        // Assert: the project-secrets Secret was still created (degraded mode — job can run without
        // OwnerReference). The Job's agent key Secret is created too, from the same UID read.
        k8sMock.Verify(
            k => k.CreateSecretAsync(
                It.Is<V1Secret>(s => s.Metadata.Name.StartsWith("caa-secrets-")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "CreateSecretAsync must still be called even when UID is unavailable");

        capturedSecret.Should().NotBeNull();

        // The core fix: OwnerReferences must be null (or empty), never containing an empty UID.
        var ownerRefs = capturedSecret!.Metadata.OwnerReferences;
        var hasEmptyUid = ownerRefs?.Any(r => string.IsNullOrEmpty(r.Uid)) ?? false;
        hasEmptyUid.Should().BeFalse("OwnerReference.Uid must never be set to an empty string");

        var isEmpty = ownerRefs is null || ownerRefs.Count == 0;
        isEmpty.Should().BeTrue(
            "when the UID is unavailable, OwnerReferences must be null/empty (not contain an invalid entry)");
    }

    // ── Test 3: successful ReadJobAsync returns null Uid → no OwnerReference ────────────────────

    /// <summary>
    /// When <c>ReadJobAsync</c> succeeds but returns a job without a <c>Metadata.Uid</c>
    /// (e.g. a malformed response or the E2E fake), the secret is created without an
    /// <c>OwnerReference</c>. No retry occurs — a successful response is not retried.
    /// </summary>
    [Fact]
    public async Task GetJobUidAsync_ReturnsNullUid_SecretCreatedWithoutOwnerReference_ReadJobCalledOnce()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ReadJobAsync succeeds but returns a job with no Uid set (Metadata.Uid is null).
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test" } });

        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAndCaptureSecretAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: ReadJobAsync called exactly once — null response is not retried
        k8sMock.Verify(
            k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a null-UID success response must not trigger a retry");

        // Assert: secret created without OwnerReference
        capturedSecret.Should().NotBeNull();
        var ownerRefs = capturedSecret!.Metadata.OwnerReferences;
        var hasEmptyUid = ownerRefs?.Any(r => string.IsNullOrEmpty(r.Uid)) ?? false;
        hasEmptyUid.Should().BeFalse("OwnerReference.Uid must never be set to an empty string");
        (ownerRefs is null || ownerRefs.Count == 0).Should().BeTrue(
            "a null Metadata.Uid must result in no OwnerReference");
    }

    // ── Test 4: happy path — valid UID → OwnerReference set, ReadJobAsync called once ──────────

    /// <summary>
    /// Happy path: <c>ReadJobAsync</c> returns a valid <c>Uid</c> on the first attempt.
    /// The secret is created with the correct <c>OwnerReference</c>, and <c>ReadJobAsync</c>
    /// is called exactly once (no spurious retries).
    /// </summary>
    [Fact]
    public async Task GetJobUidAsync_ReturnsValidUid_SecretCreatedWithCorrectOwnerReference_ReadJobCalledOnce()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();
        const string expectedUid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job
            {
                Metadata = new V1ObjectMeta { Name = "caa-test", Uid = expectedUid }
            });

        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAndCaptureSecretAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: ReadJobAsync called exactly once — no spurious retries on clean success
        k8sMock.Verify(
            k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "ReadJobAsync must be called exactly once when it succeeds on the first attempt");

        // Assert: secret created with correct OwnerReference
        capturedSecret.Should().NotBeNull();
        capturedSecret!.Metadata.OwnerReferences.Should().HaveCount(1,
            "exactly one OwnerReference must be present when the UID is available");
        var ownerRef = capturedSecret.Metadata.OwnerReferences![0];
        ownerRef.Uid.Should().Be(expectedUid, "the UID from ReadJobAsync must be set on the OwnerReference");
        ownerRef.Kind.Should().Be("Job");
        ownerRef.ApiVersion.Should().Be("batch/v1");
    }

    // ── Test 5: null projectSecrets → no project-secrets Secret ────────────────────────────────

    /// <summary>
    /// When <c>prepareVariant</c> returns <see langword="null"/> project secrets, no project-secrets
    /// Secret is created. The Job's agent key Secret is still created — every Job gets one — and the
    /// Job UID is read only once for it.
    /// </summary>
    [Fact]
    public async Task WhenProjectSecretsIsNull_ProjectSecretIsNeverCreated()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "job-uid" } });
        var createdSecrets = new List<V1Secret>();
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => createdSecrets.Add(s))
            .Returns(Task.CompletedTask);

        // Act: pass null projectSecrets → CreateJobSecretIfNeededAsync returns early
        await RunDispatchAndCaptureSecretAsync(entity, k8sMock.Object, projectSecrets: null);

        // Assert: only the agent key Secret was created
        createdSecrets.Should().ContainSingle()
            .Which.Metadata.Name.Should().StartWith("caa-key-", "the only Secret must be the Job's agent key");
        k8sMock.Verify(
            k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the Job UID is read once, for the agent key Secret");
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────────────────────

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;

        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;

        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_opts));
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            // Disable concurrency tokens and filtered indexes — not supported by in-memory provider.
            foreach (var et in mb.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in mb.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }
}
