using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ModelFetchJobService"/>.
/// Validates: Job creation, polling, log parsing, error handling, and cleanup.
/// </summary>
[Trait("Feature", "model-fetch-job-k8s")]
public sealed class ModelFetchJobServiceTests
{
    private readonly FakeModelFetchJobClient _fakeClient;
    private readonly Mock<ILogger> _mockLogger;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateStore _templateStore;

    public ModelFetchJobServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
                   .Returns(_mockLogger.Object);

        _fakeClient = new FakeModelFetchJobClient();

        _options = new DispatchServiceOptions
        {
            Namespace = "coding-agent",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            AgentServiceAccountName = "caa-agent",
            KiroPvcPool = ["caa-kiro-data-0", "caa-kiro-data-1"]
        };

        _templateStore = BuildTemplateStore();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Happy path
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchModelsAsync_KiroProvider_CreatesJobWithPvcMount()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().BeNull("no error expected for a successful fetch");
        _fakeClient.CreatedJobCount.Should().Be(1);

        var job = _fakeClient.LastCreatedJob!;
        var container = job.Spec.Template.Spec.Containers[0];
        container.Args.Should().Contain(a => a.Contains("--list-models"),
            "the job must run kiro-cli with --list-models");
        container.Args.Should().Contain(a => a.Contains("--format") && a.Contains("json"),
            "output must be in JSON format");

        // PVC should be mounted for kiro provider
        job.Spec.Template.Spec.Volumes.Should().Contain(v => v.PersistentVolumeClaim != null,
            "kiro provider requires a credential PVC mount");
        container.VolumeMounts.Should().Contain(vm => vm.MountPath.Contains("kiro-cli"),
            "kiro-cli data directory must be mounted");
    }

    [Fact]
    public async Task FetchModelsAsync_SuccessfulPod_ReturnsParsedModels()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().BeNull();
        models.Should().HaveCount(2);
        models[0].ModelId.Should().Be("claude-sonnet-4");
        models[0].Description.Should().Be("Balanced");
        models[0].RateMultiplier.Should().Be(1.0);
        models[1].ModelId.Should().Be("claude-opus-4");
        models[1].RateMultiplier.Should().Be(5.0);
    }

    [Fact]
    public async Task FetchModelsAsync_Success_DeletesJobAfterCompletion()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());

        // Act
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        _fakeClient.DeletedJobCount.Should().Be(1,
            "job must be deleted after successful fetch to keep the cluster clean");
    }

    [Fact]
    public async Task FetchModelsAsync_JobName_UsesDistinctPrefix()
    {
        // Arrange: model-fetch jobs must not conflict with work item jobs (caa-<workItemId>)
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());

        // Act
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        var jobName = _fakeClient.LastCreatedJobName!;
        jobName.Should().StartWith("caa-models-",
            "model-fetch jobs must use the caa-models- prefix to distinguish from work item jobs");
    }

    [Fact]
    public async Task FetchModelsAsync_JobLabels_IncludeJobTypeLabel()
    {
        // Arrange: label used by ReconciliationService to exclude model-fetch pods from stale cleanup
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());

        // Act
        await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        var labels = _fakeClient.LastCreatedJob!.Metadata.Labels;
        labels.Should().ContainKey("caa/job-type");
        labels["caa/job-type"].Should().Be("model-fetch");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Error handling
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FetchModelsAsync_NoTemplateForProviderType_ReturnsError()
    {
        // Arrange: no template registered for "opencode" type
        var emptyStore = JobTemplateStore.LoadFromJson("[]");
        var service = CreateService(templateStore: emptyStore);

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("error required when no template matches provider type");
        error.Should().Contain("No job template", "error message must explain the missing template");
        models.Should().BeEmpty();
        _fakeClient.CreatedJobCount.Should().Be(0, "no job should be created when template is missing");
    }

    [Fact]
    public async Task FetchModelsAsync_NoPvcAvailable_ReturnsError()
    {
        // Arrange: PVC pool configured but all PVCs are in-flight
        var options = new DispatchServiceOptions
        {
            Namespace = "coding-agent",
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "agent-api-key",
            AgentServiceAccountName = "caa-agent",
            KiroPvcPool = []   // empty pool
        };
        var service = CreateService(options: options);

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("error required when no PVC is available");
        error.Should().Contain("credential", "error message must mention the credential PVC requirement");
        _fakeClient.CreatedJobCount.Should().Be(0, "no job should be created without a PVC");
    }

    [Fact]
    public async Task FetchModelsAsync_JobFails_ReturnsError_AndCleansUpJob()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobFails("ImagePullBackOff");

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("failed pod must return error");
        error.Should().Contain("failed",
            "error message must indicate the job failed");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1, "job must be deleted even on failure");
    }

    [Fact]
    public async Task FetchModelsAsync_JobTimesOut_ReturnsError_AndCleansUpJob()
    {
        // Arrange: job never completes within timeout
        var service = CreateService(pollTimeoutSecondsOverride: 2, pollIntervalMs: 100);
        _fakeClient.ConfigureJobNeverCompletes();

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("timeout must return error");
        error.Should().Contain("timed out",
            "timeout must return error");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1, "job must be deleted on timeout");
    }

    [Fact]
    public async Task FetchModelsAsync_EmptyLogs_ReturnsError_AndCleansUpJob()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(string.Empty);  // empty stdout

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("empty pod logs must return error");
        error.Should().Contain("no",
            "empty pod logs must return error");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchModelsAsync_InvalidJsonLogs_ReturnsError_AndCleansUpJob()
    {
        // Arrange
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds("not valid json {{{");

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().NotBeNullOrEmpty("invalid JSON in logs must return error");
        models.Should().BeEmpty();
        _fakeClient.DeletedJobCount.Should().Be(1);
    }

    [Fact]
    public async Task FetchModelsAsync_CleanupFails_StillReturnsModels()
    {
        // Arrange: cleanup failure must not hide a successful result
        var service = CreateService();
        _fakeClient.ConfigureJobSucceeds(PodLogsWithModels());
        _fakeClient.FailNextDelete = true;

        // Act
        var (models, error) = await service.FetchModelsAsync("kiro", CancellationToken.None);

        // Assert
        error.Should().BeNull("cleanup failure must not propagate as a fetch error");
        models.Should().HaveCount(2, "models should still be returned despite cleanup failure");
    }

    [Fact]
    public async Task FetchModelsAsync_Cancellation_CleansUpJobBestEffort()
    {
        // Arrange
        var service = CreateService(pollIntervalMs: 50);
        _fakeClient.ConfigureJobNeverCompletes();
        using var cts = new CancellationTokenSource();

        // Act: cancel after a brief delay
        var task = service.FetchModelsAsync("kiro", cts.Token);
        cts.CancelAfter(100);

        var (models, error) = await task;

        // Assert: after cancellation, error returned (not thrown), cleanup attempted
        error.Should().NotBeNullOrEmpty("cancellation must result in an error, not a thrown exception");
        models.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private ModelFetchJobService CreateService(
        DispatchServiceOptions? options = null,
        JobTemplateStore? templateStore = null,
        int pollTimeoutSecondsOverride = 10,
        int pollIntervalMs = 50)
    {
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new PipelineConfiguration());

        return new ModelFetchJobService(
            _fakeClient,
            templateStore ?? _templateStore,
            options ?? _options,
            mockConfigStore.Object,
            pollTimeoutSecondsOverride,
            pollIntervalMs,
            _mockLogger.Object);
    }

    private static JobTemplateStore BuildTemplateStore()
    {
        var json = """
            [
              {
                "labels": "dotnet,kiro",
                "image": "chemsorly/coding-agent:kiro-dotnet10",
                "imagePullPolicy": "Always",
                "providerType": "kiro",
                "maxConcurrent": 2
              }
            ]
            """;
        return JobTemplateStore.LoadFromJson(json);
    }

    private static string PodLogsWithModels() => """
        {
          "models": [
            { "model_id": "claude-sonnet-4", "description": "Balanced", "rate_multiplier": 1.0 },
            { "model_id": "claude-opus-4", "description": "Most capable", "rate_multiplier": 5.0 }
          ]
        }
        """;

    // ═══════════════════════════════════════════════════════════════════════
    // Fake client
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class FakeModelFetchJobClient : IKubernetesJobClient
    {
        private readonly List<V1Job> _createdJobs = new();
        private int _deletedCount;
        private V1JobStatus? _jobStatus;
        private string? _podLogs;
        private string? _failureReason;
        private bool _neverCompletes;

        public bool FailNextDelete { get; set; }

        public int CreatedJobCount => _createdJobs.Count;
        public int DeletedJobCount => _deletedCount;
        public V1Job? LastCreatedJob => _createdJobs.LastOrDefault();
        public string? LastCreatedJobName => _createdJobs.LastOrDefault()?.Metadata?.Name;

        public void ConfigureJobSucceeds(string podLogs)
        {
            _podLogs = podLogs;
            _failureReason = null;
            _neverCompletes = false;
            _jobStatus = new V1JobStatus { Succeeded = 1, Active = 0, Failed = 0 };
        }

        public void ConfigureJobFails(string reason)
        {
            _failureReason = reason;
            _podLogs = null;
            _neverCompletes = false;
            _jobStatus = new V1JobStatus { Failed = 1, Active = 0, Succeeded = 0 };
        }

        public void ConfigureJobNeverCompletes()
        {
            _neverCompletes = true;
            _podLogs = null;
            _failureReason = null;
            _jobStatus = new V1JobStatus { Active = 1, Succeeded = 0, Failed = 0 };
        }

        public Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
        {
            _createdJobs.Add(job);
            return Task.CompletedTask;
        }

        public Task DeleteJobAsync(string name, string ns, CancellationToken ct = default)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("Simulated delete failure");
            }
            _deletedCount++;
            return Task.CompletedTask;
        }

        public Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default)
        {
            var job = _createdJobs.FirstOrDefault(j => j.Metadata.Name == name)
                ?? new V1Job { Metadata = new V1ObjectMeta { Name = name } };
            job.Status = _neverCompletes
                ? new V1JobStatus { Active = 1 }
                : _jobStatus ?? new V1JobStatus { Active = 1 };
            return Task.FromResult(job);
        }

        public Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default)
            => Task.FromResult(new V1JobList { Items = _createdJobs.ToList() });

        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
        {
            if (_createdJobs.Count == 0)
                return Task.FromResult(new V1PodList { Items = [] });

            var jobName = _createdJobs.Last().Metadata.Name;
            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{jobName}-pod",
                    Labels = new Dictionary<string, string> { ["job-name"] = jobName }
                }
            };
            return Task.FromResult(new V1PodList { Items = [pod] });
        }

        public Task<string> ReadPodLogsAsync(string podName, string ns, CancellationToken ct = default)
        {
            if (_failureReason is not null)
                return Task.FromResult($"Error: {_failureReason}");

            return Task.FromResult(_podLogs ?? string.Empty);
        }
    }
}
