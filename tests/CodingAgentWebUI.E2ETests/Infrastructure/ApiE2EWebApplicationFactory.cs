using CodingAgentWebUI.Api;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Runs <c>CodingAgentWebUI.Api</c> on a real Kestrel port for the E2E harness.
///
/// From Spec 044 the API is the sole host of <c>/hubs/agent</c> and <c>/api/work-items/*</c> —
/// the monolith's <c>MapHub</c> is gone — so a fake agent that connects to the Blazor app gets a
/// 405 on negotiate. The harness therefore runs both processes, and this factory is the half the
/// agents talk to.
///
/// Both hosts live in the same test process, so shared state is passed in rather than
/// synchronised: the same EF InMemory database name, the same seeded
/// <see cref="InMemoryConfigurationStore"/>, and the same run-history fake the assertions read.
///
/// Targets <see cref="ApiHostMarker"/> rather than <c>Program</c>: both assemblies expose a
/// global <c>Program</c> and this project references both.
/// </summary>
public sealed class ApiE2EWebApplicationFactory : WebApplicationFactory<ApiHostMarker>
{
    private readonly string _dbName;
    private readonly InMemoryConfigurationStore _configStore;
    private readonly InMemoryPipelineRunHistoryService _historyService;
    private readonly string _apiKey;

    public ApiE2EWebApplicationFactory(
        string dbName,
        InMemoryConfigurationStore configStore,
        InMemoryPipelineRunHistoryService historyService,
        string apiKey)
    {
        _dbName = dbName;
        _configStore = configStore;
        _historyService = historyService;
        _apiKey = apiKey;
        UseKestrel(0);
    }

    /// <summary>Base address of the running API, e.g. <c>http://localhost:12345</c>.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>Agent registry — agents register here, not on the monolith.</summary>
    public AgentRegistryService AgentRegistry => Services.GetRequiredService<AgentRegistryService>();

    /// <summary>In-memory run state — owned by the API since the hub moved.</summary>
    public IOrchestratorRunService RunService => Services.GetRequiredService<IOrchestratorRunService>();

    /// <summary>
    /// Clears the per-test state this host owns.
    ///
    /// Both of these live here rather than in the monolith because Spec 044 moved the hub, and
    /// neither was being reset between tests: an agent connected by one test stayed Idle in the
    /// registry for every test that followed. That is not merely stale data — <see
    /// cref="FakeJobController"/> claims Pending work for any idle agent it finds, so a leftover
    /// agent silently consumed work items belonging to tests that meant to drive dispatch
    /// themselves, and those tests failed with "expected 1 job, got 0".
    /// </summary>
    public void ResetAll()
    {
        Services.GetRequiredService<AgentRegistryService>().Reset();
        Services.GetRequiredService<OrchestratorRunService>().Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        E2ETestDefaults.ApplyDatabaseEnvironment();
        Environment.SetEnvironmentVariable("AGENT_API_KEY", _apiKey);
        E2ETestDefaults.ResetSerilogBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Background loops (dispatch, reconciliation, leader election, retention sweeps) would
            // race the assertions. The hub is not a hosted service, so it survives this.
            services.RemoveAll<IHostedService>();

            E2EInMemoryDatabase.Install(services, _dbName);

            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Config comes from the same store the tests seed, so the API and the Blazor app agree
            // on templates, profiles and provider configs without a round-trip to Postgres.
            ReplaceSingleton<IConfigurationStore>(services, _configStore);
            ReplaceSingleton<IPipelineConfigStore>(services, _configStore);
            ReplaceSingleton<IProviderConfigStore>(services, _configStore);
            ReplaceSingleton<IAgentProfileStore>(services, _configStore);
            ReplaceSingleton<IQualityGateConfigStore>(services, _configStore);
            ReplaceSingleton<IReviewerConfigStore>(services, _configStore);
            ReplaceSingleton<IProjectStore>(services, _configStore);

            // Run history is asserted directly by the tests via the same instance.
            ReplaceSingleton<IPipelineRunHistoryService>(services, _historyService);

            // No real GitHub / Kiro CLI / Kubernetes in the harness.
            ReplaceSingleton(services, new Mock<IProviderFactory>().Object);
            ReplaceSingleton(services, new Mock<IQualityGateValidator>().Object);
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(new FakeKubernetesJobClient());
            E2ETestDefaults.InstallKubernetesStub(services);

            // Spec 043 moved JobTemplateStore into the API too. It loads
            // /app/config/job-templates.yaml, which exists only in-cluster from the ConfigMap.
            services.RemoveAll<JobTemplateStore>();
            services.AddSingleton(JobTemplateStore.LoadFromYaml("""
                - labels: "kiro,dotnet"
                  image: "chemsorly/coding-agent:kiro-dotnet10-latest"
                  providerType: "kiro"
                  maxConcurrent: 5
                - labels: "kiro,python"
                  image: "chemsorly/coding-agent:kiro-python-latest"
                  providerType: "kiro"
                  maxConcurrent: 5
                """));
        });
    }

    private static void ReplaceSingleton<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
