using AwesomeAssertions;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

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
            Options: options ?? new DispatchServiceOptions { Namespace = "test-ns" },
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
