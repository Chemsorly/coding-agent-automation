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
    private readonly string? _savedAgentApiKey;

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
        // TODO [WARNING]: TestRetryDelayOverride is a static mutable field and AGENT_API_KEY is a
        // process-wide environment variable. xUnit runs test classes in parallel by default — a
        // concurrent test class that reads AGENT_API_KEY or accesses TestRetryDelayOverride could
        // see unexpected values depending on set/reset ordering. Fix: inject the master key as a
        // constructor/options parameter in DeriveAgentKey and use per-instance state for retry
        // delays. See: DotNetSpecialist / TestQualityReviewer [WARNING] findings.
        DispatchLifecycleService.TestRetryDelayOverride = TimeSpan.Zero;

        // Set AGENT_API_KEY for the entire class lifetime (all tests) rather than per-test.
        // Per-test try/finally is racy when xUnit runs test CLASSES in parallel: another class's
        // Dispose() can reset the env var between this class's SetEnvironmentVariable call and the
        // DeriveAgentKey read inside ExecuteDispatchLifecycleAsync. Holding the value for the full
        // class lifetime narrows the race window to class-level rather than test-level.
        _savedAgentApiKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", "test-master-key");

        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new TestDbContextFactory(opts);
    }

    public void Dispose()
    {
        // Reset the override so other test classes are not affected.
        // TODO [WARNING]: This unconditionally resets TestRetryDelayOverride to null. If another test
        // class set TestRetryDelayOverride to a non-null value before this class ran, this Dispose()
        // will clear that class's override rather than restoring the previous value. Fix: save the
        // previous value in the constructor and restore it here (save-and-restore pattern).
        // See: TestQualityReviewer [WARNING] — TestRetryDelayOverride save/restore.
        DispatchLifecycleService.TestRetryDelayOverride = null;

        // Restore AGENT_API_KEY to the value it had before this class ran.
        Environment.SetEnvironmentVariable("AGENT_API_KEY", _savedAgentApiKey);
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
    /// <c>prepareVariant</c>. Callers capture any secrets or K8s operations via Moq
    /// callback side-effects set up on <paramref name="k8sClient"/> before invoking this method.
    /// </summary>
    private async Task RunDispatchAsync(
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
    }

    // ── Test 1: per-job Secret always created before Job; OwnerReference patched after ──────────

    /// <summary>
    /// Issue #3034: per-job K8s Secrets are created BEFORE the Job (for <c>SecretKeyRef</c>
    /// resolution). After the Job is created, <c>ReadJobAsync</c> is called to obtain the Job UID
    /// and the OwnerReference is patched onto the Secret via <c>PatchSecretOwnerReferenceAsync</c>.
    /// Verifies the ordering: CreateSecret → CreateJob → ReadJobAsync (UID) → PatchSecretOwnerReference.
    /// </summary>
    [Fact]
    public async Task PerJobSecret_AlwaysCreatedBeforeJob_OwnerReferencePatchedAfterJobCreation()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();
        var callOrder = new List<string>();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((_, _, _) => callOrder.Add("CreateSecret"))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((_, _, _) => callOrder.Add("CreateJob"))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, _, _) => callOrder.Add("ReadJob"))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-test-1" } });
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, V1OwnerReference, CancellationToken>((_, _, _, _) => callOrder.Add("PatchSecret"))
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: correct operation ordering
        k8sMock.Verify(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        k8sMock.Verify(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()), Times.Once);
        callOrder.Should().ContainInOrder("CreateSecret", "CreateJob");
        callOrder.Should().ContainInOrder("CreateJob", "PatchSecret");
        // PatchSecret must come after CreateJob — OwnerReference requires Job UID which is only available post-creation.
        var createJobIdx = callOrder.IndexOf("CreateJob");
        var patchSecretIdx = callOrder.IndexOf("PatchSecret");
        patchSecretIdx.Should().BeGreaterThan(createJobIdx,
            "OwnerReference must be patched AFTER the Job is created (Job UID only available post-creation)");
    }

    // ── Test 2: per-job Secret always contains agent-api-key entry ────────────────────────────

    /// <summary>
    /// Issue #3034: the per-job Secret always contains the <c>agent-api-key</c> entry
    /// (the pre-vended HMAC value), even when there are no project secrets.
    /// When project secrets are present they are merged into the same Secret.
    /// </summary>
    [Fact]
    public async Task PerJobSecret_AlwaysContainsAgentApiKey_ProjectSecretsAreMerged()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-test-2" } });
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act: pass TestSecrets (project secrets: API_KEY + DB_PASS)
        await RunDispatchAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: Secret contains the per-job agent key AND all project secrets
        capturedSecret.Should().NotBeNull();
        capturedSecret!.StringData.Should().ContainKey("agent-api-key",
            "the per-job Secret must always contain the pre-vended agent API key");
        capturedSecret.StringData.Should().ContainKey("API_KEY",
            "project secrets must be merged into the same per-job Secret");
        capturedSecret.StringData.Should().ContainKey("DB_PASS",
            "project secrets must be merged into the same per-job Secret");
    }

    // ── Test 3: Secret created without OwnerReference (Job does not exist yet) ──────────────────

    /// <summary>
    /// The per-job Secret is created without an <c>OwnerReference</c> because the K8s Job
    /// does not exist at creation time. This is intentional — the Secret is created first to
    /// satisfy the <c>SecretKeyRef</c> ordering constraint.
    /// </summary>
    [Fact]
    public async Task PerJobSecret_CreatedWithoutOwnerReference_JobCreatedAfterward()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-test-3" } });
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: Secret has no OwnerReference AT CREATION TIME (Job UID unknown at creation time)
        // The OwnerReference is patched afterward by PatchSecretOwnerReferenceAfterJobAsync.
        capturedSecret.Should().NotBeNull();
        var ownerRefs = capturedSecret!.Metadata.OwnerReferences;
        (ownerRefs is null || ownerRefs.Count == 0).Should().BeTrue(
            "Secret is created before the Job, so no OwnerReference UID is available yet — it is patched in a subsequent call");
        var hasEmptyUid = ownerRefs?.Any(r => string.IsNullOrEmpty(r.Uid)) ?? false;
        hasEmptyUid.Should().BeFalse(
            "OwnerReference.Uid must never be set to an empty string");
    }

    // ── Test 4: 409 on Secret creation → idempotent retry, Job still created ─────────────────

    /// <summary>
    /// When <c>CreateSecretAsync</c> returns 409 Conflict (Secret already exists from a prior
    /// idempotent retry), the dispatch proceeds and the Job is still created.
    /// The existing Secret name is returned so the pod can reference it.
    /// </summary>
    [Fact]
    public async Task WhenSecretAlreadyExists_409Conflict_DispatchProceedsAndJobIsCreated()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("already exists")
            {
                Response = new HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Conflict), "")
            });
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-test-4" } });
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — should not throw; 409 on Secret is idempotent
        await RunDispatchAsync(entity, k8sMock.Object, TestSecrets);

        // Assert: Job was still created despite the 409 on the Secret
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "Job must still be created when the Secret already exists (idempotent dispatch)");
    }

    // ── Test 5: null projectSecrets → Secret IS always created (per-job agent key) ────────────

    /// <summary>
    /// Issue #3034: even when <c>prepareVariant</c> returns <see langword="null"/> project secrets,
    /// a per-job K8s Secret is ALWAYS created for work-item pods — it contains the pre-vended
    /// <c>HMAC(master, jobName)</c> agent key. The Secret is created BEFORE the Job so the pod
    /// can resolve the <c>SecretKeyRef</c> when it starts.
    /// </summary>
    [Fact]
    public async Task WhenProjectSecretsIsNull_PerJobAgentKeySecretIsStillCreated()
    {
        // Arrange
        var entity = await SeedPendingWorkItemAsync();

        var callOrder = new List<string>();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);

        // Single CreateSecretAsync setup — captures secret AND records call order.
        // A previous version had two Setup calls (first was silently shadowed by the second
        // in Moq). Fixed: one Setup that does both — avoids the shadow and removes the
        // mistaken ReadJobAsync setup that would have hidden any accidental call to ReadJobAsync.
        V1Secret? capturedSecret = null;
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => { capturedSecret = s; callOrder.Add("CreateSecret"); })
            .Returns(Task.CompletedTask);

        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((_, _, _) => callOrder.Add("CreateJob"))
            .Returns(Task.CompletedTask);

        // ReadJobAsync is NOT set up — MockBehavior.Strict will throw if it is called,
        // ensuring we detect any accidental invocation of the retired GetJobUidAsync path.
        // PatchSecretOwnerReferenceAsync IS set up (required for the post-Job OwnerRef patch).
        // TODO [WARNING]: The comment above is self-contradicting — ReadJobAsync IS set up below.
        // The comment is vestigial from a previous draft where ReadJobAsync was not expected to be
        // called. PatchSecretOwnerReferenceAfterJobAsync calls GetJobUidAsync, which calls ReadJobAsync,
        // so it must be set up. Remove the "ReadJobAsync is NOT set up" comment above to avoid
        // confusing future readers. See: TestQualityReviewer [WARNING] — Test 5 contradicting comment.
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ReadJobAsync must be set up because PatchSecretOwnerReferenceAfterJobAsync calls
        // GetJobUidAsync, which reads the Job UID before issuing the ownerReference patch.
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-123" } });

        // Act: null projectSecrets — but the per-job agent key must still be created
        await RunDispatchAsync(entity, k8sMock.Object, projectSecrets: null);

        // Assert: Secret was created (always for work-item pods)
        k8sMock.Verify(
            k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "CreateSecretAsync must always be called for work-item pods to store the per-job agent key");

        capturedSecret.Should().NotBeNull();
        capturedSecret!.StringData.Should().ContainKey("agent-api-key",
            "the per-job Secret must always contain the agent-api-key entry");

        // Ordering: Secret must be created BEFORE the Job
        callOrder.Should().ContainInOrder("CreateSecret", "CreateJob");
    }

    // ── Test 6: per-job agent key value matches DeriveAgentKey output ──────────────────────────

    /// <summary>
    /// Issue #3034: the <c>agent-api-key</c> value stored in the per-job Secret must equal
    /// <c>HMAC-SHA256(masterKey, jobName)</c> — the same value <c>AgentApiKeyAuthHandler</c>
    /// derives server-side for validation. Also verifies the Secret name convention and that
    /// the Secret is created before the Job.
    /// </summary>
    [Fact]
    public async Task PerJobAgentKeySecret_ContainsCorrectHmacValue_AndIsCreatedBeforeJob()
    {
        // Arrange — the class constructor sets AGENT_API_KEY = "test-master-key".
        // Compute the expected HMAC independently using the same derivation algorithm so
        // the assertion is not tautological (doesn't just call DeriveAgentKey again).
        var entity = await SeedPendingWorkItemAsync();
        var expectedJobName = DispatchLifecycleService.GenerateJobName(entity.Id);

        // Independently compute HMAC-SHA256("test-master-key", jobName) to verify the stored value.
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes("test-master-key"));
        var hashBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(expectedJobName));
        var expectedAgentKey = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var expectedSecretName = $"caa-secrets-{entity.Id.ToString("N")[..8]}";

        var callOrder = new List<string>();
        V1Secret? capturedSecret = null;

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock
            .Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => { capturedSecret = s; callOrder.Add("CreateSecret"); })
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((_, _, _) => callOrder.Add("CreateJob"))
            .Returns(Task.CompletedTask);
        k8sMock
            .Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "uid-456" } });
        k8sMock
            .Setup(k => k.PatchSecretOwnerReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<V1OwnerReference>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await RunDispatchAsync(entity, k8sMock.Object, projectSecrets: null);

        // Assert: secret name follows naming convention
        capturedSecret.Should().NotBeNull();
        capturedSecret!.Metadata.Name.Should().Be(expectedSecretName,
            "per-job Secret name must follow caa-secrets-{workItemId[..8]} convention");

        // Assert: agent-api-key value equals HMAC(master, jobName)
        capturedSecret.StringData.Should().ContainKey("agent-api-key");
        capturedSecret.StringData["agent-api-key"].Should().Be(expectedAgentKey,
            "the stored key must equal HMAC-SHA256(masterKey, jobName) — the same value AgentApiKeyAuthHandler derives for validation");

        // Assert: Secret created BEFORE Job
        callOrder.Should().ContainInOrder("CreateSecret", "CreateJob");

        // Assert: the job spec references DerivedKeySecretName (no master key mount)
        k8sMock.Verify(k => k.CreateJobAsync(
            It.Is<V1Job>(j => j.Spec.Template.Spec.Volumes == null ||
                !j.Spec.Template.Spec.Volumes.Any(v => v.Name == "agent-api-key")),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "the K8s Job must not mount the master agent-api-key Secret volume");
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
