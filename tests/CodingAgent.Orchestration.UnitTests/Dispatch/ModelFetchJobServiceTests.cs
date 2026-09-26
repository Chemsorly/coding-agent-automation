using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

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
}
