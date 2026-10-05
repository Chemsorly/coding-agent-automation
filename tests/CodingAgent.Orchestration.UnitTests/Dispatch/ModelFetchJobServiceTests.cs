using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using k8s.Models;
using Serilog;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ModelFetchJobService.FetchModelsAsync"/>.
/// Verifies the 5 distinct exit paths using the PollTimeoutSecondsOverride
/// and PollIntervalMs injection hooks.
/// </summary>
public sealed class ModelFetchJobServiceTests
{
    private readonly Mock<IKubernetesJobClient> _kubeClient = new();
    private readonly Mock<IPipelineConfigStore> _configStore = new();
    private readonly Mock<IModelFetchReceiver> _modelFetchReceiver = new();

    private static readonly JobTemplate SampleTemplate = new()
    {
        Labels = "dotnet,dotnet10",
        Image = "agent:latest",
        ProviderType = "opencode"
    };

    private static readonly JobTemplate KiroTemplate = new()
    {
        Labels = "kiro",
        Image = "agent:kiro",
        ProviderType = "kiro"
    };

    private readonly FakeJobClient _fakeClient;
    private readonly Mock<IModelFetchReceiver> _mockReceiver;
    private readonly Mock<ILogger> _mockLogger;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateStore _templateStore;

    private static readonly IReadOnlyList<AgentModelInfo> TwoModels =
    [
        new AgentModelInfo { ModelId = "claude-sonnet-4", Description = "Balanced", RateMultiplier = 1.0 },
        new AgentModelInfo { ModelId = "claude-opus-4",   Description = "Most capable", RateMultiplier = 5.0 }
    ];

    public ModelFetchJobServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
                   .Returns(_mockLogger.Object);

        _fakeClient = new FakeJobClient();
        _mockReceiver = new Mock<IModelFetchReceiver>();

        _options = new DispatchServiceOptions
        {
            Namespace = "coding-agent",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "caa-agent",
            KiroPvcPool = ["caa-kiro-data-0", "caa-kiro-data-1"]
        };

        _templateStore = BuildTemplateStore();
    }

    private ModelFetchJobService CreateService(
        DispatchServiceOptions? options = null,
        JobTemplateStore? templateStore = null)
    {
        // Use PollTimeoutSecondsOverride=1 so any real waiting paths resolve quickly.
        // PollIntervalMs=1 removes wait time in poll loops.
        var deps = new ModelFetchJobDependencies(
            KubeClient: _kubeClient.Object,
            TemplateStore: templateStore ?? JobTemplateStore.CreateEmpty(),
            Options: options ?? new DispatchServiceOptions { Namespace = "test-ns", AgentApiKeyValue = "test-master-key" },
            ConfigStore: _configStore.Object,
            ModelFetchReceiver: _modelFetchReceiver.Object,
            PollTimeoutSecondsOverride: 1,
            PollIntervalMs: 1,
            Logger: Serilog.Log.Logger);

        return new ModelFetchJobService(deps);
    }

    private void SetupConfigStore(int modelFetchTimeoutSeconds = 30)
    {
        _configStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { ModelFetchTimeoutSeconds = modelFetchTimeoutSeconds });
    }

    // ── 1. No matching template for providerType ──────────────────────────

    [Fact]
    public async Task FetchModelsAsync_NoMatchingTemplate_ReturnsErrorWithoutCallingKube()
    {
        SetupConfigStore();
        var service = CreateService(templateStore: JobTemplateStore.CreateEmpty());

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("No job template found", "error message must name the missing template");

        // No k8s job should be created when template lookup fails
        _kubeClient.Verify(k => k.CreateJobAsync(
            It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FetchModelsAsync_NoMatchingTemplate_ErrorContainsProviderType()
    {
        SetupConfigStore();
        var service = CreateService(templateStore: JobTemplateStore.CreateEmpty());

        var (_, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().Contain("kiro");
    }

    // ── 2. Kiro provider with empty PVC pool ─────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_KiroProviderWithEmptyPvcPool_ReturnsErrorWithoutCallingKube()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(KiroTemplate);
        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            AgentApiKeyValue = "test-master-key",
            KiroPvcPool = [] // empty — no PVCs configured
        };
        var service = CreateService(options: options, templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("PVC", "error must explain PVC is required for kiro");

        _kubeClient.Verify(k => k.CreateJobAsync(
            It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 3. Job creation failure ───────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_JobCreationThrows_ReturnsErrorMessage()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);
        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("k8s API unavailable"));

        var service = CreateService(templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("Failed to create fetch-models job");
        error.Should().Contain("k8s API unavailable");
    }

    [Fact]
    public async Task FetchModelsAsync_JobCreationThrows_DoesNotPropagateException()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);
        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("cluster unreachable"));

        var service = CreateService(templateStore: templateStore);

        var act = async () => await service.FetchModelsAsync("opencode", CancellationToken.None);

        await act.Should().NotThrowAsync("job creation failure must be returned, not thrown");
    }

    // ── 4. Cancellation before job creation ──────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_CancelledBeforeJobCreate_ReturnsErrorMessage()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);

        // Make CreateJobAsync throw OperationCanceledException (simulates cancellation during create)
        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("cancelled", "cancellation during job create must be surfaced as an error message");
    }

    // ── 5. Happy path — job created, agent reports results ────────────────

    [Fact]
    public async Task FetchModelsAsync_HappyPath_ReturnsModelsAndNullError()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);

        var expectedModels = new List<AgentModelInfo>
        {
            new() { ModelId = "gpt-4o", Description = "GPT-4o" },
            new() { ModelId = "claude-3-5-sonnet", Description = "Claude" }
        };

        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _kubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _modelFetchReceiver
            .Setup(r => r.WaitAndFetchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AgentModelInfo>)expectedModels, (string?)null));

        var service = CreateService(templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().HaveCount(2);
        models[0].ModelId.Should().Be("gpt-4o");
        error.Should().BeNull("happy path must return null error");
    }

    // ── Agent key (Spec 043 Req 8a) ────────────────────────────────────────

    /// <summary>
    /// The fetch-models pod receives only its own key: the Secret caa-key-{jobName} holding
    /// HMAC-SHA256(master key, job name). It never gets the master key.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_CreatesTheJobsAgentKeySecret()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);
        string? jobName = null;
        _kubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<k8s.Models.V1Job, string, CancellationToken>((j, _, _) => jobName = j.Metadata.Name)
            .Returns(Task.CompletedTask);
        k8s.Models.V1Secret? keySecret = null;
        _kubeClient
            .Setup(k => k.CreateSecretAsync(It.IsAny<k8s.Models.V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<k8s.Models.V1Secret, string, CancellationToken>((s, _, _) => keySecret = s)
            .Returns(Task.CompletedTask);
        _modelFetchReceiver
            .Setup(r => r.WaitAndFetchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AgentModelInfo>)Array.Empty<AgentModelInfo>(), (string?)null));

        var service = CreateService(templateStore: templateStore);

        await service.FetchModelsAsync("opencode", CancellationToken.None);

        keySecret.Should().NotBeNull("the fetch-models Job must get its own agent key Secret");
        keySecret!.Metadata.Name.Should().Be($"caa-key-{jobName}");
        keySecret.StringData["agent-api-key"].Should().Be(AgentKeyDerivation.DeriveAgentKey("test-master-key", jobName!));
    }

    /// <summary>
    /// Without its key Secret the pod could never authenticate, so the Job is deleted instead of
    /// waiting until its deadline, and the caller gets an error.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_KeySecretCannotBeCreated_DeletesJobAndReturnsError()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);
        string? jobName = null;
        _kubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<k8s.Models.V1Job, string, CancellationToken>((j, _, _) => jobName = j.Metadata.Name)
            .Returns(Task.CompletedTask);
        _kubeClient
            .Setup(k => k.CreateSecretAsync(It.IsAny<k8s.Models.V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secrets are forbidden"));
        _kubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("agent key");
        _kubeClient.Verify(k => k.DeleteJobAsync(jobName!, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _modelFetchReceiver.Verify(
            r => r.WaitAndFetchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FetchModelsAsync_NoMasterKey_ReturnsErrorWithoutCallingKube()
    {
        SetupConfigStore();
        var service = CreateService(
            options: new DispatchServiceOptions { Namespace = "test-ns" },
            templateStore: BuildStoreWith(SampleTemplate));

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Contain("AGENT_API_KEY");
        _kubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── 6. Job created, agent returns error from WaitAndFetch ─────────────

    [Fact]
    public async Task FetchModelsAsync_AgentReturnsError_ReturnsErrorAndEmptyModels()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);

        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _kubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _modelFetchReceiver
            .Setup(r => r.WaitAndFetchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AgentModelInfo>)[], "Timed out waiting for agent"));

        var service = CreateService(templateStore: templateStore);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        models.Should().BeEmpty();
        error.Should().Be("Timed out waiting for agent");
    }

    // ── 7. Cleanup failure does not mask successful result ────────────────

    [Fact]
    public async Task FetchModelsAsync_CleanupFails_DoesNotPropagateAndReturnResult()
    {
        SetupConfigStore();
        var templateStore = BuildStoreWith(SampleTemplate);
        var expectedModels = new List<AgentModelInfo> { new() { ModelId = "model-1", Description = "" } };

        _kubeClient
            .Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Cleanup throws — must not mask the result
        _kubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("job already deleted"));

        _modelFetchReceiver
            .Setup(r => r.WaitAndFetchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AgentModelInfo>)expectedModels, (string?)null));

        var service = CreateService(templateStore: templateStore);

        var act = async () => await service.FetchModelsAsync("opencode", CancellationToken.None);

        await act.Should().NotThrowAsync("cleanup failure must not propagate");

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);
        error.Should().BeNull("successful fetch result must survive cleanup failure");
    }

    // ── 8. IsPvcPoolConfigured reflects pool size ─────────────────────────

    [Fact]
    public void IsPvcPoolConfigured_EmptyPool_ReturnsFalse()
    {
        var service = CreateService(options: new DispatchServiceOptions { KiroPvcPool = [] });
        service.IsPvcPoolConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsPvcPoolConfigured_NonEmptyPool_ReturnsTrue()
    {
        var service = CreateService(options: new DispatchServiceOptions
        {
            KiroPvcPool = ["pvc-kiro-1"]
        });
        service.IsPvcPoolConfigured.Should().BeTrue();
    }

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_Success_CreatesJobAndReturnsParsedModels()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().BeNull();
        models.Should().HaveCount(2);
        models[0].ModelId.Should().Be("claude-sonnet-4");
        models[1].ModelId.Should().Be("claude-opus-4");
        _fakeClient.CreatedJobCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchModelsAsync_KiroProvider_MountsPvc()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var job = _fakeClient.LastCreatedJob!;
        job.Spec.Template.Spec.Volumes
            .Should().Contain(v => v.PersistentVolumeClaim != null,
                "kiro provider requires a credential PVC");
        job.Spec.Template.Spec.Containers[0].VolumeMounts
            .Should().Contain(vm => vm.MountPath.Contains("kiro-cli"),
                "kiro-cli data directory must be mounted");
    }

    [Fact]
    public async Task FetchModelsAsync_JobSpec_RunsNormalAgentBinary()
    {
        // No command override: the agent binary connects to the hub and handles RequestFetchModels.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var container = _fakeClient.LastCreatedJob!.Spec.Template.Spec.Containers[0];
        container.Command.Should().BeNullOrEmpty("no command override — agent binary uses its default entrypoint");
        if (container.Args is not null)
            container.Args.Should().NotContain(a => a.Contains("--list-models"),
                "args must not include kiro-cli --list-models; that's handled internally");
    }

    [Fact]
    public async Task FetchModelsAsync_Success_DeletesJobAfterCompletion()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _fakeClient.DeletedJobCount.Should().Be(1, "job must be deleted after successful fetch");
    }

    [Fact]
    public async Task FetchModelsAsync_JobName_UsesDistinctPrefix()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _fakeClient.LastCreatedJobName!.Should().StartWith("caa-models-",
            "model-fetch jobs must use the caa-models- prefix to distinguish from work item jobs");
    }

    [Fact]
    public async Task FetchModelsAsync_JobLabels_IncludeJobTypeLabel()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var labels = _fakeClient.LastCreatedJob!.Metadata.Labels;
        labels.Should().ContainKey("caa/job-type");
        labels["caa/job-type"].Should().Be("model-fetch");
    }

    [Fact]
    public async Task FetchModelsAsync_JobLabels_ComponentIsModelFetch_NotAgentJob()
    {
        // ReconciliationService excludes model-fetch jobs by component label.
        // If this is "agent-job" instead, the reconciler would try to manage them.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var labels = _fakeClient.LastCreatedJob!.Metadata.Labels;
        labels["app.kubernetes.io/component"].Should().Be("model-fetch");
    }

    [Fact]
    public async Task FetchModelsAsync_ReceiverCalledWithJobNamePrefix()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _mockReceiver.Verify(r => r.WaitAndFetchAsync(
            _fakeClient.LastCreatedJobName!,
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Job spec validation (BackoffLimit, TTL, deadline) ───────────

    [Fact]
    public async Task FetchModelsAsync_JobSpec_BackoffLimitIsZero()
    {
        // BackoffLimit=0 means no retries — model-fetch failures surface immediately.
        // If BackoffLimit were 2 (the work-item default), k8s would restart the pod
        // three times before the fetch times out, wasting credentials.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _fakeClient.LastCreatedJob!.Spec.BackoffLimit.Should().Be(0,
            "model-fetch must not retry on failure — surface errors immediately");
    }

    [Fact]
    public async Task FetchModelsAsync_JobSpec_TtlSecondsAfterFinishedIs300()
    {
        // 5-minute safety TTL — if cleanup fails, the job doesn't persist forever.
        // Work-item jobs use 3600s (1h); fetch-jobs use 300s (5m).
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient(pollTimeoutSecondsOverride: 60);
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _fakeClient.LastCreatedJob!.Spec.TtlSecondsAfterFinished.Should().Be(300,
            "model-fetch jobs must expire within 5 minutes of completion");
    }

    [Fact]
    public async Task FetchModelsAsync_JobSpec_ActiveDeadlineSeconds_IsTimeoutPlusThirty()
    {
        // ActiveDeadlineSeconds bounds the total pod lifetime. If it equals the poll
        // timeout, the pod can be killed before WaitAndFetchAsync finishes reading the
        // result. The +30s buffer absorbs k8s scheduling and connection setup latency.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient(pollTimeoutSecondsOverride: 60);
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        _fakeClient.LastCreatedJob!.Spec.ActiveDeadlineSeconds.Should().Be(90,
            "ActiveDeadlineSeconds must be pollTimeoutSeconds + 30");
    }

    [Fact]
    public async Task FetchModelsAsync_JobSpec_ContainerDropsAllCapabilities()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var container = _fakeClient.LastCreatedJob!.Spec.Template.Spec.Containers[0];
        container.SecurityContext.Should().NotBeNull();
        container.SecurityContext!.Capabilities.Should().NotBeNull();
        container.SecurityContext.Capabilities!.Drop.Should().Contain("ALL",
            "least-privilege containers must drop all Linux capabilities");
    }

    // ── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_NoPvcPool_ReturnsError_NeverCreatesJob()
    {
        var options = new DispatchServiceOptions
        {
            Namespace = "coding-agent",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "caa-agent",
            KiroPvcPool = []
        };
        var service = CreateServiceWithFakeClient(options: options);
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().Contain("credential");
        models.Should().BeEmpty();
        _fakeClient.CreatedJobCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchModelsAsync_CreateJobThrows_ReturnsError_NeverCallsReceiver()
    {
        // If job creation fails (e.g. k8s API unreachable), the receiver must not be called
        // and no delete must be attempted (there's no job to delete).
        _fakeClient.FailNextCreate = new InvalidOperationException("k8s API unavailable");
        var service = CreateServiceWithFakeClient();
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().Contain("Failed to create fetch-models job");
        models.Should().BeEmpty();
        _mockReceiver.Verify(r => r.WaitAndFetchAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "receiver must not be called when job creation fails");
        _fakeClient.DeletedJobCount.Should().Be(0, "no job exists to clean up");
    }

    [Fact]
    public async Task FetchModelsAsync_CreateJobCancelled_ReturnsSpecificMessage()
    {
        // OperationCanceledException from CreateJobAsync gets a dedicated error message.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _fakeClient.FailNextCreateCancelled = true;
        var service = CreateServiceWithFakeClient();

        var (models, error) = await service.FetchModelsAsync("kiro", cts.Token);

        error.Should().Contain("cancelled before the job could be created");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(0);
    }

    [Fact]
    public async Task FetchModelsAsync_ReceiverReturnsError_PropagatesError_AndCleansUpJob()
    {
        SetupReceiverReturns([], "Agent timed out connecting");
        var service = CreateServiceWithFakeClient();
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().Be("Agent timed out connecting");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1, "job must be deleted even when fetch fails");
    }

    [Fact]
    public async Task FetchModelsAsync_ReceiverThrows_ReturnsWrappedError_AndCleansUpJob()
    {
        // Unexpected exception from WaitAndFetchAsync is caught, wrapped, and cleanup still runs.
        _mockReceiver
            .Setup(r => r.WaitAndFetchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub transport error"));
        var service = CreateServiceWithFakeClient();

        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().Contain("Unexpected error");
        error.Should().Contain("hub transport error");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1, "cleanup must run even when receiver throws");
    }

    [Fact]
    public async Task FetchModelsAsync_CleanupFails_StillReturnsModels()
    {
        SetupReceiverReturns(TwoModels, null);
        _fakeClient.FailNextDelete = true;
        var service = CreateServiceWithFakeClient();
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().BeNull("cleanup failure must not propagate as a fetch error");
        models.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchModelsAsync_Cancellation_ReturnsError_NotThrows()
    {
        SetupReceiverReturns([], "Fetch models was cancelled.");
        var service = CreateServiceWithFakeClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (models, error) = await service.FetchModelsAsync("kiro", cts.Token);

        error.Should().NotBeNullOrEmpty();
        models.Should().BeEmpty();
    }

    // ── PVC selection logic ─────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_AllPvcsClaimedByActiveJobs_FallsBackToFirstPvc()
    {
        // When all PVCs are claimed, log a warning and fall back to KiroPvcPool[0].
        // This is the RWX path (volumes support concurrent access).
        SetupReceiverReturns(TwoModels, null);
        _fakeClient.ConfigureRunningJobsWithPvcs(["caa-kiro-data-0", "caa-kiro-data-1"]);
        var service = CreateServiceWithFakeClient();

        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().BeNull("fallback to KiroPvcPool[0] should succeed");
        var job = _fakeClient.LastCreatedJob!;
        var vols0 = job.Spec.Template.Spec.Volumes;
        vols0.Should().Contain(v => v.PersistentVolumeClaim != null && v.PersistentVolumeClaim.ClaimName == "caa-kiro-data-0",
            "fallback must use the first PVC in the pool");
    }

    [Fact]
    public async Task FetchModelsAsync_InactiveJobsClaimPvcs_PvcsConsideredAvailable()
    {
        // Jobs with Active=0 (completed/failed) do NOT block PVC availability.
        // Only running/pending jobs with Active >= 1 matter.
        SetupReceiverReturns(TwoModels, null);
        _fakeClient.ConfigureInactiveJobsWithPvcs(["caa-kiro-data-0"]);
        var service = CreateServiceWithFakeClient();

        await service.FetchModelsAsync("kiro", CancellationToken.None);

        var job2 = _fakeClient.LastCreatedJob!;
        job2.Spec.Template.Spec.Volumes
            .Should().Contain(v => v.PersistentVolumeClaim != null && v.PersistentVolumeClaim.ClaimName == "caa-kiro-data-0",
                "completed/failed jobs must not block PVC selection");
    }

    [Fact]
    public async Task FetchModelsAsync_ListJobsThrows_FallsBackToFirstPvc_NoException()
    {
        // ListJobsAsync failure falls back gracefully without propagating.
        SetupReceiverReturns(TwoModels, null);
        _fakeClient.FailListJobs = true;
        var service = CreateServiceWithFakeClient();

        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        error.Should().BeNull("ListJobs failure must use fallback PVC, not fail the whole fetch");
        var job3 = _fakeClient.LastCreatedJob!;
        job3.Spec.Template.Spec.Volumes
            .Should().Contain(v => v.PersistentVolumeClaim != null && v.PersistentVolumeClaim.ClaimName == "caa-kiro-data-0",
                "fallback to KiroPvcPool[0] when list query fails");
    }

    // ── Progress reporting ──────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_Success_ReportsProgressPhases()
    {
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        var phases = new List<string>();
        // Use a synchronous IProgress<string> so callbacks fire inline, not on the thread pool.
        // Progress<T> posts callbacks asynchronously (ThreadPool.QueueUserWorkItem) which makes
        // Task.Delay-based flushing unreliable in unit tests with no SynchronizationContext.
        IProgress<string> progress = new SyncProgress<string>(p => phases.Add(p));

        await service.FetchModelsAsync("kiro", CancellationToken.None, progress);

        phases.Should().Contain("Creating job…",      "job creation phase must be reported");
        phases.Should().Contain("Waiting for agent to connect…", "waiting phase must be reported");
        phases.Should().Contain("Received results…",  "success phase must be reported");
    }

    [Fact]
    public async Task FetchModelsAsync_Error_DoesNotReportSuccessPhase()
    {
        SetupReceiverReturns([], "Agent timed out");
        var service = CreateServiceWithFakeClient();
        var phases = new List<string>();
        IProgress<string> progress = new SyncProgress<string>(p => phases.Add(p));

        await service.FetchModelsAsync("kiro", CancellationToken.None, progress);

        phases.Should().NotContain("Received results…",
            "success phase must NOT be reported on error");
    }

    // ── Non-kiro provider (no PVC required) ─────────────────────────

    [Fact]
    public async Task FetchModelsAsync_NonKiroProvider_DoesNotMountPvc()
    {
        // opencode provider does not require a credential PVC.
        var store = JobTemplateStore.LoadFromJson("""
            [{ "labels": "opencode", "image": "img", "imagePullPolicy": "Always",
               "providerType": "opencode", "maxConcurrent": 1 }]
            """);
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient(templateStore: store);

        await service.FetchModelsAsync("opencode", CancellationToken.None);

        var job = _fakeClient.LastCreatedJob!;
        var pvcVolumes = job.Spec.Template.Spec.Volumes
            .Where(v => v.PersistentVolumeClaim != null && v.PersistentVolumeClaim.ClaimName != null && v.PersistentVolumeClaim.ClaimName.Contains("kiro"))
            .ToList();
        pvcVolumes.Should().BeEmpty("non-kiro providers must not mount kiro credential PVCs");
    }

    [Fact]
    public async Task FetchModelsAsync_NonKiroProvider_EmptyPvcPool_DoesNotBlockJob()
    {
        // KiroPvcPool=[] should only block kiro fetches, not opencode fetches.
        var store = JobTemplateStore.LoadFromJson("""
            [{ "labels": "opencode", "image": "img", "imagePullPolicy": "Always",
               "providerType": "opencode", "maxConcurrent": 1 }]
            """);
        var options = new DispatchServiceOptions
        {
            Namespace = "coding-agent",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "caa-agent",
            KiroPvcPool = []   // empty
        };
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient(templateStore: store, options: options);

        var (models, error) = await service.FetchModelsAsync("opencode", CancellationToken.None);

        error.Should().BeNull("empty PVC pool must not block non-kiro providers");
        models.Should().HaveCount(2);
    }

    // ── Concurrency ─────────────────────────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_ConcurrentCalls_ProduceDistinctJobNames()
    {
        // Each call must create a distinct job so concurrent fetches don't collide.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();

        var t1 = service.FetchModelsAsync("kiro", CancellationToken.None);
        var t2 = service.FetchModelsAsync("kiro", CancellationToken.None);
        await Task.WhenAll(t1, t2);

        _fakeClient.AllCreatedJobNames.Should().HaveCount(2);
        _fakeClient.AllCreatedJobNames.Distinct().Should().HaveCount(2,
            "each concurrent fetch must produce a unique job name");
    }

    // ── CancellationToken propagation ───────────────────────────────

    [Fact]
    public async Task FetchModelsAsync_CancellationToken_PassedToReceiver()
    {
        // The ct passed to FetchModelsAsync must flow through to WaitAndFetchAsync.
        // If it were swapped for CancellationToken.None, UI cancel wouldn't work.
        SetupReceiverReturns(TwoModels, null);
        var service = CreateServiceWithFakeClient();
        using var cts = new CancellationTokenSource();

        await service.FetchModelsAsync("kiro", cts.Token);

        // The ct used in WaitAndFetchAsync must NOT be CancellationToken.None
        // (it will be a linked token wrapping cts.Token, so we can't compare directly,
        // but we can verify the receiver was called with a non-default token)
        _mockReceiver.Verify(r => r.WaitAndFetchAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.Is<CancellationToken>(t => t != CancellationToken.None)),
            Times.Once, "the outer CancellationToken must be forwarded to the receiver");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="JobTemplateStore"/> from a JSON representation of a single template.
    /// Uses LoadFromJson to bypass YAML loading while creating a real (non-empty) store.
    /// </summary>
    private static JobTemplateStore BuildStoreWith(JobTemplate template)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                labels = template.Labels,
                image = template.Image,
                imagePullPolicy = template.ImagePullPolicy,
                providerType = template.ProviderType
            }
        });
        return JobTemplateStore.LoadFromJson(json);
    }

    private void SetupReceiverReturns(IReadOnlyList<AgentModelInfo> models, string? error)
    {
        _mockReceiver
            .Setup(r => r.WaitAndFetchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((models, error));
    }

    private ModelFetchJobService CreateServiceWithFakeClient(
        DispatchServiceOptions? options = null,
        JobTemplateStore? templateStore = null,
        int pollTimeoutSecondsOverride = 10,
        int pollIntervalMs = 50)
    {
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new PipelineConfiguration());

        return new ModelFetchJobService(
            new ModelFetchJobDependencies(
                _fakeClient,
                templateStore ?? _templateStore,
                options ?? _options,
                mockConfigStore.Object,
                _mockReceiver.Object,
                PollTimeoutSecondsOverride: pollTimeoutSecondsOverride,
                PollIntervalMs: pollIntervalMs,
                Logger: _mockLogger.Object));
    }

    private static JobTemplateStore BuildTemplateStore()
    {
        return JobTemplateStore.LoadFromJson("""
            [{
              "labels": "dotnet,kiro",
              "image": "chemsorly/coding-agent:kiro-dotnet10",
              "imagePullPolicy": "Always",
              "providerType": "kiro",
              "maxConcurrent": 2
            }]
            """);
    }

    // ── Test doubles ───

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> implementation that invokes the callback
    /// inline on the calling thread. Unlike <see cref="Progress{T}"/>, which posts to
    /// <see cref="System.Threading.SynchronizationContext"/> (or the ThreadPool when none is
    /// present), this fires immediately so unit tests do not need Task.Delay-based flushing.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ── Fake k8s client ─────────────────────────────────────────────

    private sealed class FakeJobClient : IKubernetesJobClient
    {
        private readonly List<V1Job> _createdJobs = [];
        private int _deletedCount;

        // Test configuration knobs
        public bool FailNextDelete { get; set; }
        public bool FailListJobs { get; set; }
        public Exception? FailNextCreate { get; set; }
        public bool FailNextCreateCancelled { get; set; }

        // Running jobs that claim PVCs (Active >= 1)
        private List<string> _runningJobPvcs = [];
        // Inactive jobs that claim PVCs (Active = 0) — should NOT block selection
        private List<string> _inactiveJobPvcs = [];

        public void ConfigureRunningJobsWithPvcs(IEnumerable<string> pvcs) =>
            _runningJobPvcs = pvcs.ToList();

        public void ConfigureInactiveJobsWithPvcs(IEnumerable<string> pvcs) =>
            _inactiveJobPvcs = pvcs.ToList();

        public int CreatedJobCount => _createdJobs.Count;
        public int DeletedJobCount => _deletedCount;
        public V1Job? LastCreatedJob => _createdJobs.LastOrDefault();
        public string? LastCreatedJobName => _createdJobs.LastOrDefault()?.Metadata?.Name;
        public IReadOnlyList<string> AllCreatedJobNames =>
            _createdJobs.Select(j => j.Metadata.Name).ToList();

        public Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
        {
            if (FailNextCreateCancelled)
                throw new OperationCanceledException("Simulated cancellation during create");
            if (FailNextCreate is not null)
            {
                var ex = FailNextCreate;
                FailNextCreate = null;
                throw ex;
            }
            _createdJobs.Add(job);
            return Task.CompletedTask;
        }

        public Task DeleteJobAsync(string name, string ns, CancellationToken ct = default)
        {
            if (FailNextDelete) { FailNextDelete = false; throw new InvalidOperationException("Simulated delete failure"); }
            _deletedCount++;
            return Task.CompletedTask;
        }

        public Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default)
            => Task.FromResult(new V1Job { Metadata = new V1ObjectMeta { Name = name } });

        public Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default)
        {
            if (FailListJobs) throw new InvalidOperationException("Simulated ListJobs failure");

            var jobs = new List<V1Job>();
            foreach (var pvc in _runningJobPvcs)
            {
                jobs.Add(new V1Job
                {
                    Status = new V1JobStatus { Active = 1 },
                    Spec = new V1JobSpec
                    {
                        Template = new V1PodTemplateSpec
                        {
                            Spec = new V1PodSpec
                            {
                                Volumes = [new V1Volume { Name = "kiro-cli-data",
                                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvc } }]
                            }
                        }
                    }
                });
            }
            foreach (var pvc in _inactiveJobPvcs)
            {
                jobs.Add(new V1Job
                {
                    Status = new V1JobStatus { Active = 0, Succeeded = 1 },
                    Spec = new V1JobSpec
                    {
                        Template = new V1PodTemplateSpec
                        {
                            Spec = new V1PodSpec
                            {
                                Volumes = [new V1Volume { Name = "kiro-cli-data",
                                    PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvc } }]
                            }
                        }
                    }
                });
            }
            return Task.FromResult(new V1JobList { Items = jobs });
        }

        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretAsync(string name, string ns, CancellationToken ct = default) => Task.CompletedTask;
        public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
            => Task.FromResult(new V1PodList { Items = [] });
        public Task<string> ReadPodLogsAsync(string podName, string ns, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
