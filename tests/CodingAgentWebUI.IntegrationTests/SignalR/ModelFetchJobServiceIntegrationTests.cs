using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.IntegrationTests.SignalR;

/// <summary>
/// Integration test verifying the full SignalR round-trip for k8s model fetching:
/// ModelFetchJobService dispatches a job → agent pod connects to hub → hub sends
/// RequestFetchModels → agent responds with ReportFetchModelsResult → models returned.
///
/// Uses a real in-process SignalR hub (via SignalRTestFactory), a real ModelFetchService,
/// and a fake k8s job client. No k8s cluster or kiro-cli required.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ModelFetchJobServiceIntegrationTests : IClassFixture<SignalRTestFixture>, IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly SignalRTestFixture _fixture;

    public ModelFetchJobServiceIntegrationTests(SignalRTestFixture fixture)
    {
        _fixture = fixture;
        // Reset both registry and model cache at the start of each test so no
        // prior test's state bleeds in. (DisposeAsync resets at the end too, but
        // the order between xUnit teardown and the next test's constructor is not
        // guaranteed when the fixture is shared.)
        _fixture.Registry.Reset();
        _fixture.Factory.Services.GetRequiredService<ModelFetchService>().ResetCache();
    }

    public async ValueTask DisposeAsync()
    {
        _fixture.Registry.Reset();
        // Reset the ModelFetchService cache so tests don't bleed into each other.
        // The service is a DI singleton — without this, the first successful fetch
        // caches results that subsequent tests see instead of their own responses.
        _fixture.Factory.Services.GetRequiredService<ModelFetchService>().ResetCache();
    }

    /// <summary>
    /// Full round-trip: ModelFetchJobService dispatches a fake job, a real SignalR client
    /// simulating the agent pod connects to the hub, receives RequestFetchModels, responds
    /// with ReportFetchModelsResult. ModelFetchJobService returns the models via WaitAndFetchAsync.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_AgentConnectsAndResponds_ReturnsModels()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // ── Services from DI ────────────────────────────────────────────────
        var modelFetchService = _fixture.Factory.Services.GetRequiredService<ModelFetchService>();

        // ── Fake k8s job client ─────────────────────────────────────────────
        // Captures the job name so we know what agent ID the pod will register under.
        string? capturedJobName = null;
        var fakeJobClient = new FakeKubernetesJobClient(name => capturedJobName = name);

        // ── Template store ───────────────────────────────────────────────────
        var templateStore = JobTemplateStore.LoadFromJson("""
            [{
              "labels": "kiro",
              "image": "test-image:latest",
              "imagePullPolicy": "IfNotPresent",
              "providerType": "kiro",
              "maxConcurrent": 1
            }]
            """);

        // ── Config store ────────────────────────────────────────────────────
        var mockConfig = new Mock<IPipelineConfigStore>();
        mockConfig.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new PipelineConfiguration());

        // ── Options ────────────────────────────────────────────────────────
        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            OrchestratorUrl = _fixture.ServerAddress,
            AgentApiKeySecretName = "test-key",
            AgentServiceAccountName = "test-sa",
            KiroPvcPool = ["test-pvc"]
        };

        // ── Service under test ──────────────────────────────────────────────
        var service = new ModelFetchJobService(
            new ModelFetchJobDependencies(
                fakeJobClient,
                templateStore,
                options,
                mockConfig.Object,
                modelFetchService,          // real ModelFetchService — routes through hub
                PollTimeoutSecondsOverride: 10,
                PollIntervalMs: 50));

        // ── Start FetchModelsAsync in background ────────────────────────────
        // It will dispatch the fake job, then poll the registry waiting for the agent.
        var fetchTask = Task.Run(
            () => service.FetchModelsAsync("kiro", cts.Token),
            cts.Token);

        // Wait briefly for the job to be "created" so we know the job name.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (capturedJobName is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, cts.Token);

        capturedJobName.Should().NotBeNullOrEmpty("fake job client must have captured the job name");
        capturedJobName!.Should().StartWith(ModelFetchJobService.JobNamePrefix);

        // ── Simulate the agent pod connecting ──────────────────────────────
        // The pod name is "{jobName}-{suffix}"; AGENT_ID env var = pod name.
        var podName = $"{capturedJobName}-testpod";
        await using var agentConn = CreateAgentConnection(podName);

        // Wire up the RequestFetchModels handler — responds immediately with fake models.
        var expectedModels = new List<AgentModelInfo>
        {
            new() { ModelId = "test-model-1", Description = "Fast", RateMultiplier = 1.0 },
            new() { ModelId = "test-model-2", Description = "Slow", RateMultiplier = 3.0 }
        };

        agentConn.On<FetchModelsRequest>("RequestFetchModels", async request =>
        {
            // Simulate agent running kiro-cli and reporting back
            await agentConn.InvokeAsync("ReportFetchModelsResult", new FetchModelsResponse
            {
                RequestId = request.RequestId,
                Models = expectedModels
            }, cts.Token);
        });

        await agentConn.StartAsync(cts.Token);

        // Register as an agent (mimics what AgentWorkerService does on startup)
        await agentConn.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = podName,
            Hostname = "test-pod-host",
            Labels = ["kiro"]
        }, cts.Token);

        // ── Await the result ────────────────────────────────────────────────
        var (models, error) = await fetchTask;

        // ── Assert ─────────────────────────────────────────────────────────
        error.Should().BeNull("round-trip should succeed with no error");
        models.Should().HaveCount(2);
        models[0].ModelId.Should().Be("test-model-1");
        models[0].RateMultiplier.Should().Be(1.0);
        models[1].ModelId.Should().Be("test-model-2");
        models[1].RateMultiplier.Should().Be(3.0);

        // Job should have been cleaned up
        fakeJobClient.DeletedCount.Should().Be(1, "job must be deleted after successful fetch");
    }

    /// <summary>
    /// When the agent pod never connects within the timeout, FetchModelsAsync returns an error
    /// and still cleans up the job.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_AgentNeverConnects_ReturnsTimeoutError_AndCleansUpJob()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var modelFetchService = _fixture.Factory.Services.GetRequiredService<ModelFetchService>();
        var fakeJobClient = new FakeKubernetesJobClient(_ => { });

        var templateStore = JobTemplateStore.LoadFromJson("""
            [{ "labels": "kiro", "image": "img", "imagePullPolicy": "Always",
               "providerType": "kiro", "maxConcurrent": 1 }]
            """);

        var mockConfig = new Mock<IPipelineConfigStore>();
        mockConfig.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new PipelineConfiguration());

        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            OrchestratorUrl = _fixture.ServerAddress,
            AgentApiKeySecretName = "test-key",
            AgentServiceAccountName = "test-sa",
            KiroPvcPool = ["test-pvc"]
        };

        // Use a 2-second timeout so the test is fast
        var service = new ModelFetchJobService(
            new ModelFetchJobDependencies(
                fakeJobClient, templateStore, options, mockConfig.Object, modelFetchService,
                PollTimeoutSecondsOverride: 2, PollIntervalMs: 50));

        var (models, error) = await service.FetchModelsAsync("kiro", cts.Token);

        error.Should().NotBeNullOrEmpty("timeout must produce an error");
        error.Should().Contain("connect", "error must mention the agent not connecting");
        models.Should().BeEmpty();
        fakeJobClient.DeletedCount.Should().Be(1, "job must be deleted even on timeout");
    }

    /// <summary>
    /// When the agent responds with an error (e.g. kiro-cli not found), the error
    /// is propagated through the hub, CompleteRequest, and back to FetchModelsAsync.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_AgentRespondsWithError_PropagatesError()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var modelFetchService = _fixture.Factory.Services.GetRequiredService<ModelFetchService>();

        string? capturedJobName = null;
        var fakeJobClient = new FakeKubernetesJobClient(name => capturedJobName = name);
        var templateStore = JobTemplateStore.LoadFromJson("""
            [{ "labels": "kiro", "image": "img", "imagePullPolicy": "Always", "providerType": "kiro", "maxConcurrent": 1 }]
            """);
        var mockConfig = new Mock<IPipelineConfigStore>();
        mockConfig.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns", OrchestratorUrl = _fixture.ServerAddress,
            AgentApiKeySecretName = "test-key", AgentServiceAccountName = "test-sa",
            KiroPvcPool = ["test-pvc"]
        };
        var service = new ModelFetchJobService(
            new ModelFetchJobDependencies(
                fakeJobClient, templateStore, options, mockConfig.Object, modelFetchService,
                PollTimeoutSecondsOverride: 10, PollIntervalMs: 50));
        var fetchTask = Task.Run(() => service.FetchModelsAsync("kiro", cts.Token), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (capturedJobName is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, cts.Token);
        capturedJobName.Should().NotBeNullOrEmpty();

        var podName = $"{capturedJobName}-errpod";
        await using var agentConn = CreateAgentConnection(podName);

        // Agent responds with an error instead of models
        agentConn.On<FetchModelsRequest>("RequestFetchModels", async request =>
        {
            await agentConn.InvokeAsync("ReportFetchModelsResult", new FetchModelsResponse
            {
                RequestId = request.RequestId,
                Models = [],
                Error = "kiro-cli binary not found at /home/ubuntu/.local/bin/kiro-cli"
            }, cts.Token);
        });

        await agentConn.StartAsync(cts.Token);
        await agentConn.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
        {
            AgentId = podName, Hostname = "test-host", Labels = ["kiro"]
        }, cts.Token);

        var (models, error) = await fetchTask;

        error.Should().Contain("kiro-cli binary not found",
            "the agent's error message must propagate back to the caller");
        models.Should().BeEmpty();
        fakeJobClient.DeletedCount.Should().Be(1, "job must be cleaned up even on agent error");
    }

    /// <summary>
    /// Once models are cached in ModelFetchService, WaitAndFetchAsync returns immediately
    /// without sending RequestFetchModels over SignalR — even though a job is still dispatched.
    /// The cache short-circuits the hub round-trip, not the job dispatch.
    /// </summary>
    [Fact]
    public async Task FetchModelsAsync_CacheHit_SkipsHubRoundTrip()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var modelFetchService = _fixture.Factory.Services.GetRequiredService<ModelFetchService>();

        // First call — prime the cache via WaitAndFetchAsync with a fake agent
        string? firstJobName = null;
        var firstJobClientCapturing = new FakeKubernetesJobClient(name => firstJobName = name);
        var templateStore = JobTemplateStore.LoadFromJson("""
            [{ "labels": "kiro", "image": "img", "imagePullPolicy": "Always", "providerType": "kiro", "maxConcurrent": 1 }]
            """);
        var mockConfig = new Mock<IPipelineConfigStore>();
        mockConfig.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns", OrchestratorUrl = _fixture.ServerAddress,
            AgentApiKeySecretName = "test-key", AgentServiceAccountName = "test-sa",
            KiroPvcPool = ["test-pvc"]
        };

        var firstService = new ModelFetchJobService(
            new ModelFetchJobDependencies(
                firstJobClientCapturing, templateStore, options, mockConfig.Object, modelFetchService,
                PollTimeoutSecondsOverride: 10, PollIntervalMs: 50));

        var firstFetchTask = Task.Run(() => firstService.FetchModelsAsync("kiro", cts.Token), cts.Token);

        // Wait for job to be created, then connect the agent
        var dl = DateTime.UtcNow.AddSeconds(3);
        while (firstJobName is null && DateTime.UtcNow < dl) await Task.Delay(20, cts.Token);
        firstJobName.Should().NotBeNull();

        var podName = $"{firstJobName}-cachepod";
        await using var agentConn = CreateAgentConnection(podName);
        var requestCountBefore = 0; // track hub calls
        agentConn.On<FetchModelsRequest>("RequestFetchModels", async request =>
        {
            Interlocked.Increment(ref requestCountBefore);
            await agentConn.InvokeAsync("ReportFetchModelsResult", new FetchModelsResponse
            {
                RequestId = request.RequestId,
                Models = [new AgentModelInfo { ModelId = "cached-model", RateMultiplier = 1.0 }]
            }, cts.Token);
        });
        await agentConn.StartAsync(cts.Token);
        await agentConn.InvokeAsync("RegisterAgent",
            new AgentRegistrationMessage { AgentId = podName, Hostname = "h", Labels = ["kiro"] }, cts.Token);
        await firstFetchTask; // prime the cache

        // Second fetch — cache should be populated, no new hub call
        var secondJobClient = new FakeKubernetesJobClient(_ => { });
        var secondService = new ModelFetchJobService(new ModelFetchJobDependencies(
            secondJobClient, templateStore, options, mockConfig.Object, modelFetchService,
            PollTimeoutSecondsOverride: 10, PollIntervalMs: 50));

        var hubCallsBeforeSecond = requestCountBefore;
        var (models, error) = await secondService.FetchModelsAsync("kiro", cts.Token);

        error.Should().BeNull();
        models.Should().Contain(m => m.ModelId == "cached-model",
            "the cache must return the model from the first fetch");
        // No new RequestFetchModels sent to the hub — cache short-circuited WaitAndFetchAsync
        requestCountBefore.Should().Be(hubCallsBeforeSecond,
            "cache hit must not trigger a new RequestFetchModels hub call");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HubConnection CreateAgentConnection(string agentId)
    {
        // Derive HMAC token exactly as production HubConnectionManager does
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SignalRTestFactory.TestApiKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId));
        var token = Convert.ToHexString(hash).ToLowerInvariant();

        return new HubConnectionBuilder()
            .WithUrl($"{_fixture.ServerAddress}{CodingAgentWebUI.Pipeline.HubRoutes.Agent}" +
                     $"?agentId={agentId}&access_token={token}")
            .Build();
    }

    // ── Fake k8s job client ───────────────────────────────────────────────────

    private sealed class FakeKubernetesJobClient : IKubernetesJobClient
    {
        private readonly Action<string> _onJobCreated;
        public int DeletedCount { get; private set; }
        public int CreatedCount { get; private set; }

        public FakeKubernetesJobClient(Action<string> onJobCreated) => _onJobCreated = onJobCreated;

        public Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
        {
            CreatedCount++;
            _onJobCreated(job.Metadata.Name);
            return Task.CompletedTask;
        }

        public Task DeleteJobAsync(string name, string ns, CancellationToken ct = default)
        {
            DeletedCount++;
            return Task.CompletedTask;
        }

        public Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default)
            => Task.FromResult(new V1Job { Metadata = new V1ObjectMeta { Name = name } });

        public Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default)
            => Task.FromResult(new V1JobList { Items = [] });

        public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteSecretAsync(string name, string ns, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
            => Task.FromResult(new V1PodList { Items = [] });

        public Task<string> ReadPodLogsAsync(string podName, string ns, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
