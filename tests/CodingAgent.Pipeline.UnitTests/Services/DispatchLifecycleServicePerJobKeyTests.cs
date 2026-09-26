using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the per-job API key derivation and Secret creation introduced in issue #3034.
///
/// Verifies that:
/// 1. <c>DispatchLifecycleService.DeriveJobKey</c> produces the same value as
///    <c>AgentApiKeyAuthHandler</c> server-side validation.
/// 2. The per-job K8s Secret is created with the derived key when <c>AgentApiKeyValue</c> is set.
/// 3. The per-job Secret name follows the convention <c>caa-key-{jobName}</c>.
/// 4. The per-job Secret key is stored under the <c>agent-api-key</c> data key.
/// 5. No per-job Secret is created when <c>AgentApiKeyValue</c> is absent (degraded mode).
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

    // ── DeriveJobKey ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// DeriveJobKey must produce the same value as the server-side HMAC derivation in
    /// AgentApiKeyAuthHandler. This test encodes the cross-component invariant that
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
    public void DeriveJobKey_ProducesExpectedHmacSha256()
    {
        const string masterKey = "test-master-key";
        const string jobName = "caa-aabbccdd";

        // Known-answer test (KAT): pre-computed offline to catch algorithm or encoding changes.
        // HMAC-SHA256(UTF8("test-master-key"), UTF8("caa-aabbccdd")) = the value below.
        // If this assertion fails, the algorithm, encoding, or case normalisation changed —
        // which would break compatibility with AgentApiKeyAuthHandler server-side validation.
        const string knownAnswer = "5a7c1feeee81604f96233c9805c785659b894769b27a19e59fcfb8bcd906f743";

        var result = DispatchLifecycleService.DeriveJobKey(masterKey, jobName);

        result.Should().Be(knownAnswer,
            "DeriveJobKey must produce the exact pre-computed HMAC-SHA256 hex value; " +
            "a mismatch indicates an algorithm, encoding, or case-normalisation change " +
            "that would break AgentApiKeyAuthHandler server-side validation");
    }

    [Fact]
    public void DeriveJobKey_DifferentJobs_ProduceDifferentKeys()
    {
        const string masterKey = "test-master-key";
        var key1 = DispatchLifecycleService.DeriveJobKey(masterKey, "caa-aabbccdd");
        var key2 = DispatchLifecycleService.DeriveJobKey(masterKey, "caa-eeffgghh");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveJobKey_Deterministic_SameInputSameOutput()
    {
        var key1 = DispatchLifecycleService.DeriveJobKey("master", "caa-aabbccdd");
        var key2 = DispatchLifecycleService.DeriveJobKey("master", "caa-aabbccdd");
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

        // Assert: stored value equals HMAC-SHA256(masterKey, jobName)
        // TODO: This assertion calls DeriveJobKey (the function under test) to compute the expected
        // value, creating a circular dependency — if DeriveJobKey is broken, both the production
        // path and the assertion compute the same wrong value and the test passes. Replace with an
        // inline HMACSHA256 computation (as in DeriveJobKey_ProducesExpectedHmacSha256) or a
        // known-answer constant to break the circularity.
        var expectedKey = DispatchLifecycleService.DeriveJobKey(masterKey, expectedJobName);
        capturedSecret.StringData["agent-api-key"].Should().Be(expectedKey,
            "stored value must equal HMAC-SHA256(masterKey, jobName)");

        // TODO: Assert that capturedSecret.Metadata.OwnerReferences is non-null and contains an
        // OwnerReference pointing to the Job (ApiVersion=batch/v1, Kind=Job, Name=expectedJobName,
        // Uid=test-uid). The OwnerReference is the auto-GC mechanism; a regression removing it
        // (e.g. GetJobUidAsync returning null) would not be caught by the current assertions.
    }

    /// <summary>
    /// When AgentApiKeyValue is absent, no per-job key Secret is created (degraded mode).
    /// The pod falls back to the legacy master-key-mount path.
    /// </summary>
    [Fact]
    public async Task WhenAgentApiKeyValueIsAbsent_NoPerJobKeySecretCreated()
    {
        var entity = await SeedPendingWorkItemAsync();

        var k8sMock = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // ReadJobAsync and CreateSecretAsync must NOT be called — MockBehavior.Strict enforces this.

        var service = CreateServiceWithApiKey(k8sMock.Object, agentApiKeyValue: ""); // no master key
        using var _ = service;

        await RunDispatchAsync(entity, service, projectSecrets: null);

        k8sMock.Verify(
            k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no Secret must be created when AgentApiKeyValue is absent");
        // TODO: Also assert that the V1Job passed to CreateJobAsync has DerivedKeySecretName absent
        // in its env vars (i.e. AGENT_API_KEY is not sourced via SecretKeyRef). Without this check,
        // a regression that sets DerivedKeySecretName=null but still omits the master-key mount would
        // not be caught, silently producing a pod that can never start (CreateContainerConfigError
        // because the SecretKeyRef references a non-existent Secret).
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
