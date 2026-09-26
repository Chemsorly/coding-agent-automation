using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// Tests for the per-job API key (issue #3034, Spec 043 Req 8a).
///
/// Verifies that:
/// 1. <see cref="AgentKeyDerivation.DeriveAgentKey"/> produces the value <c>AgentApiKeyAuthHandler</c>
///    accepts (known-answer test).
/// 2. The per-job K8s Secret <c>caa-key-{jobName}</c> is created with the derived key under the
///    <c>agent-api-key</c> data key, owned by the Job.
/// 3. Without a master key the dispatch fails and no Job is created — there is no fallback that
///    would mount the master key into the pod.
/// 4. When the key Secret cannot be created, the Job is deleted and the WorkItem fails.
/// 5. A key Secret left over from an earlier Job with the same name is replaced.
/// </summary>
public sealed class DispatchLifecycleServicePerJobKeyTests : IDisposable
{
    private readonly string _dbName = $"per-job-key-test-{Guid.NewGuid():N}";
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public DispatchLifecycleServicePerJobKeyTests()
    {
        DispatchLifecycleService.TestRetryDelayOverride = TimeSpan.Zero;

        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new TestDbContextFactory(opts);
    }

    public void Dispose()
    {
        DispatchLifecycleService.TestRetryDelayOverride = null;
    }

    // ── DeriveAgentKey ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// DeriveAgentKey is shared by the key Secret (issuer) and AgentApiKeyAuthHandler (verifier).
    /// This test encodes the cross-component invariant that
    /// token issued by dispatch == token accepted by auth handler.
    ///
    /// The assertion uses a pre-computed known-answer value (KAT) to guard against silent
    /// algorithm substitution (e.g. SHA1 instead of SHA256, base64 instead of hex, UTF-16
    /// instead of UTF-8, or case normalisation changing). A tautological self-consistency
    /// check using the same inline algorithm would pass even if the algorithm changed.
    /// KAT computed offline: HMAC-SHA256(key="test-master-key", data="caa-aabbccdd")
    /// → 5a7c1feeee81604f96233c9805c785659b894769b27a19e59fcfb8bcd906f743
    /// </summary>
    [Fact]
    public void DeriveAgentKey_ProducesExpectedHmacSha256()
    {
        const string masterKey = "test-master-key";
        const string jobName = "caa-aabbccdd";

        // Known-answer test (KAT): pre-computed offline to catch algorithm or encoding changes.
        // HMAC-SHA256(UTF8("test-master-key"), UTF8("caa-aabbccdd")) = the value below.
        // If this assertion fails, the algorithm, encoding, or case normalisation changed —
        // which would break compatibility with AgentApiKeyAuthHandler server-side validation.
        const string knownAnswer = "5a7c1feeee81604f96233c9805c785659b894769b27a19e59fcfb8bcd906f743";

        var result = AgentKeyDerivation.DeriveAgentKey(masterKey, jobName);

        result.Should().Be(knownAnswer,
            "DeriveAgentKey must produce the exact pre-computed HMAC-SHA256 hex value; " +
            "a mismatch indicates an algorithm, encoding, or case-normalisation change " +
            "that would break AgentApiKeyAuthHandler server-side validation");
    }

    [Fact]
    public void DeriveAgentKey_DifferentJobs_ProduceDifferentKeys()
    {
        const string masterKey = "test-master-key";
        var key1 = AgentKeyDerivation.DeriveAgentKey(masterKey, "caa-aabbccdd");
        var key2 = AgentKeyDerivation.DeriveAgentKey(masterKey, "caa-eeffgghh");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveAgentKey_Deterministic_SameInputSameOutput()
    {
        var key1 = AgentKeyDerivation.DeriveAgentKey("master", "caa-aabbccdd");
        var key2 = AgentKeyDerivation.DeriveAgentKey("master", "caa-aabbccdd");
        key1.Should().Be(key2);
    }

    // ── Per-job key Secret creation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When AgentApiKeyValue is set, a per-job Secret named caa-key-{jobName} must be created
    /// containing the HMAC-derived key under the "agent-api-key" data key.
    /// </summary>
    [Fact]
    public async Task WhenAgentApiKeyValueIsSet_PerJobKeySecretIsCreated()
    {
        const string masterKey = "super-secret-master";
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "test-uid" } });

        V1Secret? capturedSecret = null;
        k8sMock.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecret = s)
            .Returns(Task.CompletedTask);

        var service = CreateServiceWithApiKey(k8sMock.Object, masterKey);
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets: null);

        // Assert: a Secret was created
        capturedSecret.Should().NotBeNull("per-job key Secret must be created when AgentApiKeyValue is set");

        // Assert: name follows caa-key-{jobName} convention
        var expectedJobName = DispatchLifecycleService.GenerateJobName(entity.Id);
        capturedSecret!.Metadata.Name.Should().Be($"caa-key-{expectedJobName}",
            "per-job key Secret name must follow caa-key-{jobName} convention");

        // Assert: key stored under "agent-api-key"
        capturedSecret.StringData.Should().ContainKey("agent-api-key",
            "the derived key must be stored under the 'agent-api-key' data key");

        // Assert: stored value equals HMAC-SHA256(masterKey, jobName), computed independently of
        // the production helper so a broken helper cannot make the assertion agree with itself.
        var expectedKey = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterKey), Encoding.UTF8.GetBytes(expectedJobName)))
            .ToLowerInvariant();
        capturedSecret.StringData["agent-api-key"].Should().Be(expectedKey,
            "stored value must equal HMAC-SHA256(masterKey, jobName)");

        // Assert: the Job owns the Secret, so Kubernetes deletes the key together with the Job.
        capturedSecret.Metadata.OwnerReferences.Should().ContainSingle(o =>
            o.ApiVersion == "batch/v1" && o.Kind == "Job" && o.Name == expectedJobName && o.Uid == "test-uid");
    }

    /// <summary>
    /// Without a master key no per-job key can be issued. The dispatch must fail before any Job is
    /// created — there is no fallback that would mount the master key into the pod.
    /// </summary>
    [Fact]
    public async Task WhenAgentApiKeyValueIsAbsent_DispatchFailsWithoutCreatingAJob()
    {
        var entity = await SeedPendingWorkItemAsync();

        // No setups: MockBehavior.Strict fails the test on any Kubernetes call.
        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);

        var service = CreateServiceWithApiKey(k8sMock.Object, agentApiKeyValue: ""); // no master key
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets: null);

        k8sMock.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no Job may be created when no agent key can be issued");
        (await ReloadAsync(entity.Id)).Status.Should().Be(WorkItemStatus.Failed);
    }

    /// <summary>
    /// A Job whose key Secret cannot be created could never authenticate. It is deleted and the
    /// WorkItem fails, instead of the pod waiting in CreateContainerConfigError until its deadline.
    /// </summary>
    [Fact]
    public async Task WhenKeySecretCannotBeCreated_JobIsDeletedAndWorkItemFails()
    {
        var entity = await SeedPendingWorkItemAsync();
        var expectedJobName = DispatchLifecycleService.GenerateJobName(entity.Id);

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = expectedJobName, Uid = "test-uid" } });
        k8sMock.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("forbidden")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "")
            });
        k8sMock.Setup(k => k.DeleteJobAsync(expectedJobName, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateServiceWithApiKey(k8sMock.Object, "super-secret-master");
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets: null);

        k8sMock.Verify(k => k.DeleteJobAsync(expectedJobName, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "a Job whose agent key could not be issued must be deleted");
        (await ReloadAsync(entity.Id)).Status.Should().Be(WorkItemStatus.Failed);
    }

    /// <summary>
    /// A key Secret with the same name can only be left over from an earlier Job with the same name
    /// (re-dispatched work item). It is replaced so the new Job owns it; otherwise garbage collection
    /// of the old Job would delete the key under the new pod.
    /// </summary>
    [Fact]
    public async Task WhenKeySecretAlreadyExists_ItIsReplacedAndOwnedByTheNewJob()
    {
        var entity = await SeedPendingWorkItemAsync();
        var expectedJobName = DispatchLifecycleService.GenerateJobName(entity.Id);
        var secretName = $"caa-key-{expectedJobName}";

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = expectedJobName, Uid = "new-job-uid" } });
        k8sMock.SetupSequence(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("exists")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), "")
            })
            .Returns(Task.CompletedTask);
        k8sMock.Setup(k => k.DeleteSecretAsync(secretName, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateServiceWithApiKey(k8sMock.Object, "super-secret-master");
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets: null);

        k8sMock.Verify(k => k.DeleteSecretAsync(secretName, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var secretCreates = k8sMock.Invocations
            .Where(i => i.Method.Name == nameof(IKubernetesJobClient.CreateSecretAsync))
            .Select(i => (V1Secret)i.Arguments[0])
            .ToList();
        secretCreates.Should().HaveCount(2);
        secretCreates[^1].Metadata.OwnerReferences.Should().ContainSingle(o => o.Uid == "new-job-uid");
        (await ReloadAsync(entity.Id)).Status.Should().Be(WorkItemStatus.Dispatched);
    }

    /// <summary>
    /// When both per-job key Secret and project secrets are needed, both are created independently.
    /// The per-job key Secret name follows caa-key-{jobName}; the project-secrets Secret follows caa-secrets-{shortId}.
    /// </summary>
    [Fact]
    public async Task WhenBothKeyAndProjectSecretNeeded_BothSecretsAreCreated()
    {
        const string masterKey = "super-secret-master";
        var entity = await SeedPendingWorkItemAsync();
        var projectSecrets = new Dictionary<string, string> { ["DB_PASS"] = "secret-value" };

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8sMock.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "test-uid-2" } });

        var capturedSecrets = new List<V1Secret>();
        k8sMock.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => capturedSecrets.Add(s))
            .Returns(Task.CompletedTask);

        var service = CreateServiceWithApiKey(k8sMock.Object, masterKey);
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets);

        capturedSecrets.Should().HaveCount(2, "both per-job key and project-secrets Secrets must be created");

        var expectedJobName = DispatchLifecycleService.GenerateJobName(entity.Id);
        capturedSecrets.Should().Contain(s => s.Metadata.Name == $"caa-key-{expectedJobName}",
            "per-job key Secret must be one of the created Secrets");
        // TODO: Also assert that the second Secret follows the project-secrets naming convention
        // (e.g. capturedSecrets.Should().Contain(s => s.Metadata.Name.StartsWith("caa-secrets-")))
        // and that it contains the expected project secret data (e.g. "DB_PASS" key). Without this,
        // the test would pass even if two per-job key Secrets were created with duplicate names,
        // or if the project-secrets Secret was malformed or missing its data keys.
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private DispatchLifecycleService CreateServiceWithApiKey(IKubernetesJobClient k8sClient, string agentApiKeyValue)
    {
        var transitionSvc = new WorkItemTransitionService(
            _dbFactory,
            Mock.Of<ILogger<WorkItemTransitionService>>());
        var opts = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            OrchestratorUrl = "http://test",
            AgentApiKeySecretName = "agent-key",
            AgentApiKeyValue = agentApiKeyValue,
            AgentServiceAccountName = "sa",
            KiroPvcPool = ["pvc-0"]
        };
        return new DispatchLifecycleService(k8sClient, transitionSvc, opts);
    }

    private async Task<WorkItemEntity> ReloadAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
    }

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

    private async Task RunDispatchAsync(
        WorkItemEntity entity,
        DispatchLifecycleService service,
        Dictionary<string, string>? projectSecrets)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var projection = new PendingWorkItemProjection
        {
            Id = entity.Id,
            AgentSelector = entity.AgentSelector ?? "kiro,dotnet",
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType
        };

        var template = new JobTemplate
        {
            Labels = "kiro,dotnet",
            Image = "test-image:latest",
            ProviderType = "kiro",
            MaxConcurrent = 5
        };

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
